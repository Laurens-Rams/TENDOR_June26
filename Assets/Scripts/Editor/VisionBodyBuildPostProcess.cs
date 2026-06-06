#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Links the system frameworks needed by the native Apple Vision 3D body-pose plugin
    /// (Assets/Plugins/iOS/VisionBody/VisionBodyBridge.mm). Unity does not auto-link system
    /// frameworks referenced from plugin source, so we add Vision + CoreVideo to the
    /// UnityFramework target (where the .mm compiles). The plugin is Objective-C++, so no
    /// Swift standard library embedding is required.
    /// </summary>
    public static class VisionBodyBuildPostProcess
    {
        [PostProcessBuild(900)]
        public static void LinkVisionFrameworks(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            string projectPath = Path.Combine(path, "Unity-iPhone.xcodeproj/project.pbxproj");
            if (!File.Exists(projectPath))
                return;

            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string frameworkTarget = project.GetUnityFrameworkTargetGuid();
            string mainTarget = project.GetUnityMainTargetGuid();

            foreach (var fwTarget in new[] { frameworkTarget, mainTarget })
            {
                if (string.IsNullOrEmpty(fwTarget))
                    continue;
                project.AddFrameworkToProject(fwTarget, "Vision.framework", false);
                project.AddFrameworkToProject(fwTarget, "CoreVideo.framework", false);
            }

            project.WriteToFile(projectPath);
            Debug.Log("[VisionBodyBuildPostProcess] Linked Vision.framework and CoreVideo.framework.");
        }
    }
}
#endif
