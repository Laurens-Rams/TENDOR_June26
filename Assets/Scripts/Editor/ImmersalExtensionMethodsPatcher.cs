using System.IO;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Applies the TENDOR patch to Immersal's ExtensionMethods in PackageCache. The git package does not
    /// include CachedScreenOrientation (needed by ImmersalDelayedInitializer and background localization).
    /// Canonical patch source: Packages/com.immersal.core/Runtime/Scripts/Util/ExtensionMethods.cs
    /// </summary>
    [InitializeOnLoad]
    static class ImmersalExtensionMethodsPatcher
    {
        const string PatchRelativePath = "Runtime/Scripts/Util/ExtensionMethods.cs";
        const string PatchMarker = "TENDOR PATCH";

        static ImmersalExtensionMethodsPatcher()
        {
            ApplyIfNeeded();
        }

        static void ApplyIfNeeded()
        {
            var patchSource = Path.GetFullPath(
                Path.Combine(Application.dataPath, "../Packages/com.immersal.core", PatchRelativePath));
            if (!File.Exists(patchSource))
                return;

            var packageCacheRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "../Library/PackageCache"));
            if (!Directory.Exists(packageCacheRoot))
                return;

            foreach (var dir in Directory.GetDirectories(packageCacheRoot, "com.immersal.core@*"))
            {
                var target = Path.Combine(dir, PatchRelativePath);
                if (!File.Exists(target))
                    continue;

                var targetText = File.ReadAllText(target);
                if (targetText.Contains(PatchMarker))
                    continue;

                File.Copy(patchSource, target, overwrite: true);
                Debug.Log("[ImmersalExtensionMethodsPatcher] Applied ExtensionMethods patch to " + target);
            }
        }
    }
}
