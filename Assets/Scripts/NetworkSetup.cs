using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using TMPro;
using UnityEngine;
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

    private const string LastHostIpKey = "GodrejXR.LastHostIp";

    private bool isQuestClient;
    private bool sessionStarted;
    private UdpClient discoverySocket;                 // host: responder / client: prober
    private volatile string discoveredHostIp;          // written by socket thread, read on main thread
    private byte[] probeBuffer;                        // cached, allocation-free sends
    private IPEndPoint broadcastEndPoint;
    private float nextProbeTime;

    private const string ProbeMessage = "GODREJ_XR_PROBE_V1";
    private const string ReplyMessage = "GODREJ_XR_HOST_V1";

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
    }

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
        var standardModule = xrModule.gameObject.AddComponent<InputSystemUIInputModule>();
        standardModule.AssignDefaultActions();
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

        if (startHostButton != null) startHostButton.onClick.AddListener(StartHost);
        if (connectButton != null) connectButton.onClick.AddListener(ConnectManually);

        if (questIpInputField != null)
        {
            questIpInputField.text = PlayerPrefs.GetString(LastHostIpKey, "192.168.1.100");
        }

        ApplyPlatformMode();
    }

    private void OnDestroy()
    {
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
    }

    private void Update()
    {
        // Client: fire periodic discovery probes and react to a located host.
        if (!isQuestClient || sessionStarted) return;

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
            // Portrait-first presenter UI. No-op on desktop; locks rotation when the
            // salesman app runs on an Android/iOS tablet or phone. Kept out of
            // PlayerSettings because those are shared with the Quest APK build.
            Screen.orientation = ScreenOrientation.Portrait;

            UseTouchFriendlyUiInput();

            SetStatus("Ready — press Start Host");
            if (ipDisplayText != null) ipDisplayText.text = $"This device: {GetLocalIPv4()}";
        }
    }

    // ---------------------------------------------------------------- host

    /// <summary>Starts hosting. Wired to the Start Host button on the salesman canvas.</summary>
    public void StartHost()
    {
        if (sessionStarted || transport == null || NetworkManager.Singleton == null) return;

        string localIp = GetLocalIPv4();

        // Clients connect to our LAN address; we listen on all interfaces so the exact
        // NIC/hotspot in use never matters.
        transport.SetConnectionData(localIp, port, "0.0.0.0");

        if (NetworkManager.Singleton.StartHost())
        {
            sessionStarted = true;
            SetStatus("Hosting — waiting for headset…");
            if (ipDisplayText != null) ipDisplayText.text = $"Host IP: {localIp}   Port: {port}";
            if (startHostButton != null) startHostButton.interactable = false;
            StartHostDiscoveryResponder();
        }
        else
        {
            SetStatus("Failed to start host — is the port in use?");
        }
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
        if (isQuestClient)
        {
            if (questStatusText != null) questStatusText.text = message;
        }
        else
        {
            if (statusText != null) statusText.text = message;
        }

        Debug.Log($"[NetworkSetup] {message}");
    }
}
