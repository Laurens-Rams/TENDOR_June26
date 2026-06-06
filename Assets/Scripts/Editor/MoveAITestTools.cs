using System.IO;
using UnityEditor;
using UnityEngine;
using BodyTracking.Data;
using BodyTracking.MoveAI;
using BodyTracking.Storage;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Offline iteration helpers for the Move AI path. The Move job is the slow/expensive part of the loop, but
    /// the only piece that still changes frequently is local parsing/baking. These menu items let you replay a
    /// previously downloaded MOTION_DATA archive through the parser (and optionally the fusion baker) instantly,
    /// with no re-recording and no re-submission to Move.
    ///
    /// Workflow: on device, every Move job extracts its archive (and saves the raw <c>motion_data.zip</c>) to
    /// <c>Application.persistentDataPath/MoveAIDebug/&lt;timestamp&gt;/</c>. Pull that folder off the device once
    /// (Xcode &gt; Devices &gt; the app container, or the Files app if exposed), then point these tools at the
    /// saved <c>motion_data.zip</c> and iterate on <see cref="MoveMotionParser"/> as many times as needed.
    /// </summary>
    public static class MoveAITestTools
    {
        const string LastZipKey = "TENDOR.MoveAI.LastZipPath";
        const string LastRecordingKey = "TENDOR.MoveAI.LastRecordingPath";

        // Bundled device container dump (user drops Xcode Download Container under Assets/AppData).
        const string BundledMotionJson = "Assets/AppData/Documents/MoveAIDebug/20260604_190808/track00_motion_data.json";
        const string BundledRecordingJson = "Assets/AppData/Documents/BodyTrackingRecordings/hip_recording_20260604_190443.json";

        [MenuItem("TENDOR/Move AI/Parse Bundled AppData Sample", priority = 90)]
        public static void ParseBundledSample()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), BundledMotionJson);
            if (!File.Exists(jsonPath))
            {
                EditorUtility.DisplayDialog("Move MOTION_DATA",
                    $"Bundled sample not found at:\n{jsonPath}\n\nDrop the Xcode app container under Assets/AppData/.",
                    "OK");
                return;
            }

            string json = File.ReadAllText(jsonPath);
            var motion = MoveMotionParser.ParseActorJson(json, MoveJointMap.CreateDefaultMixamo());
            if (motion == null || motion.FrameCount == 0)
            {
                EditorUtility.DisplayDialog("Move MOTION_DATA", "Parse failed — see Console.", "OK");
                return;
            }

            EditorUtility.DisplayDialog("Move MOTION_DATA",
                $"Parsed bundled sample OK.\n\nJoints: {motion.JointCount}\nFrames: {motion.FrameCount}\nfps: {motion.fps:F1}\nDuration: {motion.Duration:F2}s",
                "Nice");
        }

        [MenuItem("TENDOR/Move AI/Parse + Bake Bundled AppData Sample", priority = 91)]
        public static void ParseAndBakeBundledSample()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), BundledMotionJson);
            string recPath = Path.Combine(Directory.GetCurrentDirectory(), BundledRecordingJson);
            if (!File.Exists(jsonPath) || !File.Exists(recPath))
            {
                EditorUtility.DisplayDialog("Move Fusion",
                    $"Bundled files missing.\n\nMotion: {jsonPath}\nRecording: {recPath}", "OK");
                return;
            }

            var motion = MoveMotionParser.ParseActorJson(File.ReadAllText(jsonPath), MoveJointMap.CreateDefaultMixamo());
            var recording = LoadRecordingJson(recPath, out string recError);
            if (motion == null || motion.FrameCount == 0)
            {
                EditorUtility.DisplayDialog("Move Fusion", "Motion parse failed — see Console.", "OK");
                return;
            }
            if (recording == null)
            {
                EditorUtility.DisplayDialog("Move Fusion", $"Recording load failed: {recError}", "OK");
                return;
            }

            var asset = MoveAIFusionBaker.Bake(recording, motion, MoveJointMap.CreateDefaultMixamo(), MoveAIFusionBaker.Settings.Default);
            if (asset == null)
            {
                EditorUtility.DisplayDialog("Move Fusion", "Bake returned null — see Console.", "OK");
                return;
            }

            string outPath = Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/AppData/Documents/BodyTrackingRecordings/hip_recording_20260604_190443.fusion.json");
            bool saved = asset.Save(outPath);
            EditorUtility.DisplayDialog("Move Fusion",
                saved ? $"Baked + saved:\n{outPath}" : "Bake OK but save failed — see Console.", "OK");
        }

        [MenuItem("TENDOR/Move AI/Parse MOTION_DATA Zip", priority = 100)]
        public static void ParseZip()
        {
            string start = StartDir(EditorPrefs.GetString(LastZipKey, ""));
            string path = EditorUtility.OpenFilePanel(
                "Select Move MOTION_DATA zip (NOT the Xcode .xcappdata container)",
                start, "zip");
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString(LastZipKey, path);

            var motion = LoadMotionFromZip(path, out string error);
            if (motion == null)
            {
                EditorUtility.DisplayDialog("Move MOTION_DATA",
                    $"Parse failed.\n\n{error}\n\n" +
                    "If you downloaded the app container from Xcode, use:\n" +
                    "TENDOR → Move AI → Parse MOTION_DATA JSON\n" +
                    "and pick track00_motion_data.json inside MoveAIDebug/.",
                    "OK");
                return;
            }

            EditorUtility.DisplayDialog("Move MOTION_DATA",
                $"Parsed OK.\n\nJoints: {motion.JointCount}\nFrames: {motion.FrameCount}\nfps: {motion.fps}\nDuration: {motion.Duration:F2}s",
                "Nice");
        }

        [MenuItem("TENDOR/Move AI/Parse MOTION_DATA JSON", priority = 101)]
        public static void ParseJson()
        {
            string start = StartDir(EditorPrefs.GetString(LastZipKey, ""));
            string path = EditorUtility.OpenFilePanel(
                "Select track00_motion_data.json (from MoveAIDebug folder)", start, "json");
            if (string.IsNullOrEmpty(path)) return;

            string json = File.ReadAllText(path);
            var motion = MoveMotionParser.ParseActorJson(json, MoveJointMap.CreateDefaultMixamo());
            if (motion == null || motion.FrameCount == 0)
            {
                EditorUtility.DisplayDialog("Move MOTION_DATA",
                    "Parse failed — see the Console for the top-level keys / preview.", "OK");
                return;
            }

            EditorUtility.DisplayDialog("Move MOTION_DATA",
                $"Parsed OK.\n\nJoints: {motion.JointCount}\nFrames: {motion.FrameCount}\nfps: {motion.fps}\nDuration: {motion.Duration:F2}s",
                "Nice");
        }

        [MenuItem("TENDOR/Move AI/Parse + Bake Against Recording", priority = 102)]
        public static void ParseAndBake()
        {
            string zipStart = StartDir(EditorPrefs.GetString(LastZipKey, ""));
            string zipPath = EditorUtility.OpenFilePanel("Select a Move MOTION_DATA archive", zipStart, "zip");
            if (string.IsNullOrEmpty(zipPath)) return;
            EditorPrefs.SetString(LastZipKey, zipPath);

            string recStart = StartDir(EditorPrefs.GetString(LastRecordingKey, Application.persistentDataPath));
            string recPath = EditorUtility.OpenFilePanel("Select the paired hip recording JSON", recStart, "json");
            if (string.IsNullOrEmpty(recPath)) return;
            EditorPrefs.SetString(LastRecordingKey, recPath);

            var motion = LoadMotionFromZip(zipPath, out string error);
            if (motion == null)
            {
                EditorUtility.DisplayDialog("Move Fusion", $"Parse failed.\n\n{error}", "OK");
                return;
            }

            HipRecording recording = LoadRecordingJson(recPath, out string recError);
            if (recording == null)
            {
                EditorUtility.DisplayDialog("Move Fusion", $"Recording load failed.\n\n{recError}", "OK");
                return;
            }

            var map = MoveJointMap.CreateDefaultMixamo();
            var asset = MoveAIFusionBaker.Bake(recording, motion, map, MoveAIFusionBaker.Settings.Default);
            if (asset == null)
            {
                EditorUtility.DisplayDialog("Move Fusion", "Bake returned null — see Console.", "OK");
                return;
            }

            string savePath = EditorUtility.SaveFilePanel("Save fused asset", Path.GetDirectoryName(recPath),
                Path.GetFileNameWithoutExtension(recPath) + ".fusion", "json");
            if (string.IsNullOrEmpty(savePath))
            {
                Debug.Log("[MoveAITestTools] Bake OK (not saved — no path chosen).");
                return;
            }

            bool saved = asset.Save(savePath);
            EditorUtility.DisplayDialog("Move Fusion",
                saved ? $"Baked + saved:\n{savePath}" : "Bake OK but save failed — see Console.", "OK");
        }

        [MenuItem("TENDOR/Move AI/Open MoveAIDebug Folder", priority = 120)]
        public static void OpenDebugFolder()
        {
            string dir = Path.Combine(Application.persistentDataPath, "MoveAIDebug");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        static MoveMotion LoadMotionFromZip(string path, out string error)
        {
            error = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var motion = MoveMotionParser.ParseMotionDataZip(bytes, MoveJointMap.CreateDefaultMixamo());
                if (motion == null || motion.FrameCount == 0 || motion.JointCount == 0)
                {
                    error = "Parser returned no usable motion. For Xcode app containers use Parse MOTION_DATA JSON, not Zip.";
                    return null;
                }
                return motion;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static HipRecording LoadRecordingJson(string path, out string error)
        {
            error = null;
            try
            {
                var recording = JsonUtility.FromJson<HipRecording>(File.ReadAllText(path));
                recording?.NormalizeFormatAfterLoad();
                if (recording == null) error = "JsonUtility returned null.";
                return recording;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static string StartDir(string pathHint)
        {
            if (string.IsNullOrEmpty(pathHint)) return "";
            if (Directory.Exists(pathHint)) return pathHint;
            string dir = Path.GetDirectoryName(pathHint);
            return Directory.Exists(dir) ? dir : "";
        }
    }
}
