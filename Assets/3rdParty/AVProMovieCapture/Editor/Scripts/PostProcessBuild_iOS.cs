#if UNITY_IOS && UNITY_2017_1_OR_NEWER
using System.Collections.Generic;
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
		/// <summary>
		/// Unity exports native plugins under various <c>Libraries/</c> or <c>Frameworks/</c> paths depending on
		/// Unity version and where the asset lives under <c>Assets/</c>. The stock AVPro script only checked the
		/// old default path under <c>Plugins/RenderHeads/...</c>, which breaks when the package is under e.g.
		/// <c>Assets/3rdParty/AVProMovieCapture/...</c> — the framework never gets embedded and iOS fails at
		/// launch with: Library not loaded: @rpath/AVProMovieCapture.framework/AVProMovieCapture.
		/// </summary>
		static string FindAvproMovieCaptureFrameworkGuid(PBXProject project, string projectPath)
		{
			var candidates = new[]
			{
				// Original RenderHeads default install path
				"Frameworks/Plugins/RenderHeads/AVProMovieCapture/Runtime/Plugins/iOS/AVProMovieCapture.framework",
				// Typical Unity export when plugin lives under Assets/.../Runtime/Plugins/iOS
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

			// Last resort: parse PBXFileReference line for AVProMovieCapture.framework (Unity path string varies).
			var text = File.ReadAllText(projectPath);
			var m = Regex.Match(
				text,
				@"^([0-9A-Fa-f]{24}) /\* AVProMovieCapture\.framework \*/ = \{isa = PBXFileReference;",
				RegexOptions.Multiline);
			return m.Success ? m.Groups[1].Value : null;
		}

		[PostProcessBuild]
		public static void ModifyProject(BuildTarget buildTarget, string path)
		{
			if (buildTarget != BuildTarget.iOS)
				return;

			string projectPath = path + "/Unity-iPhone.xcodeproj/project.pbxproj";
			PBXProject project = new PBXProject();
			project.ReadFromFile(projectPath);

#if UNITY_2019_3_OR_NEWER
			string targetGuid = project.GetUnityMainTargetGuid();
			string unityFrameworkTargetGuid = project.GetUnityFrameworkTargetGuid();
#else
			string targetGuid = project.TargetGuidByName(PBXProject.GetUnityTargetName());
			string unityFrameworkTargetGuid = null;
#endif
			string fileGuid = FindAvproMovieCaptureFrameworkGuid(project, projectPath);
			if (!string.IsNullOrEmpty(fileGuid))
			{
				PBXProjectExtensions.AddFileToEmbedFrameworks(project, targetGuid, fileGuid);
#if UNITY_2019_3_OR_NEWER
				// UnityFramework loads this dylib; ensure runpath can resolve sibling frameworks in the app bundle.
				if (!string.IsNullOrEmpty(unityFrameworkTargetGuid))
				{
					const string runpaths = "$(inherited) @executable_path/Frameworks @loader_path/Frameworks";
					project.SetBuildProperty(unityFrameworkTargetGuid, "LD_RUNPATH_SEARCH_PATHS", runpaths);
				}
#endif
			}
			else
			{
				Debug.LogWarning("Failed to find AVProMovieCapture.framework in the generated Xcode project. Embed AVProMovieCapture.framework manually (Embed & Sign), or fix PostProcessBuild_iOS path detection.");
			}
			project.SetBuildProperty(targetGuid, "LD_RUNPATH_SEARCH_PATHS", "$(inherited) @executable_path/Frameworks");
			project.WriteToFile(projectPath);
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

			// Enable file sharing so that files can be pulled off of the device with iTunes
			rootDict.SetBoolean("UIFileSharingEnabled", true);
			// Enable this so that the files app can access the captured movies
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
#endif // UNITY_IOS && UNITY_2017_1_OR_NEWER
