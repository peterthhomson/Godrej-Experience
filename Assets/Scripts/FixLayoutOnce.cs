using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Godrej.UI
{
    [ExecuteAlways]
    public class FixLayoutOnce : MonoBehaviour
    {
        [Header("Live Layout Adjustments")]
        [Tooltip("Slide to adjust the vertical split between the 3D room and bottom UI.")]
        [Range(0.2f, 0.8f)]
        public float viewportSplitRatio = 0.5f;

        [Header("UI References (Auto-Assigned)")]
        public RectTransform viewport;
        public RectTransform roomGrid;

        private float _lastSplitRatio = -1f;

        // --- LIVE SLIDER LOGIC (Runs in Editor and Runtime) ---
        void Update()
        {
            if (Application.isPlaying) return;

            if (Mathf.Abs(viewportSplitRatio - _lastSplitRatio) > 0.001f)
            {
                if (viewport == null || roomGrid == null) FindReferences();
                ApplyLiveSplit();
                _lastSplitRatio = viewportSplitRatio;
            }
        }

        private void FindReferences()
        {
            if (viewport == null) viewport = FindChildByName(transform, "Viewport")?.GetComponent<RectTransform>();
            if (roomGrid == null) roomGrid = (FindChildByName(transform, "Room Grid") ?? FindChildByName(transform, "Zone 3"))?.GetComponent<RectTransform>();
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

        private void ApplyLiveSplit()
        {
            if (viewport != null)
            {
                viewport.anchorMin = new Vector2(0f, viewportSplitRatio);
                viewport.offsetMin = new Vector2(0f, 0f);
            }
            if (roomGrid != null)
            {
                roomGrid.anchorMax = new Vector2(1f, viewportSplitRatio);
                roomGrid.offsetMax = new Vector2(0f, -40f);
            }
        }

#if UNITY_EDITOR
        // --- ONE-TIME SETUP LOGIC (Strictly Editor Only) ---
        [MenuItem("Godrej/Fix Phone Layout (Run Once)")]
        public static void Fix()
        {
            var safeArea = GameObject.Find("Safe Area");
            if (safeArea == null) return;

            Undo.RegisterFullObjectHierarchyUndo(safeArea.transform.root.gameObject, "Fix Phone Layout");

            // Attach THIS script to Safe Area so the slider is immediately available
            var liveEditor = safeArea.GetComponent<FixLayoutOnce>();
            if (liveEditor == null) liveEditor = Undo.AddComponent<FixLayoutOnce>(safeArea);
            float currentSplit = liveEditor.viewportSplitRatio;

            // 1. Reset Canvas Scaler to Match Width (0). 
            var canvasScaler = safeArea.GetComponentInParent<CanvasScaler>();
            if (canvasScaler != null)
            {
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = new Vector2(1080, 1920);
                canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                canvasScaler.matchWidthOrHeight = 0f; 
            }

            // Remove legacy custom scripts if they exist
            var oldAbsolute = safeArea.GetComponent("AbsoluteLayoutManager");
            if (oldAbsolute != null) Undo.DestroyObjectImmediate(oldAbsolute);

            // Clean & Stretch Parent Containers
            DestroyLayoutComponents(safeArea);
            ResetToStretch(safeArea); 

            Transform bottomPanel = safeArea.transform.Find("Bottom Panel");
            if (bottomPanel != null) 
            {
                DestroyLayoutComponents(bottomPanel.gameObject);
                ResetToStretch(bottomPanel.gameObject); 
            }
            
            Transform gridContainer = null;
            if (bottomPanel != null)
            {
                gridContainer = bottomPanel.Find("bOTTOM gRID");
                if (gridContainer != null) 
                {
                    DestroyLayoutComponents(gridContainer.gameObject);
                    ResetToStretch(gridContainer.gameObject); 
                }
            }

            // Get the 5 zones
            Transform header = FindChildByNameStatic(safeArea.transform, "Header");
            Transform viewport = FindChildByNameStatic(safeArea.transform, "Viewport");
            Transform roomGrid = FindChildByNameStatic(safeArea.transform, "Room Grid") ?? FindChildByNameStatic(safeArea.transform, "Zone 3");
            Transform slider = FindChildByNameStatic(safeArea.transform, "Start View") ?? FindChildByNameStatic(safeArea.transform, "Slider");
            Transform navDock = FindChildByNameStatic(safeArea.transform, "Nav Dock") ?? FindChildByNameStatic(safeArea.transform, "Dock");

            // --- 3. APPLY PERFECT HYBRID ANCHORS (Percentages + Pixels) ---
            
            // Header: Locked Absolute Top
            if (header != null)
            {
                var rt = header.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(0.5f, 1);
                rt.offsetMin = new Vector2(0, -120); 
                rt.offsetMax = new Vector2(0, 0);    
            }

            // Viewport: Top percentage of screen based on Slider
            if (viewport != null)
            {
                var rt = viewport.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, currentSplit); 
                rt.anchorMax = new Vector2(1, 1); 
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(0, 0);    
                rt.offsetMax = new Vector2(0, -120); 
            }

            // Nav Dock: Locked Absolute Bottom
            if (navDock != null)
            {
                var rt = navDock.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 0);
                rt.anchorMax = new Vector2(1, 0);
                rt.pivot = new Vector2(0.5f, 0);
                rt.offsetMin = new Vector2(0, 0); 
                rt.offsetMax = new Vector2(0, 150); 

                var hlg = navDock.GetComponent<HorizontalLayoutGroup>();
                if (hlg == null) hlg = Undo.AddComponent<HorizontalLayoutGroup>(navDock.gameObject);
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = true;
                hlg.childForceExpandHeight = true;
                hlg.spacing = 10;
                hlg.padding = new RectOffset(10, 10, 10, 10);
            }

            // Slider: Locked above Nav Dock 
            if (slider != null)
            {
                var rt = slider.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 0);
                rt.anchorMax = new Vector2(1, 0);
                rt.pivot = new Vector2(0.5f, 0);
                rt.offsetMin = new Vector2(40, 180); 
                rt.offsetMax = new Vector2(-40, 280); 
            }

            // Room Grid: Bottom percentage of screen up to Viewport Split
            if (roomGrid != null)
            {
                var rt = roomGrid.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 0);
                rt.anchorMax = new Vector2(1, currentSplit); 
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(0, 320);
                rt.offsetMax = new Vector2(0, -40); 
                
                // --- Room Grid Internal Scrolling Setup ---
                var oldVLG = roomGrid.GetComponent<VerticalLayoutGroup>();
                if (oldVLG != null) Undo.DestroyObjectImmediate(oldVLG);
                var oldGLG = roomGrid.GetComponent<GridLayoutGroup>();
                if (oldGLG != null) Undo.DestroyObjectImmediate(oldGLG);
                var scrollComp = roomGrid.GetComponent<ScrollRect>();
                if (scrollComp != null) Undo.DestroyObjectImmediate(scrollComp);
                var oldSGF = roomGrid.GetComponent("SquareGridFitter");
                if (oldSGF != null) Undo.DestroyObjectImmediate(oldSGF);

                var maskImg = roomGrid.GetComponent<Image>();
                if (maskImg != null) Undo.DestroyObjectImmediate(maskImg);
                var mask = roomGrid.GetComponent<Mask>();
                if (mask != null) Undo.DestroyObjectImmediate(mask);

                List<Transform> allButtons = new List<Transform>();
                foreach (Button btn in roomGrid.GetComponentsInChildren<Button>(true))
                {
                    if (!allButtons.Contains(btn.transform)) allButtons.Add(btn.transform);
                }

                foreach(var btn in allButtons) btn.SetParent(roomGrid.parent, false);

                foreach (Transform child in roomGrid.Cast<Transform>().ToArray())
                {
                    if (child.name == "Row 1" || child.name == "Row 2" || child.name == "Content")
                    {
                        Undo.DestroyObjectImmediate(child.gameObject);
                    }
                }

                var rgVLG = roomGrid.gameObject.AddComponent<VerticalLayoutGroup>();
                rgVLG.padding = new RectOffset(0, 0, 0, 0);
                rgVLG.spacing = 20; 
                rgVLG.childForceExpandWidth = true;
                rgVLG.childForceExpandHeight = true;
                rgVLG.childControlWidth = true;
                rgVLG.childControlHeight = true;

                if (allButtons.Count > 0)
                {
                    Transform row1 = BuildScrollRow(roomGrid, "Row 1");
                    Transform row2 = BuildScrollRow(roomGrid, "Row 2");
                    row1.SetSiblingIndex(0);
                    row2.SetSiblingIndex(1);

                    Transform content1 = row1.Find("Content");
                    Transform content2 = row2.Find("Content");

                    int half = Mathf.CeilToInt(allButtons.Count / 2f);
                    for (int i = 0; i < allButtons.Count; i++)
                    {
                        var btn = allButtons[i];
                        btn.SetParent(i < half ? content1 : content2, false);
                        var btnLE = btn.GetComponent<LayoutElement>();
                        if (btnLE == null) btnLE = Undo.AddComponent<LayoutElement>(btn.gameObject);
                        btnLE.flexibleHeight = 1; 
                        var btnRT = btn.GetComponent<RectTransform>();
                        btnRT.anchorMin = new Vector2(0, 1);
                        btnRT.anchorMax = new Vector2(0, 1);
                        btnRT.pivot = new Vector2(0, 1);
                    }
                }
            }

            EditorUtility.SetDirty(safeArea);
            Debug.Log("[FixLayout] Setup Complete. Click on 'Safe Area' to adjust the layout live using the slider!");
        }

        private static Transform FindChildByNameStatic(Transform parent, string nameToFind)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.Contains(nameToFind))
                    return child;
            }
            return null;
        }

        private static void DestroyLayoutComponents(GameObject go)
        {
            if (go == null) return;
            var le = go.GetComponent<LayoutElement>(); if (le != null) Undo.DestroyObjectImmediate(le);
            var vlg = go.GetComponent<VerticalLayoutGroup>(); if (vlg != null) Undo.DestroyObjectImmediate(vlg);
            var hlg = go.GetComponent<HorizontalLayoutGroup>(); if (hlg != null) Undo.DestroyObjectImmediate(hlg);
            var glg = go.GetComponent<GridLayoutGroup>(); if (glg != null) Undo.DestroyObjectImmediate(glg);
            var csf = go.GetComponent<ContentSizeFitter>(); if (csf != null) Undo.DestroyObjectImmediate(csf);
            var rlf = go.GetComponent("ResponsiveLayoutFitter"); if (rlf != null) Undo.DestroyObjectImmediate(rlf);
        }

        private static void ResetToStretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Transform BuildScrollRow(Transform parent, string name)
        {
            Transform row = parent.Find(name);
            if (row == null)
            {
                var rowGO = new GameObject(name, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(rowGO, "Create Row");
                row = rowGO.transform;
                row.SetParent(parent, false);
            }
            ResetToStretch(row.gameObject);

            var maskImg = row.GetComponent<Image>();
            if (maskImg == null) maskImg = Undo.AddComponent<Image>(row.gameObject);
            maskImg.color = new Color(0, 0, 0, 0.01f);
            var mask = row.GetComponent<Mask>();
            if (mask == null) mask = Undo.AddComponent<Mask>(row.gameObject);
            mask.showMaskGraphic = false;

            Transform content = row.Find("Content");
            if (content == null)
            {
                var contentGO = new GameObject("Content", typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(contentGO, "Create Content");
                content = contentGO.transform;
                content.SetParent(row, false);
            }

            var contentRT = content.GetComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0, 0);
            contentRT.anchorMax = new Vector2(0, 1);
            contentRT.pivot = new Vector2(0, 0.5f);
            contentRT.offsetMin = Vector2.zero;
            contentRT.offsetMax = Vector2.zero;

            var hlg = content.GetComponent<HorizontalLayoutGroup>();
            if (hlg == null) hlg = Undo.AddComponent<HorizontalLayoutGroup>(content.gameObject);
            hlg.childControlWidth = false; 
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.spacing = 15;
            hlg.padding = new RectOffset(15, 15, 5, 5); 
            hlg.childAlignment = TextAnchor.MiddleCenter;

            var csf = content.GetComponent<ContentSizeFitter>();
            if (csf == null) csf = Undo.AddComponent<ContentSizeFitter>(content.gameObject);
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            var drs = content.GetComponent("DynamicRowSizer");
            if (drs != null) Undo.DestroyObjectImmediate(drs); // Remove old dependencies 

            var scroll = row.GetComponent<ScrollRect>();
            if (scroll == null) scroll = Undo.AddComponent<ScrollRect>(row.gameObject);
            scroll.content = contentRT;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.viewport = row.GetComponent<RectTransform>();

            return row;
        }
#endif
    }
}