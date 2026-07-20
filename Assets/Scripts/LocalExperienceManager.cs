using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
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

    [Header("Panorama Transition")]
    [Tooltip("Seconds used to crossfade from the current panorama to the next. Set to 0 for an immediate change.")]
    [Min(0f)]
    [SerializeField] private float panoramaTransitionDuration = 1f;

    [Header("Panorama Button Highlight")]
    [Tooltip("Phone room buttons in the same order as panoramaMaterials.")]
    [SerializeField] private Button[] phonePanoramaButtons;

    [Tooltip("TV room buttons in the same order as panoramaMaterials.")]
    [SerializeField] private Button[] tvPanoramaButtons;

    [Tooltip("Background colour applied to the button for the active panorama.")]
    [SerializeField] private Color activePanoramaButtonColor = new Color(0.784f, 0.647f, 0.341f, 1f);

    [Header("Panorama Label Images")]
    [Tooltip("Root object holding every world-space label image. Toggled by the salesman.")]
    [SerializeField] private GameObject labelsRoot;

    [Tooltip("One image-label group per panorama, in material order. Only the current panorama's group is shown.")]
    [SerializeField] private GameObject[] perPanoramaLabelGroups;

    [Header("Floor Plan Board")]
    [Tooltip("The world-space plan board shown at the customer's knees. Toggled by the salesman.")]
    [SerializeField] private GameObject floorPlanBoard;

    [Header("Start View")]
    [Tooltip("Salesman slider (0–360°) choosing which part of the panorama faces the customer. Values set in the editor persist into the skybox materials, so tuned start views bake into builds.")]
    [SerializeField] private Slider startViewSlider;

    [Header("Salesman Preview (Host only)")]
    [Tooltip("Fixed perspective camera at the panorama centre. Never moves, only rotates.")]
    [SerializeField] private Camera previewCamera;

    [Tooltip("RawImage on the salesman canvas that displays the preview RenderTexture.")]
    [SerializeField] private RawImage previewImage;

    [Tooltip("Optional: full-screen RawImage on the TV canvas mirroring the same preview RenderTexture (wired by Godrej menu 9).")]
    [SerializeField] private RawImage tvPreviewImage;

    [Tooltip("Vertical field of view of the spectator camera.")]
    [Range(30f, 110f)]
    [SerializeField] private float previewFieldOfView = 65f;

    [Tooltip("Preview RenderTexture size. Keep 16:9 to match the salesman viewport, or the image distorts.")]
    [SerializeField] private Vector2Int previewResolution = new Vector2Int(1280, 720);

    [Tooltip("How quickly the preview camera catches up to the received head rotation (1/s). 0 = snap instantly (shows network stepping).")]
    [Range(0f, 30f)]
    [SerializeField] private float previewSmoothing = 16f;

    [Header("Desktop Testing — Mouse Look")]
    [Tooltip("Allows desktop/editor testing without a headset. Left-drag over the panorama preview, or right-drag anywhere, to look around.")]
    [SerializeField] private bool enableDesktopMouseLook = true;

    [Tooltip("Degrees of camera rotation per mouse pixel while dragging.")]
    [Range(0.02f, 1f)]
    [SerializeField] private float desktopMouseSensitivity = 0.2f;

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

    private readonly NetworkVariable<bool> boardEnabled = new NetworkVariable<bool>(
        true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> startViewYaw = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ---------------------------------------------------------------- runtime state (no per-frame allocations)

    private RenderTexture previewTexture;
    private Transform previewCameraTransform;
    private Quaternion previewTargetRotation = Quaternion.identity;
    private Quaternion lastSentRotation = Quaternion.identity;
    private float nextSendTime;

    // State applied while offline (lets the salesman preview rooms before Start Host).
    private int localPanoramaIndex;
    private bool localLabelsEnabled = true;
    private bool localBoardEnabled = true;
    private float localStartViewYaw;

    // Play Mode starts from the exact material + individual image transforms authored
    // in the scene. Runtime changes are always calculated from this immutable pair.
    private float[] panoramaRotationBaselines;
    private Transform[][] labelImageTransforms;
    private Vector3[][] labelImagePositionBaselines;
    private Quaternion[][] labelImageRotationBaselines;
    private bool panoramaAlignmentBaselinesCaptured;

    private Color[] phonePanoramaButtonBaseColors;
    private Color[] tvPanoramaButtonBaseColors;

    private Material panoramaBlendMaterial;
    private Coroutine panoramaTransitionRoutine;
    private int displayedPanoramaIndex = -1;
    private int queuedPanoramaIndex = -1;
    private int transitionTargetPanoramaIndex = -1;
    private bool desktopMouseDragging;
    private int desktopMouseButton = -1;
    private float desktopMouseYaw;
    private float desktopMousePitch;

    private static readonly int RotationProperty = Shader.PropertyToID("_Rotation");
    private static readonly int TextureProperty = Shader.PropertyToID("_Tex");
    private static readonly int TextureHdrProperty = Shader.PropertyToID("_Tex_HDR");
    private static readonly int MainTextureProperty = Shader.PropertyToID("_MainTex");
    private static readonly int MainTextureHdrProperty = Shader.PropertyToID("_MainTex_HDR");
    private static readonly int LayoutProperty = Shader.PropertyToID("_Layout");
    private static readonly int TintProperty = Shader.PropertyToID("_Tint");
    private static readonly int ExposureProperty = Shader.PropertyToID("_Exposure");
    private static readonly int BlendProperty = Shader.PropertyToID("_Blend");
    private static readonly int CubeAProperty = Shader.PropertyToID("_CubeA");
    private static readonly int CubeAHdrProperty = Shader.PropertyToID("_CubeA_HDR");
    private static readonly int PanoAProperty = Shader.PropertyToID("_PanoA");
    private static readonly int PanoAHdrProperty = Shader.PropertyToID("_PanoA_HDR");
    private static readonly int ProjectionAProperty = Shader.PropertyToID("_ProjectionA");
    private static readonly int LayoutAProperty = Shader.PropertyToID("_LayoutA");
    private static readonly int TintAProperty = Shader.PropertyToID("_TintA");
    private static readonly int ExposureAProperty = Shader.PropertyToID("_ExposureA");
    private static readonly int RotationAProperty = Shader.PropertyToID("_RotationA");
    private static readonly int CubeBProperty = Shader.PropertyToID("_CubeB");
    private static readonly int CubeBHdrProperty = Shader.PropertyToID("_CubeB_HDR");
    private static readonly int PanoBProperty = Shader.PropertyToID("_PanoB");
    private static readonly int PanoBHdrProperty = Shader.PropertyToID("_PanoB_HDR");
    private static readonly int ProjectionBProperty = Shader.PropertyToID("_ProjectionB");
    private static readonly int LayoutBProperty = Shader.PropertyToID("_LayoutB");
    private static readonly int TintBProperty = Shader.PropertyToID("_TintB");
    private static readonly int ExposureBProperty = Shader.PropertyToID("_ExposureB");
    private static readonly int RotationBProperty = Shader.PropertyToID("_RotationB");

    /// <summary>
    /// Set by NetworkSetup on the remote-control phone (baked mode "remote"): mirrors
    /// every salesman action ("pano"/"labels"/"plan"/"view" + numeric value) to the TV
    /// presenter over the LAN. Null on every other device, so forwarding costs nothing.
    /// </summary>
    public static System.Action<string, float> RemoteForward;

    /// <summary>Number of configured panoramas (for UI generation).</summary>
    public int PanoramaCount => panoramaMaterials != null ? panoramaMaterials.Length : 0;

    /// <summary>Currently applied panorama index.</summary>
    public int CurrentPanorama => IsSpawned ? panoramaIndex.Value : localPanoramaIndex;

    // ---------------------------------------------------------------- lifecycle

    private void Awake()
    {
        CapturePanoramaAlignmentBaselines();

        phonePanoramaButtonBaseColors = CachePanoramaButtonColors(phonePanoramaButtons);
        tvPanoramaButtonBaseColors = CachePanoramaButtonColors(tvPanoramaButtons);

        if (previewCamera != null)
        {
            previewCameraTransform = previewCamera.transform;
            previewTargetRotation = previewCameraTransform.rotation;
            Vector3 previewEuler = previewCameraTransform.rotation.eulerAngles;
            desktopMouseYaw = previewEuler.y;
            desktopMousePitch = ToSignedAngle(previewEuler.x);
            previewCamera.fieldOfView = previewFieldOfView;
            previewCamera.clearFlags = CameraClearFlags.Skybox;
        }

        RemoveLegacyPanoramaTextLabels();

        // Show the initial panorama, label, and board state before any network session exists.
        ApplyPanorama(localPanoramaIndex);
        ApplyLabels(localLabelsEnabled);
        ApplyBoard(localBoardEnabled);

        // Salesman device (desktop, editor, or Android phone): bring the preview up
        // immediately so rooms can be previewed before Start Host is pressed.
        // Never runs on the headset (runtime XR check, not a compile-time platform check,
        // because the same Android APK serves both the phone host and the Quest client).
        // The remote APK shows big TV controls instead of the preview — skip the
        // RenderTexture and camera cost entirely there.
        if (!NetworkSetup.IsXrHeadsetDevice() && !NetworkSetup.IsRemoteControl())
        {
            EnsurePreviewTexture();
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        panoramaIndex.OnValueChanged += OnPanoramaIndexChanged;
        labelsEnabled.OnValueChanged += OnLabelsEnabledChanged;
        boardEnabled.OnValueChanged += OnBoardEnabledChanged;
        startViewYaw.OnValueChanged += OnStartViewYawChanged;

        if (IsServer)
        {
            // Carry whatever the salesman selected while offline into the session so a
            // late-joining Quest immediately receives the correct state.
            panoramaIndex.Value = localPanoramaIndex;
            labelsEnabled.Value = localLabelsEnabled;
            boardEnabled.Value = localBoardEnabled;
            startViewYaw.Value = localStartViewYaw;

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
        ApplyBoard(boardEnabled.Value);
        ApplyStartView(startViewYaw.Value);
    }

    public override void OnNetworkDespawn()
    {
        panoramaIndex.OnValueChanged -= OnPanoramaIndexChanged;
        labelsEnabled.OnValueChanged -= OnLabelsEnabledChanged;
        boardEnabled.OnValueChanged -= OnBoardEnabledChanged;
        startViewYaw.OnValueChanged -= OnStartViewYawChanged;

        // Keep local mirrors in sync so the UI stays coherent after a disconnect.
        localPanoramaIndex = panoramaIndex.Value;
        localLabelsEnabled = labelsEnabled.Value;
        localBoardEnabled = boardEnabled.Value;
        localStartViewYaw = startViewYaw.Value;

        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        RestorePanoramaAlignmentBaselines();

        if (panoramaTransitionRoutine != null)
        {
            StopCoroutine(panoramaTransitionRoutine);
            panoramaTransitionRoutine = null;
        }

        if (panoramaBlendMaterial != null)
        {
            Destroy(panoramaBlendMaterial);
            panoramaBlendMaterial = null;
        }

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
        UpdateDesktopMouseLook();

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

    private void UpdateDesktopMouseLook()
    {
        if (!enableDesktopMouseLook || previewCameraTransform == null) return;
        if (NetworkSetup.IsXrHeadsetDevice() || NetworkSetup.IsRemoteControl()) return;

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        if (!desktopMouseDragging)
        {
            Vector2 pointerPosition = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame && IsPointerInsidePanoramaPreview(pointerPosition))
            {
                desktopMouseDragging = true;
                desktopMouseButton = 0;
            }
            else if (mouse.rightButton.wasPressedThisFrame)
            {
                desktopMouseDragging = true;
                desktopMouseButton = 1;
            }
        }

        if (!desktopMouseDragging) return;

        bool buttonHeld = desktopMouseButton == 0
            ? mouse.leftButton.isPressed
            : mouse.rightButton.isPressed;
        if (!buttonHeld)
        {
            desktopMouseDragging = false;
            desktopMouseButton = -1;
            return;
        }

        Vector2 delta = mouse.delta.ReadValue();
        if (delta.sqrMagnitude <= 0f) return;

        desktopMouseYaw = Mathf.Repeat(
            desktopMouseYaw + delta.x * desktopMouseSensitivity + 180f,
            360f) - 180f;
        desktopMousePitch = Mathf.Clamp(
            desktopMousePitch - delta.y * desktopMouseSensitivity,
            -80f,
            80f);

        previewTargetRotation = Quaternion.Euler(desktopMousePitch, desktopMouseYaw, 0f);
        if (!IsServer)
        {
            previewCameraTransform.rotation = previewTargetRotation;
        }
    }

    private bool IsPointerInsidePanoramaPreview(Vector2 screenPosition)
    {
        return IsPointerInsideRawImage(previewImage, screenPosition) ||
               IsPointerInsideRawImage(tvPreviewImage, screenPosition);
    }

    private static bool IsPointerInsideRawImage(RawImage image, Vector2 screenPosition)
    {
        if (image == null || !image.isActiveAndEnabled) return false;

        Canvas canvas = image.canvas;
        Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        return RectTransformUtility.RectangleContainsScreenPoint(
            image.rectTransform,
            screenPosition,
            eventCamera);
    }

    private void RemoveLegacyPanoramaTextLabels()
    {
        if (labelsRoot == null) return;

        TextMeshPro[] oldTexts = labelsRoot.GetComponentsInChildren<TextMeshPro>(true);
        for (int i = 0; i < oldTexts.Length; i++)
        {
            TextMeshPro oldText = oldTexts[i];
            if (oldText == null) continue;

            oldText.enabled = false;
            PanoramaDestinationLabel imageMarker = oldText.GetComponentInParent<PanoramaDestinationLabel>();
            if (imageMarker != null && oldText.gameObject == imageMarker.gameObject)
            {
                Destroy(oldText);
            }
            else
            {
                Destroy(oldText.gameObject);
            }
        }
    }

    private static float ToSignedAngle(float degrees)
    {
        return degrees > 180f ? degrees - 360f : degrees;
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

    [Rpc(SendTo.Server)]
    private void RequestPanoramaFromHotspotRpc(int index)
    {
        SetPanorama(index);
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

        RemoteForward?.Invoke("pano", index);

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
    /// Entry point for a destination label. The server still validates and owns the
    /// panorama change, while a Quest client can request the same action by selecting
    /// a label with its XR ray.
    /// </summary>
    public void SelectDestinationLabel(int index)
    {
        if (panoramaMaterials == null || index < 0 || index >= panoramaMaterials.Length) return;

        if (!IsSpawned || IsServer)
        {
            SetPanorama(index);
        }
        else
        {
            RequestPanoramaFromHotspotRpc(index);
        }
    }

    /// <summary>Room-name version used by manually placed destination labels.</summary>
    public void SelectDestinationLabel(string roomName)
    {
        SelectDestinationLabel(FindPanoramaIndex(roomName));
    }

    /// <summary>
    /// Shows/hides the floating labels. Wired to the toggle on the salesman canvas.
    /// </summary>
    public void SetLabelsVisible(bool visible)
    {
        RemoteForward?.Invoke("labels", visible ? 1f : 0f);

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

    /// <summary>
    /// Shows/hides the VR floor plan board. Wired to the PLAN toggle on the salesman canvas.
    /// </summary>
    public void SetFloorPlanVisible(bool visible)
    {
        RemoteForward?.Invoke("plan", visible ? 1f : 0f);

        if (IsSpawned)
        {
            if (!IsServer) return;
            boardEnabled.Value = visible;
        }
        else
        {
            localBoardEnabled = visible;
            ApplyBoard(visible);
        }
    }

    /// <summary>
    /// Rotates the panorama so the chosen direction faces the customer's "front" —
    /// this is what the customer sees first when the experience starts. Wired to the
    /// START VIEW slider (0–360°). In the editor the value persists into the skybox
    /// material, so per-room tuned start views carry into the next build.
    /// </summary>
    public void SetStartViewRotation(float degrees)
    {
        degrees = Mathf.Repeat(degrees, 360f);
        RemoteForward?.Invoke("view", degrees);

        if (IsSpawned)
        {
            if (!IsServer) return;
            startViewYaw.Value = degrees;
        }
        else
        {
            localStartViewYaw = degrees;
            ApplyStartView(degrees);
        }
    }

    /// <summary>Room display name for UI generation, without the numeric sort prefix.</summary>
    public string GetPanoramaName(int index)
    {
        if (panoramaMaterials == null || index < 0 || index >= panoramaMaterials.Length) return string.Empty;
        Material m = panoramaMaterials[index];
        if (m == null) return string.Empty;

        string name = m.name.Trim();
        if (name.Length > 3 && char.IsDigit(name[0]) && char.IsDigit(name[1]) &&
            (name[2] == ' ' || name[2] == '-' || name[2] == '_'))
        {
            name = name.Substring(3).Trim();
        }

        return name;
    }

    // ---------------------------------------------------------------- state application

    private void OnPanoramaIndexChanged(int previous, int next) => ApplyPanorama(next);

    private void OnLabelsEnabledChanged(bool previous, bool next) => ApplyLabels(next);

    private void OnBoardEnabledChanged(bool previous, bool next) => ApplyBoard(next);

    private void OnStartViewYawChanged(float previous, float next) => ApplyStartView(next);

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

        // Each room remembers its own start-view rotation (stored in the material).
        // The server pushes it into the synced state; the slider mirrors it silently.
        if (material.HasProperty(RotationProperty))
        {
            float roomYaw = material.GetFloat(RotationProperty);
            ApplyLabelGroupStartView(index, roomYaw);
            if (IsSpawned && IsServer) startViewYaw.Value = roomYaw;
            else if (!IsSpawned) localStartViewYaw = roomYaw;
            if (startViewSlider != null) startViewSlider.SetValueWithoutNotify(roomYaw);
        }

        ApplyPanoramaButtonHighlight(index);

        if (displayedPanoramaIndex < 0 || displayedPanoramaIndex == index ||
            panoramaTransitionDuration <= 0f || !CanCrossfade(material))
        {
            ApplyPanoramaImmediately(index, material);
            return;
        }

        // Keep the currently visible pair stable if several room commands arrive close
        // together. The latest request is queued and starts as soon as this blend ends.
        if (panoramaTransitionRoutine != null)
        {
            queuedPanoramaIndex = index;
            return;
        }

        Material source = panoramaMaterials[displayedPanoramaIndex];
        if (!CanCrossfade(source) || !EnsurePanoramaBlendMaterial())
        {
            ApplyPanoramaImmediately(index, material);
            return;
        }

        panoramaTransitionRoutine = StartCoroutine(CrossfadePanorama(
            displayedPanoramaIndex, source, index, material));
    }

    private void ApplyPanoramaImmediately(int index, Material material)
    {
        if (panoramaTransitionRoutine != null)
        {
            StopCoroutine(panoramaTransitionRoutine);
            panoramaTransitionRoutine = null;
        }

        queuedPanoramaIndex = -1;
        transitionTargetPanoramaIndex = -1;
        displayedPanoramaIndex = index;

        // Both the Quest XR camera and the host preview camera clear with the skybox,
        // so a single material assignment updates every local viewpoint.
        RenderSettings.skybox = material;
        ApplyLabelGroups(index);

        if (updateEnvironmentLighting)
        {
            DynamicGI.UpdateEnvironment();
        }

        Debug.Log($"[LocalExperienceManager] Panorama {index} applied: {material.name}");
    }

    private IEnumerator CrossfadePanorama(
        int sourceIndex, Material source, int targetIndex, Material target)
    {
        transitionTargetPanoramaIndex = targetIndex;
        ConfigureBlendEndpoint(source, true);
        ConfigureBlendEndpoint(target, false);
        panoramaBlendMaterial.SetFloat(BlendProperty, 0f);

        // Destination labels would otherwise float over both rooms during the blend.
        ApplyLabelGroups(-1);
        RenderSettings.skybox = panoramaBlendMaterial;

        float elapsed = 0f;
        while (elapsed < panoramaTransitionDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float normalized = Mathf.Clamp01(elapsed / panoramaTransitionDuration);
            float eased = normalized * normalized * (3f - 2f * normalized);
            panoramaBlendMaterial.SetFloat(BlendProperty, eased);
            yield return null;
        }

        panoramaBlendMaterial.SetFloat(BlendProperty, 1f);
        RenderSettings.skybox = target;
        displayedPanoramaIndex = targetIndex;
        transitionTargetPanoramaIndex = -1;
        panoramaTransitionRoutine = null;
        ApplyLabelGroups(targetIndex);

        if (updateEnvironmentLighting)
        {
            DynamicGI.UpdateEnvironment();
        }

        Debug.Log($"[LocalExperienceManager] Panorama {sourceIndex} -> {targetIndex} crossfade complete: {target.name}");

        int nextIndex = queuedPanoramaIndex;
        queuedPanoramaIndex = -1;
        if (nextIndex >= 0 && nextIndex != displayedPanoramaIndex &&
            panoramaMaterials != null && nextIndex < panoramaMaterials.Length)
        {
            ApplyPanorama(nextIndex);
        }
    }

    private bool EnsurePanoramaBlendMaterial()
    {
        if (panoramaBlendMaterial != null) return true;

        Shader shader = Resources.Load<Shader>("GodrejPanoramaCrossfade");
        if (shader == null)
        {
            shader = Shader.Find("Godrej/Skybox Panorama Crossfade");
        }

        if (shader == null)
        {
            Debug.LogWarning("[LocalExperienceManager] Panorama crossfade shader is missing; using immediate panorama changes.", this);
            return false;
        }

        panoramaBlendMaterial = new Material(shader)
        {
            name = "Godrej Panorama Crossfade (Runtime)",
            hideFlags = HideFlags.HideAndDontSave
        };
        return true;
    }

    private static bool CanCrossfade(Material material)
    {
        if (material == null) return false;

        return UsesPanoramaTexture(material) ||
               (material.HasProperty(TextureProperty) &&
                material.GetTexture(TextureProperty) is Cubemap);
    }

    /// <summary>
    /// Skybox/Panoramic materials can retain a stale cubemap in _Tex even though Unity
    /// renders _MainTex. Prefer the shader's real panoramic input so the blend endpoint
    /// exactly matches the material assigned after the fade and cannot visibly snap.
    /// </summary>
    private static bool UsesPanoramaTexture(Material material)
    {
        if (material == null || !material.HasProperty(MainTextureProperty) ||
            !(material.GetTexture(MainTextureProperty) is Texture2D))
        {
            return false;
        }

        string shaderName = material.shader != null ? material.shader.name : string.Empty;
        bool isPanoramicShader = shaderName.IndexOf(
            "Panoramic",
            System.StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasUsableCubemap = material.HasProperty(TextureProperty) &&
                               material.GetTexture(TextureProperty) is Cubemap;
        return isPanoramicShader || !hasUsableCubemap;
    }

    private void ConfigureBlendEndpoint(Material source, bool endpointA)
    {
        int cube = endpointA ? CubeAProperty : CubeBProperty;
        int cubeHdr = endpointA ? CubeAHdrProperty : CubeBHdrProperty;
        int pano = endpointA ? PanoAProperty : PanoBProperty;
        int panoHdr = endpointA ? PanoAHdrProperty : PanoBHdrProperty;
        int projection = endpointA ? ProjectionAProperty : ProjectionBProperty;
        int layout = endpointA ? LayoutAProperty : LayoutBProperty;
        int tint = endpointA ? TintAProperty : TintBProperty;
        int exposure = endpointA ? ExposureAProperty : ExposureBProperty;
        int rotation = endpointA ? RotationAProperty : RotationBProperty;

        bool usesPanorama = UsesPanoramaTexture(source);
        bool usesCubemap = !usesPanorama && source.HasProperty(TextureProperty) &&
                           source.GetTexture(TextureProperty) is Cubemap;
        panoramaBlendMaterial.SetFloat(projection, usesCubemap ? 0f : 1f);

        if (usesCubemap)
        {
            panoramaBlendMaterial.SetTexture(cube, source.GetTexture(TextureProperty));
            panoramaBlendMaterial.SetVector(cubeHdr, source.HasProperty(TextureHdrProperty)
                ? source.GetVector(TextureHdrProperty)
                : new Vector4(1f, 1f, 0f, 1f));
        }
        else
        {
            panoramaBlendMaterial.SetTexture(pano, source.GetTexture(MainTextureProperty));
            panoramaBlendMaterial.SetVector(panoHdr, source.HasProperty(MainTextureHdrProperty)
                ? source.GetVector(MainTextureHdrProperty)
                : new Vector4(1f, 1f, 0f, 1f));
            panoramaBlendMaterial.SetFloat(layout, source.HasProperty(LayoutProperty)
                ? source.GetFloat(LayoutProperty)
                : 0f);
        }

        panoramaBlendMaterial.SetColor(tint, source.HasProperty(TintProperty)
            ? source.GetColor(TintProperty)
            : Color.white);
        panoramaBlendMaterial.SetFloat(exposure, source.HasProperty(ExposureProperty)
            ? source.GetFloat(ExposureProperty)
            : 1f);
        panoramaBlendMaterial.SetFloat(rotation, source.HasProperty(RotationProperty)
            ? source.GetFloat(RotationProperty)
            : 0f);
    }

    private static Color[] CachePanoramaButtonColors(Button[] buttons)
    {
        if (buttons == null) return null;

        var colors = new Color[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            colors[i] = button != null && button.targetGraphic != null
                ? button.targetGraphic.color
                : Color.white;
        }

        return colors;
    }

    private void ApplyPanoramaButtonHighlight(int activeIndex)
    {
        ApplyPanoramaButtonHighlight(phonePanoramaButtons, phonePanoramaButtonBaseColors, activeIndex);
        ApplyPanoramaButtonHighlight(tvPanoramaButtons, tvPanoramaButtonBaseColors, activeIndex);
    }

    private void ApplyPanoramaButtonHighlight(Button[] buttons, Color[] baseColors, int activeIndex)
    {
        if (buttons == null || baseColors == null) return;

        int count = Mathf.Min(buttons.Length, baseColors.Length);
        for (int i = 0; i < count; i++)
        {
            Button button = buttons[i];
            if (button == null || button.targetGraphic == null) continue;

            Color color = i == activeIndex ? activePanoramaButtonColor : baseColors[i];
            if (button.targetGraphic.color != color)
            {
                button.targetGraphic.color = color;
            }
        }
    }

    private void ApplyLabels(bool visible)
    {
        if (labelsRoot != null && labelsRoot.activeSelf != visible)
        {
            labelsRoot.SetActive(visible);
        }
    }

    private void ApplyBoard(bool visible)
    {
        if (floorPlanBoard != null && floorPlanBoard.activeSelf != visible)
        {
            floorPlanBoard.SetActive(visible);
        }
    }

    /// <summary>Spins the active skybox so the given yaw faces the customer's front.</summary>
    private void ApplyStartView(float degrees)
    {
        int activeIndex = CurrentPanorama;
        Material active = panoramaMaterials != null && activeIndex >= 0 && activeIndex < panoramaMaterials.Length
            ? panoramaMaterials[activeIndex]
            : null;

        if (active != null && active.HasProperty(RotationProperty))
        {
            active.SetFloat(RotationProperty, degrees);
            ApplyLabelGroupStartView(activeIndex, degrees);
        }

        if (panoramaBlendMaterial != null && transitionTargetPanoramaIndex == activeIndex)
        {
            panoramaBlendMaterial.SetFloat(RotationBProperty, degrees);
        }
    }

    /// <summary>
    /// Captures the authored material rotation and image transforms as one immutable
    /// Play Mode baseline. This prevents networking/startup events from applying the
    /// same angular correction twice.
    /// </summary>
    private void CapturePanoramaAlignmentBaselines()
    {
        int count = panoramaMaterials != null ? panoramaMaterials.Length : 0;
        panoramaRotationBaselines = new float[count];
        labelImageTransforms = new Transform[count][];
        labelImagePositionBaselines = new Vector3[count][];
        labelImageRotationBaselines = new Quaternion[count][];

        for (int i = 0; i < count; i++)
        {
            Material material = panoramaMaterials[i];
            panoramaRotationBaselines[i] = material != null && material.HasProperty(RotationProperty)
                ? material.GetFloat(RotationProperty)
                : 0f;

            GameObject group = perPanoramaLabelGroups != null && i < perPanoramaLabelGroups.Length
                ? perPanoramaLabelGroups[i]
                : null;
            int childCount = group != null ? group.transform.childCount : 0;
            labelImageTransforms[i] = new Transform[childCount];
            labelImagePositionBaselines[i] = new Vector3[childCount];
            labelImageRotationBaselines[i] = new Quaternion[childCount];

            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                Transform image = group.transform.GetChild(childIndex);
                labelImageTransforms[i][childIndex] = image;
                labelImagePositionBaselines[i][childIndex] = image.position;
                labelImageRotationBaselines[i][childIndex] = image.rotation;
            }
        }

        panoramaAlignmentBaselinesCaptured = true;
    }

    /// <summary>Places each image absolutely for the requested panorama view.</summary>
    private void ApplyLabelGroupStartView(int index, float degrees)
    {
        if (!panoramaAlignmentBaselinesCaptured) CapturePanoramaAlignmentBaselines();
        if (perPanoramaLabelGroups == null || index < 0 ||
            index >= perPanoramaLabelGroups.Length || index >= panoramaRotationBaselines.Length)
        {
            return;
        }

        if (labelImageTransforms[index] == null) return;

        float rotationDelta = Mathf.DeltaAngle(panoramaRotationBaselines[index], degrees);
        Quaternion adjustment = Quaternion.AngleAxis(-rotationDelta, Vector3.up);

        Vector3 viewerPosition = labelsRoot != null
            ? labelsRoot.transform.position
            : Vector3.zero;
        for (int childIndex = 0; childIndex < labelImageTransforms[index].Length; childIndex++)
        {
            Transform image = labelImageTransforms[index][childIndex];
            if (image == null) continue;

            Vector3 baselineOffset = labelImagePositionBaselines[index][childIndex] - viewerPosition;
            image.SetPositionAndRotation(
                viewerPosition + adjustment * baselineOffset,
                adjustment * labelImageRotationBaselines[index][childIndex]);
        }
    }

    /// <summary>Prevents Play Mode changes leaking into the next editor test.</summary>
    private void RestorePanoramaAlignmentBaselines()
    {
        if (!panoramaAlignmentBaselinesCaptured) return;

        for (int i = 0; i < panoramaRotationBaselines.Length; i++)
        {
            Material material = panoramaMaterials != null && i < panoramaMaterials.Length
                ? panoramaMaterials[i]
                : null;
            if (material != null && material.HasProperty(RotationProperty))
            {
                material.SetFloat(RotationProperty, panoramaRotationBaselines[i]);
            }

            if (labelImageTransforms[i] == null) continue;
            for (int childIndex = 0; childIndex < labelImageTransforms[i].Length; childIndex++)
            {
                Transform image = labelImageTransforms[i][childIndex];
                if (image == null) continue;
                image.SetPositionAndRotation(
                    labelImagePositionBaselines[i][childIndex],
                    labelImageRotationBaselines[i][childIndex]);
            }
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

    /// <summary>Finds a panorama by display name so labels survive array reordering.</summary>
    public int FindPanoramaIndex(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName) || panoramaMaterials == null) return -1;

        for (int i = 0; i < panoramaMaterials.Length; i++)
        {
            if (string.Equals(GetPanoramaName(i), roomName.Trim(), System.StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
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
            // The TV canvas shows the preview full-screen, so size the texture to the
            // panel itself: height from the screen's short side (720p floor, 4K ceiling
            // — an 8K panel gets a 4K preview upscaled by the display, invisible at TV
            // viewing distance and safe on the GPU), width following the panel's own
            // aspect so the full-screen image needs neither stretching nor cropping.
            // The portrait phone viewport keeps the configured (smaller) 16:9 size.
            Vector2Int size = previewResolution;
            if (NetworkSetup.IsTvDevice())
            {
                float longSide = Mathf.Max(Screen.width, Screen.height);
                float shortSide = Mathf.Min(Screen.width, Screen.height);
                int rtHeight = Mathf.Clamp(Mathf.RoundToInt(shortSide), 720, 2160);
                int rtWidth = Mathf.RoundToInt(rtHeight * (longSide / shortSide));
                if (rtWidth > 4096) // stay under the safe GLES3 texture ceiling
                {
                    rtHeight = Mathf.RoundToInt(rtHeight * 4096f / rtWidth);
                    rtWidth = 4096;
                }
                size = new Vector2Int(rtWidth, rtHeight);
            }

            previewTexture = new RenderTexture(size.x, size.y, 24)
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

        if (tvPreviewImage != null)
        {
            tvPreviewImage.texture = previewTexture;
            tvPreviewImage.color = Color.white;

            // Keep the full-screen view at the texture's exact aspect: the fitter
            // scales it to cover the screen without ever distorting the image.
            if (tvPreviewImage.TryGetComponent(out AspectRatioFitter fitter))
            {
                fitter.aspectRatio = (float)previewTexture.width / previewTexture.height;
            }
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

        ValidatePanoramaButtonCount(phonePanoramaButtons, "phonePanoramaButtons");
        ValidatePanoramaButtonCount(tvPanoramaButtons, "tvPanoramaButtons");
    }

    private void ValidatePanoramaButtonCount(Button[] buttons, string fieldName)
    {
        if (buttons != null && buttons.Length > 0 && panoramaMaterials != null &&
            buttons.Length != panoramaMaterials.Length)
        {
            Debug.LogWarning($"[LocalExperienceManager] {fieldName} should match panoramaMaterials length (or be empty).", this);
        }
    }
}
