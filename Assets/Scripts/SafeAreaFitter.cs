using UnityEngine;

/// <summary>
/// Resizes its RectTransform to the device <see cref="Screen.safeArea"/> so portrait UI
/// never renders under notches, rounded corners, or the home indicator on phones/tablets.
/// Re-applies automatically when the resolution or orientation changes. On displays with no
/// insets (desktop, Editor Game view) the safe area equals the full screen, so it is a no-op.
/// Place this on the root "Safe Area" panel that holds the vertical zone stack.
/// </summary>
[RequireComponent(typeof(RectTransform))]
[DisallowMultipleComponent]
public sealed class SafeAreaFitter : MonoBehaviour
{
    private RectTransform rectTransform;
    private Rect lastSafeArea;
    private Vector2Int lastResolution;
    private ScreenOrientation lastOrientation;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        Apply();
    }

    private void Update()
    {
        // Cheap per-frame guard: only re-anchor when something actually changed.
        if (Screen.safeArea != lastSafeArea ||
            Screen.width != lastResolution.x ||
            Screen.height != lastResolution.y ||
            Screen.orientation != lastOrientation)
        {
            Apply();
        }
    }

    private void Apply()
    {
        if (rectTransform == null || Screen.width <= 0 || Screen.height <= 0) return;

        Rect safe = Screen.safeArea;

        Vector2 anchorMin = safe.position;
        Vector2 anchorMax = safe.position + safe.size;
        anchorMin.x /= Screen.width;
        anchorMin.y /= Screen.height;
        anchorMax.x /= Screen.width;
        anchorMax.y /= Screen.height;

        // Reject implausible values (some platforms report a zero rect for a frame on rotate).
        if (anchorMin.x < 0f || anchorMin.y < 0f || anchorMax.x > 1f || anchorMax.y > 1f) return;
        if (anchorMax.x <= anchorMin.x || anchorMax.y <= anchorMin.y) return;

        rectTransform.anchorMin = anchorMin;
        rectTransform.anchorMax = anchorMax;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        lastSafeArea = safe;
        lastResolution = new Vector2Int(Screen.width, Screen.height);
        lastOrientation = Screen.orientation;
    }
}
