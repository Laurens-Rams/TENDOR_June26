#if UNITY_EDITOR
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Makes the runtime performance log (written by <c>PerfSampler</c> to the app's Documents folder on device)
    /// downloadable. Sets the Info.plist keys that expose the Documents directory to Finder and the Files app:
    ///   • <c>UIFileSharingEnabled</c> — the app's Documents folder appears under the device in Finder.
    ///   • <c>LSSupportsOpeningDocumentsInPlace</c> — the same folder is browsable from the Files app.
    /// (Xcode's "Download Container" already works for development builds; these keys add the easier Finder path.)
    /// </summary>
    public static class PerfLogBuildPostProcess
    {
        [PostProcessBuild(950)]
        public static void EnableFileSharing(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            string plistPath = Path.Combine(path, "Info.plist");
            if (!File.Exists(plistPath))
                return;

            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetBoolean("UIFileSharingEnabled", true);
            plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
            plist.WriteToFile(plistPath);

            Debug.Log("[PerfLogBuildPostProcess] Enabled file sharing (UIFileSharingEnabled + " +
                      "LSSupportsOpeningDocumentsInPlace) so the perf log can be downloaded from the app container.");

            TryEnableXcodeLogStreaming(path);
        }

        /// <summary>
        /// Xcode sometimes prints "Failed to initialize logging system" when launching on device. Apple recommends
        /// IDEPreferLogStreaming=YES on the Run scheme — inject it here so every Unity iOS export gets it.
        /// </summary>
        private static void TryEnableXcodeLogStreaming(string buildPath)
        {
            string schemeDir = Path.Combine(buildPath, "Unity-iPhone.xcodeproj", "xcshareddata", "xcschemes");
            if (!Directory.Exists(schemeDir))
                return;

            foreach (string schemePath in Directory.GetFiles(schemeDir, "*.xcscheme"))
            {
                string xml = File.ReadAllText(schemePath);
                if (xml.Contains("IDEPreferLogStreaming"))
                    continue;

                const string envBlock =
                    "      <EnvironmentVariables>\n" +
                    "         <EnvironmentVariable\n" +
                    "            key = \"IDEPreferLogStreaming\"\n" +
                    "            value = \"YES\"\n" +
                    "            isEnabled = \"YES\">\n" +
                    "         </EnvironmentVariable>\n" +
                    "      </EnvironmentVariables>\n";

                // Insert into the Run (Launch) action so device console streaming initializes reliably.
                string patched = Regex.Replace(
                    xml,
                    @"(<LaunchAction\b[^>]*>)(\s*)",
                    "$1\n" + envBlock,
                    RegexOptions.Singleline);

                if (patched == xml)
                    continue;

                File.WriteAllText(schemePath, patched);
                Debug.Log("[PerfLogBuildPostProcess] Set IDEPreferLogStreaming=YES in " + Path.GetFileName(schemePath));
            }
        }
    }
}
#endif
