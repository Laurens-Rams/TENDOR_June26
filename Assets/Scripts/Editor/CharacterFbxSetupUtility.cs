using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BodyTracking.Animation;
using BodyTracking.MoveAI;
using BodyTracking.Playback;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-shot pipeline for a new character FBX: humanoid import, URP materials from embedded/fbm textures,
    /// Move AI retarget map, and scene wiring.
    /// </summary>
    public static class CharacterFbxSetupUtility
    {
        public struct SetupResult
        {
            public bool success;
            public string log;
        }

        public static SetupResult SetupCharacterFbx(
            string fbxAssetPath,
            bool configureHumanoid = true,
            bool bindMaterials = true,
            bool buildRetargetMap = true,
            bool assignScene = true,
            string materialsOutputFolder = null)
        {
            var log = new StringBuilder();
            if (string.IsNullOrEmpty(fbxAssetPath) || !fbxAssetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return Fail(log, "Select a .fbx asset path.");

            if (AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath) == null)
                return Fail(log, $"Could not load GameObject at '{fbxAssetPath}'.");

            log.AppendLine($"=== Character FBX setup: {fbxAssetPath} ===");

            var importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;

            if (configureHumanoid && importer != null)
                ApplyHumanoidImportSettings(importer, log);

            if (bindMaterials)
            {
                int mats = BindMaterialsFromTextures(fbxAssetPath, materialsOutputFolder, log);
                log.AppendLine(mats > 0
                    ? $"Material binding: {mats} slot(s) remapped."
                    : "Material binding: no texture-based materials created (check .fbm folder).");
            }
            else if (configureHumanoid && importer != null)
            {
                importer.SaveAndReimport();
            }

            if (configureHumanoid)
                ValidateHumanoidAvatar(fbxAssetPath, log);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            MoveJointMap jointMap = null;
            string hipBoneName = null;

            if (buildRetargetMap && prefab != null)
            {
                jointMap = MoveJointMap.BuildFromHumanoidRig(prefab, out string mapReport);
                hipBoneName = MoveJointMap.GetPrimaryHipBoneName(prefab);
                log.AppendLine(mapReport);
                log.AppendLine($"Primary hip bone: {hipBoneName}");
            }

            if (assignScene && prefab != null)
            {
                int wired = WireScene(prefab, jointMap, hipBoneName, log);
                log.AppendLine(wired > 0
                    ? $"Scene wiring: updated {wired} component(s)."
                    : "Scene wiring: no FBXCharacterController / FusedCharacterPlayer found in open scene.");
            }

            AssetDatabase.SaveAssets();
            log.AppendLine("Done.");
            return new SetupResult { success = true, log = log.ToString() };
        }

        static SetupResult Fail(StringBuilder log, string message)
        {
            log.AppendLine(message);
            return new SetupResult { success = false, log = log.ToString() };
        }

        public static bool ConfigureHumanoidImport(string fbxPath, StringBuilder log)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                log.AppendLine("Not a model importer.");
                return false;
            }

            ApplyHumanoidImportSettings(importer, log);
            importer.SaveAndReimport();
            return ValidateHumanoidAvatar(fbxPath, log);
        }

        static void ApplyHumanoidImportSettings(ModelImporter importer, StringBuilder log)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.optimizeGameObjects = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.External;
            log.AppendLine("Humanoid: import settings staged (single reimport after material bind).");
        }

        static bool ValidateHumanoidAvatar(string fbxPath, StringBuilder log)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var animator = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
            bool valid = animator != null && animator.avatar != null && animator.avatar.isValid && animator.isHuman;

            log.AppendLine(valid
                ? "Humanoid: avatar created and valid."
                : "Humanoid: import settings applied but avatar is not valid — open Rig tab and click Configure if needed.");

            if (!valid)
                log.AppendLine("WARNING: Humanoid setup may need manual Rig configuration in the Model Import Settings.");

            return valid;
        }

        public static int BindMaterialsFromTextures(string fbxPath, string materialsOutputFolder, StringBuilder log)
        {
            if (AssetImporter.GetAtPath(fbxPath) as ModelImporter == null)
                return 0;

            if (string.IsNullOrEmpty(materialsOutputFolder))
            {
                string fbxDir = Path.GetDirectoryName(fbxPath)?.Replace('\\', '/');
                materialsOutputFolder = $"{fbxDir}/Materials";
            }

            return AvaturnMaterialBinder.Bind(fbxPath, materialsOutputFolder, log);
        }

        static int WireScene(GameObject prefab, MoveJointMap jointMap, string hipBoneName, StringBuilder log)
        {
            int updated = 0;

            foreach (var controller in UnityEngine.Object.FindObjectsByType<FBXCharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var so = new SerializedObject(controller);
                so.FindProperty("characterPrefab").objectReferenceValue = prefab;
                if (!string.IsNullOrEmpty(hipBoneName))
                    PrependHipBoneName(so.FindProperty("hipBoneNames"), hipBoneName);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(controller);
                updated++;
                log.AppendLine($"  FBXCharacterController '{controller.name}' -> {prefab.name}");
            }

            if (jointMap != null)
            {
                foreach (var player in UnityEngine.Object.FindObjectsByType<FusedCharacterPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    ApplyJointMap(player, "jointMap", jointMap);
                    var so = new SerializedObject(player);
                    so.FindProperty("fbxCharacterController").objectReferenceValue =
                        UnityEngine.Object.FindAnyObjectByType<FBXCharacterController>();
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(player);
                    updated++;
                    log.AppendLine($"  FusedCharacterPlayer '{player.name}' joint map ({jointMap.entries.Count} entries)");
                }

                foreach (var coord in UnityEngine.Object.FindObjectsByType<MoveAIFusionCoordinator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    ApplyJointMap(coord, "jointMap", jointMap);
                    EditorUtility.SetDirty(coord);
                    updated++;
                    log.AppendLine($"  MoveAIFusionCoordinator '{coord.name}' joint map updated");
                }
            }

            if (updated > 0)
                EditorSceneManager.MarkAllScenesDirty();

            return updated;
        }

        static void ApplyJointMap(UnityEngine.Object target, string propertyName, MoveJointMap map)
        {
            var so = new SerializedObject(target);
            var mapProp = so.FindProperty(propertyName);
            if (mapProp == null)
                return;

            var entries = mapProp.FindPropertyRelative("entries");
            if (entries == null)
                return;

            entries.ClearArray();
            for (int i = 0; i < map.entries.Count; i++)
            {
                entries.InsertArrayElementAtIndex(i);
                var el = entries.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("moveJoint").stringValue = map.entries[i].moveJoint;
                el.FindPropertyRelative("bone").stringValue = map.entries[i].bone;
            }

            so.ApplyModifiedProperties();
        }

        static void PrependHipBoneName(SerializedProperty arrayProp, string boneName)
        {
            if (arrayProp == null || !arrayProp.isArray)
                return;

            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                if (string.Equals(arrayProp.GetArrayElementAtIndex(i).stringValue, boneName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            arrayProp.InsertArrayElementAtIndex(0);
            arrayProp.GetArrayElementAtIndex(0).stringValue = boneName;
        }
    }
}
