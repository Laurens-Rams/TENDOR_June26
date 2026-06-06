#if UNITY_EDITOR
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;
using UnityEngine;

namespace BodyTracking.Editor
{
	/// <summary>
	/// Backup iOS post-process: ensures AVProMovieCapture.framework is embedded when the plugin lives under
	/// Assets/3rdParty. Runs after AVPro's own script (order 1000).
	/// </summary>
	public static class IosAvProFrameworkEmbedFix
	{
		[PostProcessBuild(1000)]
		public static void EmbedAvProFramework(BuildTarget target, string path)
		{
			if (target != BuildTarget.iOS)
				return;

			string projectPath = Path.Combine(path, "Unity-iPhone.xcodeproj/project.pbxproj");
			if (!File.Exists(projectPath))
				return;

			string pbxText = File.ReadAllText(projectPath);
			if (!pbxText.Contains("AVProMovieCapture.framework"))
				return;

			var project = new PBXProject();
			project.ReadFromFile(projectPath);

			string mainTarget = project.GetUnityMainTargetGuid();
			string frameworkTarget = project.GetUnityFrameworkTargetGuid();

			string fileGuid = null;
			foreach (var dir in Directory.GetDirectories(path, "AVProMovieCapture.framework", SearchOption.AllDirectories))
			{
				var rel = dir.Substring(path.Length).TrimStart('/', '\\').Replace('\\', '/');
				fileGuid = project.FindFileGuidByProjectPath(rel);
				if (!string.IsNullOrEmpty(fileGuid))
					break;
			}

			if (string.IsNullOrEmpty(fileGuid))
			{
				var m = Regex.Match(
					pbxText,
					@"^([0-9A-Fa-f]{24}) /\* [^*]*AVProMovieCapture\.framework[^*]* \*/ = \{isa = PBXFileReference;",
					RegexOptions.Multiline);
				if (m.Success)
					fileGuid = m.Groups[1].Value;
			}

			if (string.IsNullOrEmpty(fileGuid))
			{
				Debug.LogWarning("[IosAvProFrameworkEmbedFix] AVProMovieCapture.framework found in project but guid lookup failed.");
				return;
			}

			project.AddFileToBuild(frameworkTarget, fileGuid);
			PBXProjectExtensions.AddFileToEmbedFrameworks(project, mainTarget, fileGuid);

			const string runpaths = "$(inherited) @executable_path/Frameworks @loader_path/Frameworks";
			project.SetBuildProperty(mainTarget, "LD_RUNPATH_SEARCH_PATHS", runpaths);
			project.SetBuildProperty(frameworkTarget, "LD_RUNPATH_SEARCH_PATHS", runpaths);
			project.WriteToFile(projectPath);

			Debug.Log("[IosAvProFrameworkEmbedFix] Embedded AVProMovieCapture.framework.");
		}
	}
}
#endif
