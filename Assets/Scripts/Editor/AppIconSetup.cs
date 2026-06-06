using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.iOS;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Applies the icon PNGs under Assets/AppIcons to Unity Player Settings for iOS and Android builds.
    /// Run via TENDOR ▸ App Icons ▸ Apply App Icons.
    /// </summary>
    public static class AppIconSetup
    {
        private const string IconSetFolder = "Assets/AppIcons/Assets.xcassets/AppIcon.appiconset";
        private const string AppStoreIcon = "Assets/AppIcons/appstore.png";
        private const string PlayStoreIcon = "Assets/AppIcons/playstore.png";

        [MenuItem("TENDOR/App Icons/Apply App Icons", priority = 0)]
        [MenuItem("TENDOR/Apply App Icons", priority = 50)]
        public static void ApplyAppIcons()
        {
            var log = new StringBuilder();
            log.AppendLine("=== Apply App Icons ===");

            if (!AssetDatabase.IsValidFolder("Assets/AppIcons"))
            {
                EditorUtility.DisplayDialog("App Icons",
                    "No Assets/AppIcons folder found. Add your icon pack there first.", "OK");
                return;
            }

            int prepared = PrepareIconTextures(log);
            int ios = ApplyPlatformIcons(NamedBuildTarget.iOS, log);
            int android = ApplyPlatformIcons(NamedBuildTarget.Android, log);

            AssetDatabase.SaveAssets();
            log.AppendLine($"Done. Prepared {prepared} texture(s), assigned {ios} iOS slot(s), {android} Android slot(s).");
            Debug.Log(log.ToString());

            EditorUtility.DisplayDialog("App Icons",
                $"App icons applied.\n\niOS: {ios} slots\nAndroid: {android} slots\n\n" +
                "Verify under Edit ▸ Project Settings ▸ Player ▸ Icon, then rebuild for device.",
                "OK");
        }

        private static int PrepareIconTextures(StringBuilder log)
        {
            int count = 0;
            foreach (string path in CollectIconPaths())
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                bool changed = false;
                if (importer.textureType != TextureImporterType.Default)
                {
                    importer.textureType = TextureImporterType.Default;
                    changed = true;
                }
                if (importer.mipmapEnabled)
                {
                    importer.mipmapEnabled = false;
                    changed = true;
                }
                if (importer.alphaIsTransparency)
                {
                    importer.alphaIsTransparency = false;
                    changed = true;
                }

                int maxSize = GetMaxSizeForPath(path);
                if (importer.maxTextureSize < maxSize)
                {
                    importer.maxTextureSize = maxSize;
                    changed = true;
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                    count++;
                }
            }

            if (count > 0)
                log.AppendLine($"Reimported {count} icon texture(s) with icon-friendly import settings.");
            return count;
        }

        private static int ApplyPlatformIcons(NamedBuildTarget target, StringBuilder log)
        {
            int assigned = 0;
            PlatformIconKind[] kinds = PlayerSettings.GetSupportedIconKinds(target);
            if (kinds == null || kinds.Length == 0)
            {
                log.AppendLine($"{target.TargetName}: no supported icon kinds.");
                return 0;
            }

            foreach (PlatformIconKind kind in kinds)
            {
                PlatformIcon[] icons = PlayerSettings.GetPlatformIcons(target, kind);
                if (icons == null || icons.Length == 0) continue;

                bool changed = false;
                foreach (PlatformIcon icon in icons)
                {
                    string path = ResolveIconPath(target, kind, icon);
                    if (string.IsNullOrEmpty(path)) continue;

                    Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (tex == null)
                    {
                        log.AppendLine($"  WARNING: could not load '{path}' for {target.TargetName} {kind} {icon}.");
                        continue;
                    }

                    icon.SetTexture(tex);
                    changed = true;
                    assigned++;
                }

                if (changed)
                    PlayerSettings.SetPlatformIcons(target, kind, icons);
            }

            log.AppendLine($"{target.TargetName}: assigned {assigned} icon slot(s).");
            return assigned;
        }

        private static string ResolveIconPath(NamedBuildTarget target, PlatformIconKind kind, PlatformIcon icon)
        {
            if (target == NamedBuildTarget.iOS)
                return ResolveIosPath(kind, icon);
            if (target == NamedBuildTarget.Android)
                return ResolveAndroidPath(icon);
            return null;
        }

        private static string ResolveIosPath(PlatformIconKind kind, PlatformIcon icon)
        {
            // App Store marketing icon (1024).
            if (kind == iOSPlatformIconKind.Marketing)
                return FirstExisting(AppStoreIcon, IconPath("1024.png"));

            return IconPath($"{icon.width}.png");
        }

        private static string ResolveAndroidPath(PlatformIcon icon)
        {
            // Adaptive icon slots are 432/324/216 — use the Play Store master asset (no UnityEditor.Android dependency).
            if (icon.width >= 216)
                return FirstExisting(PlayStoreIcon, IconPath("512.png"));

            // Legacy / round launcher icons.
            string exact = IconPath($"{icon.width}.png");
            if (AssetExists(exact))
                return exact;

            int[] fallbacks = { 192, 180, 152, 144, 120, 108, 96, 87, 72, 64, 58, 48, 40, 36, 32, 29, 20 };
            int best = 0;
            int bestDelta = int.MaxValue;
            foreach (int size in fallbacks)
            {
                string candidate = IconPath($"{size}.png");
                if (!AssetExists(candidate)) continue;
                int delta = Mathf.Abs(size - icon.width);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = size;
                }
            }

            return best > 0 ? IconPath($"{best}.png") : FirstExisting(PlayStoreIcon, IconPath("512.png"));
        }

        private static string IconPath(string filename) => $"{IconSetFolder}/{filename}";

        private static string FirstExisting(params string[] paths)
        {
            foreach (string path in paths)
            {
                if (AssetExists(path))
                    return path;
            }
            return null;
        }

        private static IEnumerable<string> CollectIconPaths()
        {
            var list = new List<string>();
            var seen = new HashSet<string>();

            void TryAdd(string path)
            {
                if (!string.IsNullOrEmpty(path) && seen.Add(path))
                    list.Add(path);
            }

            TryAdd(AppStoreIcon);
            TryAdd(PlayStoreIcon);

            string iconSetAbsolute = Path.Combine(Directory.GetCurrentDirectory(), IconSetFolder);
            if (Directory.Exists(iconSetAbsolute))
            {
                foreach (string file in Directory.GetFiles(iconSetAbsolute, "*.png"))
                    TryAdd(ToAssetPath(file));
            }

            return list;
        }

        private static bool AssetExists(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) &&
            File.Exists(Path.Combine(Directory.GetCurrentDirectory(), assetPath));

        private static string ToAssetPath(string absolutePath)
        {
            string projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/');
            string normalized = absolutePath.Replace('\\', '/');
            if (normalized.StartsWith(projectRoot))
                return normalized.Substring(projectRoot.Length + 1);
            return normalized;
        }

        private static int GetMaxSizeForPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, out int size))
                return Mathf.NextPowerOfTwo(Mathf.Max(size, 32));
            if (name.Contains("appstore") || name.Contains("playstore"))
                return 1024;
            return 512;
        }
    }
}
