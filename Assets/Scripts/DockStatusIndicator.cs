using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live status colors for the salesman nav dock:
///
///  START HOST knob — red: headset not connected, green: headset connected,
///  yellow: connection lost. While yellow, pressing the button retries hosting if the
///  server died (otherwise the headset reconnects on its own within seconds) and the
///  label switches to RECONNECTING…. The label reads START HOST → WAITING… →
///  CONNECTED as the session progresses.
///
///  LABELS / PLAN toggle knobs — green when on, red when off.
///
/// Lives on the dock, which only exists on presenter devices, so it never runs on the
/// headset. All updates are event-driven — no per-frame work.
/// </summary>
[DisallowMultipleComponent]
public sealed class DockStatusIndicator : MonoBehaviour
{
    [Header("Wiring (set by Godrej menu 8)")]
    [SerializeField] private NetworkSetup networkSetup;
    [SerializeField] private Button startHostButton;
    [SerializeField] private Image startHostIcon;
    [SerializeField] private TextMeshProUGUI startHostLabel;
    [SerializeField] private Toggle labelsToggle;
    [SerializeField] private Image labelsIcon;
    [SerializeField] private Toggle planToggle;
    [SerializeField] private Image planIcon;

    private static readonly Color StateRed = new Color(0.86f, 0.24f, 0.24f);
    private static readonly Color StateGreen = new Color(0.20f, 0.78f, 0.35f);
    private static readonly Color StateYellow = new Color(0.96f, 0.77f, 0.19f);

    private enum HeadsetState { NotConnected, Connected, Lost }

    private HeadsetState state = HeadsetState.NotConnected;
    private bool hosting;

    private void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }

        if (startHostButton != null) startHostButton.onClick.AddListener(OnStartHostClicked);
        if (labelsToggle != null) labelsToggle.onValueChanged.AddListener(OnLabelsToggled);
        if (planToggle != null) planToggle.onValueChanged.AddListener(OnPlanToggled);

        // Initial visuals.
        RefreshHeadsetVisuals();
        if (labelsToggle != null) OnLabelsToggled(labelsToggle.isOn);
        if (planToggle != null) OnPlanToggled(planToggle.isOn);
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        if (startHostButton != null) startHostButton.onClick.RemoveListener(OnStartHostClicked);
        if (labelsToggle != null) labelsToggle.onValueChanged.RemoveListener(OnLabelsToggled);
        if (planToggle != null) planToggle.onValueChanged.RemoveListener(OnPlanToggled);
    }

    // ---------------------------------------------------------------- headset state

    private void OnServerStarted()
    {
        hosting = true;
        RefreshHeadsetVisuals();
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsRemoteClient(clientId)) return;
        state = HeadsetState.Connected;
        RefreshHeadsetVisuals();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsRemoteClient(clientId)) return;
        state = HeadsetState.Lost;
        RefreshHeadsetVisuals();
    }

    private static bool IsRemoteClient(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsHost && clientId != nm.LocalClientId;
    }

    /// <summary>
    /// Runs in addition to NetworkSetup.StartHost (both are wired to the button).
    /// When the connection was lost and the server itself died (e.g. transport failure),
    /// this restarts hosting; if the server is still up, the headset auto-reconnects and
    /// the press just gives immediate visual feedback.
    /// </summary>
    private void OnStartHostClicked()
    {
        if (state == HeadsetState.Lost)
        {
            if (networkSetup != null && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsListening)
            {
                hosting = false;
                networkSetup.StartHost();
            }

            if (startHostLabel != null) startHostLabel.text = "RECONNECTING…";
            return;
        }

        RefreshHeadsetVisuals();
    }

    private void RefreshHeadsetVisuals()
    {
        Color knob;
        string label;

        switch (state)
        {
            case HeadsetState.Connected:
                knob = StateGreen;
                label = "CONNECTED";
                break;
            case HeadsetState.Lost:
                knob = StateYellow;
                label = "RECONNECT";
                break;
            default:
                knob = StateRed;
                label = hosting ? "WAITING…" : "START HOST";
                break;
        }

        if (startHostIcon != null) startHostIcon.color = knob;
        if (startHostLabel != null) startHostLabel.text = label;
    }

    // ---------------------------------------------------------------- toggle state

    private void OnLabelsToggled(bool isOn)
    {
        if (labelsIcon != null) labelsIcon.color = isOn ? StateGreen : StateRed;
    }

    private void OnPlanToggled(bool isOn)
    {
        if (planIcon != null) planIcon.color = isOn ? StateGreen : StateRed;
    }
}
