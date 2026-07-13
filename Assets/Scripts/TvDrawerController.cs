using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Slide-up menu drawer for the TV presenter canvas. The whole screen is the live
/// customer view; a pull tab pinned to the bottom edge toggles this drawer, which
/// carries the room buttons and the presenter controls (Start Host / Labels / Plan).
/// The tab is a child of the sliding panel, so it rides along and always stays
/// touchable at the drawer's top edge — tab at screen bottom when closed, on top of
/// the open drawer otherwise.
/// </summary>
[DisallowMultipleComponent]
public sealed class TvDrawerController : MonoBehaviour
{
    [Header("Wiring (set by Godrej menu 9)")]
    [Tooltip("The panel that slides. Anchored to the bottom screen edge; its height is the travel distance.")]
    [SerializeField] private RectTransform drawerPanel;

    [Tooltip("Tab button that toggles the drawer open/closed.")]
    [SerializeField] private Button pullTab;

    [Tooltip("Arrow glyph on the tab; flipped vertically to point up (closed) or down (open).")]
    [SerializeField] private RectTransform tabArrow;

    [Header("Behaviour")]
    [Tooltip("Approximate seconds for the drawer to travel its full height.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float slideSeconds = 0.22f;

    [Tooltip("Open at launch so the presenter immediately sees Start Host and the rooms.")]
    [SerializeField] private bool startOpen = true;

    private bool open;
    private float velocity;

    public bool IsOpen => open;

    private void Awake()
    {
        if (pullTab != null) pullTab.onClick.AddListener(Toggle);
        SetOpen(startOpen, instant: true);
    }

    private void OnDestroy()
    {
        if (pullTab != null) pullTab.onClick.RemoveListener(Toggle);
    }

    public void Toggle() => SetOpen(!open, instant: false);

    public void SetOpen(bool value, bool instant)
    {
        open = value;

        // Arrow: point up when there is something to pull up, down when it can close.
        // (The built-in DropdownArrow sprite points down at scale +1.)
        if (tabArrow != null)
        {
            Vector3 s = tabArrow.localScale;
            s.y = Mathf.Abs(s.y) * (open ? 1f : -1f);
            tabArrow.localScale = s;
        }

        if (instant && drawerPanel != null)
        {
            Vector2 pos = drawerPanel.anchoredPosition;
            pos.y = TargetY();
            drawerPanel.anchoredPosition = pos;
            velocity = 0f;
        }

        // When the drawer is off-screen, keep D-pad focus on the one control that
        // remains visible so OK/Enter always has a useful action.
        if (!open && pullTab != null && EventSystem.current != null &&
            pullTab.gameObject.activeInHierarchy)
        {
            pullTab.Select();
        }
    }

    private float TargetY() => open ? 0f : -drawerPanel.rect.height;

    private void Update()
    {
        if (drawerPanel == null) return;

        float target = TargetY();
        Vector2 pos = drawerPanel.anchoredPosition;
        if (Mathf.Approximately(pos.y, target)) return;

        pos.y = Mathf.SmoothDamp(pos.y, target, ref velocity, slideSeconds,
            Mathf.Infinity, Time.unscaledDeltaTime);
        if (Mathf.Abs(pos.y - target) < 0.5f)
        {
            pos.y = target;
            velocity = 0f;
        }
        drawerPanel.anchoredPosition = pos;
    }
}
