using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BodyTracking.Animation;
using BodyTracking.Playback;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-click setup for the in-app "cycle character" button.
    ///
    /// Drop every character .fbx you want to cycle into <see cref="CharactersFolder"/>, then run
    /// TENDOR ▸ Characters ▸ Build Character Switcher From Folder. This:
    ///   1. Forces each FBX to Humanoid import (+ binds URP materials), so the Move AI retarget works on any rig.
    ///   2. Creates/【reuses】a "Characters" group GameObject in the open scene and parents one instance of each
    ///      FBX under it (disabled — the player reveals the selected one during playback).
    ///   3. Adds a <see cref="CharacterSwitcher"/> to that group and wires Characters Parent + Fused Player.
    ///
    /// To finish, select your UI Button and run TENDOR ▸ Characters ▸ Wire Selected Button To Cycle Character.
    /// </summary>
    public static class CharacterSwitcherSetup
    {
        // Where to put the character FBX files. Created on demand if missing.
        private const string CharactersFolder = "Assets/DeepMotion/Characters";
        private const string GroupName = "Characters";

        [MenuItem("TENDOR/Characters/Build Character Switcher From Folder", priority = 30)]
        public static void BuildFromFolder()
        {
            var log = new StringBuilder();
            log.AppendLine("=== Build Character Switcher ===");

            EnsureFolderExists(CharactersFolder, log);

            var fbxPaths = FindFbxInFolder(CharactersFolder);
            if (fbxPaths.Count == 0)
            {
                string msg = $"No .fbx files found in '{CharactersFolder}'.\n\n" +
                             "Drop your character models into that folder in the Project window, then run this menu again.";
                Debug.LogWarning("[CharacterSwitcherSetup] " + msg);
                EditorUtility.DisplayDialog("Character Switcher", msg, "OK");
                EditorUtility.RevealInFinder(CharactersFolder);
                return;
            }

            // 1. Humanoid + materials for each FBX so the retarget "just works".
            foreach (string fbxPath in fbxPaths)
            {
                var result = CharacterFbxSetupUtility.SetupCharacterFbx(
                    fbxPath,
                    configureHumanoid: true,
                    bindMaterials: true,
                    buildRetargetMap: false, // humanoid avatar mapping handles retarget; don't fight the shared map
                    assignScene: false);     // we wire the switcher below instead of the single-character path
                log.AppendLine($"FBX prepared: {Path.GetFileName(fbxPath)} (success={result.success})");
            }

            // 2. Group GameObject in the open scene.
            GameObject group = GameObject.Find(GroupName);
            if (group == null)
            {
                group = new GameObject(GroupName);
                Undo.RegisterCreatedObjectUndo(group, "Create Characters group");
                log.AppendLine($"Created group GameObject '{GroupName}'.");
            }
            else
            {
                log.AppendLine($"Reusing existing group GameObject '{GroupName}'.");
            }

            // 3. Instantiate one of each FBX under the group (skip ones already present by name).
            var existingChildren = new HashSet<string>();
            for (int i = 0; i < group.transform.childCount; i++)
                existingChildren.Add(group.transform.GetChild(i).name);

            int added = 0;
            foreach (string fbxPath in fbxPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (prefab == null) continue;
                if (existingChildren.Contains(prefab.name)) continue;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null)
                {
                    log.AppendLine($"  WARNING: could not instantiate '{prefab.name}'.");
                    continue;
                }

                Undo.RegisterCreatedObjectUndo(instance, "Add character");
                instance.name = prefab.name;
                instance.transform.SetParent(group.transform, false);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;
                instance.SetActive(false); // player shows the selected one during playback
                added++;
                log.AppendLine($"  Added character '{instance.name}'.");
            }

            // 4. CharacterSwitcher on the group, wired to the group + the fused player.
            var switcher = group.GetComponent<CharacterSwitcher>();
            if (switcher == null)
            {
                switcher = Undo.AddComponent<CharacterSwitcher>(group);
                log.AppendLine("Added CharacterSwitcher component.");
            }

            var fusedPlayer = UnityEngine.Object.FindAnyObjectByType<FusedCharacterPlayer>();
            var fbxController = UnityEngine.Object.FindAnyObjectByType<FBXCharacterController>();

            var so = new SerializedObject(switcher);
            so.FindProperty("charactersParent").objectReferenceValue = group.transform;
            so.FindProperty("fusedPlayer").objectReferenceValue = fusedPlayer;
            // Leave fbxCharacterController empty by default (fused playback is the live path). Wire only if present
            // AND the legacy BodyTrackingPlayer FBX path is in use; harmless to leave unset otherwise.
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(switcher);

            log.AppendLine(fusedPlayer != null
                ? $"Wired Fused Player -> '{fusedPlayer.name}'."
                : "WARNING: No FusedCharacterPlayer found in the open scene — assign it on CharacterSwitcher manually.");

            EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = group;

            log.AppendLine($"Done. {added} character(s) added; {fbxPaths.Count} FBX prepared.");
            log.AppendLine("Next: select your UI Button and run TENDOR ▸ Characters ▸ Wire Selected Button To Cycle Character.");
            Debug.Log("[CharacterSwitcherSetup] " + log);
            EditorUtility.DisplayDialog("Character Switcher",
                $"Set up {fbxPaths.Count} character(s) under '{GroupName}'.\n\n" +
                "Now select your UI Button and run:\nTENDOR ▸ Characters ▸ Wire Selected Button To Cycle Character.",
                "OK");
        }

        // Button name (in the main panel) the Next Character button should sit beneath. This is the
        // "load map by ID" button. Matched case-insensitively by name substring.
        private const string AnchorButtonNameContains = "Load";

        [MenuItem("TENDOR/Characters/Create + Wire Next Character Button", priority = 31)]
        public static void CreateAndWireNextCharacterButton()
        {
            // The button we want to sit beneath (the map-ID / Load button in the main, always-visible panel).
            Button anchor = FindAnchorButton();
            if (anchor == null)
            {
                EditorUtility.DisplayDialog("Next Character Button",
                    "No UI Button found in the open scene to anchor to. " +
                    "Open the scene that has your Record/Load buttons and try again.", "OK");
                return;
            }

            Transform parent = anchor.transform.parent;

            // Reuse an existing NextCharacterButton (it may currently live in a hidden panel) — move it into the
            // visible panel instead of creating a duplicate. Otherwise clone the anchor so the style matches.
            GameObject buttonGo = FindExistingButtonByName("NextCharacterButton");
            if (buttonGo != null)
            {
                Undo.SetTransformParent(buttonGo.transform, parent, "Move Next Character Button");
                buttonGo.transform.SetParent(parent, false);
            }
            else
            {
                buttonGo = (GameObject)UnityEngine.Object.Instantiate(anchor.gameObject, parent);
                Undo.RegisterCreatedObjectUndo(buttonGo, "Create Next Character Button");
            }

            buttonGo.name = "NextCharacterButton";
            buttonGo.SetActive(true);

            // Match the anchor's size/anchors, then place it one row directly below the anchor button.
            var rect = buttonGo.GetComponent<RectTransform>();
            var anchorRect = anchor.GetComponent<RectTransform>();
            PlaceBelowButton(rect, anchorRect);

            // Label.
            var label = buttonGo.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = "Next Character";

            // Wire OnClick → CycleCharacter (drop any inherited persistent calls first).
            var button = buttonGo.GetComponent<Button>();
            for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(button.onClick, i);

            var switcher = UnityEngine.Object.FindAnyObjectByType<CharacterSwitcher>();
            if (switcher != null)
                UnityEventTools.AddPersistentListener(button.onClick, switcher.CycleCharacter);

            EditorUtility.SetDirty(buttonGo);
            EditorSceneManager.MarkAllScenesDirty();
            Selection.activeGameObject = buttonGo;

            Debug.Log($"[CharacterSwitcherSetup] Next Character button placed under '{anchor.name}' in '{parent.name}' " +
                      $"(wired={switcher != null}).");
            EditorUtility.DisplayDialog("Next Character Button",
                switcher != null
                    ? $"'Next Character' button is now under '{anchor.name}' and wired to cycle characters."
                    : $"'Next Character' button is now under '{anchor.name}'.\n\nNo CharacterSwitcher found yet — run " +
                      "'Build Character Switcher From Folder', then 'Wire Selected Button To Cycle Character' with it selected.",
                "OK");
        }

        [MenuItem("TENDOR/Characters/Wire Selected Button To Cycle Character", priority = 32)]
        public static void WireSelectedButton()
        {
            var go = Selection.activeGameObject;
            var button = go != null ? go.GetComponent<Button>() : null;
            if (button == null)
            {
                EditorUtility.DisplayDialog("Wire Button",
                    "Select a UI Button in the Hierarchy first, then run this menu.", "OK");
                return;
            }

            var switcher = UnityEngine.Object.FindAnyObjectByType<CharacterSwitcher>();
            if (switcher == null)
            {
                EditorUtility.DisplayDialog("Wire Button",
                    "No CharacterSwitcher found in the open scene. Run 'Build Character Switcher From Folder' first.", "OK");
                return;
            }

            // Avoid adding a duplicate listener if it's already wired.
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                if (button.onClick.GetPersistentTarget(i) == switcher &&
                    button.onClick.GetPersistentMethodName(i) == nameof(CharacterSwitcher.CycleCharacter))
                {
                    EditorUtility.DisplayDialog("Wire Button",
                        $"'{button.name}' is already wired to CharacterSwitcher.CycleCharacter.", "OK");
                    return;
                }
            }

            UnityEventTools.AddPersistentListener(button.onClick, switcher.CycleCharacter);
            EditorUtility.SetDirty(button);
            EditorSceneManager.MarkAllScenesDirty();

            Debug.Log($"[CharacterSwitcherSetup] Wired button '{button.name}' OnClick -> CharacterSwitcher.CycleCharacter.");
            EditorUtility.DisplayDialog("Wire Button",
                $"Wired '{button.name}' → CharacterSwitcher.CycleCharacter.\nEach press cycles to the next model.", "OK");
        }

        /// <summary>
        /// Find the "load map by ID" button to sit beneath: prefer a button whose name contains
        /// <see cref="AnchorButtonNameContains"/> AND whose parent panel also holds a Record button (i.e. the main,
        /// always-visible button row — not the playback-only panel). Falls back to any Load/Record button.
        /// </summary>
        private static Button FindAnchorButton()
        {
            var buttons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (buttons == null || buttons.Length == 0)
                return null;

            Button anyMatch = null;
            foreach (var b in buttons)
            {
                if (b.name.IndexOf(AnchorButtonNameContains, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                anyMatch ??= b;

                // Prefer the one whose parent also contains the Record button (the main button row).
                var parent = b.transform.parent;
                if (parent == null) continue;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var sib = parent.GetChild(i);
                    if (sib != b.transform && sib.name.IndexOf("Record", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return b;
                }
            }
            if (anyMatch != null) return anyMatch;

            foreach (var b in buttons)
                if (b.name.IndexOf("Record", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return b;
            return buttons[0];
        }

        private static GameObject FindExistingButtonByName(string name)
        {
            foreach (var b in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (b.name == name)
                    return b.gameObject;
            return null;
        }

        /// <summary>Place <paramref name="newRect"/> one row directly below <paramref name="anchorRect"/>, same column/size/anchors.</summary>
        private static void PlaceBelowButton(RectTransform newRect, RectTransform anchorRect)
        {
            const float gap = 30f;
            if (anchorRect == null) return;

            newRect.anchorMin = anchorRect.anchorMin;
            newRect.anchorMax = anchorRect.anchorMax;
            newRect.pivot = anchorRect.pivot;
            newRect.sizeDelta = anchorRect.sizeDelta;
            newRect.localScale = Vector3.one;

            float anchorBottom = anchorRect.anchoredPosition.y - anchorRect.sizeDelta.y * (1f - anchorRect.pivot.y);
            float top = anchorBottom - gap;
            float y = top - newRect.sizeDelta.y * newRect.pivot.y;
            newRect.anchoredPosition = new Vector2(anchorRect.anchoredPosition.x, y);
        }

        private static void EnsureFolderExists(string folder, StringBuilder log)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent) && AssetDatabase.IsValidFolder(parent))
            {
                AssetDatabase.CreateFolder(parent, leaf);
                AssetDatabase.Refresh();
                log.AppendLine($"Created folder '{folder}'.");
            }
        }

        private static List<string> FindFbxInFolder(string folder)
        {
            var paths = new List<string>();
            if (!AssetDatabase.IsValidFolder(folder))
                return paths;

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) &&
                    Path.GetDirectoryName(path)?.Replace('\\', '/') == folder)
                {
                    paths.Add(path);
                }
            }

            paths.Sort(System.StringComparer.OrdinalIgnoreCase);
            return paths;
        }
    }
}
