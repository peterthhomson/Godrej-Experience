using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Godrej.Editor
{
    /// <summary>
    /// Scene-view authoring tool for finished panorama marker images. Each PNG can
    /// contain its own room text, icon and left/right arrow.
    /// </summary>
    public sealed class PanoramaLabelPlacementWindow : EditorWindow
    {
        private LocalExperienceManager manager;
        private SerializedObject managerSerializedObject;
        private int sourceIndex;
        private Texture2D markerImage;
        private float userStartViewRotation;
        private int loadedStartViewSource = -1;
        private float placementDistance = 2.8f;
        private float imageHeight = 0.65f;
        private bool isPlacing;
        private RenderTexture headsetPreviewTexture;
        private float previewYaw;
        private float previewPitch;
        private bool isDraggingPreview;
        private Vector2 scrollPosition;

        [MenuItem("Godrej/14. Place Panorama Image Labels", priority = 9)]
        private static void Open()
        {
            GetWindow<PanoramaLabelPlacementWindow>("Panorama Images");
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGui;
            FindManager();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGui;
            isPlacing = false;
            ReleaseHeadsetPreviewTexture();
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnHierarchyChange()
        {
            if (manager == null) FindManager();
            Repaint();
        }

        private void OnGUI()
        {
            Event current = Event.current;
            if (isPlacing && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                isPlacing = false;
                current.Use();
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawContents();
            EditorGUILayout.EndScrollView();
        }

        private void DrawContents()
        {
            EditorGUILayout.LabelField("Panorama Image Placement", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Place one finished PNG containing the room text, icon and arrow. " +
                "Markers are visual-only and do not react to VR controllers.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            LocalExperienceManager selectedManager = (LocalExperienceManager)EditorGUILayout.ObjectField(
                "Experience Manager", manager, typeof(LocalExperienceManager), true);
            if (EditorGUI.EndChangeCheck()) SetManager(selectedManager);

            if (manager == null)
            {
                if (GUILayout.Button("Find Experience Manager")) FindManager();
                return;
            }

            string[] roomNames = GetRoomNames();
            if (roomNames.Length == 0)
            {
                EditorGUILayout.HelpBox("The Experience Manager has no panorama materials.", MessageType.Warning);
                return;
            }

            sourceIndex = Mathf.Clamp(sourceIndex, 0, roomNames.Length - 1);

            EditorGUILayout.Space(4f);
            int newSourceIndex = EditorGUILayout.Popup("Current Panorama", sourceIndex, roomNames);
            if (newSourceIndex != sourceIndex)
            {
                sourceIndex = newSourceIndex;
                previewYaw = 0f;
                previewPitch = 0f;
                LoadUserStartView();
                PreviewSourceRoom();
            }

            if (loadedStartViewSource != sourceIndex) LoadUserStartView();
            DrawUserStartViewControls();

            markerImage = (Texture2D)EditorGUILayout.ObjectField(
                "Finished Label Image", markerImage, typeof(Texture2D), false);
            placementDistance = EditorGUILayout.Slider("Distance From Viewer", placementDistance, 1.5f, 8f);
            imageHeight = EditorGUILayout.Slider("Image Height", imageHeight, 0.2f, 1.5f);

            DrawImagePreview(markerImage);

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview Current Panorama")) PreviewSourceRoom();
                if (GUILayout.Button("Scene View From Viewer")) CenterSceneView();
            }

            Color originalBackground = GUI.backgroundColor;
            GUI.backgroundColor = isPlacing ? new Color(1f, 0.75f, 0.3f) : new Color(0.55f, 1f, 0.65f);
            if (GUILayout.Button(isPlacing ? "CANCEL PLACEMENT" : "PLACE IMAGE", GUILayout.Height(36f)))
            {
                if (isPlacing) isPlacing = false;
                else BeginPlacement();
            }
            GUI.backgroundColor = originalBackground;

            if (isPlacing)
            {
                EditorGUILayout.HelpBox(
                    "Click directly inside HEADSET VIEW below, or click in the Scene view. Press Esc to cancel.",
                    MessageType.Warning);
            }

            DrawHeadsetPreview();

            DrawPlacedImages();
        }

        private void DrawUserStartViewControls()
        {
            Material material = GetPanoramaMaterial(sourceIndex);
            if (material == null || !material.HasProperty("_Rotation"))
            {
                EditorGUILayout.HelpBox(
                    "This panorama material does not expose a start-view rotation.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("USER START VIEW", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Reset the headset look to centre, then move this slider until the exact view the user should see first is centred.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();
            float editedRotation = EditorGUILayout.Slider(
                "Panorama Rotation",
                userStartViewRotation,
                0f,
                360f);
            if (EditorGUI.EndChangeCheck())
            {
                SetUserStartView(editedRotation);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("VIEW SAVED START"))
                {
                    previewYaw = 0f;
                    previewPitch = 0f;
                    PreviewSourceRoom();
                }

                if (GUILayout.Button("RESET ROTATION TO 0°"))
                {
                    SetUserStartView(0f);
                    previewYaw = 0f;
                    previewPitch = 0f;
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void LoadUserStartView()
        {
            Material material = GetPanoramaMaterial(sourceIndex);
            userStartViewRotation = material != null && material.HasProperty("_Rotation")
                ? Mathf.Clamp(material.GetFloat("_Rotation"), 0f, 360f)
                : 0f;
            loadedStartViewSource = sourceIndex;
        }

        private void SetUserStartView(float rotation)
        {
            Material material = GetPanoramaMaterial(sourceIndex);
            if (material == null || !material.HasProperty("_Rotation")) return;

            rotation = Mathf.Clamp(rotation, 0f, 360f);
            float previousRotation = material.GetFloat("_Rotation");
            float rotationDelta = Mathf.DeltaAngle(previousRotation, rotation);

            Undo.RecordObject(material, "Set Panorama User Start View");
            material.SetFloat("_Rotation", rotation);

            // The images are world-space markers attached to features in the panorama.
            // Keep the whole room group on the same bearing whenever the skybox turns.
            GameObject labelGroup = GetLabelGroup(sourceIndex);
            if (labelGroup != null && Mathf.Abs(rotationDelta) > 0.0001f)
            {
                // Skybox/Panoramic rotates its geometry, so visible image features move
                // opposite to the material's numeric rotation delta.
                RotateLabelImages(
                    labelGroup.transform,
                    GetViewerPosition(),
                    -rotationDelta,
                    "Align Labels With Panorama Start View");
                MarkSceneDirty();
            }

            userStartViewRotation = rotation;
            loadedStartViewSource = sourceIndex;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            if (RenderSettings.skybox != material) RenderSettings.skybox = material;
            SceneView.RepaintAll();
            Repaint();
        }

        private static void DrawImagePreview(Texture2D image)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Exact Image Preview", EditorStyles.miniBoldLabel);

            if (image == null)
            {
                EditorGUILayout.HelpBox("Drag one PNG from Assets/Godrej/Icons here.", MessageType.None);
                return;
            }

            float aspect = image.height > 0 ? (float)image.width / image.height : 1f;
            Rect previewRect = GUILayoutUtility.GetAspectRect(aspect, GUILayout.MaxHeight(120f));
            EditorGUI.DrawPreviewTexture(previewRect, image, null, ScaleMode.ScaleToFit);
        }

        private void DrawHeadsetPreview()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("HEADSET VIEW — NO HEADSET REQUIRED", EditorStyles.boldLabel);

            Camera viewerCamera = GetViewerCamera();
            if (viewerCamera == null)
            {
                EditorGUILayout.HelpBox(
                    "The Experience Manager has no Preview Camera assigned, so an exact viewer preview cannot be rendered.",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                previewYaw = EditorGUILayout.Slider("Look Left / Right", previewYaw, -180f, 180f);
                if (GUILayout.Button("Reset", GUILayout.Width(52f)))
                {
                    previewYaw = 0f;
                    previewPitch = 0f;
                }
            }
            previewPitch = EditorGUILayout.Slider("Look Up / Down", previewPitch, -80f, 80f);

            RenderHeadsetPreview(viewerCamera);

            Rect previewRect = GUILayoutUtility.GetAspectRect(16f / 9f, GUILayout.MaxHeight(300f));
            if (headsetPreviewTexture != null)
            {
                EditorGUI.DrawPreviewTexture(previewRect, headsetPreviewTexture, null, ScaleMode.ScaleToFit);
            }
            EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.y, previewRect.width, 2f),
                isPlacing ? new Color(1f, 0.65f, 0.15f) : new Color(0.78f, 0.65f, 0.34f));

            string overlay = isPlacing
                ? "CLICK HERE TO PLACE THE IMAGE"
                : "Drag to look around • This is the viewer's eye position";
            GUI.Label(
                new Rect(previewRect.x + 8f, previewRect.y + 7f, previewRect.width - 16f, 22f),
                overlay,
                EditorStyles.whiteBoldLabel);

            HandleHeadsetPreviewInput(previewRect, viewerCamera);

            Vector3 viewerPosition = GetViewerPosition();
            EditorGUILayout.LabelField(
                $"Viewer stands at X {viewerPosition.x:0.00}, Y {viewerPosition.y:0.00}, Z {viewerPosition.z:0.00}. " +
                "The preview camera is the placement origin.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void RenderHeadsetPreview(Camera viewerCamera)
        {
            EnsureHeadsetPreviewTexture();
            if (headsetPreviewTexture == null || viewerCamera == null) return;

            Transform cameraTransform = viewerCamera.transform;
            Quaternion originalRotation = cameraTransform.rotation;
            RenderTexture originalTarget = viewerCamera.targetTexture;

            try
            {
                cameraTransform.rotation = GetPreviewRotation();
                viewerCamera.targetTexture = headsetPreviewTexture;
                viewerCamera.Render();
            }
            finally
            {
                viewerCamera.targetTexture = originalTarget;
                cameraTransform.rotation = originalRotation;
            }
        }

        private void HandleHeadsetPreviewInput(Rect previewRect, Camera viewerCamera)
        {
            Event current = Event.current;
            bool inside = previewRect.Contains(current.mousePosition);

            if (isPlacing && inside && current.type == EventType.MouseDown && current.button == 0)
            {
                float x = Mathf.InverseLerp(previewRect.xMin, previewRect.xMax, current.mousePosition.x);
                float y = 1f - Mathf.InverseLerp(previewRect.yMin, previewRect.yMax, current.mousePosition.y);

                Transform cameraTransform = viewerCamera.transform;
                Quaternion originalRotation = cameraTransform.rotation;
                cameraTransform.rotation = GetPreviewRotation();
                Ray ray = viewerCamera.ViewportPointToRay(new Vector3(x, y, 0f));
                cameraTransform.rotation = originalRotation;

                PlaceImage(ray.direction.normalized);
                isPlacing = false;
                current.Use();
                Repaint();
                return;
            }

            if (!isPlacing && inside && current.type == EventType.MouseDown && current.button == 0)
            {
                isDraggingPreview = true;
                current.Use();
            }
            else if (isDraggingPreview && current.type == EventType.MouseDrag)
            {
                previewYaw = Mathf.Repeat(previewYaw + current.delta.x * 0.35f + 180f, 360f) - 180f;
                previewPitch = Mathf.Clamp(previewPitch - current.delta.y * 0.35f, -80f, 80f);
                current.Use();
                Repaint();
            }
            else if (isDraggingPreview && current.type == EventType.MouseUp)
            {
                isDraggingPreview = false;
                current.Use();
            }
        }

        private Quaternion GetPreviewRotation()
        {
            return Quaternion.Euler(previewPitch, previewYaw, 0f);
        }

        private void EnsureHeadsetPreviewTexture()
        {
            if (headsetPreviewTexture != null && headsetPreviewTexture.IsCreated()) return;

            ReleaseHeadsetPreviewTexture();
            headsetPreviewTexture = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32)
            {
                name = "Godrej Headset Label Preview",
                hideFlags = HideFlags.HideAndDontSave
            };
            headsetPreviewTexture.Create();
        }

        private void ReleaseHeadsetPreviewTexture()
        {
            if (headsetPreviewTexture == null) return;
            headsetPreviewTexture.Release();
            DestroyImmediate(headsetPreviewTexture);
            headsetPreviewTexture = null;
        }

        private void DrawPlacedImages()
        {
            GameObject group = GetLabelGroup(sourceIndex);
            if (group == null)
            {
                EditorGUILayout.HelpBox("No image group is assigned for this panorama.", MessageType.Error);
                return;
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Images In Current Panorama", EditorStyles.boldLabel);

            PanoramaDestinationLabel[] labels = group.GetComponentsInChildren<PanoramaDestinationLabel>(true);
            if (labels.Length == 0)
            {
                EditorGUILayout.LabelField("No images have been placed yet.");
            }

            for (int i = 0; i < labels.Length; i++)
            {
                PanoramaDestinationLabel label = labels[i];
                if (label == null) continue;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label.DisplayName, EditorStyles.boldLabel, GUILayout.MinWidth(100f));
                    if (GUILayout.Button("Select", GUILayout.Width(55f))) Selection.activeGameObject = label.gameObject;
                    if (GUILayout.Button("Face Viewer", GUILayout.Width(80f))) FaceViewer(label.transform);
                    if (GUILayout.Button("Delete", GUILayout.Width(55f)))
                    {
                        Undo.DestroyObjectImmediate(label.gameObject);
                        MarkSceneDirty();
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUI.BeginChangeCheck();
                Texture2D editedImage = (Texture2D)EditorGUILayout.ObjectField(
                    "Finished Image", label.SourceImage, typeof(Texture2D), false);
                float editedHeight = EditorGUILayout.Slider("Image Height", label.ImageHeight, 0.2f, 1.5f);
                if (EditorGUI.EndChangeCheck())
                {
                    UpdatePlacedImage(label, editedImage, editedHeight);
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(4f);
            if (GUILayout.Button("Remove Every Image In Current Panorama"))
            {
                if (EditorUtility.DisplayDialog(
                        "Remove panorama images?",
                        $"Remove every image under '{group.name}'? This can be undone with Ctrl+Z.",
                        "Remove", "Cancel"))
                {
                    for (int i = group.transform.childCount - 1; i >= 0; i--)
                    {
                        Undo.DestroyObjectImmediate(group.transform.GetChild(i).gameObject);
                    }

                    MarkSceneDirty();
                }
            }
        }

        private void BeginPlacement()
        {
            if (GetLabelGroup(sourceIndex) == null)
            {
                EditorUtility.DisplayDialog("Panorama Images", "This panorama has no assigned image group.", "OK");
                return;
            }

            if (markerImage == null)
            {
                EditorUtility.DisplayDialog(
                    "Panorama Images",
                    "Choose a finished PNG from Assets/Godrej/Icons first.",
                    "OK");
                return;
            }

            PreviewSourceRoom();
            isPlacing = true;
            SceneView.RepaintAll();
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            if (!isPlacing || manager == null) return;

            Event current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                isPlacing = false;
                current.Use();
                Repaint();
                return;
            }

            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 390f, 58f), "Place Panorama Image", "Window");
            GUILayout.Label("Click where the finished image should appear. Esc cancels.");
            GUILayout.EndArea();
            Handles.EndGUI();

            if (current.type != EventType.MouseDown || current.button != 0 || current.alt) return;
            if (current.mousePosition.x < 410f && current.mousePosition.y < 80f) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            PlaceImage(ray.direction.normalized);
            isPlacing = false;
            current.Use();
            Repaint();
        }

        private void PlaceImage(Vector3 worldDirection)
        {
            GameObject group = GetLabelGroup(sourceIndex);
            if (group == null || markerImage == null) return;

            Sprite sprite = EnsureSprite(markerImage, out Texture2D importedImage);
            if (sprite == null)
            {
                EditorUtility.DisplayDialog(
                    "Panorama Images",
                    "Unity could not import this image as a Sprite. Check that it is inside the Assets folder.",
                    "OK");
                return;
            }

            markerImage = importedImage;
            Vector3 origin = GetViewerPosition();
            Vector3 up = Mathf.Abs(Vector3.Dot(worldDirection, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Place Panorama Image");

            var markerObject = new GameObject($"Panorama Image - {markerImage.name}");
            Undo.RegisterCreatedObjectUndo(markerObject, "Place Panorama Image");
            markerObject.transform.SetParent(group.transform, true);
            markerObject.transform.position = origin + worldDirection * placementDistance;
            markerObject.transform.rotation = Quaternion.LookRotation(worldDirection, up);

            SpriteRenderer renderer = CreateImageRenderer(markerObject.transform);
            PanoramaDestinationLabel marker = Undo.AddComponent<PanoramaDestinationLabel>(markerObject);
            marker.Configure(markerImage.name, markerImage, sprite, renderer, imageHeight);

            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(group);
            Selection.activeGameObject = markerObject;
            Undo.CollapseUndoOperations(undoGroup);
            MarkSceneDirty();
            SceneView.RepaintAll();
        }

        private void UpdatePlacedImage(PanoramaDestinationLabel marker, Texture2D image, float height)
        {
            if (marker == null || image == null) return;

            Sprite sprite = EnsureSprite(image, out Texture2D importedImage);
            if (sprite == null) return;

            SpriteRenderer renderer = marker.GetComponentInChildren<SpriteRenderer>(true);
            if (renderer == null) renderer = CreateImageRenderer(marker.transform);

            RemoveLegacyComponents(marker);
            Undo.RecordObject(marker, "Edit Panorama Image");
            Undo.RecordObject(renderer, "Edit Panorama Image");
            Undo.RecordObject(renderer.transform, "Edit Panorama Image");

            marker.Configure(importedImage.name, importedImage, sprite, renderer, height);
            marker.gameObject.name = $"Panorama Image - {importedImage.name}";

            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(renderer);
            MarkSceneDirty();
            SceneView.RepaintAll();
        }

        private static Sprite EnsureSprite(Texture2D image, out Texture2D importedImage)
        {
            importedImage = image;
            if (image == null) return null;

            string path = AssetDatabase.GetAssetPath(image);
            if (string.IsNullOrWhiteSpace(path)) return null;

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return null;

            bool needsReimport = importer.textureType != TextureImporterType.Sprite ||
                                 importer.spriteImportMode != SpriteImportMode.Single ||
                                 !importer.alphaIsTransparency;
            if (needsReimport)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }

            importedImage = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null) return sprite;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < assets.Length; i++)
            {
                if (assets[i] is Sprite foundSprite) return foundSprite;
            }

            return null;
        }

        private static SpriteRenderer CreateImageRenderer(Transform parent)
        {
            var imageObject = new GameObject("Finished Label Image");
            Undo.RegisterCreatedObjectUndo(imageObject, "Create Panorama Image Renderer");
            imageObject.transform.SetParent(parent, false);

            SpriteRenderer renderer = Undo.AddComponent<SpriteRenderer>(imageObject);
            renderer.enabled = false;
            renderer.sortingOrder = 1;
            return renderer;
        }

        private void PreviewSourceRoom()
        {
            Material material = GetPanoramaMaterial(sourceIndex);
            if (material != null) RenderSettings.skybox = material;

            managerSerializedObject.Update();
            SerializedProperty labelsRootProperty = managerSerializedObject.FindProperty("labelsRoot");
            GameObject labelsRoot = labelsRootProperty != null
                ? labelsRootProperty.objectReferenceValue as GameObject
                : null;
            if (labelsRoot != null && !labelsRoot.activeSelf)
            {
                Undo.RecordObject(labelsRoot, "Show Panorama Images");
                labelsRoot.SetActive(true);
            }

            SerializedProperty groups = managerSerializedObject.FindProperty("perPanoramaLabelGroups");
            if (groups != null && groups.isArray)
            {
                for (int i = 0; i < groups.arraySize; i++)
                {
                    GameObject group = groups.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                    if (group == null || group.activeSelf == (i == sourceIndex)) continue;
                    Undo.RecordObject(group, "Preview Panorama Images");
                    group.SetActive(i == sourceIndex);
                }
            }

            MarkSceneDirty();
            SceneView.RepaintAll();
        }

        private void CenterSceneView()
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null) return;

            Quaternion viewRotation = GetPreviewRotation();
            sceneView.pivot = GetViewerPosition() + viewRotation * Vector3.forward * 0.05f;
            sceneView.rotation = viewRotation;
            sceneView.size = 0.05f;
            sceneView.orthographic = false;
            sceneView.Repaint();
        }

        private void FaceViewer(Transform marker)
        {
            if (marker == null) return;

            Vector3 origin = GetViewerPosition();
            Vector3 direction = (marker.position - origin).normalized;
            if (direction.sqrMagnitude < 0.001f) return;

            Undo.RecordObject(marker, "Face Panorama Image Toward Viewer");
            marker.rotation = Quaternion.LookRotation(direction, Vector3.up);
            MarkSceneDirty();
        }

        private Camera GetViewerCamera()
        {
            if (managerSerializedObject == null) return null;
            managerSerializedObject.Update();
            SerializedProperty cameraProperty = managerSerializedObject.FindProperty("previewCamera");
            return cameraProperty != null ? cameraProperty.objectReferenceValue as Camera : null;
        }

        private Vector3 GetViewerPosition()
        {
            Camera viewerCamera = GetViewerCamera();
            if (viewerCamera != null) return viewerCamera.transform.position;

            GameObject group = GetLabelGroup(sourceIndex);
            Transform root = group != null ? group.transform.parent : null;
            Vector3 floorOrigin = root != null ? root.position : Vector3.zero;
            return floorOrigin + Vector3.up * 1.4f;
        }

        private string[] GetRoomNames()
        {
            int count = manager != null ? manager.PanoramaCount : 0;
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = manager.GetPanoramaName(i);
            return names;
        }

        private Material GetPanoramaMaterial(int index)
        {
            if (managerSerializedObject == null) return null;
            managerSerializedObject.Update();
            SerializedProperty materials = managerSerializedObject.FindProperty("panoramaMaterials");
            if (materials == null || !materials.isArray || index < 0 || index >= materials.arraySize) return null;
            return materials.GetArrayElementAtIndex(index).objectReferenceValue as Material;
        }

        private GameObject GetLabelGroup(int index)
        {
            if (managerSerializedObject == null) return null;
            managerSerializedObject.Update();
            SerializedProperty groups = managerSerializedObject.FindProperty("perPanoramaLabelGroups");
            if (groups == null || !groups.isArray || index < 0 || index >= groups.arraySize) return null;
            return groups.GetArrayElementAtIndex(index).objectReferenceValue as GameObject;
        }

        private void FindManager()
        {
            SetManager(Object.FindAnyObjectByType<LocalExperienceManager>(FindObjectsInactive.Include));
        }

        private void SetManager(LocalExperienceManager value)
        {
            manager = value;
            managerSerializedObject = manager != null ? new SerializedObject(manager) : null;
            NormalizeLabelGroupsPreservingImages();
            sourceIndex = 0;
            loadedStartViewSource = -1;
            previewYaw = 0f;
            previewPitch = 0f;
            RemoveLegacyInteractionFromMarkers();
            Repaint();
        }

        /// <summary>
        /// Bakes any old accumulated room-group rotation into the image transforms,
        /// preserving their exact visible positions while returning the group to a
        /// stable identity transform.
        /// </summary>
        private void NormalizeLabelGroupsPreservingImages()
        {
            if (managerSerializedObject == null || EditorApplication.isPlayingOrWillChangePlaymode) return;

            managerSerializedObject.Update();
            SerializedProperty groups = managerSerializedObject.FindProperty("perPanoramaLabelGroups");
            if (groups == null || !groups.isArray) return;

            bool changed = false;
            for (int i = 0; i < groups.arraySize; i++)
            {
                GameObject groupObject = groups.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                if (groupObject == null) continue;

                Transform group = groupObject.transform;
                bool alreadyNormalized = group.localPosition.sqrMagnitude < 0.000001f &&
                                         Quaternion.Angle(group.localRotation, Quaternion.identity) < 0.001f &&
                                         (group.localScale - Vector3.one).sqrMagnitude < 0.000001f;
                if (alreadyNormalized) continue;

                int childCount = group.childCount;
                var children = new Transform[childCount];
                var worldPositions = new Vector3[childCount];
                var worldRotations = new Quaternion[childCount];
                for (int childIndex = 0; childIndex < childCount; childIndex++)
                {
                    Transform child = group.GetChild(childIndex);
                    children[childIndex] = child;
                    worldPositions[childIndex] = child.position;
                    worldRotations[childIndex] = child.rotation;
                    Undo.RecordObject(child, "Bake Panorama Label Group Transform");
                }

                Undo.RecordObject(group, "Normalize Panorama Label Group");
                group.localPosition = Vector3.zero;
                group.localRotation = Quaternion.identity;
                group.localScale = Vector3.one;
                EditorUtility.SetDirty(group);

                for (int childIndex = 0; childIndex < childCount; childIndex++)
                {
                    children[childIndex].SetPositionAndRotation(
                        worldPositions[childIndex],
                        worldRotations[childIndex]);
                    EditorUtility.SetDirty(children[childIndex]);
                }

                changed = true;
            }

            if (!changed) return;

            MarkSceneDirty();
            Scene scene = SceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.SaveScene(scene);
        }

        private static void RotateLabelImages(
            Transform group,
            Vector3 viewerPosition,
            float degrees,
            string undoName)
        {
            for (int i = 0; i < group.childCount; i++)
            {
                Transform image = group.GetChild(i);
                Undo.RecordObject(image, undoName);
                image.RotateAround(viewerPosition, Vector3.up, degrees);
                EditorUtility.SetDirty(image);
            }
        }

        private void RemoveLegacyInteractionFromMarkers()
        {
            PanoramaDestinationLabel[] labels = Object.FindObjectsByType<PanoramaDestinationLabel>(
                FindObjectsInactive.Include);
            bool changed = false;

            for (int i = 0; i < labels.Length; i++)
            {
                changed |= RemoveLegacyComponents(labels[i]);
            }

            if (managerSerializedObject != null)
            {
                managerSerializedObject.Update();
                SerializedProperty rootProperty = managerSerializedObject.FindProperty("labelsRoot");
                GameObject root = rootProperty != null ? rootProperty.objectReferenceValue as GameObject : null;
                if (root != null)
                {
                    TextMeshPro[] oldTexts = root.GetComponentsInChildren<TextMeshPro>(true);
                    for (int i = 0; i < oldTexts.Length; i++)
                    {
                        TextMeshPro oldText = oldTexts[i];
                        if (oldText == null) continue;

                        PanoramaDestinationLabel marker = oldText.GetComponentInParent<PanoramaDestinationLabel>();
                        if (marker != null && oldText.gameObject == marker.gameObject)
                        {
                            Undo.DestroyObjectImmediate(oldText);
                        }
                        else
                        {
                            Undo.DestroyObjectImmediate(oldText.gameObject);
                        }
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                MarkSceneDirty();
                Scene scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorSceneManager.SaveScene(scene);
                }
            }
        }

        private static bool RemoveLegacyComponents(PanoramaDestinationLabel marker)
        {
            if (marker == null) return false;
            bool changed = false;

            XRSimpleInteractable interactable = marker.GetComponent<XRSimpleInteractable>();
            if (interactable != null)
            {
                Undo.DestroyObjectImmediate(interactable);
                changed = true;
            }

            BoxCollider collider = marker.GetComponent<BoxCollider>();
            if (collider != null)
            {
                Undo.DestroyObjectImmediate(collider);
                changed = true;
            }

            TextMeshPro oldText = marker.GetComponent<TextMeshPro>();
            if (oldText != null)
            {
                Undo.DestroyObjectImmediate(oldText);
                changed = true;
            }

            return changed;
        }

        private static void MarkSceneDirty()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
