using UnityEngine;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;
using System.IO;

namespace Godrej.Editor
{
    // RETIRED: superseded by SceneSetupWizard menus 5 (phone), 6 (Quest) and 10 (TV),
    // which swap XR/orientation/graphics settings only for the duration of the build
    // and always restore them. This class instead saved InitManagerOnStart permanently
    // (SetXREnabled -> SaveAssets), silently leaving the project in the wrong state for
    // the next build. Menu entries removed so it cannot be triggered; safe to delete.
    public class BuildAutomator : EditorWindow
    {
        public static void BuildForPhone()
        {
            if (EditorUtility.DisplayDialog("Build for Phone", "This will automatically disable XR on startup and build an APK for the Salesman's Phone. Proceed?", "Yes", "Cancel"))
            {
                SetXREnabled(false);
                string path = EditorUtility.SaveFilePanel("Save Phone APK", "", "Godrej_Phone_Build", "apk");
                if (!string.IsNullOrEmpty(path))
                {
                    PerformBuild(path);
                }
            }
        }

        public static void BuildForQuest()
        {
            if (EditorUtility.DisplayDialog("Build for Quest", "This will automatically enable XR on startup and build an APK for the Meta Quest. Proceed?", "Yes", "Cancel"))
            {
                SetXREnabled(true);
                string path = EditorUtility.SaveFilePanel("Save Quest APK", "", "Godrej_Quest_Build", "apk");
                if (!string.IsNullOrEmpty(path))
                {
                    PerformBuild(path);
                }
            }
        }

        private static void SetXREnabled(bool isEnabled)
        {
            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings != null)
            {
                settings.InitManagerOnStart = isEnabled;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Build Automator] XR Initialization on Android set to: {isEnabled}");
            }
            else
            {
                Debug.LogError("[Build Automator] Could not find XR settings for Android!");
            }
        }

        private static void PerformBuild(string buildPath)
        {
            // Get all enabled scenes
            var scenes = EditorBuildSettings.scenes;
            string[] scenePaths = new string[scenes.Length];
            for (int i = 0; i < scenes.Length; i++)
            {
                scenePaths[i] = scenes[i].path;
            }

            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = buildPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[Build Automator] Starting build to: {buildPath}");
            var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            
            // Check result
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[Build Automator] Build succeeded! File size: {report.summary.totalSize / (1024 * 1024)} MB");
                EditorUtility.RevealInFinder(Path.GetDirectoryName(buildPath));
            }
            else if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Failed)
            {
                Debug.LogError("[Build Automator] Build failed! Check the console for errors.");
            }
        }
    }
}
