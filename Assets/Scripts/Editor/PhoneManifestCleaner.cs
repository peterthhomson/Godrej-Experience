using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Godrej.Editor
{
    /// <summary>
    /// Removes headset-only manifest declarations from non-XR builds and adds the
    /// Android TV launcher declarations to the dedicated TV APK.
    /// </summary>
    public class PhoneManifestCleaner : IPostGenerateGradleAndroidProject
    {
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        private const string PresenterModeAssetPath = "Assets/Resources/GodrejPresenterMode.txt";

        public int callbackOrder => 10000; // Run late so this wins over XR manifest contributors.

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            XRGeneralSettings settings =
                XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings != null && settings.InitManagerOnStart)
            {
                Debug.Log("[PhoneManifestCleaner] XR is enabled. Skipping non-XR manifest processing.");
                return;
            }

            string presenterMode = File.Exists(PresenterModeAssetPath)
                ? File.ReadAllText(PresenterModeAssetPath).Trim().ToLowerInvariant()
                : "auto";
            bool tvBuild = presenterMode == "tv";

            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[PhoneManifestCleaner] Manifest not found at: {manifestPath}");
                return;
            }

            var document = new XmlDocument();
            document.Load(manifestPath);

            var nsMgr = new XmlNamespaceManager(document.NameTable);
            nsMgr.AddNamespace("android", AndroidNamespace);
            nsMgr.AddNamespace("tools", "http://schemas.android.com/tools");

            bool modified = false;

            modified |= RemoveNodes(document,
                "//category[@android:name='com.oculus.intent.category.VR']", nsMgr);
            modified |= RemoveNodes(document,
                "//uses-permission[@android:name='com.oculus.permission.HAND_TRACKING']", nsMgr);
            modified |= RemoveNodes(document,
                "//uses-feature[@android:name='oculus.software.handtracking']", nsMgr);
            modified |= RemoveNodes(document,
                "//meta-data[@android:name='com.samsung.android.vr.application.mode']", nsMgr);

            // Native orientation must agree with Unity from the splash onward. The
            // salesman phone APK uses fullSensor so it can swap canvases on rotation.
            string desiredOrientation = presenterMode switch
            {
                "phone" => "fullSensor",
                "tv" => "sensorLandscape",
                "remote" => "sensorPortrait",
                _ => PlayerSettings.defaultInterfaceOrientation switch
                {
                    UIOrientation.LandscapeLeft => "sensorLandscape",
                    UIOrientation.LandscapeRight => "sensorLandscape",
                    UIOrientation.Portrait => "sensorPortrait",
                    UIOrientation.PortraitUpsideDown => "sensorPortrait",
                    _ => "fullSensor"
                }
            };

            XmlNodeList activities = document.SelectNodes("//activity", nsMgr);
            if (activities != null)
            {
                foreach (XmlNode activity in activities)
                {
                    modified |= SetAndroidAttribute((XmlElement)activity,
                        "screenOrientation", desiredOrientation);
                }
            }

            if (presenterMode == "phone")
            {
                XmlElement mainIntent = document.SelectSingleNode(
                    "//activity/intent-filter[action[@android:name='android.intent.action.MAIN']]",
                    nsMgr) as XmlElement;
                if (mainIntent?.ParentNode is XmlElement mainActivity)
                {
                    modified |= SetAndroidAttribute(mainActivity, "name",
                        "com.godrej.presenter.GodrejPhoneActivity");
                    WritePhoneActivitySource(path);
                }
                else
                {
                    Debug.LogWarning(
                        "[PhoneManifestCleaner] MAIN intent-filter not found; phone rotation activity could not be installed.");
                }
            }

            // Keep the ordinary launcher category for generic touch panels, and add
            // Leanback so the same TV APK appears on Android TV / Google TV home screens.
            if (tvBuild)
            {
                XmlElement manifest = document.DocumentElement;
                XmlElement application =
                    document.SelectSingleNode("/manifest/application") as XmlElement;
                XmlElement mainIntent = document.SelectSingleNode(
                    "//activity/intent-filter[action[@android:name='android.intent.action.MAIN']]",
                    nsMgr) as XmlElement;

                if (manifest != null)
                {
                    modified |= EnsureUsesFeature(document, manifest, nsMgr,
                        "android.software.leanback", required: false);
                    modified |= EnsureUsesFeature(document, manifest, nsMgr,
                        "android.hardware.touchscreen", required: false);
                }

                if (application != null)
                {
                    modified |= SetAndroidAttribute(application,
                        "banner", "@drawable/godrej_tv_banner");
                    modified |= EnsureMetadata(document, application, nsMgr,
                        "android.software.leanback.supports_touch", "true");
                }

                if (mainIntent != null)
                {
                    modified |= EnsureCategory(document, mainIntent, nsMgr,
                        "android.intent.category.LEANBACK_LAUNCHER");

                    // A tiny activity subclass normalizes D-pad key codes before they
                    // reach Unity. Android TV vendors expose remotes as keyboards,
                    // gamepads, or bare Android KeyEvents; this path covers all three.
                    if (mainIntent.ParentNode is XmlElement mainActivity)
                    {
                        modified |= SetAndroidAttribute(mainActivity, "name",
                            "com.godrej.presenter.GodrejTvActivity");
                    }
                }
                else
                {
                    Debug.LogWarning(
                        "[PhoneManifestCleaner] MAIN intent-filter not found; TV launcher entry could not be added.");
                }

                WriteTvBannerResource(path);
                WriteTvActivitySource(path);
            }

            if (modified)
            {
                document.Save(manifestPath);
            }

            Debug.Log($"[PhoneManifestCleaner] Manifest ready: mode={presenterMode}, " +
                      $"orientation={desiredOrientation}, leanback={tvBuild}.");
        }

        private static bool RemoveNodes(XmlDocument document, string xpath,
            XmlNamespaceManager nsMgr)
        {
            bool modified = false;
            XmlNodeList nodes = document.SelectNodes(xpath, nsMgr);
            if (nodes == null) return false;

            foreach (XmlNode node in nodes)
            {
                node.ParentNode?.RemoveChild(node);
                modified = true;
            }
            return modified;
        }

        private static bool SetAndroidAttribute(XmlElement element, string name, string value)
        {
            if (element.GetAttribute(name, AndroidNamespace) == value) return false;
            element.SetAttribute(name, AndroidNamespace, value);
            return true;
        }

        private static bool EnsureUsesFeature(XmlDocument document, XmlElement manifest,
            XmlNamespaceManager nsMgr, string featureName, bool required)
        {
            XmlElement feature = document.SelectSingleNode(
                $"/manifest/uses-feature[@android:name='{featureName}']", nsMgr) as XmlElement;
            bool modified = false;
            if (feature == null)
            {
                feature = document.CreateElement("uses-feature");
                SetAndroidAttribute(feature, "name", featureName);
                manifest.AppendChild(feature);
                modified = true;
            }
            return SetAndroidAttribute(feature, "required", required ? "true" : "false") || modified;
        }

        private static bool EnsureMetadata(XmlDocument document, XmlElement application,
            XmlNamespaceManager nsMgr, string metadataName, string value)
        {
            XmlElement metadata = application.SelectSingleNode(
                $"meta-data[@android:name='{metadataName}']", nsMgr) as XmlElement;
            bool modified = false;
            if (metadata == null)
            {
                metadata = document.CreateElement("meta-data");
                SetAndroidAttribute(metadata, "name", metadataName);
                application.AppendChild(metadata);
                modified = true;
            }
            return SetAndroidAttribute(metadata, "value", value) || modified;
        }

        private static bool EnsureCategory(XmlDocument document, XmlElement intentFilter,
            XmlNamespaceManager nsMgr, string categoryName)
        {
            if (intentFilter.SelectSingleNode(
                    $"category[@android:name='{categoryName}']", nsMgr) != null)
            {
                return false;
            }

            XmlElement category = document.CreateElement("category");
            SetAndroidAttribute(category, "name", categoryName);
            intentFilter.AppendChild(category);
            return true;
        }

        private static void WriteTvBannerResource(string gradleProjectPath)
        {
            string drawableDirectory = Path.Combine(
                gradleProjectPath, "src", "main", "res", "drawable");
            Directory.CreateDirectory(drawableDirectory);
            File.WriteAllText(Path.Combine(drawableDirectory, "godrej_tv_banner.xml"),
@"<?xml version=""1.0"" encoding=""utf-8""?>
<layer-list xmlns:android=""http://schemas.android.com/apk/res/android"">
    <item>
        <shape android:shape=""rectangle"">
            <solid android:color=""#12161C"" />
        </shape>
    </item>
    <item android:left=""24dp"" android:top=""16dp"" android:right=""24dp"" android:bottom=""16dp"">
        <shape android:shape=""rectangle"">
            <solid android:color=""#C8A557"" />
            <corners android:radius=""12dp"" />
            <stroke android:width=""2dp"" android:color=""#EDF1F6"" />
        </shape>
    </item>
</layer-list>");
        }

        private static void WriteTvActivitySource(string gradleProjectPath)
        {
            string javaDirectory = Path.Combine(gradleProjectPath, "src", "main", "java",
                "com", "godrej", "presenter");
            Directory.CreateDirectory(javaDirectory);
            File.WriteAllText(Path.Combine(javaDirectory, "GodrejTvActivity.java"),
@"package com.godrej.presenter;

import android.view.KeyEvent;
import com.unity3d.player.UnityPlayerGameActivity;

/** Normalizes Android TV remotes without affecting touch or pointer events. */
public class GodrejTvActivity extends UnityPlayerGameActivity {
    private static int pendingGodrejKeyCode = KeyEvent.KEYCODE_UNKNOWN;

    private static boolean isGodrejRemoteKey(int keyCode) {
        switch (keyCode) {
            case KeyEvent.KEYCODE_DPAD_UP:
            case KeyEvent.KEYCODE_DPAD_DOWN:
            case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_RIGHT:
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
            case KeyEvent.KEYCODE_SPACE:
            case KeyEvent.KEYCODE_BUTTON_A:
            case KeyEvent.KEYCODE_BUTTON_B:
            case KeyEvent.KEYCODE_BUTTON_SELECT:
            case KeyEvent.KEYCODE_BACK:
                return true;
            default:
                return false;
        }
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        int keyCode = event.getKeyCode();
        if (isGodrejRemoteKey(keyCode)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                synchronized (GodrejTvActivity.class) {
                    pendingGodrejKeyCode = keyCode;
                }
            }
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    public static synchronized int consumeGodrejKeyCode() {
        int keyCode = pendingGodrejKeyCode;
        pendingGodrejKeyCode = KeyEvent.KEYCODE_UNKNOWN;
        return keyCode;
    }
}");
        }

        private static void WritePhoneActivitySource(string gradleProjectPath)
        {
            string javaDirectory = Path.Combine(gradleProjectPath, "src", "main", "java",
                "com", "godrej", "presenter");
            Directory.CreateDirectory(javaDirectory);
            File.WriteAllText(Path.Combine(javaDirectory, "GodrejPhoneActivity.java"),
@"package com.godrej.presenter;

import android.content.pm.ActivityInfo;
import com.unity3d.player.UnityPlayerGameActivity;

/** Keeps Android in charge of phone rotation and ignores Unity's later fixed requests. */
public class GodrejPhoneActivity extends UnityPlayerGameActivity {
    @Override
    public void setRequestedOrientation(int requestedOrientation) {
        super.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR);
    }
}");
        }
    }
}
