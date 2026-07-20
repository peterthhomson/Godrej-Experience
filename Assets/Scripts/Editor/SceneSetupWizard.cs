using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Godrej.Editor
{
    /// <summary>
    /// One-click project setup:
    ///   Godrej > 1. Import Panoramas + Floor Plan   — copies the renders from "Godrej Panos"
    ///        into Assets, applies Quest-friendly import settings and creates one
    ///        Skybox/Panoramic material per render.
    ///   Godrej > 2. Generate Presentation Scene     — builds the complete two-device scene
    ///        (NetworkManager, XR Origin, preview camera, salesman UI, quest connect panel,
    ///        floating labels) with every Inspector reference and button already wired.
    /// </summary>
    public static class SceneSetupWizard
    {
        private const string PanoramaFolder = "Assets/Godrej/Panoramas";
        private const string MaterialFolder = "Assets/Godrej/PanoramaMaterials";
        private const string UiFolder = "Assets/Godrej/UI";
        private const string ScenePath = "Assets/Scenes/Presentation.unity";

        // ---- palette -------------------------------------------------------------
        private static readonly Color ColBackground = Hex("#12161C");
        private static readonly Color ColPanel = Hex("#1B222B");
        private static readonly Color ColCard = Hex("#0C0F13");
        private static readonly Color ColButton = Hex("#2A3441");
        private static readonly Color ColButtonHover = Hex("#39465A");
        private static readonly Color ColAccent = Hex("#C8A557");
        private static readonly Color ColText = Hex("#EDF1F6");
        private static readonly Color ColTextMuted = Hex("#93A0B0");

        // =====================================================================
        //  MENU 1 — IMPORT PANORAMAS
        // =====================================================================

        [MenuItem("Godrej/1. Import Panoramas + Floor Plan", priority = 1)]
        public static void ImportPanoramas()
        {
            string sourceRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Godrej Panos");
            if (!Directory.Exists(sourceRoot))
            {
                sourceRoot = EditorUtility.OpenFolderPanel("Select folder containing the 360 renders", "", "");
                if (string.IsNullOrEmpty(sourceRoot)) return;
            }

            string[] images = Directory.GetFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (images.Length == 0)
            {
                EditorUtility.DisplayDialog("Godrej Setup", $"No images found under:\n{sourceRoot}", "OK");
                return;
            }

            EnsureFolder(PanoramaFolder);
            EnsureFolder(MaterialFolder);
            EnsureFolder(UiFolder);

            int panoramaCount = 0, planCount = 0;

            foreach (string source in images)
            {
                string fileName = Path.GetFileName(source);
                bool isFloorPlan = fileName.ToLowerInvariant().Contains("plan");
                string destFolder = isFloorPlan ? UiFolder : PanoramaFolder;
                string destPath = $"{destFolder}/{fileName}";

                File.Copy(source, destPath, overwrite: true);
                AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);
                ConfigureTexture(destPath, isFloorPlan);

                if (isFloorPlan) planCount++;
                else
                {
                    CreatePanoramaMaterial(destPath);
                    panoramaCount++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Godrej Setup",
                $"Imported {panoramaCount} panorama(s) and {planCount} floor plan(s).\n\n" +
                "Materials created in:\n" + MaterialFolder +
                "\n\nNow run  Godrej > 2. Generate Presentation Scene", "OK");
        }

        private static void ConfigureTexture(string assetPath, bool isFloorPlan)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) return;

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;

            if (isFloorPlan)
            {
                // The plan board is a single small texture mounted at a steep angle in world
                // space (viewed from above, tilted) — NOT flat screen UI. That combination
                // (oblique viewing + thin lines/small text) is exactly what block compression
                // (ASTC/ETC2) and disabled mipmaps ruin: compression blurs fine linework, and
                // without mips an obliquely-viewed texture blurs/aliases from minification.
                // One uncompressed 4K image costs ~64MB VRAM — trivial next to 14 compressed
                // panoramas — so there is no real tradeoff here.
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = true;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 16; // keeps text/lines sharp at the board's tilt angle
                importer.wrapMode = TextureWrapMode.Clamp;
            }
            else
            {
                // Photographic panoramas: compression artifacts are invisible in a photo and
                // memory matters a lot more with 14 of these loaded. 4096 keeps all rooms
                // comfortably inside Quest 3 memory once ASTC-compressed.
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = true;
                importer.wrapModeU = TextureWrapMode.Repeat; // 360° horizontal seam must wrap
                importer.wrapModeV = TextureWrapMode.Clamp;  // avoid pole bleeding
            }

            importer.SaveAndReimport();
        }

        private static void CreatePanoramaMaterial(string texturePath)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null) return;

            string cleanName = Path.GetFileNameWithoutExtension(texturePath).Replace('_', ' ').Trim();
            string materialPath = $"{MaterialFolder}/{cleanName}.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Skybox/Panoramic"));
                AssetDatabase.CreateAsset(material, materialPath);
            }

            material.shader = Shader.Find("Skybox/Panoramic");
            material.SetTexture("_MainTex", texture);
            material.SetFloat("_Mapping", 1f);    // Latitude Longitude layout
            material.SetFloat("_ImageType", 0f);  // 360 degrees
            material.SetFloat("_Exposure", 1f);
            material.SetFloat("_Rotation", 0f);
            EditorUtility.SetDirty(material);
        }

        // =====================================================================
        //  MENU 2 — GENERATE SCENE
        // =====================================================================

        [MenuItem("Godrej/2. Generate Presentation Scene", priority = 2)]
        public static void GenerateScene()
        {
            Material[] panoramas = LoadPanoramaMaterials();
            if (panoramas.Length == 0)
            {
                if (!EditorUtility.DisplayDialog("Godrej Setup",
                    "No panorama materials found.\nRun 'Godrej > 1. Import Panoramas + Floor Plan' first.\n\nGenerate an empty scene anyway?",
                    "Generate Anyway", "Cancel")) return;
            }

            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Godrej Setup — WARNING",
                "Assets/Scenes/Presentation.unity already exists.\n\n" +
                "Regenerating REPLACES THE WHOLE SCENE, including any manual changes you made " +
                "(rearranged salesman canvas, moved labels, repositioned board).\n\n" +
                "For small updates use the other Godrej menu items instead, or commit the scene " +
                "to git first so you can recover it.\n\nReplace everything?", "Replace Everything", "Cancel"))
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- networking -------------------------------------------------
            var networkManagerGO = new GameObject("NetworkManager");
            var networkManager = networkManagerGO.AddComponent<NetworkManager>();
            var transport = networkManagerGO.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0");
            networkManager.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                TickRate = 60 // low-latency LAN cadence
            };

            var experienceGO = new GameObject("Experience Manager");
            experienceGO.AddComponent<NetworkObject>();
            var experience = experienceGO.AddComponent<LocalExperienceManager>();

            var setupGO = new GameObject("Network Setup");
            var setup = setupGO.AddComponent<NetworkSetup>();

            // ---- XR rig (Quest) ---------------------------------------------
            // Saved inactive: NetworkSetup activates it on the Quest at startup. This keeps
            // exactly one AudioListener active in edit mode and on the desktop host.
            GameObject xrOriginGO = CreateXrOrigin(out Camera xrCamera);
            xrOriginGO.SetActive(false);

            // ---- host rig (salesman) ----------------------------------------
            var hostRig = new GameObject("Host Rig");

            var backdropGO = new GameObject("Backdrop Camera");
            backdropGO.transform.SetParent(hostRig.transform, false);
            var backdrop = backdropGO.AddComponent<Camera>();
            backdrop.clearFlags = CameraClearFlags.SolidColor;
            backdrop.backgroundColor = ColCard;
            backdrop.cullingMask = 0;
            backdrop.depth = -10;
            backdropGO.AddComponent<AudioListener>();

            var previewGO = new GameObject("Preview Camera");
            previewGO.transform.SetParent(hostRig.transform, false);
            previewGO.transform.localPosition = new Vector3(0f, 1.4f, 0f); // panorama centre, eye height
            var previewCamera = previewGO.AddComponent<Camera>();
            previewCamera.clearFlags = CameraClearFlags.Skybox;
            previewCamera.fieldOfView = 65f;
            previewCamera.nearClipPlane = 0.05f;
            previewCamera.depth = -5;

            // ---- salesman canvas ---------------------------------------------
            SalesmanUi ui = BuildSalesmanCanvas(hostRig.transform, panoramas);

            // ---- quest connect panel (saved inactive; NetworkSetup enables it on Quest)
            QuestUi questUi = BuildQuestConnectPanel(setup);
            questUi.root.SetActive(false);

            // ---- floating labels ----------------------------------------------
            GameObject labelsRoot = BuildFloatingLabels(panoramas, out GameObject[] labelGroups);

            // ---- VR floor plan board (floats near the customer's legs) ---------
            GameObject floorPlanBoard = BuildFloorPlanBoard();

            // ---- event system --------------------------------------------------
            // XRUIInputModule (XRI) instead of InputSystemUIInputModule: it ships built-in
            // mouse/touch pointer actions that need no asset wiring (survives scene reloads,
            // unlike AssignDefaultActions' transient references), and it is the module XRI
            // ray interactors require for clicking world-space UI on the Quest.
            var eventSystemGO = new GameObject("EventSystem");
            eventSystemGO.AddComponent<EventSystem>();
            var uiInputModule = eventSystemGO.AddComponent<XRUIInputModule>();
            uiInputModule.enableBuiltinActionsAsFallback = true;
            uiInputModule.enableXRInput = true;
            uiInputModule.enableMouseInput = true;
            uiInputModule.enableTouchInput = true;

            // ---- XR interaction manager -----------------------------------------
            // Required by the ray interactors on the XR rig for their UI-click plumbing
            // to reach TrackedDeviceGraphicRaycaster on the Quest connect panel.
            var interactionManagerGO = new GameObject("XR Interaction Manager");
            interactionManagerGO.AddComponent<XRInteractionManager>();

            // ---- initial skybox -------------------------------------------------
            if (panoramas.Length > 0) RenderSettings.skybox = panoramas[0];

            // ---- wire LocalExperienceManager ------------------------------------
            var expSO = new SerializedObject(experience);
            SetObjectArray(expSO, "panoramaMaterials", panoramas);
            SetObjectArray(expSO, "phonePanoramaButtons", ui.roomButtons.ToArray());
            SetRef(expSO, "labelsRoot", labelsRoot);
            SetObjectArray(expSO, "perPanoramaLabelGroups", labelGroups);
            SetRef(expSO, "previewCamera", previewCamera);
            SetRef(expSO, "previewImage", ui.previewImage);
            SetRef(expSO, "xrHeadTransform", xrCamera.transform);
            if (floorPlanBoard != null) SetRef(expSO, "floorPlanBoard", floorPlanBoard);
            SetRef(expSO, "startViewSlider", ui.startViewSlider);
            expSO.ApplyModifiedPropertiesWithoutUndo();

            // ---- wire NetworkSetup ------------------------------------------------
            var setupSO = new SerializedObject(setup);
            SetRef(setupSO, "transport", transport);
            SetRef(setupSO, "hostRoot", hostRig);
            SetRef(setupSO, "xrOrigin", xrOriginGO);
            SetRef(setupSO, "questConnectPanel", questUi.root);
            SetRef(setupSO, "startHostButton", ui.startHostButton);
            SetRef(setupSO, "statusText", ui.statusText);
            SetRef(setupSO, "ipDisplayText", ui.ipText);
            SetRef(setupSO, "questIpInputField", questUi.ipInput);
            SetRef(setupSO, "connectButton", questUi.connectButton);
            SetRef(setupSO, "questStatusText", questUi.statusText);
            setupSO.ApplyModifiedPropertiesWithoutUndo();

            // ---- persistent button wiring -------------------------------------------
            UnityEventTools.AddPersistentListener(ui.startHostButton.onClick, new UnityAction(setup.StartHost));
            UnityEventTools.AddPersistentListener(questUi.connectButton.onClick, new UnityAction(setup.ConnectManually));
            UnityEventTools.AddPersistentListener(ui.labelsToggle.onValueChanged, new UnityAction<bool>(experience.SetLabelsVisible));
            UnityEventTools.AddPersistentListener(ui.planToggle.onValueChanged, new UnityAction<bool>(experience.SetFloorPlanVisible));
            UnityEventTools.AddPersistentListener(ui.startViewSlider.onValueChanged, new UnityAction<float>(experience.SetStartViewRotation));
            for (int i = 0; i < ui.roomButtons.Count; i++)
            {
                UnityEventTools.AddIntPersistentListener(ui.roomButtons[i].onClick, experience.SetPanorama, i);
            }

            // ---- save + register ---------------------------------------------------------
            EnsureFolder("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            RegisterSceneInBuildSettings();
            PlayerSettings.Android.forceInternetPermission = true; // sockets need it on Quest

            // Project-wide orientation MUST stay Landscape Left: Meta Quest's OpenXR build
            // validation hard-rejects Portrait/AutoRotation and fails the Android build before
            // Gradle even runs (easy to break by accident — Player Settings has a plain
            // "Portrait" checkbox that looks like it only affects 2D platforms, but it doesn't).
            // Dedicated presenter builds temporarily override this setting: the salesman
            // APK enables Android rotation and swaps between its phone and TV canvases.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

            Selection.activeGameObject = experienceGO;
            EditorUtility.DisplayDialog("Godrej Setup",
                $"Presentation scene generated with {panoramas.Length} room(s).\n\n" +
                "• Press Play in the editor and click Start Host to test the salesman side.\n" +
                "• Build the SAME scene for Android to deploy on the Quest 3.\n" +
                "• The same APK also works on an Android phone — it auto-detects: " +
                "headset = customer viewer, phone = salesman presenter.\n" +
                "• The Quest finds the host automatically over the local network.\n\n" +
                "IMPORTANT: if the app is already installed on the Quest, you MUST rebuild and " +
                "reinstall it now (Build And Run). Regenerating the scene changes internal network " +
                "IDs, so an old headset build will connect and instantly disconnect in a loop.", "OK");
        }

        // =====================================================================
        //  SALESMAN UI
        // =====================================================================

        private struct SalesmanUi
        {
            public Button startHostButton;
            public Toggle labelsToggle;
            public Toggle planToggle;
            public Slider startViewSlider;
            public TextMeshProUGUI statusText;
            public TextMeshProUGUI ipText;
            public RawImage previewImage;
            public List<Button> roomButtons;
        }

        // Portrait mobile layout (phone/tablet salesman device):
        //   Canvas > Background (edge-to-edge) + Safe Area (VerticalLayoutGroup)
        //     Zone 1  Header        (fixed 170px, HorizontalLayoutGroup)
        //     Zone 2  VR Viewport   (flexible, 4:3 AspectRatioFitter + reticle)
        //     Zone 3  Room Grid     (flexible, VerticalLayoutGroup of HorizontalLayoutGroup rows)
        //     Zone 4  Nav Dock      (fixed 140px, HorizontalLayoutGroup)
        // Every zone height is driven by a LayoutElement so it scales fluidly on any portrait aspect.
        private static SalesmanUi BuildSalesmanCanvas(Transform parent, Material[] panoramas)
        {
            var ui = new SalesmanUi { roomButtons = new List<Button>() };

            // ---- canvas + portrait scaler ---------------------------------------
            var canvasGO = new GameObject("Salesman Canvas");
            canvasGO.transform.SetParent(parent, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);            // portrait phone reference
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;                                  // balance across phone/tablet aspects
            canvasGO.AddComponent<GraphicRaycaster>();

            // Edge-to-edge dark backdrop, behind the safe area so notch margins stay dark.
            Image backdrop = CreatePanel(canvas.transform, "Background", ColBackground);
            backdrop.raycastTarget = true; // absorb taps that miss a control
            Stretch(backdrop.rectTransform, Vector2.zero, Vector2.one);

            // ---- safe area root: holds the vertical zone stack ------------------
            var safeAreaGO = new GameObject("Safe Area", typeof(RectTransform));
            safeAreaGO.transform.SetParent(canvas.transform, false);
            var safeRect = (RectTransform)safeAreaGO.transform;
            Stretch(safeRect, Vector2.zero, Vector2.one);
            safeAreaGO.AddComponent<SafeAreaFitter>();

            var rootStack = safeAreaGO.AddComponent<VerticalLayoutGroup>();
            rootStack.padding = new RectOffset(24, 24, 24, 24);
            rootStack.spacing = 18f;
            rootStack.childControlWidth = true;
            rootStack.childControlHeight = true;
            rootStack.childForceExpandWidth = true;   // zones fill the width
            rootStack.childForceExpandHeight = false;  // heights come from each zone's LayoutElement

            BuildHeader(safeRect);                                     // Zone 1
            ui.previewImage = BuildViewport(safeRect);                 // Zone 2
            BuildRoomGrid(safeRect, panoramas, ui.roomButtons);        // Zone 3
            BuildNavDock(safeRect, out ui.startHostButton,            // Zone 4
                out ui.labelsToggle, out ui.statusText, out ui.ipText,
                out ui.planToggle, out ui.startViewSlider);

            return ui;
        }

        // ---- ZONE 1: HEADER --------------------------------------------------
        private static void BuildHeader(Transform parent)
        {
            Image header = CreatePanel(parent, "Zone 1 - Header", ColPanel);
            AddZoneHeight(header.gameObject, 170f);

            var hlg = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(28, 28, 12, 12);
            hlg.spacing = 16f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleCenter;

            // Left brand/settings glyph (decorative).
            Image left = CreateIcon(header.transform, "Settings Icon", "UI/Skin/UISprite.psd", ColButton);
            AddFixedWidth(left.gameObject, 90f);

            // Center title (auto-sizes so it never clips on narrow phones).
            var title = CreateText(header.transform, "Title", "GODREJ VERSOVA", 40f, ColAccent,
                TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.enableAutoSizing = true;
            title.fontSizeMax = 40f;
            title.fontSizeMin = 18f;
            AddFlexibleWidth(title.gameObject, 1f);

            // Right profile glyph (decorative).
            Image right = CreateIcon(header.transform, "Profile Icon", "UI/Skin/Knob.psd", ColButton);
            AddFixedWidth(right.gameObject, 90f);
        }

        // ---- ZONE 2: VR VIEWPORT (16:9, edge-to-edge) ------------------------
        private static RawImage BuildViewport(Transform parent)
        {
            Image container = CreatePanel(parent, "Zone 2 - VR Viewport", ColCard);
            // No LayoutElement: this fitter reports height = width * 9/16 to the parent
            // VerticalLayoutGroup, so the zone is exactly as tall as a full-width 16:9
            // view needs — no letterboxing, no dead panel space, at any resolution.
            container.gameObject.AddComponent<ViewportHeightFitter>().heightPerWidth = 9f / 16f;

            // The live view fills the whole zone edge-to-edge (RT is 16:9 to match).
            var viewGO = new GameObject("VR View (16:9)", typeof(RectTransform));
            viewGO.transform.SetParent(container.transform, false);
            Stretch((RectTransform)viewGO.transform, Vector2.zero, Vector2.one);
            var view = viewGO.AddComponent<RawImage>();
            view.color = Color.black; // becomes the live RenderTexture at runtime

            // Caption overlays the video like a broadcast badge instead of using up a row.
            TextMeshProUGUI caption = CreateText(viewGO.transform, "Caption", "LIVE · CUSTOMER VIEW",
                20f, ColTextMuted, TextAlignmentOptions.Top);
            var capRect = caption.rectTransform;
            capRect.anchorMin = new Vector2(0f, 1f);
            capRect.anchorMax = new Vector2(1f, 1f);
            capRect.pivot = new Vector2(0.5f, 1f);
            capRect.sizeDelta = new Vector2(0f, 34f);
            capRect.anchoredPosition = new Vector2(0f, -8f);

            // Reticle: dead-centre gaze indicator.
            Image reticle = CreateIcon(viewGO.transform, "Reticle", "UI/Skin/Knob.psd",
                new Color(1f, 1f, 1f, 0.85f));
            var reticleRect = reticle.rectTransform;
            reticleRect.anchorMin = new Vector2(0.5f, 0.5f);
            reticleRect.anchorMax = new Vector2(0.5f, 0.5f);
            reticleRect.pivot = new Vector2(0.5f, 0.5f);
            reticleRect.sizeDelta = new Vector2(28f, 28f);
            reticleRect.anchoredPosition = Vector2.zero;

            return view;
        }

        // ---- ZONE 3: ROOM GRID (always-square buttons) ------------------------
        private static void BuildRoomGrid(Transform parent, Material[] panoramas, List<Button> roomButtons)
        {
            Image gridPanel = CreatePanel(parent, "Zone 3 - Room Grid", ColPanel);
            AddFlexibleZone(gridPanel.gameObject, 1f);

            // GridLayoutGroup + SquareGridFitter: the fitter recomputes a square cell size
            // whenever the panel resizes, so buttons never stretch on any screen/aspect.
            var grid = gridPanel.gameObject.AddComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(18, 18, 18, 18);
            grid.spacing = new Vector2(14f, 14f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            grid.childAlignment = TextAnchor.MiddleCenter;
            gridPanel.gameObject.AddComponent<SquareGridFitter>();

            for (int i = 0; i < panoramas.Length; i++)
            {
                string label = GetRoomDisplayName(panoramas[i], i);
                roomButtons.Add(CreateRoomButton(gridPanel.transform, $"Room {i:00}", label));
            }
        }

        // ---- ZONE 4: NAV DOCK ------------------------------------------------
        private static void BuildNavDock(Transform parent, out Button startHostButton,
            out Toggle labelsToggle, out TextMeshProUGUI statusText, out TextMeshProUGUI ipText,
            out Toggle planToggle, out Slider startViewSlider)
        {
            Image dock = CreatePanel(parent, "Zone 4 - Nav Dock", ColCard);
            AddZoneHeight(dock.gameObject, 140f);

            var hlg = dock.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(14, 14, 12, 12);
            hlg.spacing = 12f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            startHostButton = CreateDockButton(dock.transform, "Start Host", "START HOST", accent: true);
            statusText = CreateDockDisplay(dock.transform, "VR Status", "STATUS", "Ready", 1.7f);
            ipText = CreateDockDisplay(dock.transform, "Host IP", "HOST IP", "—", 1f);
            labelsToggle = CreateDockToggle(dock.transform, "Labels", "LABELS");
            planToggle = CreateDockToggle(dock.transform, "Plan Toggle", "PLAN");
            startViewSlider = CreateDockSlider(dock.transform, "Start View", "START VIEW");
        }

        // ---- room button: rounded square, icon over label -------------------
        // Sized by the parent GridLayoutGroup (kept square by SquareGridFitter).
        private static Button CreateRoomButton(Transform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"); // rounded corners
            image.type = Image.Type.Sliced;
            image.color = ColButton;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            colors.pressedColor = ColAccent;
            colors.selectedColor = Color.white;
            button.colors = colors;

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(8, 8, 12, 12);
            stack.spacing = 6f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childAlignment = TextAnchor.MiddleCenter;

            Image icon = CreateIcon(go.transform, "Icon", "UI/Skin/Knob.psd", ColAccent);
            var iconLE = icon.gameObject.AddComponent<LayoutElement>();
            iconLE.preferredWidth = 54f;
            iconLE.preferredHeight = 54f;
            iconLE.flexibleWidth = 0f;
            iconLE.flexibleHeight = 0f;

            TextMeshProUGUI text = CreateText(go.transform, "Label", label, 22f, ColText,
                TextAlignmentOptions.Center);
            text.enableAutoSizing = true;
            text.fontSizeMax = 22f;
            text.fontSizeMin = 10f;
            var textLE = text.gameObject.AddComponent<LayoutElement>();
            textLE.preferredHeight = 44f;
            textLE.flexibleHeight = 0f;

            return button;
        }

        // ---- dock cell: rounded bg + centred icon-over-label ----------------
        private static GameObject CreateDockCell(Transform parent, string name, string label,
            Color bgColor, Color iconColor, Color labelColor, out TextMeshProUGUI labelText, out Image bgImage)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            AddFlexibleWidth(go, 1f);

            bgImage = go.AddComponent<Image>();
            bgImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            bgImage.type = Image.Type.Sliced;
            bgImage.color = bgColor;

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(6, 6, 8, 8);
            stack.spacing = 4f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childAlignment = TextAnchor.MiddleCenter;

            Image icon = CreateIcon(go.transform, "Icon", "UI/Skin/Knob.psd", iconColor);
            var iconLE = icon.gameObject.AddComponent<LayoutElement>();
            iconLE.preferredWidth = 38f;
            iconLE.preferredHeight = 38f;
            iconLE.flexibleWidth = 0f;
            iconLE.flexibleHeight = 0f;

            labelText = CreateText(go.transform, "Label", label, 18f, labelColor, TextAlignmentOptions.Center);
            labelText.enableAutoSizing = true;
            labelText.fontSizeMax = 18f;
            labelText.fontSizeMin = 9f;
            var labelLE = labelText.gameObject.AddComponent<LayoutElement>();
            labelLE.preferredHeight = 36f;
            labelLE.flexibleHeight = 0f;

            return go;
        }

        private static Button CreateDockButton(Transform parent, string name, string label, bool accent)
        {
            Color bg = accent ? ColAccent : ColButton;
            Color icon = accent ? ColCard : ColAccent;
            Color text = accent ? ColCard : ColText;
            GameObject cell = CreateDockCell(parent, name, label, bg, icon, text,
                out TextMeshProUGUI labelText, out Image bgImage);
            if (accent) labelText.fontStyle = FontStyles.Bold;

            var button = cell.AddComponent<Button>();
            button.targetGraphic = bgImage;
            return button;
        }

        /// <summary>
        /// Read-only dock readout: small muted caption over a live value. Deliberately has
        /// NO background chip or icon so it cannot be mistaken for a pressable button.
        /// </summary>
        private static TextMeshProUGUI CreateDockDisplay(Transform parent, string name, string caption,
            string value, float widthWeight)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            AddFlexibleWidth(go, widthWeight);

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(6, 6, 12, 12);
            stack.spacing = 4f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childAlignment = TextAnchor.MiddleCenter;

            TextMeshProUGUI captionText = CreateText(go.transform, "Caption", caption, 14f, ColTextMuted,
                TextAlignmentOptions.Center);
            var capLE = captionText.gameObject.AddComponent<LayoutElement>();
            capLE.preferredHeight = 22f;
            capLE.flexibleHeight = 0f;

            TextMeshProUGUI valueText = CreateText(go.transform, "Value", value, 20f, ColText,
                TextAlignmentOptions.Center);
            valueText.enableAutoSizing = true;   // long status strings shrink to fit
            valueText.fontSizeMax = 20f;
            valueText.fontSizeMin = 9f;
            valueText.textWrappingMode = TextWrappingModes.Normal;
            var valLE = valueText.gameObject.AddComponent<LayoutElement>();
            valLE.flexibleHeight = 1f;

            return valueText; // live status / IP writes into this label
        }

        private static Toggle CreateDockToggle(Transform parent, string name, string label)
        {
            GameObject cell = CreateDockCell(parent, name, label, ColButton, ColAccent, ColText, out _, out Image bgImage);

            var toggle = cell.AddComponent<Toggle>();
            toggle.targetGraphic = bgImage;
            toggle.transition = Selectable.Transition.None;
            toggle.isOn = true;

            // Accent strip along the bottom edge, shown only while the toggle is on.
            var stripGO = new GameObject("On Accent", typeof(RectTransform));
            stripGO.transform.SetParent(cell.transform, false);
            var stripLE = stripGO.AddComponent<LayoutElement>();
            stripLE.ignoreLayout = true; // keep the VerticalLayoutGroup from arranging it
            var stripRect = (RectTransform)stripGO.transform;
            stripRect.anchorMin = new Vector2(0f, 0f);
            stripRect.anchorMax = new Vector2(1f, 0f);
            stripRect.pivot = new Vector2(0.5f, 0f);
            stripRect.sizeDelta = new Vector2(0f, 6f);
            stripRect.anchoredPosition = Vector2.zero;
            var strip = stripGO.AddComponent<Image>();
            strip.color = ColAccent;
            strip.raycastTarget = false;

            toggle.graphic = strip;
            return toggle;
        }

        // ---- layout-element helpers -----------------------------------------
        private static void AddZoneHeight(GameObject go, float fixedHeight)
        {
            if (!go.TryGetComponent(out LayoutElement le)) le = go.AddComponent<LayoutElement>();
            le.minHeight = fixedHeight;
            le.preferredHeight = fixedHeight;
            le.flexibleHeight = 0f;
        }

        private static void AddFlexibleZone(GameObject go, float weight)
        {
            if (!go.TryGetComponent(out LayoutElement le)) le = go.AddComponent<LayoutElement>();
            le.minHeight = 0f;
            le.preferredHeight = 0f;
            le.flexibleHeight = weight;
        }

        private static void AddFixedWidth(GameObject go, float width)
        {
            if (!go.TryGetComponent(out LayoutElement le)) le = go.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
        }

        private static void AddFlexibleWidth(GameObject go, float weight)
        {
            if (!go.TryGetComponent(out LayoutElement le)) le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = weight;
        }

        private static Image CreateIcon(Transform parent, string name, string spriteResource, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>(spriteResource);
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        // =====================================================================
        //  QUEST CONNECT PANEL (world space)
        // =====================================================================

        private struct QuestUi
        {
            public GameObject root;
            public TMP_InputField ipInput;
            public Button connectButton;
            public TextMeshProUGUI statusText;
        }

        private static QuestUi BuildQuestConnectPanel(NetworkSetup setup)
        {
            var ui = new QuestUi();

            var canvasGO = new GameObject("Quest Connect Panel");
            ui.root = canvasGO;
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Plain GraphicRaycaster only understands screen-space pointers (mouse/touch).
            // World-space UI clicked by an XR controller ray needs this XRI raycaster instead.
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rect = (RectTransform)canvasGO.transform;
            rect.sizeDelta = new Vector2(900f, 480f);
            rect.position = new Vector3(0f, 1.5f, 2.2f);
            rect.localScale = Vector3.one * 0.0018f; // ≈1.6 m wide at 2.2 m distance

            Image bg = CreatePanel(canvas.transform, "Background", ColPanel);
            Stretch(bg.rectTransform, Vector2.zero, Vector2.one);

            TextMeshProUGUI title = CreateText(bg.transform, "Title", "GODREJ EXPERIENCE", 52f, ColAccent,
                TextAlignmentOptions.Top);
            Stretch(title.rectTransform, new Vector2(0f, 0.78f), Vector2.one, new Vector4(30, 0, 30, 28));
            title.fontStyle = FontStyles.Bold;

            ui.statusText = CreateText(bg.transform, "Status", "Searching for presenter…", 34f, ColText,
                TextAlignmentOptions.Center);
            Stretch(ui.statusText.rectTransform, new Vector2(0.05f, 0.52f), new Vector2(0.95f, 0.78f));

            ui.ipInput = CreateInputField(bg.transform, "IP Input", "192.168.1.100");
            var ipRect = (RectTransform)ui.ipInput.transform;
            ipRect.anchorMin = new Vector2(0.09f, 0.24f);
            ipRect.anchorMax = new Vector2(0.55f, 0.42f);
            ipRect.offsetMin = Vector2.zero;
            ipRect.offsetMax = Vector2.zero;

            ui.connectButton = CreateButton(bg.transform, "Connect Button", "CONNECT", 30f);
            var btnRect = (RectTransform)ui.connectButton.transform;
            btnRect.anchorMin = new Vector2(0.60f, 0.24f);
            btnRect.anchorMax = new Vector2(0.91f, 0.42f);
            btnRect.offsetMin = Vector2.zero;
            btnRect.offsetMax = Vector2.zero;
            SetButtonAccent(ui.connectButton);

            TextMeshProUGUI hint = CreateText(bg.transform, "Hint",
                "Connects automatically when the presenter starts hosting.", 24f, ColTextMuted,
                TextAlignmentOptions.Bottom);
            Stretch(hint.rectTransform, new Vector2(0.05f, 0.04f), new Vector2(0.95f, 0.2f));

            return ui;
        }

        // =====================================================================
        //  XR ORIGIN + LABELS
        // =====================================================================

        // Unity's own validated rig: head tracking + both controllers already wired with
        // NearFar (ray) interactors, UI-click support, and real Quest/OpenXR input bindings
        // via the accompanying "XRI Default Input Actions" asset baked into the prefab.
        // Hand-rolling the equivalent controller/ray-interactor wiring from raw input
        // bindings is exactly the kind of thing that "compiles but silently does nothing
        // in VR" — reusing Unity's tested rig avoids that failure mode entirely.
        private const string XrRigPrefabPath =
            "Assets/Samples/XR Interaction Toolkit/3.4.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";

        private static GameObject CreateXrOrigin(out Camera xrCamera)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(XrRigPrefabPath);
            GameObject originGO;

            if (prefab != null)
            {
                originGO = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                originGO.name = "XR Origin (XR Rig)";
                xrCamera = originGO.GetComponentInChildren<Camera>(true);
            }
            else
            {
                Debug.LogWarning("[SceneSetupWizard] XR Interaction Toolkit 'Starter Assets' sample not found at:\n" +
                    XrRigPrefabPath + "\n" +
                    "Import it via Window > Package Manager > XR Interaction Toolkit > Samples, then regenerate " +
                    "the scene. Falling back to a camera-only rig — head tracking will work but NOTHING will be " +
                    "clickable in the headset (no controllers, no ray interactors).");
                originGO = CreateFallbackCameraOnlyRig(out xrCamera);
            }

            if (xrCamera == null)
            {
                Debug.LogError("[SceneSetupWizard] XR rig has no Camera component — head tracking will not work.");
            }
            else
            {
                xrCamera.gameObject.tag = "MainCamera";
            }

            return originGO;
        }

        /// <summary>Degraded rig used only if the XRI Starter Assets sample isn't imported.</summary>
        private static GameObject CreateFallbackCameraOnlyRig(out Camera xrCamera)
        {
            var originGO = new GameObject("XR Origin (VR)");

            var offsetGO = new GameObject("Camera Offset");
            offsetGO.transform.SetParent(originGO.transform, false);

            var cameraGO = new GameObject("Main Camera");
            cameraGO.transform.SetParent(offsetGO.transform, false);

            xrCamera = cameraGO.AddComponent<Camera>();
            xrCamera.clearFlags = CameraClearFlags.Skybox;
            xrCamera.nearClipPlane = 0.05f;
            cameraGO.AddComponent<AudioListener>();

            var poseDriver = cameraGO.AddComponent<TrackedPoseDriver>();
            poseDriver.positionInput = new InputActionProperty(new InputAction("Position",
                InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            poseDriver.rotationInput = new InputActionProperty(new InputAction("Rotation",
                InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
            poseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            poseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            var xrOrigin = originGO.AddComponent<XROrigin>();
            xrOrigin.Camera = xrCamera;
            xrOrigin.CameraFloorOffsetObject = offsetGO;
            xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
            xrOrigin.CameraYOffset = 1.4f;

            return originGO;
        }

        /// <summary>
        /// Makes ALL room buttons fit inside the grid panel by (re)arming the grid fitter
        /// with the currently authored cell proportions — nothing else in the layout is
        /// touched. Cells scale down uniformly just enough for every row to be visible.
        /// </summary>
        [MenuItem("Godrej/7. Fit All Room Buttons (keeps your layout)", priority = 5)]
        public static void FitRoomGrid()
        {
            GridLayoutGroup roomGrid = null;
            foreach (GridLayoutGroup candidate in Object.FindObjectsByType<GridLayoutGroup>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // The TV drawer grid sizes its own cells — this tool is for the phone grid.
                if (candidate.GetComponent<DynamicGridSizer>() != null) continue;

                // The room grid is the one full of buttons.
                if (candidate.GetComponentsInChildren<Button>(true).Length >= 8)
                {
                    roomGrid = candidate;
                    break;
                }
            }

            if (roomGrid == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup", "No room button grid found in the open scene.", "OK");
                return;
            }

            var fitter = roomGrid.GetComponent<SquareGridFitter>();
            if (fitter == null) fitter = roomGrid.gameObject.AddComponent<SquareGridFitter>();
            fitter.preferredCellSize = roomGrid.cellSize; // keep the authored card proportions
            fitter.enabled = true;

            EditorUtility.SetDirty(fitter);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("Godrej Setup",
                $"Room grid fitter armed on '{roomGrid.gameObject.name}' (cell proportions {roomGrid.cellSize.x:F0}×{roomGrid.cellSize.y:F0} preserved).\n\n" +
                "All rows now scale to fit. Save the scene (Ctrl+S), then rebuild the affected APK(s).", "OK");
        }

        /// <summary>
        /// Adds live status colors to the dock (red/green/yellow headset knob on START
        /// HOST with reconnect behavior, green/red knobs on the LABELS and PLAN toggles).
        /// Finds the existing controls by name and wires a DockStatusIndicator — nothing
        /// else in the layout is touched. Safe to run repeatedly.
        /// </summary>
        [MenuItem("Godrej/8. Add Dock Status Colors (keeps your layout)", priority = 6)]
        public static void AddDockStatusColors()
        {
            var setup = Object.FindFirstObjectByType<NetworkSetup>(FindObjectsInactive.Include);
            if (setup == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup", "No NetworkSetup found in the open scene.", "OK");
                return;
            }

            Button startHost = FindNamedComponent<Button>("Start Host");
            Toggle labels = FindNamedComponent<Toggle>("Labels");
            Toggle plan = FindNamedComponent<Toggle>("Plan Toggle");

            if (startHost == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup",
                    "Could not find the 'Start Host' button in the open scene (was it renamed?).", "OK");
                return;
            }

            GameObject dockGO = startHost.transform.parent != null
                ? startHost.transform.parent.gameObject
                : startHost.gameObject;

            var indicator = dockGO.GetComponent<DockStatusIndicator>();
            if (indicator == null) indicator = dockGO.AddComponent<DockStatusIndicator>();

            var so = new SerializedObject(indicator);
            SetRef(so, "networkSetup", setup);
            SetRef(so, "startHostButton", startHost);
            SetRef(so, "startHostIcon", FindChildComponent<Image>(startHost.transform, "Icon"));
            SetRef(so, "startHostLabel", FindChildComponent<TextMeshProUGUI>(startHost.transform, "Label"));
            if (labels != null)
            {
                SetRef(so, "labelsToggle", labels);
                SetRef(so, "labelsIcon", FindChildComponent<Image>(labels.transform, "Icon"));
            }
            if (plan != null)
            {
                SetRef(so, "planToggle", plan);
                SetRef(so, "planIcon", FindChildComponent<Image>(plan.transform, "Icon"));
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("Godrej Setup",
                "Dock status colors wired:\n" +
                "• START HOST knob — red: no headset, green: connected, yellow: lost (press to reconnect)\n" +
                "• LABELS / PLAN knobs — green: on, red: off\n" +
                (labels == null || plan == null ? "\nNote: some toggles were not found by name and were skipped.\n" : "") +
                "\nSave the scene (Ctrl+S), then rebuild the PHONE APK (menu 5).", "OK");
        }

        private static T FindNamedComponent<T>(string gameObjectName) where T : Component
        {
            foreach (T candidate in Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.gameObject.name == gameObjectName) return candidate;
            }
            return null;
        }

        private static T FindChildComponent<T>(Transform parent, string childName) where T : Component
        {
            foreach (T candidate in parent.GetComponentsInChildren<T>(true))
            {
                if (candidate.gameObject.name == childName) return candidate;
            }
            return null;
        }

        // =====================================================================
        //  MENU 9 — TV CANVAS (large landscape touch screens)
        //  Full-screen live customer view + a slide-up drawer (pull tab at the
        //  bottom edge) holding the room buttons and the presenter controls
        //  (Start Host / Labels / Plan — deliberately NO start-view slider: each
        //  room uses the start view baked into its material). NetworkSetup
        //  enables this canvas instead of the phone canvas on TV-class devices;
        //  the hand-arranged phone canvas is never touched.
        // =====================================================================

        private const string TvCanvasName = "TV Canvas";

        [MenuItem("Godrej/9. Create or Update TV Canvas (keeps your layout)", priority = 7)]
        public static void CreateOrUpdateTvCanvas()
        {
            var experience = Object.FindFirstObjectByType<LocalExperienceManager>(FindObjectsInactive.Include);
            var setup = Object.FindFirstObjectByType<NetworkSetup>(FindObjectsInactive.Include);
            if (experience == null || setup == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup",
                    "No LocalExperienceManager / NetworkSetup found in the open scene.\n" +
                    "Open Assets/Scenes/Presentation.unity first.", "OK");
                return;
            }

            int roomCount = experience.PanoramaCount;
            if (roomCount == 0)
            {
                EditorUtility.DisplayDialog("Godrej Setup",
                    "LocalExperienceManager has no panorama materials assigned — no room buttons to build.", "OK");
                return;
            }

            var setupSO = new SerializedObject(setup);
            GameObject hostRoot = setupSO.FindProperty("hostRoot").objectReferenceValue as GameObject;
            GameObject phoneCanvas = FindPhoneCanvas(hostRoot);

            // Idempotent: replace only a previously generated TV canvas.
            Scene scene = SceneManager.GetActiveScene();
            GameObject existing = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (existing != null) break;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == TvCanvasName) { existing = t.gameObject; break; }
                }
            }
            if (existing != null) Object.DestroyImmediate(existing);

            // ---- canvas + landscape scaler ------------------------------------
            // Parented under the host rig so the Quest deactivates it with everything else.
            var canvasGO = new GameObject(TvCanvasName);
            canvasGO.transform.SetParent(hostRoot != null ? hostRoot.transform : null, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);            // landscape TV reference
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // ---- full-screen live viewport --------------------------------------
            Image backdrop = CreatePanel(canvas.transform, "Background", ColBackground);
            backdrop.raycastTarget = true;
            Stretch(backdrop.rectTransform, Vector2.zero, Vector2.one);

            var viewGO = new GameObject("TV Viewport", typeof(RectTransform));
            viewGO.transform.SetParent(canvas.transform, false);
            var view = viewGO.AddComponent<RawImage>();
            view.color = Color.black;  // becomes the live RenderTexture at runtime
            view.raycastTarget = true; // absorb taps that miss a control

            // Never stretch the live image: the fitter keeps the texture's aspect and
            // scales it to cover the whole screen at ANY panel resolution or shape
            // (1080p/4K/8K, 16:9 or not). LocalExperienceManager sets the exact ratio
            // once the RenderTexture exists (sized to the panel, so normally zero crop).
            var viewFitter = viewGO.AddComponent<AspectRatioFitter>();
            viewFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            viewFitter.aspectRatio = 16f / 9f;

            // Overlays parent to the canvas (not the image) so cropping on odd aspect
            // ratios can never push them off-screen.
            TextMeshProUGUI caption = CreateText(canvas.transform, "Caption", "LIVE · CUSTOMER VIEW",
                22f, ColTextMuted, TextAlignmentOptions.TopLeft);
            var capRect = caption.rectTransform;
            capRect.anchorMin = new Vector2(0f, 1f);
            capRect.anchorMax = new Vector2(0f, 1f);
            capRect.pivot = new Vector2(0f, 1f);
            capRect.sizeDelta = new Vector2(480f, 34f);
            capRect.anchoredPosition = new Vector2(28f, -20f);

            Image reticle = CreateIcon(canvas.transform, "Reticle", "UI/Skin/Knob.psd",
                new Color(1f, 1f, 1f, 0.85f));
            var reticleRect = reticle.rectTransform;
            reticleRect.anchorMin = new Vector2(0.5f, 0.5f);
            reticleRect.anchorMax = new Vector2(0.5f, 0.5f);
            reticleRect.pivot = new Vector2(0.5f, 0.5f);
            reticleRect.sizeDelta = new Vector2(26f, 26f);
            reticleRect.anchoredPosition = Vector2.zero;

            // ---- slide-up drawer -------------------------------------------------
            const float DrawerHeight = 430f;

            Image drawerImage = CreatePanel(canvas.transform, "Menu Drawer", ColPanel);
            drawerImage.raycastTarget = true; // taps inside the drawer never reach the viewport
            var drawerRect = drawerImage.rectTransform;
            drawerRect.anchorMin = new Vector2(0f, 0f);
            drawerRect.anchorMax = new Vector2(1f, 0f);
            drawerRect.pivot = new Vector2(0.5f, 0f);
            drawerRect.sizeDelta = new Vector2(0f, DrawerHeight);
            drawerRect.anchoredPosition = Vector2.zero; // saved open; the controller animates it

            var drawerLayout = drawerImage.gameObject.AddComponent<HorizontalLayoutGroup>();
            drawerLayout.padding = new RectOffset(24, 24, 18, 18);
            drawerLayout.spacing = 20f;
            drawerLayout.childControlWidth = true;
            drawerLayout.childControlHeight = true;
            drawerLayout.childForceExpandWidth = false;
            drawerLayout.childForceExpandHeight = true;

            // ---- rooms section (left) --------------------------------------------
            Image roomsSection = CreatePanel(drawerImage.transform, "Rooms Section", ColCard);
            AddFlexibleWidth(roomsSection.gameObject, 1f);
            var roomsStack = roomsSection.gameObject.AddComponent<VerticalLayoutGroup>();
            roomsStack.padding = new RectOffset(16, 16, 10, 12);
            roomsStack.spacing = 6f;
            roomsStack.childControlWidth = true;
            roomsStack.childControlHeight = true;
            roomsStack.childForceExpandWidth = true;
            roomsStack.childForceExpandHeight = false;

            TextMeshProUGUI roomsCaption = CreateText(roomsSection.transform, "Caption", "ROOMS",
                16f, ColTextMuted, TextAlignmentOptions.MidlineLeft);
            var roomsCapLE = roomsCaption.gameObject.AddComponent<LayoutElement>();
            roomsCapLE.preferredHeight = 24f;
            roomsCapLE.flexibleHeight = 0f;

            var gridHostGO = new GameObject("Grid Host", typeof(RectTransform));
            gridHostGO.transform.SetParent(roomsSection.transform, false);
            var gridHostLE = gridHostGO.AddComponent<LayoutElement>();
            gridHostLE.flexibleHeight = 1f;

            int columns = Mathf.Clamp(Mathf.CeilToInt(roomCount / 2f), 4, 8);
            int rows = Mathf.CeilToInt(roomCount / (float)columns);

            var gridGO = new GameObject("TV Room Grid", typeof(RectTransform));
            gridGO.transform.SetParent(gridHostGO.transform, false);
            Stretch((RectTransform)gridGO.transform, Vector2.zero, Vector2.one);
            var grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(0, 0, 0, 0);
            grid.spacing = new Vector2(12f, 12f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;
            grid.childAlignment = TextAnchor.MiddleCenter;
            var sizer = gridGO.AddComponent<DynamicGridSizer>();
            sizer.visibleColumns = columns;
            sizer.totalRows = rows;

            var roomButtons = new List<Button>();
            for (int i = 0; i < roomCount; i++)
            {
                string label = experience.GetPanoramaName(i);
                if (string.IsNullOrEmpty(label)) label = $"Room {i + 1}";
                roomButtons.Add(CreateRoomButton(gridGO.transform, $"TV Room {i:00}", label));
            }

            // ---- divider: the requested distinction between rooms and controls ----
            Image divider = CreatePanel(drawerImage.transform, "Divider", ColButton);
            AddFixedWidth(divider.gameObject, 2f);

            // ---- controls section (right): Start Host / Labels / Plan + status ----
            Image controlsSection = CreatePanel(drawerImage.transform, "Controls Section", ColCard);
            AddFixedWidth(controlsSection.gameObject, 470f);
            var controlsStack = controlsSection.gameObject.AddComponent<VerticalLayoutGroup>();
            controlsStack.padding = new RectOffset(16, 16, 10, 12);
            controlsStack.spacing = 8f;
            controlsStack.childControlWidth = true;
            controlsStack.childControlHeight = true;
            controlsStack.childForceExpandWidth = true;
            controlsStack.childForceExpandHeight = false;

            TextMeshProUGUI controlsCaption = CreateText(controlsSection.transform, "Caption", "CONTROLS",
                16f, ColTextMuted, TextAlignmentOptions.MidlineLeft);
            var ctrlCapLE = controlsCaption.gameObject.AddComponent<LayoutElement>();
            ctrlCapLE.preferredHeight = 24f;
            ctrlCapLE.flexibleHeight = 0f;

            var buttonRowGO = new GameObject("Button Row", typeof(RectTransform));
            buttonRowGO.transform.SetParent(controlsSection.transform, false);
            var buttonRowLE = buttonRowGO.AddComponent<LayoutElement>();
            buttonRowLE.flexibleHeight = 1f;
            var buttonRow = buttonRowGO.AddComponent<HorizontalLayoutGroup>();
            buttonRow.spacing = 10f;
            buttonRow.childControlWidth = true;
            buttonRow.childControlHeight = true;
            buttonRow.childForceExpandWidth = true;
            buttonRow.childForceExpandHeight = true;

            Button tvStartHost = CreateDockButton(buttonRowGO.transform, "TV Start Host", "START HOST", accent: true);
            Toggle tvLabels = CreateDockToggle(buttonRowGO.transform, "TV Labels", "LABELS");
            Toggle tvPlan = CreateDockToggle(buttonRowGO.transform, "TV Plan Toggle", "PLAN");

            var statusRowGO = new GameObject("Status Row", typeof(RectTransform));
            statusRowGO.transform.SetParent(controlsSection.transform, false);
            var statusRowLE = statusRowGO.AddComponent<LayoutElement>();
            statusRowLE.preferredHeight = 104f;
            statusRowLE.flexibleHeight = 0f;
            var statusRow = statusRowGO.AddComponent<HorizontalLayoutGroup>();
            statusRow.spacing = 10f;
            statusRow.childControlWidth = true;
            statusRow.childControlHeight = true;
            statusRow.childForceExpandWidth = true;
            statusRow.childForceExpandHeight = true;

            TextMeshProUGUI tvStatus = CreateDockDisplay(statusRowGO.transform, "TV VR Status", "STATUS", "Ready", 1.5f);
            TextMeshProUGUI tvIp = CreateDockDisplay(statusRowGO.transform, "TV Host IP", "HOST IP", "—", 1f);

            // ---- pull tab (rides on the drawer's top edge) -------------------------
            var tabGO = new GameObject("Pull Tab", typeof(RectTransform));
            tabGO.transform.SetParent(drawerImage.transform, false);
            var tabLE = tabGO.AddComponent<LayoutElement>();
            tabLE.ignoreLayout = true; // manually anchored above the drawer, outside the HLG
            var tabRect = (RectTransform)tabGO.transform;
            tabRect.anchorMin = new Vector2(0.5f, 1f);
            tabRect.anchorMax = new Vector2(0.5f, 1f);
            tabRect.pivot = new Vector2(0.5f, 0f);
            tabRect.sizeDelta = new Vector2(320f, 58f);
            tabRect.anchoredPosition = new Vector2(0f, -2f); // tiny overlap hides the seam
            var tabImage = tabGO.AddComponent<Image>();
            tabImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            tabImage.type = Image.Type.Sliced;
            tabImage.color = ColPanel;
            var tabButton = tabGO.AddComponent<Button>();
            tabButton.targetGraphic = tabImage;
            ColorBlock tabColors = tabButton.colors;
            tabColors.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            tabColors.pressedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            tabButton.colors = tabColors;

            Image tabArrow = CreateIcon(tabGO.transform, "Arrow", "UI/Skin/DropdownArrow.psd", ColAccent);
            var arrowRect = tabArrow.rectTransform;
            arrowRect.anchorMin = new Vector2(0.5f, 0.5f);
            arrowRect.anchorMax = new Vector2(0.5f, 0.5f);
            arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.sizeDelta = new Vector2(26f, 16f);
            arrowRect.anchoredPosition = new Vector2(-64f, 0f);

            TextMeshProUGUI tabLabel = CreateText(tabGO.transform, "Label", "MENU", 24f, ColText,
                TextAlignmentOptions.Center);
            Stretch(tabLabel.rectTransform, Vector2.zero, Vector2.one);
            tabLabel.fontStyle = FontStyles.Bold;

            // ---- behaviour components ---------------------------------------------
            var drawerController = drawerImage.gameObject.AddComponent<TvDrawerController>();
            var drawerSO = new SerializedObject(drawerController);
            SetRef(drawerSO, "drawerPanel", drawerRect);
            SetRef(drawerSO, "pullTab", tabButton);
            SetRef(drawerSO, "tabArrow", arrowRect);
            drawerSO.ApplyModifiedPropertiesWithoutUndo();

            var indicator = controlsSection.gameObject.AddComponent<DockStatusIndicator>();
            var indicatorSO = new SerializedObject(indicator);
            SetRef(indicatorSO, "networkSetup", setup);
            SetRef(indicatorSO, "startHostButton", tvStartHost);
            SetRef(indicatorSO, "startHostIcon", FindChildComponent<Image>(tvStartHost.transform, "Icon"));
            SetRef(indicatorSO, "startHostLabel", FindChildComponent<TextMeshProUGUI>(tvStartHost.transform, "Label"));
            SetRef(indicatorSO, "labelsToggle", tvLabels);
            SetRef(indicatorSO, "labelsIcon", FindChildComponent<Image>(tvLabels.transform, "Icon"));
            SetRef(indicatorSO, "planToggle", tvPlan);
            SetRef(indicatorSO, "planIcon", FindChildComponent<Image>(tvPlan.transform, "Icon"));
            indicatorSO.ApplyModifiedPropertiesWithoutUndo();

            // ---- wire the managers ---------------------------------------------------
            var expSO = new SerializedObject(experience);
            SetRef(expSO, "tvPreviewImage", view);
            SetObjectArray(expSO, "tvPanoramaButtons", roomButtons.ToArray());
            expSO.ApplyModifiedPropertiesWithoutUndo();

            SetRef(setupSO, "tvCanvas", canvasGO);
            if (phoneCanvas != null) SetRef(setupSO, "phoneCanvas", phoneCanvas);
            SetRef(setupSO, "tvStartHostButton", tvStartHost);
            SetRef(setupSO, "tvStatusText", tvStatus);
            SetRef(setupSO, "tvIpDisplayText", tvIp);
            setupSO.ApplyModifiedPropertiesWithoutUndo();

            // ---- persistent button wiring ---------------------------------------------
            UnityEventTools.AddPersistentListener(tvStartHost.onClick, new UnityAction(setup.StartHost));
            UnityEventTools.AddPersistentListener(tvLabels.onValueChanged, new UnityAction<bool>(experience.SetLabelsVisible));
            UnityEventTools.AddPersistentListener(tvPlan.onValueChanged, new UnityAction<bool>(experience.SetFloorPlanVisible));
            for (int i = 0; i < roomButtons.Count; i++)
            {
                UnityEventTools.AddIntPersistentListener(roomButtons[i].onClick, experience.SetPanorama, i);
            }

            // Saved inactive: NetworkSetup turns it on at boot on TV-class devices only.
            canvasGO.SetActive(false);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = canvasGO;
            EditorUtility.DisplayDialog("Godrej Setup",
                $"TV canvas built: full-screen live view + slide-up drawer ({roomCount} rooms in a {columns}×{rows} grid, " +
                "START HOST / LABELS / PLAN — no slider; every room uses the start view baked into its material).\n\n" +
                "Saved INACTIVE — it enables itself on TV-class devices (Android TV, large touch panels) while phones " +
                "keep the portrait canvas. Editor preview: a LANDSCAPE Game view — e.g. the 'Godrej — Android TV " +
                "Touchscreen' Device Simulator profile or the 16:9 preset — shows the TV canvas in Play mode; a " +
                "portrait Game view shows the phone canvas.\n\n" +
                "Save the scene (Ctrl+S), then rebuild the PHONE APK (menu 5) — the TV runs that same salesman APK. " +
                "The Quest APK does NOT need a rebuild (no networked objects changed).", "OK");
        }

        private static GameObject FindPhoneCanvas(GameObject hostRoot)
        {
            foreach (Canvas candidate in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.gameObject.name == "Salesman Canvas") return candidate.gameObject;
            }

            // Fallback: any screen-space canvas under the host rig that isn't the TV canvas.
            if (hostRoot != null)
            {
                foreach (Canvas candidate in hostRoot.GetComponentsInChildren<Canvas>(true))
                {
                    if (candidate.renderMode == RenderMode.ScreenSpaceOverlay &&
                        candidate.gameObject.name != TvCanvasName)
                    {
                        return candidate.gameObject;
                    }
                }
            }

            return null;
        }

        // =====================================================================
        //  DEVICE-SPECIFIC BUILDS
        //  The Quest build is XR-first: the OpenXR loader injects a pre-init hook and an
        //  "offscreen swapchain, no main display buffer" mode into the player at BUILD
        //  time. On a phone (no XR runtime) such a build runs its scripts but can never
        //  present a frame — permanent black screen. So phones get their own build with
        //  XR stripped and OpenGL ES 3 for maximum device compatibility.
        //  All settings are swapped temporarily and restored after the build.
        // =====================================================================

        private enum ApkKind { Phone, Quest, Tv, Remote }
        private static bool suppressBuildDialogs;

        // Per-APK presenter-mode override baked into the build as a Resources text asset:
        // "phone" / "tv" pin the canvas, "auto" (the editor default) uses runtime detection.
        private const string PresenterModeAssetPath = "Assets/Resources/GodrejPresenterMode.txt";

        [MenuItem("Godrej/5. Build PHONE APK (salesman app)", priority = 20)]
        public static void BuildPhoneApk()
        {
            BuildAndroidApk(ApkKind.Phone, "Builds/Godrej-Salesman-Phone.apk");
        }

        [MenuItem("Godrej/6. Build QUEST APK (customer headset)", priority = 21)]
        public static void BuildQuestApk()
        {
            BuildAndroidApk(ApkKind.Quest, "Builds/Godrej-Quest.apk");
        }

        [MenuItem("Godrej/10. Build TV APK (salesman app, landscape)", priority = 22)]
        public static void BuildTvApk()
        {
            // The TV canvas ships inside the scene — building without it would give an
            // APK that silently falls back to the phone layout.
            if (!Application.isBatchMode &&
                Object.FindFirstObjectByType<TvDrawerController>(FindObjectsInactive.Include) == null &&
                !EditorUtility.DisplayDialog("Godrej Build",
                    "The open scene has no TV Canvas.\n\nRun 'Godrej > 9. Create or Update TV Canvas' " +
                    "first and save the scene, or the TV app will fall back to the phone layout.\n\nBuild anyway?",
                    "Build Anyway", "Cancel"))
            {
                return;
            }

            BuildAndroidApk(ApkKind.Tv, "Builds/Godrej-Salesman-TV.apk");
        }

        [MenuItem("Godrej/11. Build REMOTE APK (phone remote for the TV)", priority = 23)]
        public static void BuildRemoteApk()
        {
            // Same salesman phone UI, but baked mode "remote": it never hosts — it finds
            // the TV over the LAN and mirrors every action to it. Ships under its own
            // application id, so it installs ALONGSIDE the salesman phone app.
            BuildAndroidApk(ApkKind.Remote, "Builds/Godrej-Remote.apk");
        }

        [MenuItem("Godrej/12. Build ALL PRESENTER APKs (TV + phone + remote)", priority = 24)]
        public static void BuildAllPresenterApks()
        {
            suppressBuildDialogs = true;
            try
            {
                BuildAndroidApk(ApkKind.Tv, "Builds/Godrej-Salesman-TV.apk");
                BuildAndroidApk(ApkKind.Phone, "Builds/Godrej-Salesman-Phone.apk");
                BuildAndroidApk(ApkKind.Remote, "Builds/Godrej-Remote.apk");
            }
            finally
            {
                suppressBuildDialogs = false;
            }
        }

        [MenuItem("Godrej/13. Build WINDOWS EXE (Samsung Flip touchscreen)", priority = 25)]
        public static void BuildSamsungFlipWindowsExe()
        {
            if (!Application.isBatchMode &&
                Object.FindFirstObjectByType<TvDrawerController>(FindObjectsInactive.Include) == null &&
                !EditorUtility.DisplayDialog("Godrej Build",
                    "The open scene has no TV Canvas.\n\nRun 'Godrej > 9. Create or Update TV Canvas' " +
                    "first and save the scene, or the Windows app will fall back to the phone layout.\n\nBuild anyway?",
                    "Build Anyway", "Cancel"))
            {
                return;
            }

            const string outputPath =
                "Builds/Godrej-Samsung-Flip-Windows/Godrej-Samsung-Flip.exe";
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            XRGeneralSettings standaloneXr =
                XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            XRManagerSettings standaloneXrManager = standaloneXr != null ? standaloneXr.Manager : null;
            List<UnityEngine.XR.Management.XRLoader> savedLoaders = standaloneXrManager != null
                ? new List<UnityEngine.XR.Management.XRLoader>(standaloneXrManager.activeLoaders)
                : null;
            bool savedInitOnStart = standaloneXr != null && standaloneXr.InitManagerOnStart;
            string savedProductName = PlayerSettings.productName;
            ScriptingImplementation savedScriptingBackend = PlayerSettings.GetScriptingBackend(
                UnityEditor.Build.NamedBuildTarget.Standalone);
            FullScreenMode savedFullScreenMode = PlayerSettings.fullScreenMode;
            int savedWidth = PlayerSettings.defaultScreenWidth;
            int savedHeight = PlayerSettings.defaultScreenHeight;
            bool savedResizable = PlayerSettings.resizableWindow;

            try
            {
                WritePresenterMode("tv");
                if (standaloneXrManager != null)
                    standaloneXrManager.TrySetLoaders(new List<UnityEngine.XR.Management.XRLoader>());
                if (standaloneXr != null)
                    standaloneXr.InitManagerOnStart = false;

                PlayerSettings.productName = "Godrej Experience - Samsung Flip";
                PlayerSettings.SetScriptingBackend(
                    UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
                PlayerSettings.defaultScreenWidth = 1920;
                PlayerSettings.defaultScreenHeight = 1080;
                PlayerSettings.resizableWindow = true;

                string outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);

                BuildReport report = BuildPipeline.BuildPlayer(
                    scenes, outputPath, BuildTarget.StandaloneWindows64, BuildOptions.None);
                if (report.summary.result == BuildResult.Succeeded)
                {
                    float sizeMb = report.summary.totalSize / (1024f * 1024f);
                    ReportBuildResult(
                        $"WINDOWS Samsung Flip build succeeded ({sizeMb:F0} MB): {outputPath}");
                }
                else
                {
                    ReportBuildResult(
                        $"WINDOWS Samsung Flip build {report.summary.result}. Check the Console for details.",
                        error: true);
                }
            }
            finally
            {
                WritePresenterMode("auto");
                if (standaloneXrManager != null && savedLoaders != null)
                    standaloneXrManager.TrySetLoaders(savedLoaders);
                if (standaloneXr != null)
                    standaloneXr.InitManagerOnStart = savedInitOnStart;
                PlayerSettings.productName = savedProductName;
                PlayerSettings.SetScriptingBackend(
                    UnityEditor.Build.NamedBuildTarget.Standalone, savedScriptingBackend);
                PlayerSettings.fullScreenMode = savedFullScreenMode;
                PlayerSettings.defaultScreenWidth = savedWidth;
                PlayerSettings.defaultScreenHeight = savedHeight;
                PlayerSettings.resizableWindow = savedResizable;
            }
        }

        private static void WritePresenterMode(string mode)
        {
            EnsureFolder("Assets/Resources");
            File.WriteAllText(PresenterModeAssetPath, mode);
            AssetDatabase.ImportAsset(PresenterModeAssetPath, ImportAssetOptions.ForceUpdate);
        }

        private static void BuildAndroidApk(ApkKind kind, string outputPath)
        {
            XRGeneralSettings androidXr =
                XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            XRManagerSettings xrManager = androidXr != null ? androidXr.Manager : null;
            if (xrManager == null)
            {
                EditorUtility.DisplayDialog("Godrej Build", "XR settings for Android not found.", "OK");
                return;
            }

            bool xrEnabled = kind == ApkKind.Quest;

            var savedLoaders = new List<UnityEngine.XR.Management.XRLoader>(xrManager.activeLoaders);
            bool savedInitOnStart = androidXr.InitManagerOnStart;
            UIOrientation savedOrientation = PlayerSettings.defaultInterfaceOrientation;
            bool savedAutoPortrait = PlayerSettings.allowedAutorotateToPortrait;
            bool savedAutoPortraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
            bool savedAutoLandscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
            bool savedAutoLandscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;
            GraphicsDeviceType[] savedApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            AndroidSdkVersions savedMinSdk = PlayerSettings.Android.minSdkVersion;
            AndroidArchitecture savedArchitectures = PlayerSettings.Android.targetArchitectures;
            string savedAppId = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
            string savedProductName = PlayerSettings.productName;
            var savedMsaa = new Dictionary<UniversalRenderPipelineAsset, int>();

            try
            {
                WritePresenterMode(kind switch
                {
                    ApkKind.Phone => "phone",
                    ApkKind.Tv => "tv",
                    ApkKind.Remote => "remote",
                    _ => "auto"
                });

                if (kind == ApkKind.Remote)
                {
                    // Own identity: the remote must coexist with the salesman app on the
                    // same phone (identical ids would overwrite each other on install).
                    PlayerSettings.SetApplicationIdentifier(
                        UnityEditor.Build.NamedBuildTarget.Android, savedAppId + ".remote");
                    PlayerSettings.productName = "Godrej Remote";
                }

                if (xrEnabled)
                {
                    androidXr.InitManagerOnStart = true;
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft; // Quest requirement
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                }
                else
                {
                    // No XR loaders => no OpenXR pre-init hook, a normal main display buffer,
                    // and no Quest-only manifest requirements. A plain presenter app.
                    xrManager.TrySetLoaders(new List<UnityEngine.XR.Management.XRLoader>());
                    androidXr.InitManagerOnStart = false;
                    if (kind == ApkKind.Phone)
                    {
                        // The salesman APK owns both presenter layouts: portrait keeps the
                        // existing phone canvas, while landscape activates the TV canvas.
                        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                        PlayerSettings.allowedAutorotateToPortrait = true;
                        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                        PlayerSettings.allowedAutorotateToLandscapeRight = true;
                    }
                    else
                    {
                        PlayerSettings.defaultInterfaceOrientation = kind == ApkKind.Tv
                            ? UIOrientation.LandscapeLeft
                            : UIOrientation.Portrait; // the phone remote remains portrait-only
                    }

                    // TV: Vulkan first with OpenGL ES 3 as a broad hardware fallback.
                    // Unity 6 supports GLES 3.0; an ARM APK translated by an x86 Android
                    // emulator is not a reliable graphics-compatibility test.
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, kind == ApkKind.Tv
                        ? new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }
                        : new[] { GraphicsDeviceType.OpenGLES3 });

                    // The Quest needs minSdk 32, but presenter hardware in the field is often
                    // older — Android 8 (API 26) keeps virtually every phone/panel installable.
                    PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)26;

                    // Older phones and many TV/set-top boxes still expose a 32-bit userspace.
                    // All non-XR APKs ship both ABIs so installation is not needlessly blocked.
                    PlayerSettings.Android.targetArchitectures =
                        AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;

                    if (kind == ApkKind.Tv)
                    {
                        // TV screen buffers are often single-sample (the Android TV emulator's
                        // always is); URP then cannot open its 4x MSAA render pass and drops
                        // every frame — the app runs but the screen stays black. Ship the TV
                        // APK with MSAA off on every quality level; Quest keeps its 4x.
                        foreach (RenderPipelineAsset asset in EnumeratePipelineAssets())
                        {
                            if (asset is UniversalRenderPipelineAsset urp && !savedMsaa.ContainsKey(urp))
                            {
                                savedMsaa[urp] = urp.msaaSampleCount;
                                urp.msaaSampleCount = 1;
                            }
                        }
                    }
                }

                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
                Directory.CreateDirectory("Builds");

                BuildReport report = BuildPipeline.BuildPlayer(scenes, outputPath, BuildTarget.Android, BuildOptions.None);

                if (report.summary.result == BuildResult.Succeeded)
                {
                    float sizeMb = report.summary.totalSize / (1024f * 1024f);
                    ReportBuildResult(
                        $"{kind.ToString().ToUpperInvariant()} build succeeded ({sizeMb:F0} MB): {outputPath}");
                }
                else
                {
                    ReportBuildResult(
                        $"{kind.ToString().ToUpperInvariant()} build {report.summary.result}. Check the Console for details.",
                        error: true);
                }
            }
            finally
            {
                WritePresenterMode("auto");
                xrManager.TrySetLoaders(savedLoaders);
                androidXr.InitManagerOnStart = savedInitOnStart;
                PlayerSettings.defaultInterfaceOrientation = savedOrientation;
                PlayerSettings.allowedAutorotateToPortrait = savedAutoPortrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = savedAutoPortraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = savedAutoLandscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = savedAutoLandscapeRight;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, savedApis);
                PlayerSettings.Android.minSdkVersion = savedMinSdk;
                PlayerSettings.Android.targetArchitectures = savedArchitectures;
                PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, savedAppId);
                PlayerSettings.productName = savedProductName;
                foreach (KeyValuePair<UniversalRenderPipelineAsset, int> pair in savedMsaa)
                    pair.Key.msaaSampleCount = pair.Value;

                // BuildPlayer flushes dirty assets to disk mid-build, so the URP assets
                // were written with the TV overrides; save again or the restored values
                // exist only in memory and the next Quest build ships without MSAA.
                if (savedMsaa.Count > 0) AssetDatabase.SaveAssets();
            }
        }

        private static void ReportBuildResult(string message, bool error = false)
        {
            if (error) Debug.LogError($"[Godrej Build] {message}");
            else Debug.Log($"[Godrej Build] {message}");

            if (!Application.isBatchMode && !suppressBuildDialogs)
                EditorUtility.DisplayDialog("Godrej Build", message, "OK");
        }

        /// <summary>Every render pipeline asset the built player can end up using:
        /// the graphics default plus each quality level's override.</summary>
        private static IEnumerable<RenderPipelineAsset> EnumeratePipelineAssets()
        {
            if (GraphicsSettings.defaultRenderPipeline != null)
                yield return GraphicsSettings.defaultRenderPipeline;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                RenderPipelineAsset asset = QualitySettings.GetRenderPipelineAssetAt(i);
                if (asset != null)
                    yield return asset;
            }
        }

        /// <summary>
        /// Adds the PLAN on/off toggle and the START VIEW slider to the existing nav dock,
        /// fully wired to LocalExperienceManager — WITHOUT touching the rest of the layout.
        /// Safe to run repeatedly (replaces its own previous controls).
        /// </summary>
        [MenuItem("Godrej/4. Add Plan Toggle + Start View Slider (keeps your layout)", priority = 4)]
        public static void AddPresenterExtras()
        {
            var experience = Object.FindFirstObjectByType<LocalExperienceManager>(FindObjectsInactive.Include);
            if (experience == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup", "No LocalExperienceManager found in the open scene.", "OK");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            Transform dock = null;
            GameObject board = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "Floor Plan Board (VR)") board = root;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Zone 4 - Nav Dock") dock = t;
                    else if (t.name == "Floor Plan Board (VR)") board = t.gameObject;
                }
            }

            if (dock == null)
            {
                EditorUtility.DisplayDialog("Godrej Setup",
                    "Could not find 'Zone 4 - Nav Dock' in the open scene.\n" +
                    "Rename your bottom bar back to that, or ask me to target a different container.", "OK");
                return;
            }

            // Idempotency: remove previously added copies of these controls.
            for (int i = dock.childCount - 1; i >= 0; i--)
            {
                Transform child = dock.GetChild(i);
                if (child.name == "Plan Toggle" || child.name == "Start View")
                {
                    Object.DestroyImmediate(child.gameObject);
                }
            }

            Toggle planToggle = CreateDockToggle(dock, "Plan Toggle", "PLAN");
            Slider slider = CreateDockSlider(dock, "Start View", "START VIEW");

            var so = new SerializedObject(experience);
            if (board != null) SetRef(so, "floorPlanBoard", board);
            SetRef(so, "startViewSlider", slider);
            so.ApplyModifiedPropertiesWithoutUndo();

            UnityEventTools.AddPersistentListener(planToggle.onValueChanged,
                new UnityAction<bool>(experience.SetFloorPlanVisible));
            UnityEventTools.AddPersistentListener(slider.onValueChanged,
                new UnityAction<float>(experience.SetStartViewRotation));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorUtility.DisplayDialog("Godrej Setup",
                "Added to the dock:\n• PLAN toggle — shows/hides the floor plan board in the headset\n" +
                "• START VIEW slider — spins the panorama to choose what the customer faces first " +
                "(per room; values tuned in the editor bake into builds)\n\n" +
                "Save the scene (Ctrl+S), then Build And Run — the headset app must be rebuilt for these to sync.", "OK");
        }

        /// <summary>Dock cell containing a caption and a 0–360° slider.</summary>
        private static Slider CreateDockSlider(Transform parent, string name, string caption)
        {
            var cell = new GameObject(name, typeof(RectTransform));
            cell.transform.SetParent(parent, false);
            AddFlexibleWidth(cell, 1.4f);

            var stack = cell.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(10, 10, 12, 14);
            stack.spacing = 6f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childAlignment = TextAnchor.MiddleCenter;

            TextMeshProUGUI captionText = CreateText(cell.transform, "Caption", caption, 14f, ColTextMuted,
                TextAlignmentOptions.Center);
            var capLE = captionText.gameObject.AddComponent<LayoutElement>();
            capLE.preferredHeight = 22f;
            capLE.flexibleHeight = 0f;

            var sliderGO = new GameObject("Slider", typeof(RectTransform));
            sliderGO.transform.SetParent(cell.transform, false);
            var sliderLE = sliderGO.AddComponent<LayoutElement>();
            sliderLE.preferredHeight = 36f;
            sliderLE.flexibleHeight = 0f;

            var track = new GameObject("Track", typeof(RectTransform));
            track.transform.SetParent(sliderGO.transform, false);
            Stretch((RectTransform)track.transform, new Vector2(0f, 0.36f), new Vector2(1f, 0.64f));
            var trackImage = track.AddComponent<Image>();
            trackImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            trackImage.type = Image.Type.Sliced;
            trackImage.color = ColButton;
            trackImage.raycastTarget = false;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sliderGO.transform, false);
            Stretch((RectTransform)handleArea.transform, Vector2.zero, Vector2.one, new Vector4(14f, 0f, 14f, 0f));

            var handle = new GameObject("Handle", typeof(RectTransform));
            handle.transform.SetParent(handleArea.transform, false);
            var handleRect = (RectTransform)handle.transform;
            handleRect.sizeDelta = new Vector2(28f, 28f);
            var handleImage = handle.AddComponent<Image>();
            handleImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
            handleImage.color = ColAccent;

            var slider = sliderGO.AddComponent<Slider>();
            slider.targetGraphic = handleImage;
            slider.handleRect = handleRect;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 360f;
            slider.wholeNumbers = false;

            return slider;
        }

        /// <summary>
        /// Replaces ONLY the VR floor plan board in the currently open scene, leaving the
        /// rest of the scene (including any hand-arranged salesman canvas) untouched.
        /// Use this instead of a full scene regeneration once you have customized the layout.
        /// </summary>
        [MenuItem("Godrej/3. Update VR Floor Plan Board (keeps your layout)", priority = 3)]
        public static void UpdateFloorPlanBoard()
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == "Floor Plan Board (VR)")
                {
                    Object.DestroyImmediate(root);
                    break;
                }
            }

            GameObject board = BuildFloorPlanBoard();

            // Rebuilding replaces the object, so the manager's reference (used by the
            // PLAN toggle) must be re-pointed at the new instance.
            var experience = Object.FindFirstObjectByType<LocalExperienceManager>(FindObjectsInactive.Include);
            if (experience != null && board != null)
            {
                var so = new SerializedObject(experience);
                SetRef(so, "floorPlanBoard", board);
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorUtility.DisplayDialog("Godrej Setup",
                "VR floor plan board updated in the open scene (knee height, tilted toward the viewer).\n\n" +
                "Save the scene (Ctrl+S), then Build And Run to update the headset.", "OK");
        }

        /// <summary>
        /// World-space board showing the VR proposal floor plan: ~45 cm wide, knee height,
        /// just in front of the customer, horizontal with a gentle tilt toward the eyes.
        /// Thanks to the recenter at session start, "in front" is wherever the customer
        /// is facing when the experience begins. Select it in the Hierarchy to reposition.
        /// </summary>
        private static GameObject BuildFloorPlanBoard()
        {
            Texture2D plan = LoadFloorPlanTexture();
            if (plan == null)
            {
                Debug.LogWarning("[SceneSetupWizard] No floor plan texture found — skipping the VR floor plan board.");
                return null;
            }

            var canvasGO = new GameObject("Floor Plan Board (VR)");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = (RectTransform)canvasGO.transform;
            float heightPerWidth = (float)plan.height / plan.width;
            rect.sizeDelta = new Vector2(450f, 450f * heightPerWidth);
            rect.localScale = Vector3.one * 0.001f; // 450 px canvas => 0.45 m wide board

            // Horizontal like a plan sheet on an invisible table (90° = perfectly flat),
            // raised 22° toward the viewer so it reads comfortably when looking down.
            rect.position = new Vector3(0f, 0.52f, 0.55f); // knee height, just in front
            rect.rotation = Quaternion.Euler(68f, 0f, 0f);

            // Thin dark frame extending slightly past the plan for contrast against bright rooms.
            Image frame = CreatePanel(canvas.transform, "Frame", ColCard);
            Stretch(frame.rectTransform, Vector2.zero, Vector2.one, new Vector4(-10f, -10f, -10f, -10f));

            var imageGO = new GameObject("Plan Image", typeof(RectTransform));
            imageGO.transform.SetParent(canvas.transform, false);
            Stretch((RectTransform)imageGO.transform, Vector2.zero, Vector2.one);
            var planImage = imageGO.AddComponent<RawImage>();
            planImage.texture = plan;
            planImage.raycastTarget = false;

            return canvasGO;
        }

        private static GameObject BuildFloatingLabels(Material[] panoramas, out GameObject[] labelGroups)
        {
            var root = new GameObject("Floating Labels");
            labelGroups = new GameObject[panoramas.Length];

            for (int i = 0; i < panoramas.Length; i++)
            {
                string roomName = GetRoomDisplayName(panoramas[i], i);
                var group = new GameObject($"{i + 1:00} {roomName}");
                group.transform.SetParent(root.transform, false);
                labelGroups[i] = group;

                group.SetActive(i == 0);
            }

            return root;
        }

        // =====================================================================
        //  UI PRIMITIVES
        // =====================================================================

        private static Image CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size,
            Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static Button CreateButton(Transform parent, string name, string label, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            image.type = Image.Type.Sliced;
            image.color = ColButton;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            button.colors = colors;

            TextMeshProUGUI text = CreateText(go.transform, "Label", label, fontSize, ColText,
                TextAlignmentOptions.Center);
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, new Vector4(8, 4, 8, 4));
            text.enableAutoSizing = true;
            text.fontSizeMax = fontSize;
            text.fontSizeMin = 12f;

            return button;
        }

        private static void SetButtonAccent(Button button)
        {
            if (button.targetGraphic is Image image) image.color = ColAccent;
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.color = ColCard;
                label.fontStyle = FontStyles.Bold;
            }
        }

        private static Toggle CreateToggle(Transform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var toggle = go.AddComponent<Toggle>();

            var bgGO = new GameObject("Background", typeof(RectTransform));
            bgGO.transform.SetParent(go.transform, false);
            var bgRect = (RectTransform)bgGO.transform;
            bgRect.anchorMin = new Vector2(0f, 0.5f);
            bgRect.anchorMax = new Vector2(0f, 0.5f);
            bgRect.pivot = new Vector2(0f, 0.5f);
            bgRect.anchoredPosition = new Vector2(4f, 0f);
            bgRect.sizeDelta = new Vector2(36f, 36f);
            var bgImage = bgGO.AddComponent<Image>();
            bgImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            bgImage.type = Image.Type.Sliced;
            bgImage.color = ColButton;

            var checkGO = new GameObject("Checkmark", typeof(RectTransform));
            checkGO.transform.SetParent(bgGO.transform, false);
            Stretch((RectTransform)checkGO.transform, Vector2.zero, Vector2.one, new Vector4(6, 6, 6, 6));
            var checkImage = checkGO.AddComponent<Image>();
            checkImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
            checkImage.color = ColAccent;

            TextMeshProUGUI text = CreateText(go.transform, "Label", label, 22f, ColText,
                TextAlignmentOptions.MidlineLeft);
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, new Vector4(52, 0, 0, 0));

            toggle.targetGraphic = bgImage;
            toggle.graphic = checkImage;
            toggle.isOn = true;

            return toggle;
        }

        private static TMP_InputField CreateInputField(Transform parent, string name, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd");
            image.type = Image.Type.Sliced;
            image.color = ColText;

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = image;

            var areaGO = new GameObject("Text Area", typeof(RectTransform));
            areaGO.transform.SetParent(go.transform, false);
            var areaRect = (RectTransform)areaGO.transform;
            Stretch(areaRect, Vector2.zero, Vector2.one, new Vector4(14, 6, 14, 7));
            areaGO.AddComponent<RectMask2D>();

            TextMeshProUGUI textComponent = CreateText(areaGO.transform, "Text", string.Empty, 28f,
                ColCard, TextAlignmentOptions.MidlineLeft);
            Stretch(textComponent.rectTransform, Vector2.zero, Vector2.one);

            TextMeshProUGUI placeholder = CreateText(areaGO.transform, "Placeholder", "Host IP…", 28f,
                new Color(0.2f, 0.24f, 0.3f, 0.6f), TextAlignmentOptions.MidlineLeft);
            Stretch(placeholder.rectTransform, Vector2.zero, Vector2.one);
            placeholder.fontStyle = FontStyles.Italic;

            input.textViewport = areaRect;
            input.textComponent = textComponent;
            input.placeholder = placeholder;
            input.text = text;

            return input;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        private static string GetRoomDisplayName(Material material, int index)
        {
            if (material == null) return $"Room {index + 1}";

            string name = material.name.Trim();
            if (name.Length > 3 && char.IsDigit(name[0]) && char.IsDigit(name[1]) &&
                (name[2] == ' ' || name[2] == '-' || name[2] == '_'))
            {
                name = name.Substring(3).Trim();
            }

            return string.IsNullOrEmpty(name) ? $"Room {index + 1}" : name;
        }

        private static Material[] LoadPanoramaMaterials()
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder)) return new Material[0];

            return AssetDatabase.FindAssets("t:Material", new[] { MaterialFolder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(m => m != null)
                // Material filenames carry the floor-plan sequence ("01 ..." through "14 ...").
                // Sort by asset path so regenerating the scene preserves that intended order
                // while each material's display name can remain clean for the room buttons.
                .OrderBy(m => AssetDatabase.GetAssetPath(m))
                .ToArray();
        }

        private static Texture2D LoadFloorPlanTexture()
        {
            if (!AssetDatabase.IsValidFolder(UiFolder)) return null;

            return AssetDatabase.FindAssets("t:Texture2D", new[] { UiFolder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid)))
                .FirstOrDefault(t => t != null);
        }

        private static void RegisterSceneInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };

            // Keep any other previously listed scenes, disabled, so nothing is lost.
            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (existing.path != ScenePath && !string.IsNullOrEmpty(existing.path))
                {
                    scenes.Add(new EditorBuildSettingsScene(existing.path, false));
                }
            }

            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void SetRef(SerializedObject so, string property, Object value)
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop == null)
            {
                Debug.LogError($"[SceneSetupWizard] Missing serialized field '{property}' on {so.targetObject.GetType().Name}.");
                return;
            }
            prop.objectReferenceValue = value;
        }

        private static void SetObjectArray<T>(SerializedObject so, string property, T[] values) where T : Object
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop == null)
            {
                Debug.LogError($"[SceneSetupWizard] Missing serialized field '{property}' on {so.targetObject.GetType().Name}.");
                return;
            }

            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        /// <summary>Anchors a RectTransform inside its parent; padding = (left, bottom, right, top) in px.</summary>
        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector4 padding = default)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = new Vector2(padding.x, padding.y);
            rect.offsetMax = new Vector2(-padding.z, -padding.w);
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color color);
            return color;
        }
    }
}
