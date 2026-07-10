using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Godrej.Editor
{
    public class PhoneManifestCleaner : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder { get { return 10000; } } // Run very late to ensure we overwrite Oculus changes

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // Only clean if XR is currently DISABLED for Android
            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings != null && settings.InitManagerOnStart)
            {
                Debug.Log("[PhoneManifestCleaner] XR is enabled. Skipping manifest clean (Building for Quest).");
                return;
            }

            Debug.Log("[PhoneManifestCleaner] XR is disabled. Cleaning AndroidManifest for Phone build...");

            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[PhoneManifestCleaner] Manifest not found at: {manifestPath}");
                return;
            }

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(manifestPath);

            // Setup namespaces
            XmlNamespaceManager nsMgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsMgr.AddNamespace("android", "http://schemas.android.com/apk/res/android");
            nsMgr.AddNamespace("tools", "http://schemas.android.com/tools");

            bool modified = false;

            // 1. Remove Oculus VR intent categories
            XmlNodeList categories = xmlDoc.SelectNodes("//category[@android:name='com.oculus.intent.category.VR']", nsMgr);
            if (categories != null)
            {
                foreach (XmlNode node in categories)
                {
                    node.ParentNode.RemoveChild(node);
                    modified = true;
                }
            }

            // 2. Remove Oculus hand tracking permissions
            XmlNodeList permissions = xmlDoc.SelectNodes("//uses-permission[@android:name='com.oculus.permission.HAND_TRACKING']", nsMgr);
            if (permissions != null)
            {
                foreach (XmlNode node in permissions)
                {
                    node.ParentNode.RemoveChild(node);
                    modified = true;
                }
            }

            // 3. Remove Oculus hand tracking features
            XmlNodeList features = xmlDoc.SelectNodes("//uses-feature[@android:name='oculus.software.handtracking']", nsMgr);
            if (features != null)
            {
                foreach (XmlNode node in features)
                {
                    node.ParentNode.RemoveChild(node);
                    modified = true;
                }
            }
            
            // 4. Remove VR Only meta-data
            XmlNodeList metadata = xmlDoc.SelectNodes("//meta-data[@android:name='com.samsung.android.vr.application.mode']", nsMgr);
            if (metadata != null)
            {
                foreach (XmlNode node in metadata)
                {
                    node.ParentNode.RemoveChild(node);
                    modified = true;
                }
            }

            // 5. Pin every activity to the build's presenter orientation. The Godrej build
            //    menu sets PlayerSettings.defaultInterfaceOrientation before building:
            //    Portrait for the phone APK, LandscapeLeft for the TV APK — so the splash
            //    and first frame are already correct on each device.
            bool landscapeBuild =
                PlayerSettings.defaultInterfaceOrientation == UIOrientation.LandscapeLeft ||
                PlayerSettings.defaultInterfaceOrientation == UIOrientation.LandscapeRight;
            string desiredOrientation = landscapeBuild ? "sensorLandscape" : "sensorPortrait";

            XmlNodeList activities = xmlDoc.SelectNodes("//activity", nsMgr);
            if (activities != null)
            {
                foreach (XmlNode activity in activities)
                {
                    XmlAttribute orientationAttr = activity.Attributes["android:screenOrientation"];
                    if (orientationAttr == null)
                    {
                        orientationAttr = xmlDoc.CreateAttribute("android", "screenOrientation", "http://schemas.android.com/apk/res/android");
                        activity.Attributes.Append(orientationAttr);
                    }

                    if (orientationAttr.Value != desiredOrientation)
                    {
                        orientationAttr.Value = desiredOrientation;
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                xmlDoc.Save(manifestPath);
                Debug.Log("[PhoneManifestCleaner] Successfully stripped Oculus flags and forced Portrait mode.");
            }
        }
    }
}
