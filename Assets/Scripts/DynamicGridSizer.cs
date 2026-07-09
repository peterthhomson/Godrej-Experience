using UnityEngine;
using UnityEngine.UI;

namespace Godrej
{
    /// <summary>
    /// Sizes grid cells dynamically so that a specific number of columns 
    /// are visible at a time within the viewport.
    /// Works perfectly with horizontal ScrollRects + ContentSizeFitter.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(GridLayoutGroup))]
    public class DynamicGridSizer : MonoBehaviour
    {
        [Header("Visible Layout")]
        public int visibleColumns = 3;
        public int totalRows = 1;

        private GridLayoutGroup grid;
        private RectTransform viewportRT;
        private float lastWidth, lastHeight;

        void Awake()
        {
            grid = GetComponent<GridLayoutGroup>();
            // In a standard ScrollRect setup, the Content's parent is the Viewport
            if (transform.parent != null)
                viewportRT = transform.parent.GetComponent<RectTransform>();
        }

        void Update()
        {
            if (grid == null || viewportRT == null) return;

            float viewW = viewportRT.rect.width;
            float viewH = viewportRT.rect.height;

            if (viewW <= 0 || viewH <= 0) return;
            if (Mathf.Approximately(viewW, lastWidth) && Mathf.Approximately(viewH, lastHeight)) return;

            lastWidth = viewW;
            lastHeight = viewH;

            // Calculate cell height based on available viewport height
            float usableH = viewH - grid.padding.top - grid.padding.bottom;
            float spacingY = grid.spacing.y * Mathf.Max(0, totalRows - 1);
            float cellH = totalRows > 0 ? (usableH - spacingY) / totalRows : usableH;

            // Calculate cell width to show exactly N columns
            float usableW = viewW - grid.padding.left - grid.padding.right;
            float spacingX = grid.spacing.x * Mathf.Max(0, visibleColumns - 1);
            float cellW = visibleColumns > 0 ? (usableW - spacingX) / visibleColumns : usableW;

            // Enforce minimum sizes so they don't break
            cellH = Mathf.Max(cellH, 40);
            cellW = Mathf.Max(cellW, 60);

            grid.cellSize = new Vector2(cellW, cellH);
        }
    }
}
