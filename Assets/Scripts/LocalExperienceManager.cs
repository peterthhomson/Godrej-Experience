using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Authoritative presentation state shared between the Salesman device (Host) and the
/// Meta Quest 3 (Client).
///
/// Synchronized data (nothing else ever crosses the network):
///  - Current panorama index  (NetworkVariable&lt;int&gt;, Host -> Quest)
///  - Labels enabled          (NetworkVariable&lt;bool&gt;, Host -> Quest)
///  - Quest head rotation     (unreliable RPC, Quest -> Host)
///
/// The Host owns a fixed perspective "spectator" camera at the panorama centre.
/// The Quest streams only its head rotation; the Host applies it (smoothed) to the
/// preview camera which renders into a runtime-created 9:16 RenderTexture shown in
/// a RawImage. No video/texture streaming, zero per-frame GC allocations.
/// </summary>
[DisallowMultipleComponent]
public class LocalExperienceManager : NetworkBehaviour
{
    [Header("Panoramas")]
    [Tooltip("Skybox/Panoramic materials, one per room. Index order == room button order.")]
    [SerializeField] private Material[] panoramaMaterials;

    [Tooltip("Re-bake ambient lighting when the panorama changes. Expensive on Quest — leave OFF unless lit 3D objects share the scene.")]
    [SerializeField] private bool updateEnvironmentLighting = false;

    [Header("Floating Labels")]
    [Tooltip("Root object holding every world-space label. Toggled by the salesman.")]
    [SerializeField] private GameObject labelsRoot;

    [Tooltip("Optional: one label group per panorama (same order as materials). Only the group matching the current panorama is shown. Leave empty to show all labels in every room.")]
    [SerializeField] private GameObject[] perPanoramaLabelGroups;

    [Header("Salesman Preview (Host only)")]
    [Tooltip("Fixed perspective camera at the panorama centre. Never moves, only rotates.")]
    [SerializeField] private Camera previewCamera;

    [Tooltip("RawImage on the salesman canvas that displays the preview RenderTexture.")]
    [SerializeField] private RawImage previewImage;

    [Tooltip("Vertical field of view of the spectator camera.")]
    [Range(30f, 110f)]
    [SerializeField] private float previewFieldOfView = 65f;

    [Tooltip("Preview RenderTexture size. Keep 4:3 to match the viewport's AspectRatioFitter, or the image distorts.")]
    [SerializeField] private Vector2Int previewResolution = new Vector2Int(1024, 768);

    [Tooltip("How quickly the preview camera catches up to the received head rotation (1/s). 0 = snap instantly (shows network stepping).")]
    [Range(0f, 30f)]
    [SerializeField] private float previewSmoothing = 16f;

    [Header("Quest Head Tracking (Client only)")]
    [Tooltip("Transform of the XR Main Camera (the customer's head). Auto-resolves to Camera.main if left empty.")]
    [SerializeField] private Transform xrHeadTransform;

    [Tooltip("Maximum head-rotation updates per second sent to the host.")]
    [Range(10f, 72f)]
    [SerializeField] private float rotationSendRate = 60f;

    [Tooltip("Minimum rotation change (degrees) before an update is sent.")]
    [Range(0.01f, 5f)]
    [SerializeField] private float rotationSendThreshold = 0.05f;

    // ---------------------------------------------------------------- networked state

    private readonly NetworkVariable<int> panoramaIndex = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> labelsEnabled = new NetworkVariable<bool>(
        true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ---------------------------------------------------------------- runtime state (no per-frame allocations)

    private RenderTexture previewTexture;
    private Transform previewCameraTransform;
    private Quaternion previewTargetRotation = Quaternion.identity;
    private Quaternion lastSentRotation = Quaternion.identity;
    private float nextSendTime;

    // State applied while offline (lets the salesman preview rooms before Start Host).
    private int localPanoramaIndex;
    private bool localLabelsEnabled = true;

    /// <summary>Number of configured panoramas (for UI generation).</summary>
    public int PanoramaCount => panoramaMaterials != null ? panoramaMaterials.Length : 0;

    /// <summary>Currently applied panorama index.</summary>
    public int CurrentPanorama => IsSpawned ? panoramaIndex.Value : localPanoramaIndex;

    // ---------------------------------------------------------------- lifecycle

    private void Awake()
    {
        if (previewCamera != null)
        {
            previewCameraTransform = previewCamera.transform;
            previewTargetRotation = previewCameraTransform.rotation;
            previewCamera.fieldOfView = previewFieldOfView;
            previewCamera.clearFlags = CameraClearFlags.Skybox;
        }

        // Show the initial panorama and label state even before any network session exists.
        ApplyPanorama(localPanoramaIndex);
        ApplyLabels(localLabelsEnabled);

#if !UNITY_ANDROID || UNITY_EDITOR
        // Salesman device: bring the preview up immediately so rooms can be previewed
        // even before Start Host is pressed. Never runs on the Quest build.
        EnsurePreviewTexture();
#endif
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        panoramaIndex.OnValueChanged += OnPanoramaIndexChanged;
        labelsEnabled.OnValueChanged += OnLabelsEnabledChanged;

        if (IsServer)
        {
            // Carry whatever the salesman selected while offline into the session so a
            // late-joining Quest immediately receives the correct state.
            panoramaIndex.Value = localPanoramaIndex;
            labelsEnabled.Value = localLabelsEnabled;

            EnsurePreviewTexture();
        }
        else if (xrHeadTransform == null && Camera.main != null)
        {
            // Defensive fallback: resolve the XR head if the reference was not wired.
            xrHeadTransform = Camera.main.transform;
        }

        // NetworkVariable initial values do NOT fire OnValueChanged on spawn — apply explicitly.
        ApplyPanorama(panoramaIndex.Value);
        ApplyLabels(labelsEnabled.Value);
    }

    public override void OnNetworkDespawn()
    {
        panoramaIndex.OnValueChanged -= OnPanoramaIndexChanged;
        labelsEnabled.OnValueChanged -= OnLabelsEnabledChanged;

        // Keep local mirrors in sync so the UI stays coherent after a disconnect.
        localPanoramaIndex = panoramaIndex.Value;
        localLabelsEnabled = labelsEnabled.Value;

        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        if (previewTexture != null)
        {
            if (previewCamera != null) previewCamera.targetTexture = null;
            previewTexture.Release();
            Destroy(previewTexture);
            previewTexture = null;
        }

        base.OnDestroy();
    }

    private void Update()
    {
        // Quest client: stream head rotation to the host at a bounded rate.
        if (!IsSpawned || !IsClient || IsServer) return;
        if (xrHeadTransform == null) return;

        float now = Time.unscaledTime;
        if (now < nextSendTime) return;

        Quaternion current = xrHeadTransform.rotation;
        if (Quaternion.Angle(lastSentRotation, current) < rotationSendThreshold) return;

        SubmitHeadRotationRpc(current);
        lastSentRotation = current;
        nextSendTime = now + 1f / rotationSendRate;
    }

    private void LateUpdate()
    {
        // Host: ease the preview camera toward the latest received head rotation.
        if (!IsServer || previewCameraTransform == null) return;

        if (previewSmoothing <= 0f)
        {
            previewCameraTransform.rotation = previewTargetRotation;
        }
        else
        {
            // Frame-rate-independent exponential smoothing, no allocations.
            float t = 1f - Mathf.Exp(-previewSmoothing * Time.unscaledDeltaTime);
            previewCameraTransform.rotation = Quaternion.Slerp(
                previewCameraTransform.rotation, previewTargetRotation, t);
        }
    }

    // ---------------------------------------------------------------- RPCs

    /// <summary>
    /// Quest -> Host head rotation. Unreliable delivery: a lost packet is instantly
    /// superseded by the next one, so we avoid reliable-channel head-of-line blocking
    /// and keep latency minimal on busy Wi-Fi.
    /// </summary>
    [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
    private void SubmitHeadRotationRpc(Quaternion headRotation)
    {
        previewTargetRotation = headRotation;
    }

    // ---------------------------------------------------------------- salesman UI entry points

    /// <summary>
    /// Switches the active panorama. Wired to the room buttons on the salesman canvas.
    /// Works offline (before Start Host) as a local preview; once hosting, the change
    /// replicates to the Quest automatically.
    /// </summary>
    public void SetPanorama(int index)
    {
        if (panoramaMaterials == null || index < 0 || index >= panoramaMaterials.Length)
        {
            Debug.LogError($"[LocalExperienceManager] Invalid panorama index {index}.");
            return;
        }

        if (IsSpawned)
        {
            if (!IsServer) return; // Host-authoritative: the Quest can never change rooms.
            panoramaIndex.Value = index;
        }
        else
        {
            localPanoramaIndex = index;
            ApplyPanorama(index);
        }
    }

    /// <summary>
    /// Shows/hides the floating labels. Wired to the toggle on the salesman canvas.
    /// </summary>
    public void SetLabelsVisible(bool visible)
    {
        if (IsSpawned)
        {
            if (!IsServer) return;
            labelsEnabled.Value = visible;
        }
        else
        {
            localLabelsEnabled = visible;
            ApplyLabels(visible);
        }
    }

    /// <summary>Room display name for UI generation (material name).</summary>
    public string GetPanoramaName(int index)
    {
        if (panoramaMaterials == null || index < 0 || index >= panoramaMaterials.Length) return string.Empty;
        Material m = panoramaMaterials[index];
        return m != null ? m.name : string.Empty;
    }

    // ---------------------------------------------------------------- state application

    private void OnPanoramaIndexChanged(int previous, int next) => ApplyPanorama(next);

    private void OnLabelsEnabledChanged(bool previous, bool next) => ApplyLabels(next);

    private void ApplyPanorama(int index)
    {
        if (panoramaMaterials == null || panoramaMaterials.Length == 0) return;
        if (index < 0 || index >= panoramaMaterials.Length) return;

        Material material = panoramaMaterials[index];
        if (material == null)
        {
            Debug.LogError($"[LocalExperienceManager] Panorama material {index} is missing.");
            return;
        }

        // Both the Quest XR camera and the host preview camera clear with the skybox,
        // so a single material swap updates every viewpoint at once.
        RenderSettings.skybox = material;
        Debug.Log($"[LocalExperienceManager] Panorama {index} applied: {material.name}");

        if (updateEnvironmentLighting)
        {
            DynamicGI.UpdateEnvironment();
        }

        ApplyLabelGroups(index);
    }

    private void ApplyLabels(bool visible)
    {
        if (labelsRoot != null && labelsRoot.activeSelf != visible)
        {
            labelsRoot.SetActive(visible);
        }
    }

    /// <summary>Activates only the label group belonging to the current panorama.</summary>
    private void ApplyLabelGroups(int index)
    {
        if (perPanoramaLabelGroups == null || perPanoramaLabelGroups.Length == 0) return;

        for (int i = 0; i < perPanoramaLabelGroups.Length; i++)
        {
            GameObject group = perPanoramaLabelGroups[i];
            if (group == null) continue;

            bool shouldBeActive = i == index;
            if (group.activeSelf != shouldBeActive)
            {
                group.SetActive(shouldBeActive);
            }
        }
    }

    /// <summary>
    /// Creates the 9:16 preview RenderTexture at runtime and connects camera + RawImage.
    /// Runtime creation avoids stale sub-asset references and keeps the texture off the
    /// Quest build's memory entirely (it is only ever created on the host).
    /// </summary>
    private void EnsurePreviewTexture()
    {
        if (previewCamera == null)
        {
            Debug.LogWarning("[LocalExperienceManager] No preview camera assigned — salesman preview disabled.");
            return;
        }

        if (previewTexture == null)
        {
            previewTexture = new RenderTexture(previewResolution.x, previewResolution.y, 24)
            {
                name = "HostPreviewRT",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            previewTexture.Create();
        }

        previewCamera.fieldOfView = previewFieldOfView;
        previewCamera.targetTexture = previewTexture;
        previewCamera.enabled = true;

        if (previewImage != null)
        {
            previewImage.texture = previewTexture;
            previewImage.color = Color.white;
        }
    }

    // ---------------------------------------------------------------- editor validation

    private void OnValidate()
    {
        // Keep the preview portrait 9:16 and within sane GPU limits.
        previewResolution.x = Mathf.Clamp(previewResolution.x, 256, 2160);
        previewResolution.y = Mathf.Clamp(previewResolution.y, 256, 3840);

        if (previewCamera != null && !Application.isPlaying)
        {
            previewCamera.fieldOfView = previewFieldOfView;
        }

        if (perPanoramaLabelGroups != null && perPanoramaLabelGroups.Length > 0 &&
            panoramaMaterials != null && perPanoramaLabelGroups.Length != panoramaMaterials.Length)
        {
            Debug.LogWarning("[LocalExperienceManager] perPanoramaLabelGroups should match panoramaMaterials length (or be empty).", this);
        }
    }
}
