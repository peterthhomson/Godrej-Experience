using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Management;

/// <summary>
/// Connection bootstrap for the offline two-device presentation.
///
/// Salesman device (Windows / macOS / Android tablet / Editor)  -> Host
/// Meta Quest 3 (Android device build)                          -> Client
///
/// Platform selection uses compiler directives: an Android *device* build is treated
/// as the Quest client; Editor and desktop builds are treated as the host.
///
/// The host always listens on 0.0.0.0 and displays its detected LAN IP, so the salesman
/// never has to know or type an address. The Quest finds the host automatically through
/// a tiny UDP LAN discovery handshake (client broadcasts a probe, host answers), with a
/// manual IP entry + Connect button kept as fallback per requirements.
/// </summary>
[DisallowMultipleComponent]
public class NetworkSetup : MonoBehaviour
{
    [Header("Network")]
    [Tooltip("Unity Transport used by the NetworkManager. Auto-resolved if left empty.")]
    [SerializeField] private UnityTransport transport;

    [Tooltip("Game traffic port (UTP).")]
    [SerializeField] private ushort port = 7777;

    [Tooltip("UDP port used for LAN host discovery.")]
    [SerializeField] private ushort discoveryPort = 47777;

    [Tooltip("Quest: keep searching and connect automatically as soon as a host is found.")]
    [SerializeField] private bool autoConnectOnQuest = true;

    [Header("Platform Roots")]
    [Tooltip("Everything only the salesman device needs (canvas, preview camera, backdrop camera).")]
    [SerializeField] private GameObject hostRoot;

    [Tooltip("Everything only the Quest needs (XR Origin).")]
    [SerializeField] private GameObject xrOrigin;

    [Tooltip("World-space connection panel shown in the headset until connected.")]
    [SerializeField] private GameObject questConnectPanel;

    [Header("Salesman UI")]
    [SerializeField] private Button startHostButton;
    [SerializeField] private TextMeshProUGUI statusText;
    [Tooltip("Shows this machine's LAN IP once hosting (fallback info for manual entry).")]
    [SerializeField] private TextMeshProUGUI ipDisplayText;

    [Header("Quest UI (fallback manual connect)")]
    [SerializeField] private TMP_InputField questIpInputField;
    [SerializeField] private Button connectButton;
    [SerializeField] private TextMeshProUGUI questStatusText;

    [Header("TV Presenter (large landscape touch screens)")]
    [Tooltip("Landscape drawer UI, enabled instead of the phone canvas on TV-class devices. Built by Godrej menu 9.")]
    [SerializeField] private GameObject tvCanvas;
    [Tooltip("The portrait phone/tablet presenter canvas, disabled on TV-class devices.")]
    [SerializeField] private GameObject phoneCanvas;
    [SerializeField] private Button tvStartHostButton;
    [SerializeField] private TextMeshProUGUI tvStatusText;
    [SerializeField] private TextMeshProUGUI tvIpDisplayText;

    private const string LastHostIpKey = "GodrejXR.LastHostIp";

    private bool isQuestClient;
    private bool sessionStarted;

    // Original phone-canvas UI references, kept so the presenter canvas (and the
    // status/IP/button routing) can be switched as a unit at boot — and live in the
    // editor when the Game view is resized between portrait and landscape.
    private Button phoneStartHostButton;
    private TextMeshProUGUI phoneStatusText;
    private TextMeshProUGUI phoneIpDisplayText;
    private string lastStatusMessage;
    private string lastIpMessage;
    private bool presenterTvApplied;
    private UdpClient discoverySocket;                 // host: responder / client: prober
    private volatile string discoveredHostIp;          // written by socket thread, read on main thread
    private byte[] probeBuffer;                        // cached, allocation-free sends
    private IPEndPoint broadcastEndPoint;
    private float nextProbeTime;

    private const string ProbeMessage = "GODREJ_XR_PROBE_V1";
    private const string ReplyMessage = "GODREJ_XR_HOST_V1";

    // ---- TV remote link (phone remote APK <-> TV presenter, out-of-band of Netcode) ----
    // Own port so remote traffic can never confuse the Quest discovery handshake.
    private const ushort RemotePort = 47778;
    private const string RemoteProbeMessage = "GODREJ_TV_PROBE_V1";
    private const string RemoteStatePrefix = "GODREJ_TV_HERE_V1";  // + |hosting=0|pano=3
    private const string RemoteCommandPrefix = "GODREJ_TV_CMD_V1"; // + |pano=5 / |labels=1 / |plan=0 / |view=123.4 / |starthost=1 / |stophost=1 / |menutoggle=1
    private const float RemoteProbeIntervalSeconds = 2f;
    private const float RemoteLinkTimeoutSeconds = 7f;

    private UdpClient remoteSocket;                    // TV: command listener / remote: link socket
    private readonly ConcurrentQueue<(string Message, IPEndPoint Sender)> remoteInbox =
        new ConcurrentQueue<(string, IPEndPoint)>();   // socket thread -> main thread
    private IPEndPoint remoteTvEndPoint;               // remote: the linked TV (null while searching)
    private readonly List<IPEndPoint> remoteProbeEndPoints = new List<IPEndPoint>();
    private float remoteTvLastSeen;
    private float nextRemoteProbeTime;
    private bool remoteTvHosting;                      // remote: TV's hosting state from echoes
    private LocalExperienceManager experienceManager;  // TV: command target, resolved lazily
    private TvDrawerController tvDrawer;               // TV: menu drawer, resolved lazily

    // Remote control panel (built at runtime in place of the pano preview).
    private Image remoteConnectFill;
    private TextMeshProUGUI remoteConnectLabel;
    private Button remoteMenuButton;
    private Image remoteHostFill;
    private TextMeshProUGUI remoteHostLabel;

    // Connect→drop flap detection: a connection that dies faster than this window is
    // almost always a build mismatch (headset APK built from an older scene — Netcode's
    // in-scene object hashes change on every scene regeneration, so sync fails client-side).
    private const float RapidDropWindowSeconds = 12f;
    private float lastConnectTime = float.NegativeInfinity;
    private int rapidDropCount;

    // Presentation eye level: content (labels, plan board) is authored against this height,
    // and the recenter pins the customer's eyes to it regardless of tracking-space quirks.
    private const float TargetEyeHeight = 1.6f;
    private static readonly List<XRInputSubsystem> InputSubsystems = new List<XRInputSubsystem>();

    // ---------------------------------------------------------------- lifecycle

    private void Awake()
    {
        // The host preview must keep updating even if the salesman switches apps briefly,
        // and neither device may dim mid-presentation.
        Application.runInBackground = true;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        // Headset vs presenter is decided at runtime, so ONE Android APK serves both
        // devices: on the Quest the OpenXR loader initializes (XR active => customer
        // headset/client); on an ordinary Android phone or tablet it cannot, so the same
        // build boots as the salesman host. Editor and desktop builds are always the host.
        isQuestClient = IsXrHeadsetDevice();

        // TV presenter: same host role as the phone, different canvas. Applied here,
        // before Start() wires listeners, so status/IP/button writes land on the
        // canvas the presenter can actually see.
        phoneStartHostButton = startHostButton;
        phoneStatusText = statusText;
        phoneIpDisplayText = ipDisplayText;
        if (!isQuestClient) ApplyPresenterCanvas(IsTvDevice());
    }

    /// <summary>
    /// Enables exactly one presenter canvas and routes every status/IP/button write
    /// to it. Safe to call repeatedly; used at boot and by the editor live-preview
    /// swap when the Game view is resized between portrait and landscape.
    /// </summary>
    private void ApplyPresenterCanvas(bool tvPresenter)
    {
        if (phoneCanvas != null) phoneCanvas.SetActive(!tvPresenter);
        if (tvCanvas != null) tvCanvas.SetActive(tvPresenter);

        // One line of boot truth for field debugging on unknown panels: which canvas
        // was chosen and whether the scene actually lets it show.
        Debug.Log($"[NetworkSetup] Presenter canvas: {(tvPresenter ? "TV" : "PHONE")} — " +
                  $"tv {DescribeCanvasState(tvCanvas)}, phone {DescribeCanvasState(phoneCanvas)}, " +
                  $"screen {Screen.width}x{Screen.height}");

        bool useTvRefs = tvPresenter && tvCanvas != null;
        startHostButton = useTvRefs && tvStartHostButton != null ? tvStartHostButton : phoneStartHostButton;
        statusText = useTvRefs && tvStatusText != null ? tvStatusText : phoneStatusText;
        ipDisplayText = useTvRefs && tvIpDisplayText != null ? tvIpDisplayText : phoneIpDisplayText;

        // Carry live values over so the newly shown canvas is never stale.
        if (statusText != null && !string.IsNullOrEmpty(lastStatusMessage)) statusText.text = lastStatusMessage;
        if (ipDisplayText != null && !string.IsNullOrEmpty(lastIpMessage)) ipDisplayText.text = lastIpMessage;
        // START HOST is a toggle (start/stop), so it stays pressable while hosting.
        if (startHostButton != null) startHostButton.interactable = true;

        presenterTvApplied = tvPresenter;

        if (tvPresenter && isActiveAndEnabled)
            StartCoroutine(EnsureTvDpadSelection());
    }

    private static string DescribeCanvasState(GameObject canvas) =>
        canvas == null ? "unwired"
        : !canvas.activeSelf ? "off"
        : canvas.activeInHierarchy ? "on" : "blocked-by-inactive-parent";

    /// <summary>
    /// Android TV launches without a pointer, so its D-pad needs a selected control
    /// before navigation/OK can generate UI events. Layout and the input module settle
    /// one frame after the canvas is activated.
    /// </summary>
    private IEnumerator EnsureTvDpadSelection()
    {
        yield return null;

        if (!presenterTvApplied || EventSystem.current == null ||
            tvStartHostButton == null || !tvStartHostButton.gameObject.activeInHierarchy)
        {
            yield break;
        }

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || !selected.activeInHierarchy ||
            (tvCanvas != null && !selected.transform.IsChildOf(tvCanvas.transform)))
        {
            EventSystem.current.SetSelectedGameObject(null);
            tvStartHostButton.Select();
            Debug.Log("[NetworkSetup] Android TV D-pad focus: TV Start Host.");
        }
    }

    /// <summary>
    /// Runs one frame after the presenter canvas swap, once layout has happened:
    /// catches the failures SetActive cannot see (driven rect never sized, scaler
    /// collapse, no active graphics, missing EventSystem) in the device log.
    /// </summary>
    private IEnumerator LogPresenterCanvasHealth(bool tvPresenter)
    {
        yield return null;

        GameObject go = tvPresenter ? tvCanvas : phoneCanvas;
        Canvas canvas = go != null ? go.GetComponent<Canvas>() : null;
        if (canvas == null)
        {
            Debug.LogWarning("[NetworkSetup] Canvas health: no presenter canvas to inspect.");
            yield break;
        }

        RectTransform rect = (RectTransform)canvas.transform;
        Debug.Log($"[NetworkSetup] Canvas health: activeInHierarchy={go.activeInHierarchy} " +
                  $"enabled={canvas.enabled} renderMode={canvas.renderMode} " +
                  $"scaleFactor={canvas.scaleFactor:F3} rect={rect.rect.width:F0}x{rect.rect.height:F0} " +
                  $"scale={rect.localScale.x:F3} " +
                  $"activeGraphics={go.GetComponentsInChildren<Graphic>(false).Length} " +
                  $"eventSystem={(EventSystem.current != null ? EventSystem.current.gameObject.name : "MISSING")}");
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static bool? cachedIsTvDevice;
    private static bool? cachedAndroidLandscape;
    private static float nextAndroidOrientationCheck;
#endif

    /// <summary>
    /// True on TV-class presenter hardware: Android TV / Google TV devices and large
    /// Android touch panels (interactive displays / signage screens). Phones and
    /// tablets stay on the portrait canvas. In the editor (and desktop builds) the
    /// window shape decides, so a landscape Game view — or the "Godrej — Android TV"
    /// profile in the Device Simulator — previews the TV canvas, while a portrait
    /// Game view previews the phone canvas.
    /// </summary>
    public static bool IsTvDevice()
    {
        if (IsXrHeadsetDevice()) return false;

        // Per-APK override baked by the wizard build menus. TV stays on the TV canvas;
        // the salesman phone intentionally follows orientation (portrait phone canvas,
        // landscape TV canvas); the separate remote remains on its portrait canvas.
        string mode = BakedPresenterMode();
        if (mode == "tv") return true;
        if (mode == "phone")
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return IsAndroidConfigurationLandscape();
#else
            return Screen.width > Screen.height;
#endif
        }
        if (mode == "remote") return false; // the remote drives the TV but wears the phone layout

#if UNITY_ANDROID && !UNITY_EDITOR
        cachedIsTvDevice ??= DetectAndroidTv();
        return cachedIsTvDevice.Value;
#else
        return Screen.width > Screen.height;
#endif
    }

    /// <summary>
    /// True in the remote-control APK (baked mode "remote"): the phone salesman UI
    /// acting as a wireless controller for the TV presenter instead of hosting itself.
    /// </summary>
    public static bool IsRemoteControl() => BakedPresenterMode() == "remote";

    private static string cachedPresenterMode;
    private static AndroidJavaClass androidTvActivityClass;

#if UNITY_EDITOR
    // Editor safety net: statics must not leak between play sessions (the baked mode
    // file is rewritten by the build menus, and RemoteForward must never survive a
    // session) — even if Enter Play Mode ever runs without a domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticsForPlayMode()
    {
        cachedPresenterMode = null;
        LocalExperienceManager.RemoteForward = null;
    }
#endif

    private static string BakedPresenterMode()
    {
        if (cachedPresenterMode == null)
        {
            TextAsset asset = Resources.Load<TextAsset>("GodrejPresenterMode");
            cachedPresenterMode = asset != null ? asset.text.Trim().ToLowerInvariant() : "auto";
        }
        return cachedPresenterMode;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static bool IsAndroidConfigurationLandscape()
    {
        // GameActivity can report its new Configuration before Unity swaps Screen.width
        // and Screen.height. Polling at 4 Hz makes rotation feel instant without doing
        // JNI work every frame. Android Configuration: portrait=1, landscape=2.
        if (cachedAndroidLandscape.HasValue &&
            Time.unscaledTime < nextAndroidOrientationCheck)
        {
            return cachedAndroidLandscape.Value;
        }

        nextAndroidOrientationCheck = Time.unscaledTime + 0.25f;
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity =
                player.GetStatic<AndroidJavaObject>("currentActivity");
            using AndroidJavaObject resources =
                activity.Call<AndroidJavaObject>("getResources");
            using AndroidJavaObject configuration =
                resources.Call<AndroidJavaObject>("getConfiguration");
            cachedAndroidLandscape = configuration.Get<int>("orientation") == 2;
        }
        catch (Exception)
        {
            cachedAndroidLandscape = Screen.width > Screen.height;
        }

        return cachedAndroidLandscape.Value;
    }

    private IEnumerator ApplyAndroidFullSensorOrientation()
    {
        // Let Unity finish applying Screen.orientation first, then make Android the
        // authority. ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR == 10.
        yield return null;
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity =
                player.GetStatic<AndroidJavaObject>("currentActivity");
            activity.Call("setRequestedOrientation", 10);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NetworkSetup] Could not enable Android full-sensor rotation: {exception.Message}");
        }
    }

    private static bool DetectAndroidTv()
    {
        // Real Android TV / Google TV: Unity classifies these as consoles, and the OS
        // reports the television UI mode. Either signal alone is decisive.
        if (SystemInfo.deviceType == DeviceType.Console) return true;
        if (IsAndroidTvUiMode()) return true;

        // An Android device with no touchscreen at all can only be a TV/set-top box.
        if (Touchscreen.current == null) return true;

        // Plain-Android touch panels (interactive displays) identify as ordinary
        // handhelds — fall back to the physical diagonal: >= 20 inches is no tablet.
        float dpi = Screen.dpi;
        if (dpi > 0f)
        {
            float widthInches = Screen.width / dpi;
            float heightInches = Screen.height / dpi;
            return Mathf.Sqrt(widthInches * widthInches + heightInches * heightInches) >= 20f;
        }

        return false;
    }

    private static bool IsAndroidTvUiMode()
    {
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject uiModeManager = activity.Call<AndroidJavaObject>("getSystemService", "uimode"))
            {
                const int UiModeTypeTelevision = 4; // android.content.res.Configuration.UI_MODE_TYPE_TELEVISION
                return uiModeManager != null && uiModeManager.Call<int>("getCurrentModeType") == UiModeTypeTelevision;
            }
        }
        catch (Exception)
        {
            return false; // any JNI hiccup: fall through to the size heuristic
        }
    }
#endif

    /// <summary>
    /// True only on an Android device where an XR loader actually initialized (a headset).
    /// Phones/tablets, desktop builds, and the editor all return false.
    /// </summary>
    public static bool IsXrHeadsetDevice()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        XRGeneralSettings settings = XRGeneralSettings.Instance;
        return settings != null && settings.Manager != null && settings.Manager.activeLoader != null;
#else
        return false;
#endif
    }

    /// <summary>
    /// Recenters the customer onto the panorama centre: rotates the XR rig so their
    /// current gaze direction becomes world +Z (where the labels and floor plan board
    /// live), then slides the rig so their head sits exactly on the world origin.
    /// Floor-based tracking maps the headset to its physical position in the room, so
    /// without the position step a customer standing away from their play-space centre
    /// is metres away from all the world-anchored content (board looks tiny/misplaced,
    /// labels off to the side). Height is left untouched — real eye height is correct.
    /// </summary>
    private IEnumerator RecenterHeadset()
    {
        // Give head tracking a moment to deliver real poses before sampling.
        yield return new WaitForSecondsRealtime(0.75f);

        if (!isQuestClient || xrOrigin == null) yield break;

        Camera headCamera = Camera.main;
        if (headCamera == null) yield break;

        // 1) Yaw: current facing direction becomes the panorama front (+Z).
        float yaw = headCamera.transform.rotation.eulerAngles.y;
        xrOrigin.transform.Rotate(0f, -yaw, 0f, Space.World);

        // 2) Position: after the rotation settles the camera's world offset, pull the
        //    rig back so the head lands on the origin (XZ).
        Vector3 head = headCamera.transform.position;
        xrOrigin.transform.position -= new Vector3(head.x, 0f, head.z);

        // 3) Height: pin the eyes to presentation eye level. Makes the experience immune
        //    to tracking-space differences (floor vs local) — booting before the Guardian
        //    is ready otherwise leaves the customer's eyes near floor level.
        xrOrigin.transform.position += new Vector3(0f, TargetEyeHeight - head.y, 0f);

        Debug.Log($"[NetworkSetup] Headset recentered (yaw {yaw:F0}°, offset {head.x:F2},{head.z:F2}, eye {head.y:F2}→{TargetEyeHeight}).");
    }

    /// <summary>
    /// Presenter devices swap the EventSystem's input module at runtime: XRUIInputModule
    /// (required on the headset for controller-ray UI) has a defect in its built-in
    /// fallback path — touch updates only the pointer POSITION and never registers the
    /// press, so on a mouse-less phone nothing is ever clickable. Unity's standard
    /// InputSystemUIInputModule handles touch/mouse fully; assigning its default actions
    /// at runtime is safe (the edit-time pitfall is only about serializing them).
    /// </summary>
    private void UseTouchFriendlyUiInput()
    {
        var xrModule = FindFirstObjectByType<XRUIInputModule>(FindObjectsInactive.Include);
        if (xrModule == null) return;

        xrModule.enabled = false; // leave in place; EventSystem uses the enabled module
        var standardModule = xrModule.GetComponent<InputSystemUIInputModule>();
        if (standardModule == null)
        {
            standardModule = xrModule.gameObject.AddComponent<InputSystemUIInputModule>();
            standardModule.AssignDefaultActions();
        }
        standardModule.enabled = true;

        // Pointer/touch stays on the standard module. TV directional/submit/back keys
        // are handled below so vendor remotes that appear as either keyboards or
        // gamepads behave identically and never double-submit a button.
        standardModule.move = null;
        standardModule.submit = null;
        standardModule.cancel = null;
    }

    private void HandleTvDpadInput(int androidKeyCode)
    {
        if (EventSystem.current == null) return;

        Keyboard keyboard = Keyboard.current;
        Gamepad gamepad = Gamepad.current;
        MoveDirection direction = MoveDirection.None;

        if (androidKeyCode == 21 || // KeyEvent.KEYCODE_DPAD_LEFT
            (keyboard != null && keyboard.leftArrowKey.wasPressedThisFrame) ||
            (gamepad != null && gamepad.dpad.left.wasPressedThisFrame))
            direction = MoveDirection.Left;
        else if (androidKeyCode == 22 || // KeyEvent.KEYCODE_DPAD_RIGHT
                 (keyboard != null && keyboard.rightArrowKey.wasPressedThisFrame) ||
                 (gamepad != null && gamepad.dpad.right.wasPressedThisFrame))
            direction = MoveDirection.Right;
        else if (androidKeyCode == 19 || // KeyEvent.KEYCODE_DPAD_UP
                 (keyboard != null && keyboard.upArrowKey.wasPressedThisFrame) ||
                 (gamepad != null && gamepad.dpad.up.wasPressedThisFrame))
            direction = MoveDirection.Up;
        else if (androidKeyCode == 20 || // KeyEvent.KEYCODE_DPAD_DOWN
                 (keyboard != null && keyboard.downArrowKey.wasPressedThisFrame) ||
                 (gamepad != null && gamepad.dpad.down.wasPressedThisFrame))
            direction = MoveDirection.Down;

        if (direction != MoveDirection.None)
            MoveTvDpadSelection(direction);

        bool submit = androidKeyCode == 23 || androidKeyCode == 66 ||
            androidKeyCode == 160 || androidKeyCode == 62 ||
            androidKeyCode == 96 || androidKeyCode == 109 ||
            (keyboard != null &&
             (keyboard.enterKey.wasPressedThisFrame ||
              keyboard.numpadEnterKey.wasPressedThisFrame ||
              keyboard.spaceKey.wasPressedThisFrame)) ||
            (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame);

        if (submit && EventSystem.current.currentSelectedGameObject != null)
        {
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            ExecuteEvents.Execute(selected, new BaseEventData(EventSystem.current),
                ExecuteEvents.submitHandler);
            Debug.Log($"[NetworkSetup] Android TV D-pad submit: {selected.name}.");
        }
    }

    private void MoveTvDpadSelection(MoveDirection direction)
    {
        GameObject selectedObject = EventSystem.current.currentSelectedGameObject;
        Selectable current = selectedObject != null ? selectedObject.GetComponent<Selectable>() : null;
        if (current == null)
        {
            if (tvStartHostButton != null) tvStartHostButton.Select();
            return;
        }

        Selectable next = direction switch
        {
            MoveDirection.Left => current.FindSelectableOnLeft(),
            MoveDirection.Right => current.FindSelectableOnRight(),
            MoveDirection.Up => current.FindSelectableOnUp(),
            MoveDirection.Down => current.FindSelectableOnDown(),
            _ => null
        };

        // Automatic geometry can report no neighbour when controls sit in separate
        // layout groups (rooms on the left, controls on the right). Fall back to the
        // TV canvas hierarchy so every arrow press still advances and wraps safely.
        if (next == null && tvCanvas != null)
        {
            Selectable[] controls = tvCanvas.GetComponentsInChildren<Selectable>(false);
            int currentIndex = Array.IndexOf(controls, current);
            int step = direction == MoveDirection.Left || direction == MoveDirection.Up ? -1 : 1;
            for (int offset = 1; controls.Length > 1 && offset <= controls.Length; offset++)
            {
                int index = (currentIndex + step * offset) % controls.Length;
                if (index < 0) index += controls.Length;
                Selectable candidate = controls[index];
                if (candidate != current && candidate.IsActive() && candidate.IsInteractable())
                {
                    next = candidate;
                    break;
                }
            }
        }

        if (next != null && next.IsActive() && next.IsInteractable())
        {
            next.Select();
            Debug.Log($"[NetworkSetup] Android TV D-pad focus: {next.gameObject.name}.");
        }
    }

    /// <summary>Android TV Back/B closes the drawer first, then returns to the launcher.</summary>
    private void HandleTvBackButton(int androidKeyCode)
    {
        bool backPressed = androidKeyCode == 4 || androidKeyCode == 97 ||
            (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
            (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
        if (!backPressed) return;

        if (tvDrawer == null)
            tvDrawer = FindFirstObjectByType<TvDrawerController>(FindObjectsInactive.Include);

        if (tvDrawer != null && tvDrawer.IsOpen)
            tvDrawer.SetOpen(false, instant: false);
        else
            Application.Quit();
    }

    private static int ConsumeAndroidTvKeyCode()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            androidTvActivityClass ??=
                new AndroidJavaClass("com.godrej.presenter.GodrejTvActivity");
            return androidTvActivityClass.CallStatic<int>("consumeGodrejKeyCode");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSetup] Android TV key bridge unavailable: {e.Message}");
        }
#endif
        return 0;
    }

    /// <summary>
    /// The XRI Starter rig ships with a full locomotion stack (gravity, stick move, snap
    /// turn, teleport, climb). The customer only looks around — and in a panorama scene
    /// with no floor collider, GravityProvider makes the rig FREE-FALL from app start
    /// (labels and the plan board appear to shoot upward). Disable the entire stack.
    /// </summary>
    private void DisableLocomotion()
    {
        if (xrOrigin == null) return;

        foreach (Transform child in xrOrigin.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Locomotion")
            {
                child.gameObject.SetActive(false);
                Debug.Log("[NetworkSetup] Locomotion stack disabled (gravity/move/turn/teleport).");
                return;
            }
        }

        Debug.LogWarning("[NetworkSetup] No 'Locomotion' object found under the XR rig — if the view falls or drifts, disable its locomotion providers manually.");
    }

    /// <summary>Re-anchor whenever the runtime moves the tracking space (system recenter, Guardian re-localization, headset re-worn).</summary>
    private void SubscribeTrackingOriginChanges()
    {
        SubsystemManager.GetSubsystems(InputSubsystems);
        foreach (XRInputSubsystem subsystem in InputSubsystems)
        {
            subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
        }
    }

    private void UnsubscribeTrackingOriginChanges()
    {
        foreach (XRInputSubsystem subsystem in InputSubsystems)
        {
            if (subsystem != null) subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }
        InputSubsystems.Clear();
    }

    private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
    {
        if (isQuestClient && isActiveAndEnabled) StartCoroutine(RecenterHeadset());
    }

    private void Start()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetworkSetup] No NetworkManager in scene — networking disabled.");
            SetStatus("Setup error: NetworkManager missing");
            return;
        }

        if (transport == null)
        {
            transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        }

        if (transport == null)
        {
            Debug.LogError("[NetworkSetup] No UnityTransport on the NetworkManager — networking disabled.");
            SetStatus("Setup error: UnityTransport missing");
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        NetworkManager.Singleton.OnTransportFailure += OnTransportFailure;

        // Generated scenes already have persistent button wiring. Add a runtime fallback
        // only for hand-edited scenes, otherwise TV Start Host fires twice (start then stop).
        if (startHostButton != null && startHostButton.onClick.GetPersistentEventCount() == 0)
            startHostButton.onClick.AddListener(StartHost);
        if (connectButton != null && connectButton.onClick.GetPersistentEventCount() == 0)
            connectButton.onClick.AddListener(ConnectManually);

        if (questIpInputField != null)
        {
            questIpInputField.text = PlayerPrefs.GetString(LastHostIpKey, "192.168.1.100");
        }

        ApplyPlatformMode();
    }

    private void OnDestroy()
    {
        // Belt-and-braces: if a session ends while hosting (play mode exit, app close),
        // force the transport to dispose its native socket. A leaked bind keeps port
        // 7777 busy inside the editor process and every later session then fails with
        // "port in use" until the editor restarts.
        if (sessionStarted && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.Shutdown();
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
        }

        if (startHostButton != null) startHostButton.onClick.RemoveListener(StartHost);
        if (connectButton != null) connectButton.onClick.RemoveListener(ConnectManually);

        UnsubscribeTrackingOriginChanges();
        StopDiscovery();
        StopRemoteLink();
    }

    private void Update()
    {
        // The salesman APK swaps live on device as the phone rotates. The same path keeps
        // editor preview useful; dedicated TV and remote builds are pinned by baked mode.
        if (!isQuestClient)
        {
            bool tvPresenter = IsTvDevice();
            if (tvPresenter != presenterTvApplied)
            {
                ApplyPresenterCanvas(tvPresenter);
                StartCoroutine(LogPresenterCanvasHealth(tvPresenter));
            }
        }

        // Presenter devices: pump the TV remote link (no Netcode involved).
        if (!isQuestClient)
        {
            if (IsRemoteControl()) PumpRemoteLink();
            else if (BakedPresenterMode() == "tv")
            {
                int androidTvKeyCode = ConsumeAndroidTvKeyCode();
                HandleTvDpadInput(androidTvKeyCode);
                PumpTvRemote();
                HandleTvBackButton(androidTvKeyCode);
            }
            return;
        }

        // Client: fire periodic discovery probes and react to a located host.
        if (sessionStarted) return;

        if (discoveredHostIp != null)
        {
            string ip = discoveredHostIp;
            discoveredHostIp = null;
            SetStatus($"Presenter found at {ip} — connecting…");
            StartClient(ip);
            return;
        }

        if (autoConnectOnQuest && discoverySocket != null && Time.unscaledTime >= nextProbeTime)
        {
            nextProbeTime = Time.unscaledTime + 2f;
            try
            {
                discoverySocket.Send(probeBuffer, probeBuffer.Length, broadcastEndPoint);
            }
            catch (SocketException e)
            {
                Debug.LogWarning($"[NetworkSetup] Discovery probe failed: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- platform switching

    /// <summary>Enables exactly the objects the current device needs.</summary>
    private void ApplyPlatformMode()
    {
        if (hostRoot != null) hostRoot.SetActive(!isQuestClient);
        if (xrOrigin != null) xrOrigin.SetActive(isQuestClient);
        if (questConnectPanel != null) questConnectPanel.SetActive(isQuestClient);

        if (isQuestClient)
        {
            SetStatus(autoConnectOnQuest ? "Searching for presenter…" : "Enter presenter IP to connect");
            if (autoConnectOnQuest) StartClientDiscovery();
            DisableLocomotion();               // stop gravity free-fall & stick movement
            SubscribeTrackingOriginChanges();  // re-anchor after Guardian/system recenters
            StartCoroutine(RecenterHeadset());
        }
        else
        {
            // Exactly one presenter canvas per device: big landscape screens get the
            // full-screen drawer UI, phones/tablets keep the portrait canvas.
            bool tvPresenter = IsTvDevice();
            ApplyPresenterCanvas(tvPresenter);
            StartCoroutine(LogPresenterCanvasHealth(tvPresenter));

            string presenterMode = BakedPresenterMode();
            if (presenterMode == "phone")
            {
                // Salesman phone: portrait = existing salesman canvas; either landscape
                // rotation = existing TV canvas. Upside-down portrait is intentionally off.
                Screen.autorotateToPortrait = true;
                Screen.autorotateToPortraitUpsideDown = false;
                Screen.autorotateToLandscapeLeft = true;
                Screen.autorotateToLandscapeRight = true;
#if UNITY_ANDROID && !UNITY_EDITOR
                // The manifest and Android activity own rotation on-device. Assigning
                // Screen.orientation here queues a second request that can overwrite
                // FULL_SENSOR after Android has already delivered its first rotation.
                StartCoroutine(ApplyAndroidFullSensorOrientation());
#else
                Screen.orientation = ScreenOrientation.AutoRotation;
#endif
            }
            else
            {
                Screen.orientation = presenterMode == "tv"
                    ? ScreenOrientation.LandscapeLeft
                    : ScreenOrientation.Portrait;
            }

            UseTouchFriendlyUiInput();

            if (IsRemoteControl())
            {
                // Remote APK: never hosts. Every salesman action mirrors to the TV;
                // the pano preview area becomes the big TV controls instead.
                LocalExperienceManager.RemoteForward = ForwardSalesmanAction;
                StartRemoteLink();
                BuildRemoteControlPanel();
                SetStatus("Searching for TV…");
            }
            else
            {
                LocalExperienceManager.RemoteForward = null;
                if (presenterMode == "tv")
                    StartTvRemoteListener(); // commands accepted from boot, before hosting
                SetStatus("Ready — press Start Host");
            }

            SetIpText($"This device: {GetLocalIPv4()}");
        }
    }

    // ---------------------------------------------------------------- host

    /// <summary>Starts hosting. Wired to the Start Host button on the salesman canvas.</summary>
    public void StartHost()
    {
        if (IsRemoteControl())
        {
            // The remote's button toggles hosting ON THE TV (headset connect/disconnect).
            if (remoteTvEndPoint == null)
            {
                SetStatus("No TV linked yet — still searching…");
                return;
            }

            ForwardSalesmanAction(remoteTvHosting ? "stophost" : "starthost", 1f);
            SetStatus(remoteTvHosting ? "Stop sent to TV…" : "Start Host sent to TV…");
            return;
        }

        if (sessionStarted)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            {
                StopHosting(); // second press of the toggle: end the session
                return;
            }

            sessionStarted = false; // server died underneath us — fall through and restart
        }

        if (transport == null || NetworkManager.Singleton == null) return;

        string localIp = GetLocalIPv4();

        // Clients connect to our LAN address; we listen on all interfaces so the exact
        // NIC/hotspot in use never matters.
        transport.SetConnectionData(localIp, port, "0.0.0.0");

        if (NetworkManager.Singleton.StartHost())
        {
            sessionStarted = true;
            SetStatus("Hosting — waiting for headset…");
            SetIpText($"Host IP: {localIp}   Port: {port}");
            StartHostDiscoveryResponder();
        }
        else
        {
            SetStatus("Failed to start host — is the port in use?");
        }
    }

    /// <summary>
    /// Deliberately ends the hosted session (the START HOST toggle's second press —
    /// locally or via the remote's "stophost"). The headset falls back to searching,
    /// so pressing START HOST again resumes the presentation without touching the Quest.
    /// </summary>
    public void StopHosting()
    {
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.Shutdown();
        sessionStarted = false;
        StopDiscovery();
        SetStatus("Ready — press Start Host");
        SetIpText($"This device: {GetLocalIPv4()}");
    }

    // ---------------------------------------------------------------- client

    /// <summary>Manual fallback: connect to the IP typed into the input field.</summary>
    public void ConnectManually()
    {
        string ip = questIpInputField != null ? questIpInputField.text.Trim() : string.Empty;

        if (!IsValidIPv4(ip))
        {
            SetStatus("Invalid IP address (use e.g. 192.168.1.42)");
            return;
        }

        StartClient(ip);
    }

    private void StartClient(string ip)
    {
        if (sessionStarted || transport == null || NetworkManager.Singleton == null) return;

        transport.SetConnectionData(ip, port);

        if (NetworkManager.Singleton.StartClient())
        {
            sessionStarted = true;
            StopDiscovery();
            PlayerPrefs.SetString(LastHostIpKey, ip);
            PlayerPrefs.Save();
            SetStatus($"Connecting to {ip}…");
            if (connectButton != null) connectButton.interactable = false;
        }
        else
        {
            SetStatus("Could not start connection — retrying search…");
            if (autoConnectOnQuest) StartClientDiscovery();
        }
    }

    // ---------------------------------------------------------------- connection callbacks

    private void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsHost)
        {
            if (clientId != NetworkManager.Singleton.LocalClientId)
            {
                lastConnectTime = Time.unscaledTime;
                SetStatus("Headset connected — presentation live");
            }
        }
        else
        {
            lastConnectTime = Time.unscaledTime;
            SetStatus("Connected!");
            // Hide the connect panel for a clean, UI-free customer experience.
            if (questConnectPanel != null) questConnectPanel.SetActive(false);
            // Re-align so the presentation "front" matches wherever the customer now faces.
            StartCoroutine(RecenterHeadset());
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (NetworkManager.Singleton.IsHost)
        {
            if (clientId != NetworkManager.Singleton.LocalClientId)
            {
                SetStatus(RegisterDrop()
                    ? "Headset app OUTDATED — reinstall via Build And Run"
                    : "Headset disconnected — waiting…");
            }
            return;
        }

        // Presenter after a deliberate shutdown: callbacks may arrive with IsHost
        // already false — never run the Quest-side reconnect UI on a presenter.
        if (!isQuestClient) return;

        // Quest side: connection lost or the connect attempt timed out.
        sessionStarted = false;
        bool flapping = RegisterDrop();
        SetStatus(flapping
            ? "App outdated — ask the presenter to rebuild and reinstall it"
            : "Disconnected — searching for presenter…");
        if (questConnectPanel != null) questConnectPanel.SetActive(true);
        if (connectButton != null) connectButton.interactable = true;
        if (autoConnectOnQuest && isQuestClient)
        {
            StartClientDiscovery();
            // Back off when flapping so we don't hammer the host with doomed reconnects
            // (auto-retry continues — a rebuilt host scene could also fix the mismatch).
            if (flapping) nextProbeTime = Time.unscaledTime + 15f;
        }
    }

    /// <summary>
    /// Records a disconnect and reports whether we're in a rapid connect→drop loop
    /// (two or more connections in a row that died within seconds of connecting).
    /// </summary>
    private bool RegisterDrop()
    {
        if (Time.unscaledTime - lastConnectTime < RapidDropWindowSeconds)
        {
            rapidDropCount++;
        }
        else
        {
            rapidDropCount = 0; // the connection lived a while — a normal drop
        }

        return rapidDropCount >= 2;
    }

    private void OnTransportFailure()
    {
        sessionStarted = false;
        SetStatus("Network transport failure — restart the app if this persists.");
        if (startHostButton != null) startHostButton.interactable = true;
        if (connectButton != null) connectButton.interactable = true;
    }

    // ---------------------------------------------------------------- LAN discovery
    // Client broadcasts a probe; host answers with a unicast reply. Receiving a unicast
    // needs no multicast lock on Android, which keeps the Quest side dependency-free.

    private void StartClientDiscovery()
    {
        if (discoverySocket != null) return;

        try
        {
            probeBuffer ??= Encoding.ASCII.GetBytes(ProbeMessage);
            broadcastEndPoint ??= new IPEndPoint(IPAddress.Broadcast, discoveryPort);

            // Bind to an ephemeral port so we can receive the host's unicast reply.
            discoverySocket = new UdpClient(0) { EnableBroadcast = true };
            discoverySocket.BeginReceive(OnClientDiscoveryReceive, null);
            nextProbeTime = 0f;
        }
        catch (Exception e)
        {
            Debug.LogError($"[NetworkSetup] Could not start discovery: {e.Message}");
            SetStatus("Discovery unavailable — enter IP manually");
        }
    }

    private void OnClientDiscoveryReceive(IAsyncResult result)
    {
        UdpClient socket = discoverySocket;
        if (socket == null) return;

        try
        {
            IPEndPoint sender = null;
            byte[] data = socket.EndReceive(result, ref sender);

            if (sender != null && Encoding.ASCII.GetString(data) == ReplyMessage)
            {
                discoveredHostIp = sender.Address.ToString(); // handled on the main thread in Update()
            }

            // Keep listening: if a connect attempt fails we fall back to discovery,
            // and duplicate replies simply refresh the discovered address.
            socket.BeginReceive(OnClientDiscoveryReceive, null);
        }
        catch (ObjectDisposedException) { /* socket closed during shutdown — expected */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSetup] Discovery receive error: {e.Message}");
        }
    }

    private void StartHostDiscoveryResponder()
    {
        if (discoverySocket != null) return;

        try
        {
            discoverySocket = new UdpClient();
            discoverySocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            discoverySocket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            discoverySocket.BeginReceive(OnHostDiscoveryReceive, null);
        }
        catch (Exception e)
        {
            // Not fatal: manual IP entry still works.
            Debug.LogWarning($"[NetworkSetup] Discovery responder unavailable: {e.Message}");
        }
    }

    private void OnHostDiscoveryReceive(IAsyncResult result)
    {
        UdpClient socket = discoverySocket;
        if (socket == null) return;

        try
        {
            IPEndPoint sender = null;
            byte[] data = socket.EndReceive(result, ref sender);

            if (sender != null && Encoding.ASCII.GetString(data) == ProbeMessage)
            {
                byte[] reply = Encoding.ASCII.GetBytes(ReplyMessage);
                socket.Send(reply, reply.Length, sender);
            }

            socket.BeginReceive(OnHostDiscoveryReceive, null);
        }
        catch (ObjectDisposedException) { /* socket closed during shutdown — expected */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSetup] Discovery responder error: {e.Message}");
        }
    }

    private void StopDiscovery()
    {
        if (discoverySocket == null) return;

        try { discoverySocket.Close(); }
        catch (Exception) { /* ignore shutdown races */ }
        discoverySocket = null;
    }

    // ---------------------------------------------------------------- TV remote link
    // The remote APK is the phone salesman UI acting as a wireless controller: it keeps
    // driving its own scene as a live in-hand preview, and mirrors every action to the
    // TV over UDP. The TV applies commands through the same LocalExperienceManager entry
    // points, so a hosted Quest follows automatically. Same probe/unicast-reply pattern
    // as Quest discovery (no multicast lock needed) on a separate port; unlike hosting,
    // the TV listens from BOOT, so the remote can even press Start Host for it. Socket
    // callbacks only enqueue — all protocol logic runs on the main thread in Update().

    private LocalExperienceManager Experience
    {
        get
        {
            if (experienceManager == null)
            {
                experienceManager = FindFirstObjectByType<LocalExperienceManager>(FindObjectsInactive.Include);
            }
            return experienceManager;
        }
    }

    /// <summary>TV side: accept remote probes and commands from app start.</summary>
    private void StartTvRemoteListener()
    {
        if (remoteSocket != null) return;

        try
        {
            remoteSocket = new UdpClient();
            remoteSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            remoteSocket.Client.Bind(new IPEndPoint(IPAddress.Any, RemotePort));
            remoteSocket.BeginReceive(OnRemoteSocketReceive, null);
            Debug.Log($"[NetworkSetup] TV remote listener ready on UDP {RemotePort}.");
        }
        catch (Exception e)
        {
            // Not fatal: the TV still works by touch.
            Debug.LogWarning($"[NetworkSetup] TV remote listener unavailable: {e.Message}");
        }
    }

    /// <summary>Remote side: open the link socket and start probing for the TV.</summary>
    private void StartRemoteLink()
    {
        if (remoteSocket != null) return;

        try
        {
            // Ephemeral port; the TV replies unicast to whatever address probed it.
            remoteSocket = new UdpClient(0) { EnableBroadcast = true };
            RefreshRemoteProbeEndPoints();
            remoteSocket.BeginReceive(OnRemoteSocketReceive, null);
            nextRemoteProbeTime = 0f;
        }
        catch (Exception e)
        {
            Debug.LogError($"[NetworkSetup] Could not start the remote link: {e.Message}");
            SetStatus("Remote link unavailable — restart the app");
        }
    }

    /// <summary>
    /// Builds both the limited broadcast and each active Wi-Fi/Ethernet subnet's
    /// directed broadcast. Some Android routers drop 255.255.255.255 but accept the
    /// directed address (for example 192.168.1.255), so sending both is substantially
    /// more reliable without needing multicast permissions or an internet service.
    /// </summary>
    private void RefreshRemoteProbeEndPoints()
    {
        remoteProbeEndPoints.Clear();
        var addresses = new HashSet<string>(StringComparer.Ordinal)
        {
            IPAddress.Broadcast.ToString()
        };

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in
                         adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address) || unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    byte[] addressBytes = unicast.Address.GetAddressBytes();
                    byte[] maskBytes = unicast.IPv4Mask.GetAddressBytes();
                    if (addressBytes.Length != maskBytes.Length) continue;

                    var broadcastBytes = new byte[addressBytes.Length];
                    for (int i = 0; i < addressBytes.Length; i++)
                        broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);

                    addresses.Add(new IPAddress(broadcastBytes).ToString());
                }
            }
        }
        catch (Exception e)
        {
            // Limited broadcast remains available even on devices whose runtime does
            // not expose interface masks.
            Debug.LogWarning($"[NetworkSetup] Could not enumerate directed broadcasts: {e.Message}");
        }

        foreach (string address in addresses)
            remoteProbeEndPoints.Add(new IPEndPoint(IPAddress.Parse(address), RemotePort));

        Debug.Log($"[NetworkSetup] Remote discovery using {remoteProbeEndPoints.Count} broadcast address(es).");
    }

    private void OnRemoteSocketReceive(IAsyncResult result)
    {
        UdpClient socket = remoteSocket;
        if (socket == null) return;

        try
        {
            IPEndPoint sender = null;
            byte[] data = socket.EndReceive(result, ref sender);
            if (sender != null)
            {
                remoteInbox.Enqueue((Encoding.ASCII.GetString(data), sender));
            }

            socket.BeginReceive(OnRemoteSocketReceive, null);
        }
        catch (ObjectDisposedException) { /* socket closed during shutdown — expected */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSetup] Remote link receive error: {e.Message}");
        }
    }

    /// <summary>TV side (main thread): answer probes, apply commands, echo state as the ack.</summary>
    private void PumpTvRemote()
    {
        if (remoteSocket == null) return;

        while (remoteInbox.TryDequeue(out (string Message, IPEndPoint Sender) item))
        {
            if (item.Message == RemoteProbeMessage)
            {
                SendTvState(item.Sender);
            }
            else if (item.Message.StartsWith(RemoteCommandPrefix, StringComparison.Ordinal))
            {
                int bar = item.Message.IndexOf('|');
                int eq = item.Message.IndexOf('=');
                if (bar < 0 || eq <= bar + 1) continue;

                ApplyRemoteCommand(
                    item.Message.Substring(bar + 1, eq - bar - 1),
                    item.Message.Substring(eq + 1));
                SendTvState(item.Sender);
            }
        }
    }

    private void ApplyRemoteCommand(string key, string value)
    {
        switch (key)
        {
            case "pano":
                if (Experience != null && int.TryParse(value, out int index)) Experience.SetPanorama(index);
                break;
            case "labels":
                if (Experience != null) Experience.SetLabelsVisible(value == "1");
                break;
            case "plan":
                if (Experience != null) Experience.SetFloorPlanVisible(value == "1");
                break;
            case "view":
                if (Experience != null &&
                    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw))
                {
                    Experience.SetStartViewRotation(yaw);
                }
                break;
            case "starthost":
                if (!sessionStarted) StartHost();
                break;
            case "stophost":
                if (sessionStarted) StopHosting();
                break;
            case "menutoggle":
                if (tvDrawer == null)
                {
                    tvDrawer = FindFirstObjectByType<TvDrawerController>(FindObjectsInactive.Include);
                }
                if (tvDrawer != null) tvDrawer.Toggle();
                break;
        }
    }

    private void SendTvState(IPEndPoint to)
    {
        if (remoteSocket == null || to == null) return;

        int pano = Experience != null ? Experience.CurrentPanorama : 0;
        byte[] state = Encoding.ASCII.GetBytes(
            FormattableString.Invariant($"{RemoteStatePrefix}|hosting={(sessionStarted ? 1 : 0)}|pano={pano}"));
        try { remoteSocket.Send(state, state.Length, to); }
        catch (SocketException) { /* remote may have left — next probe re-links */ }
    }

    /// <summary>Remote side (main thread): handle TV replies, keep probing, drop dead links.</summary>
    private void PumpRemoteLink()
    {
        if (remoteSocket == null) return;

        while (remoteInbox.TryDequeue(out (string Message, IPEndPoint Sender) item))
        {
            if (!item.Message.StartsWith(RemoteStatePrefix, StringComparison.Ordinal)) continue;

            remoteTvEndPoint = item.Sender;
            remoteTvLastSeen = Time.unscaledTime;
            remoteTvHosting = item.Message.Contains("|hosting=1");
            SetStatus(remoteTvHosting
                ? $"Remote → TV {item.Sender.Address} · presentation live"
                : $"Remote → TV {item.Sender.Address} · ready");
            RefreshRemotePanel();
        }

        // State echoes double as the heartbeat; silence means the TV app is gone.
        if (remoteTvEndPoint != null && Time.unscaledTime - remoteTvLastSeen > RemoteLinkTimeoutSeconds)
        {
            remoteTvEndPoint = null;
            remoteTvHosting = false;
            RefreshRemoteProbeEndPoints();
            SetStatus("TV lost — searching…");
            RefreshRemotePanel();
        }

        if (Time.unscaledTime >= nextRemoteProbeTime)
        {
            nextRemoteProbeTime = Time.unscaledTime + RemoteProbeIntervalSeconds;
            byte[] probe = Encoding.ASCII.GetBytes(RemoteProbeMessage);
            if (remoteTvEndPoint != null)
            {
                try
                {
                    remoteSocket.Send(probe, probe.Length, remoteTvEndPoint); // unicast heartbeat
                }
                catch (SocketException e)
                {
                    Debug.LogWarning($"[NetworkSetup] Remote heartbeat failed: {e.Message}");
                }
            }
            else
            {
                if (remoteProbeEndPoints.Count == 0) RefreshRemoteProbeEndPoints();
                int successfulSends = 0;
                SocketException lastError = null;
                foreach (IPEndPoint endPoint in remoteProbeEndPoints)
                {
                    try
                    {
                        remoteSocket.Send(probe, probe.Length, endPoint);
                        successfulSends++;
                    }
                    catch (SocketException e)
                    {
                        lastError = e;
                    }
                }

                if (successfulSends == 0 && lastError != null)
                    Debug.LogWarning($"[NetworkSetup] Remote probes failed: {lastError.Message}");
            }
        }
    }

    /// <summary>Remote side: LocalExperienceManager.RemoteForward target — one action, one datagram.</summary>
    private void ForwardSalesmanAction(string key, float value)
    {
        if (remoteSocket == null || remoteTvEndPoint == null) return; // still searching: preview-only

        string payload = key == "view"
            ? FormattableString.Invariant($"{RemoteCommandPrefix}|{key}={value:F1}")
            : FormattableString.Invariant($"{RemoteCommandPrefix}|{key}={(int)value}");
        byte[] data = Encoding.ASCII.GetBytes(payload);
        try { remoteSocket.Send(data, data.Length, remoteTvEndPoint); }
        catch (SocketException e)
        {
            Debug.LogWarning($"[NetworkSetup] Remote command failed: {e.Message}");
        }
    }

    private void StopRemoteLink()
    {
        LocalExperienceManager.RemoteForward = null;
        if (remoteSocket == null) return;

        try { remoteSocket.Close(); }
        catch (Exception) { /* ignore shutdown races */ }
        remoteSocket = null;
        remoteProbeEndPoints.Clear();
    }

    // ---------------------------------------------------------------- remote control panel
    // The remote has no use for the pano preview (the TV is the display) — its
    // viewport zone becomes three large controls: CONNECT TO TV (green when linked),
    // TV MENU (slides the TV drawer up/down) and START HOST · HEADSET (toggles the
    // hosted session on the TV). Built at runtime so the shared scene is untouched.

    private static readonly Color32 PanelBackground = new Color32(0x12, 0x16, 0x1C, 0xFF);
    private static readonly Color32 PanelButton = new Color32(0x2A, 0x34, 0x41, 0xFF);
    private static readonly Color32 PanelConnected = new Color32(0x2E, 0x7D, 0x4F, 0xFF);
    private static readonly Color32 PanelAccent = new Color32(0xC8, 0xA5, 0x57, 0xFF);
    private static readonly Color32 PanelStop = new Color32(0x8A, 0x3B, 0x35, 0xFF);
    private static readonly Color32 PanelText = new Color32(0xED, 0xF1, 0xF6, 0xFF);
    private static readonly Color32 PanelTextDark = new Color32(0x12, 0x16, 0x1C, 0xFF);

    private void BuildRemoteControlPanel()
    {
        if (phoneCanvas == null || remoteConnectLabel != null) return; // build once

        Transform zone = null;
        foreach (Transform t in phoneCanvas.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Zone 2 - VR Viewport") { zone = t; break; }
        }
        if (zone == null)
        {
            Debug.LogWarning("[NetworkSetup] 'Zone 2 - VR Viewport' not found — remote TV buttons not built.");
            return;
        }

        // The live preview widgets are meaningless on the remote — the panel replaces them.
        for (int i = zone.childCount - 1; i >= 0; i--)
        {
            zone.GetChild(i).gameObject.SetActive(false);
        }

        var panelGO = new GameObject("Remote Control Panel", typeof(RectTransform));
        panelGO.transform.SetParent(zone, false);
        var rect = (RectTransform)panelGO.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        panelGO.AddComponent<Image>().color = PanelBackground;

        var layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(28, 28, 28, 28);
        layout.spacing = 22;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        remoteConnectFill = CreateRemoteButton(panelGO.transform, "Connect To TV",
            "CONNECT TO TV", out remoteConnectLabel, OnRemoteConnectPressed);
        Image menuFill = CreateRemoteButton(panelGO.transform, "TV Menu",
            "TV MENU", out _, OnRemoteMenuPressed);
        remoteMenuButton = menuFill.GetComponent<Button>();
        remoteHostFill = CreateRemoteButton(panelGO.transform, "Host Toggle",
            "START HOST · HEADSET", out remoteHostLabel, StartHost);

        RefreshRemotePanel();
    }

    private static Image CreateRemoteButton(Transform parent, string name, string caption,
        out TextMeshProUGUI label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.color = PanelButton;
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var textRect = (RectTransform)textGO.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        label = textGO.AddComponent<TextMeshProUGUI>();
        label.text = caption;
        label.fontSize = 42f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = PanelText;
        return image;
    }

    /// <summary>Manual connect: fire a probe right now instead of waiting for the timer.</summary>
    private void OnRemoteConnectPressed()
    {
        RefreshRemoteProbeEndPoints();
        nextRemoteProbeTime = 0f;
        if (remoteTvEndPoint == null) SetStatus("Connecting to TV…");
    }

    /// <summary>One button slides the TV menu up and down (the TV just toggles).</summary>
    private void OnRemoteMenuPressed() => ForwardSalesmanAction("menutoggle", 1f);

    private void RefreshRemotePanel()
    {
        if (remoteConnectLabel == null) return;

        bool linked = remoteTvEndPoint != null;

        remoteConnectFill.color = linked ? PanelConnected : (Color)PanelButton;
        remoteConnectLabel.text = linked
            ? $"TV CONNECTED · {remoteTvEndPoint.Address}"
            : "CONNECT TO TV";

        if (remoteMenuButton != null) remoteMenuButton.interactable = linked;

        remoteHostFill.color = !linked ? (Color)PanelButton
            : remoteTvHosting ? PanelStop
            : (Color)PanelAccent;
        remoteHostLabel.text = remoteTvHosting ? "STOP HOSTING" : "START HOST · HEADSET";
        remoteHostLabel.color = linked && !remoteTvHosting ? PanelTextDark : (Color)PanelText;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Best-guess LAN IPv4 of this device. Interfaces with a default gateway win (the real
    /// Wi-Fi/router link), then 192.168.x.x (typical hotspot/router ranges), then anything else —
    /// this keeps VPN and virtual adapters from hijacking the displayed address.
    /// </summary>
    private static string GetLocalIPv4()
    {
        string best = null;
        int bestScore = -1;

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties props = nic.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork) { hasGateway = true; break; }
                }

                foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;

                    string ip = addr.Address.ToString();
                    int score = hasGateway ? 2 : 0;
                    if (ip.StartsWith("192.168.")) score += 1;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = ip;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSetup] Could not detect LAN IP: {e.Message}");
        }

        return best ?? "127.0.0.1";
    }

    /// <summary>Strict IPv4 dotted-quad validation.</summary>
    private static bool IsValidIPv4(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;

        string[] parts = ip.Split('.');
        if (parts.Length != 4) return false;

        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length == 0 || parts[i].Length > 3) return false;
            if (!byte.TryParse(parts[i], out _)) return false;
        }

        return true;
    }

    private void SetStatus(string message)
    {
        // The remote heartbeat re-asserts the same status every couple of seconds —
        // repeating identical writes would only flood the device log.
        if (!isQuestClient && message == lastStatusMessage) return;

        if (isQuestClient)
        {
            if (questStatusText != null) questStatusText.text = message;
        }
        else
        {
            lastStatusMessage = message;
            if (statusText != null) statusText.text = message;
        }

        Debug.Log($"[NetworkSetup] {message}");
    }

    private void SetIpText(string message)
    {
        lastIpMessage = message;
        if (ipDisplayText != null) ipDisplayText.text = message;
    }
}
