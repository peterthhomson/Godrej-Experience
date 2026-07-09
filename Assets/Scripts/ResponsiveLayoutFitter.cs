using UnityEngine;
using UnityEngine.UI;

namespace Godrej
{
    /// <summary>
    /// Dynamically resizes the Bottom Panel (Room Grid + Slider + Nav Dock) to fill
    /// the remaining screen space after the Header and Viewport have claimed their share.
    /// This ensures all 14 buttons + slider + dock always fit on any phone screen.
    /// Attach this to the "Safe Area" GameObject.
    /// </summary>
    [ExecuteInEditMode]
    public class ResponsiveLayoutFitter : MonoBehaviour
    {
        [Header("Zone References")]
        [Tooltip("Zone 1 - Header (fixed height)")]
        public RectTransform header;

        [Tooltip("Zone 2 - VR Viewport (fixed height based on aspect ratio)")]
        public RectTransform viewport;

        [Tooltip("Bottom Panel containing the grid, slider, and dock")]
        public RectTransform bottomPanel;

        [Header("Layout Settings")]
        [Tooltip("Minimum height for the viewport (in reference pixels)")]
        public float minViewportHeight = 300f;

        [Tooltip("Maximum percentage of screen the viewport can take (0-1)")]
        [Range(0.1f, 0.6f)]
        public float maxViewportPercent = 0.35f;

        private RectTransform safeArea;
        private float lastScreenHeight;

        void Awake()
        {
            safeArea = GetComponent<RectTransform>();
        }

        void Update()
        {
            if (safeArea == null || header == null || viewport == null || bottomPanel == null)
                return;

            float totalHeight = safeArea.rect.height;
            if (totalHeight <= 0 || Mathf.Approximately(totalHeight, lastScreenHeight))
                return;

            lastScreenHeight = totalHeight;
            RearrangeLayout(totalHeight);
        }

        void RearrangeLayout(float totalHeight)
        {
            float spacing = 18f; // matches the Safe Area VerticalLayoutGroup spacing
            float padding = 24f; // top + bottom padding

            // Header: fixed height, anchored to top
            float headerHeight = header.sizeDelta.y; // 180
            header.anchorMin = new Vector2(0, 1);
            header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1f);
            header.anchoredPosition = new Vector2(0, -padding);
            header.sizeDelta = new Vector2(0, headerHeight);

            // Calculate remaining space for viewport + bottom panel
            float usedByHeader = padding + headerHeight + spacing;
            float remaining = totalHeight - usedByHeader - padding; // subtract bottom padding too

            // Viewport: takes up to maxViewportPercent of total, but at least minViewportHeight
            float viewportHeight = Mathf.Min(totalHeight * maxViewportPercent, remaining * 0.4f);
            viewportHeight = Mathf.Max(viewportHeight, minViewportHeight);

            viewport.anchorMin = new Vector2(0, 1);
            viewport.anchorMax = new Vector2(1, 1);
            viewport.pivot = new Vector2(0.5f, 1f);
            viewport.anchoredPosition = new Vector2(0, -(usedByHeader + viewportHeight * 0.5f));
            // Use stretch anchoring instead for cleaner positioning
            float viewportTop = usedByHeader;
            float viewportBottom = totalHeight - viewportTop - viewportHeight;
            viewport.anchorMin = new Vector2(0, viewportBottom / totalHeight);
            viewport.anchorMax = new Vector2(1, (totalHeight - viewportTop) / totalHeight);
            viewport.offsetMin = new Vector2(0, 0);
            viewport.offsetMax = new Vector2(0, 0);

            // Bottom Panel: fills the rest
            float bottomTop = usedByHeader + viewportHeight + spacing;
            float bottomHeight = totalHeight - bottomTop - padding;
            if (bottomHeight < 200) bottomHeight = 200; // safety minimum

            bottomPanel.anchorMin = new Vector2(0, 0);
            bottomPanel.anchorMax = new Vector2(1, 0);
            bottomPanel.pivot = new Vector2(0.5f, 0f);
            bottomPanel.anchoredPosition = new Vector2(0, padding);
            bottomPanel.sizeDelta = new Vector2(0, bottomHeight);
        }
    }
}
