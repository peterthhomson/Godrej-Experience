using UnityEngine;
using UnityEngine.UI;

namespace Godrej
{
    /// <summary>
    /// Attaches to Content inside a scroll row. Sets each child's actual
    /// sizeDelta so that exactly N items fit on screen. The rest scroll.
    /// </summary>
    [ExecuteInEditMode]
    public class DynamicRowSizer : MonoBehaviour
    {
        public int visibleItems = 3;
        private RectTransform viewportRT;

        void Awake()
        {
            if (transform.parent != null)
                viewportRT = transform.parent.GetComponent<RectTransform>();
        }

        void Update()
        {
            if (viewportRT == null)
            {
                if (transform.parent != null)
                    viewportRT = transform.parent.GetComponent<RectTransform>();
                if (viewportRT == null) return;
            }

            float viewW = viewportRT.rect.width;
            float viewH = viewportRT.rect.height;
            if (viewW <= 0 || viewH <= 0) return;

            // Calculate item width based on visible count + spacing
            var hlg = GetComponent<HorizontalLayoutGroup>();
            float spacing = hlg != null ? hlg.spacing : 10f;
            int padL = hlg != null ? hlg.padding.left : 0;
            int padR = hlg != null ? hlg.padding.right : 0;

            float usableW = viewW - padL - padR;
            float totalSpacing = spacing * Mathf.Max(0, visibleItems - 1);
            float itemWidth = (usableW - totalSpacing) / visibleItems;
            itemWidth = Mathf.Max(itemWidth, 80);

            float itemHeight = viewH - (hlg != null ? hlg.padding.top + hlg.padding.bottom : 0);
            itemHeight = Mathf.Max(itemHeight, 40);

            int childCount = transform.childCount;

            // Set the actual sizeDelta on each child button
            foreach (Transform child in transform)
            {
                var rt = child.GetComponent<RectTransform>();
                if (rt == null) continue;
                rt.sizeDelta = new Vector2(itemWidth, itemHeight);
            }

            // Set the content RectTransform width so the ScrollRect knows how far to scroll
            var contentRT = GetComponent<RectTransform>();
            float totalWidth = padL + padR
                + childCount * itemWidth
                + spacing * Mathf.Max(0, childCount - 1);
            contentRT.sizeDelta = new Vector2(totalWidth, contentRT.sizeDelta.y);
        }
    }
}
