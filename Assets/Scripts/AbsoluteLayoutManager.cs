using UnityEngine;
using UnityEngine.UI;

namespace Godrej
{
    /// <summary>
    /// Total manual control over your UI.
    /// Define the exact X, Y, Width, and Height for each zone.
    /// Origin (0,0) is the Top-Left of the screen. Y goes negative downwards.
    /// 
    /// Enable "Full Width" to automatically stretch a zone edge-to-edge
    /// regardless of device aspect ratio (recommended for portrait consistency).
    /// </summary>
    [ExecuteInEditMode]
    public class AbsoluteLayoutManager : MonoBehaviour
    {
        [Header("UI Elements (Auto-assigned)")]
        [Tooltip("Make sure you assign the ScrollView container here, NOT the Content object inside it.")]
        public RectTransform header;
        public RectTransform viewport;
        public RectTransform roomGrid;
        public RectTransform slider;
        public RectTransform navDock;

        [Header("Auto-stretch width to fill screen")]
        [Tooltip("When ON, the X is treated as left padding and Width is ignored — each zone stretches edge to edge. This ensures the layout looks the same on all portrait devices.")]
        public bool fullWidth = true;
        [Tooltip("Left/right margin when fullWidth is ON")]
        public float horizontalMargin = 0f;

        [Header("1. Header")]
        public float headerX = 0f;
        public float headerY = 0f;
        public float headerWidth = 1080f;
        public float headerHeight = 120f;

        [Header("2. VR Viewport")]
        public float viewportX = 0f;
        public float viewportY = -120f;
        public float viewportWidth = 1080f;
        public float viewportHeight = 900f;

        [Header("3. Room Grid (Buttons)")]
        public float gridX = 0f;
        public float gridY = -1050f;
        public float gridWidth = 1080f;
        public float gridHeight = 550f;

        [Header("4. Slider")]
        public float sliderX = 0f;
        public float sliderY = -1600f;
        public float sliderWidth = 1080f;
        public float sliderHeight = 100f;

        [Header("5. Nav Dock")]
        public float dockX = 0f;
        public float dockY = -1720f;
        public float dockWidth = 1080f;
        public float dockHeight = 150f;

        private Vector2 _lastScreenSize;

        void OnValidate()
        {
            FindReferences();
        }

        void Awake()
        {
            FindReferences();
        }

        void Start()
        {
            ApplyLayout();
            _lastScreenSize = new Vector2(Screen.width, Screen.height);
        }

        public void FindReferences()
        {
            if (header == null) header = FindChildByName(transform, "Header");
            if (viewport == null) viewport = FindChildByName(transform, "Viewport");
            if (roomGrid == null) roomGrid = FindChildByName(transform, "Room Grid") ?? FindChildByName(transform, "Zone 3");
            if (slider == null) slider = FindChildByName(transform, "Start View") ?? FindChildByName(transform, "Slider");
            if (navDock == null) navDock = FindChildByName(transform, "Nav Dock") ?? FindChildByName(transform, "Dock");
        }

        private RectTransform FindChildByName(Transform parent, string nameToFind)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.Contains(nameToFind))
                    return child.GetComponent<RectTransform>();
            }
            return null;
        }

        void Update()
        {
            // In the Unity Editor, update continuously so you can visually design in real-time
            if (!Application.isPlaying)
            {
                ApplyLayout();
                return;
            }

            // At runtime, ONLY update the layout if the physical screen resolution changes.
            // This prevents the script from fighting the ScrollRect every frame (fixes the elasticity).
            Vector2 currentScreenSize = new Vector2(Screen.width, Screen.height);
            if (currentScreenSize != _lastScreenSize)
            {
                ApplyLayout();
                _lastScreenSize = currentScreenSize;
            }
        }

        private void ApplyLayout()
        {
            // Remove layout groups on parents that fight us
            if (header != null && header.parent != transform) RemoveLayoutGroups(header.parent);
            if (roomGrid != null && roomGrid.parent != transform) RemoveLayoutGroups(roomGrid.parent);
            if (slider != null && slider.parent != transform) RemoveLayoutGroups(slider.parent);
            if (navDock != null && navDock.parent != transform) RemoveLayoutGroups(navDock.parent);

            // Get actual canvas width for full-width mode
            float canvasWidth = GetComponent<RectTransform>().rect.width;

            if (header != null) SetRect(header, headerX, headerY, headerWidth, headerHeight, canvasWidth);
            if (viewport != null) SetRect(viewport, viewportX, viewportY, viewportWidth, viewportHeight, canvasWidth);
            if (roomGrid != null) SetRect(roomGrid, gridX, gridY, gridWidth, gridHeight, canvasWidth);
            if (slider != null) SetRect(slider, sliderX, sliderY, sliderWidth, sliderHeight, canvasWidth);
            if (navDock != null) SetRect(navDock, dockX, dockY, dockWidth, dockHeight, canvasWidth);
        }

        private void SetRect(RectTransform target, float x, float y, float w, float h, float canvasWidth)
        {
            if (fullWidth && canvasWidth > 0)
            {
                // Stretch edge-to-edge with optional margin
                target.anchorMin = new Vector2(0, 1);
                target.anchorMax = new Vector2(1, 1); // Right edge anchored to parent right
                target.pivot = new Vector2(0.5f, 1);
                target.offsetMin = new Vector2(horizontalMargin, -h + y); // left, bottom
                target.offsetMax = new Vector2(-horizontalMargin, y);     // right, top
            }
            else
            {
                // Manual pixel positioning
                target.anchorMin = new Vector2(0, 1);
                target.anchorMax = new Vector2(0, 1);
                target.pivot = new Vector2(0, 1);
                target.anchoredPosition = new Vector2(x, y);
                target.sizeDelta = new Vector2(w, h);
            }

            // Disable LayoutElement if it exists so it doesn't fight
            var le = target.GetComponent<LayoutElement>();
            if (le != null) le.enabled = false;
        }

        private void RemoveLayoutGroups(Transform targetParent)
        {
            if (targetParent == null || targetParent == transform) return;
            var vlg = targetParent.GetComponent<VerticalLayoutGroup>();
            if (vlg != null) vlg.enabled = false;
            var hlg = targetParent.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.enabled = false;
            var csf = targetParent.GetComponent<ContentSizeFitter>();
            if (csf != null) csf.enabled = false;
        }
    }
}

