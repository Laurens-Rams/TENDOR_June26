#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Links Photos.framework for TendorVideoPhotosExport.mm (PHPhotoLibrary / PHAssetCreationRequest).
    /// Plugin .meta also lists FrameworkDependencies: Photos.
    /// </summary>
    public static class IosPhotosFrameworkLinker
    {
        const string PhotosFramework = "Photos.framework";
        const string AddPhotosUsageKey = "NSPhotoLibraryAddUsageDescription";
        const string AddPhotosUsageText = "TENDOR saves climb recordings to your photo library so you can review them.";

        [PostProcessBuild(1100)]
        public static void LinkPhotosFramework(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            string projectPath = Path.Combine(path, "Unity-iPhone.xcodeproj/project.pbxproj");
            if (!File.Exists(projectPath))
            {
                Debug.LogWarning("[IosPhotosFrameworkLinker] project.pbxproj not found — skipping Photos link.");
                return;
            }

            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string mainTarget = project.GetUnityMainTargetGuid();
            string frameworkTarget = project.GetUnityFrameworkTargetGuid();

            LinkPhotosToTarget(project, mainTarget);
            LinkPhotosToTarget(project, frameworkTarget);

            project.WriteToFile(projectPath);
            Debug.Log("[IosPhotosFrameworkLinker] Linked Photos.framework (Unity-iPhone + UnityFramework).");
        }

        static void LinkPhotosToTarget(PBXProject project, string targetGuid)
        {
            if (string.IsNullOrEmpty(targetGuid))
                return;

            project.AddFrameworkToProject(targetGuid, PhotosFramework, false);
            project.AddBuildProperty(targetGuid, "OTHER_LDFLAGS", "-framework Photos");
        }

        [PostProcessBuild(1101)]
        public static void EnsurePhotoLibraryUsageDescription(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            string plistPath = Path.Combine(path, "Info.plist");
            if (!File.Exists(plistPath))
                return;

            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;

            if (!root.values.ContainsKey(AddPhotosUsageKey))
                root.SetString(AddPhotosUsageKey, AddPhotosUsageText);

            plist.WriteToFile(plistPath);
        }
    }
}
#endif
