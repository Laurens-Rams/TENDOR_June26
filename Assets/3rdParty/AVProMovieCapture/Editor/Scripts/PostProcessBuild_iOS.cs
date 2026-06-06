#if UNITY_EDITOR
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;

//-----------------------------------------------------------------------------
// Copyright 2012-2022 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

namespace RenderHeads.Media.AVProMovieCapture.Editor
{
	public class PostProcessBuild_iOS
	{
		static string FindAvproMovieCaptureFrameworkGuid(PBXProject project, string buildPath, string projectPath)
		{
			var candidates = new[]
			{
				"Frameworks/Plugins/RenderHeads/AVProMovieCapture/Runtime/Plugins/iOS/AVProMovieCapture.framework",
				"Frameworks/3rdParty/AVProMovieCapture/Runtime/Plugins/iOS/AVProMovieCapture.framework",
				"Libraries/3rdParty/AVProMovieCapture/Runtime/Plugins/iOS/AVProMovieCapture.framework",
				"Libraries/AVProMovieCapture/Runtime/Plugins/iOS/AVProMovieCapture.framework",
				"Libraries/Plugins/iOS/AVProMovieCapture.framework",
				"Frameworks/Plugins/iOS/AVProMovieCapture.framework",
				"Libraries/AVProMovieCapture.framework",
				"Frameworks/AVProMovieCapture.framework",
			};

			foreach (var rel in candidates)
			{
				var guid = project.FindFileGuidByProjectPath(rel);
				if (!string.IsNullOrEmpty(guid))
					return guid;
			}

			// Match exported folder on disk to a project path (Unity 2022+ path strings vary).
			if (Directory.Exists(buildPath))
			{
				foreach (var dir in Directory.GetDirectories(buildPath, "AVProMovieCapture.framework", SearchOption.AllDirectories))
				{
					var rel = dir.Substring(buildPath.Length).TrimStart('/', '\\').Replace('\\', '/');
					var guid = project.FindFileGuidByProjectPath(rel);
					if (!string.IsNullOrEmpty(guid))
						return guid;
				}
			}

			var text = File.ReadAllText(projectPath);

			var named = Regex.Match(
				text,
				@"^([0-9A-Fa-f]{24}) /\* [^*]*AVProMovieCapture\.framework[^*]* \*/ = \{isa = PBXFileReference;",
				RegexOptions.Multiline);
			if (named.Success)
				return named.Groups[1].Value;

			// Any PBXFileReference block whose path mentions AVProMovieCapture.framework.
			foreach (Match block in Regex.Matches(
				text,
				@"^([0-9A-Fa-f]{24}) /\* .+? \*/ = \{isa = PBXFileReference;.*?path = (?:""?)?([^;""]+?)(?:""?)?;.*?\};",
				RegexOptions.Multiline | RegexOptions.Singleline))
			{
				if (block.Groups[2].Value.Contains("AVProMovieCapture.framework"))
					return block.Groups[1].Value;
			}

			return null;
		}

		[PostProcessBuild(999)]
		public static void ModifyProject(BuildTarget buildTarget, string path)
		{
			if (buildTarget != BuildTarget.iOS)
				return;

			string projectPath = Path.Combine(path, "Unity-iPhone.xcodeproj/project.pbxproj");
			if (!File.Exists(projectPath))
			{
				Debug.LogWarning("[AVProMovieCapture] Xcode project not found; skipping framework embed.");
				return;
			}

			var project = new PBXProject();
			project.ReadFromFile(projectPath);

#if UNITY_2019_3_OR_NEWER
			string targetGuid = project.GetUnityMainTargetGuid();
			string unityFrameworkTargetGuid = project.GetUnityFrameworkTargetGuid();
#else
			string targetGuid = project.TargetGuidByName(PBXProject.GetUnityTargetName());
			string unityFrameworkTargetGuid = null;
#endif

			string fileGuid = FindAvproMovieCaptureFrameworkGuid(project, path, projectPath);
			if (string.IsNullOrEmpty(fileGuid))
			{
				Debug.LogError(
					"[AVProMovieCapture] Could not find AVProMovieCapture.framework in the Xcode project. " +
					"The app will crash on launch. Rebuild from Unity, or in Xcode set AVProMovieCapture.framework to Embed & Sign.");
				return;
			}

#if UNITY_2019_3_OR_NEWER
			if (!string.IsNullOrEmpty(unityFrameworkTargetGuid))
				project.AddFileToBuild(unityFrameworkTargetGuid, fileGuid);
#endif

				PBXProjectExtensions.AddFileToEmbedFrameworks(project, targetGuid, fileGuid);

			const string runpaths = "$(inherited) @executable_path/Frameworks @loader_path/Frameworks";
			project.SetBuildProperty(targetGuid, "LD_RUNPATH_SEARCH_PATHS", runpaths);
#if UNITY_2019_3_OR_NEWER
				if (!string.IsNullOrEmpty(unityFrameworkTargetGuid))
					project.SetBuildProperty(unityFrameworkTargetGuid, "LD_RUNPATH_SEARCH_PATHS", runpaths);
#endif

			project.WriteToFile(projectPath);
			Debug.Log("[AVProMovieCapture] Embedded AVProMovieCapture.framework for iOS (Embed & Sign).");
		}

		[PostProcessBuild]
		public static void ModfifyPlist(BuildTarget buildTarget, string path)
		{
			if (buildTarget != BuildTarget.iOS)
				return;

			string plistPath = Path.Combine(path, "Info.plist");
			if (!File.Exists(plistPath))
			{
				Debug.LogWarning(@"Unable to locate Info.plist, you may need to add the following keys yourself:
	NSPhotoLibraryUsageDescription,
	NSPhotoLibraryAddUsageDescription");
				return;
			}

			Debug.Log("Modifying the Info.plist file at: " + plistPath);

			PlistDocument plist = new PlistDocument();
			plist.ReadFromFile(plistPath);

			PlistElementDict rootDict = plist.root;

			rootDict.SetBoolean("UIFileSharingEnabled", true);
			rootDict.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);

			SerializedObject settings = Settings.GetSerializedSettings();

			SerializedProperty propPhotoLibraryUsageDescription = settings.FindProperty("_photoLibraryUsageDescription");
			string photoLibraryUsageDescription = propPhotoLibraryUsageDescription.stringValue;
			if (photoLibraryUsageDescription != null && photoLibraryUsageDescription.Length > 0)
			{
				Debug.Log("Adding 'NSPhotoLibraryUsageDescription' to Info.plist");
				rootDict.SetString("NSPhotoLibraryUsageDescription", photoLibraryUsageDescription);
			}

			SerializedProperty propPhotoLibraryAddUsageDescription = settings.FindProperty("_photoLibraryAddUsageDescription");
			string photoLibraryAddUsageDescription = propPhotoLibraryAddUsageDescription.stringValue;
			if (photoLibraryAddUsageDescription != null && photoLibraryAddUsageDescription.Length > 0)
			{
				Debug.Log("Adding 'NSPhotoLibraryAddUsageDescription' to Info.plist");
				rootDict.SetString("NSPhotoLibraryAddUsageDescription", photoLibraryAddUsageDescription);
			}

			File.WriteAllText(plistPath, plist.WriteToString());

			Debug.Log("Finished modifying the Info.plist");
		}
	}
}
#endif // UNITY_EDITOR
