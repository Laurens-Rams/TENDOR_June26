using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BodyTracking.Animation;
using BodyTracking.Playback;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-click wiring for GLB-only playback. Configures the scene's <see cref="CharacterSwitcher"/> so the
    /// top-left toggle cycles through GLB display characters only, each driven by the Move AI GLB muscle retarget.
    ///
    /// What it does (idempotent — safe to re-run after adding more GLB characters):
    ///   • Treats the child named exactly "model"/"model 1" (or one carrying an FBXCharacterController) as the FBX
    ///     fallback, and children containing "source"/"moveai" as the Move AI motion source — both are DISABLED so they
    ///     don't display (the motion source is loaded from file at runtime; the static scene copy just T-poses).
    ///   • Every other child is enabled and becomes a cyclable GLB display character.
    ///   • Sets the switcher to auto-collect the parent, default articulation = MoveGlb, start index 0, and clears
    ///     the legacy FBX controller link so it can't force-show a character.
    ///
    /// The FBX assets/code stay in the project as a fallback; this only changes the scene wiring. Save the scene
    /// afterwards to keep the changes.
    /// </summary>
    public static class GlbCharacterSetupTool
    {
        const string ModelArtPath = "Assets/DeepMotion/Characters/modelART.glb";
        const string PriyalPath = "Assets/DeepMotion/Characters/priyal.glb";

        // Repairs a Characters object whose children were flattened by an earlier buggy run. It wipes the
        // Characters subtree and rebuilds it deterministically from the source GLB assets:
        //   • Avaturn_Target  = modelART.glb  (scale 0.2, active)   — the display avatar
        //   • priyal          = priyal.glb    (inactive)            — second cyclable character
        // Then it re-applies the GLB-only switcher wiring. Save the scene afterwards.
        [MenuItem("TENDOR/Characters/Repair Characters (Rebuild from GLB)")]
        public static void RepairCharacters()
        {
            var switcher = Object.FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            if (switcher == null)
            {
                EditorUtility.DisplayDialog("Repair Characters",
                    "No CharacterSwitcher found in the open scene. Open the scene that contains your Characters " +
                    "object and try again.", "OK");
                return;
            }

            var modelArt = AssetDatabase.LoadAssetAtPath<GameObject>(ModelArtPath);
            var priyal = AssetDatabase.LoadAssetAtPath<GameObject>(PriyalPath);
            if (modelArt == null || priyal == null)
            {
                EditorUtility.DisplayDialog("Repair Characters",
                    $"Could not load source GLBs:\n  {ModelArtPath} → {(modelArt != null ? "ok" : "MISSING")}\n" +
                    $"  {PriyalPath} → {(priyal != null ? "ok" : "MISSING")}\n\nMake sure both are imported.", "OK");
                return;
            }

            Transform parent = switcher.transform;

            if (!EditorUtility.DisplayDialog("Repair Characters",
                    $"This will DELETE all {parent.childCount} current children under '{parent.name}' " +
                    "and rebuild:\n  • Avaturn_Target (modelART.glb, scale 0.2)\n  • priyal (priyal.glb)\n\n" +
                    "Proceed?", "Rebuild", "Cancel"))
                return;

            Undo.RegisterFullObjectHierarchyUndo(parent.gameObject, "Repair Characters");

            // Wipe the corrupted subtree.
            for (int i = parent.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(parent.GetChild(i).gameObject);

            var kept = new List<string>();

            var avatar = (GameObject)PrefabUtility.InstantiatePrefab(modelArt, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(avatar, "Repair Characters");
            avatar.name = "Avaturn_Target";
            avatar.transform.SetParent(parent, false);
            avatar.transform.localPosition = Vector3.zero;
            avatar.transform.localRotation = Quaternion.identity;
            avatar.transform.localScale = Vector3.one * 0.2f;
            avatar.SetActive(true);
            kept.Add(avatar.name);

            var second = (GameObject)PrefabUtility.InstantiatePrefab(priyal, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(second, "Repair Characters");
            second.name = "priyal";
            second.transform.SetParent(parent, false);
            second.transform.localPosition = Vector3.zero;
            second.transform.localRotation = Quaternion.identity;
            second.transform.localScale = Vector3.one * 0.2f;
            second.SetActive(false);
            kept.Add(second.name);

            WireSwitcher(switcher, parent);

            EditorSceneManager.MarkSceneDirty(switcher.gameObject.scene);
            string keptStr = string.Join(", ", kept);
            Debug.Log($"[GlbCharacterSetup] Repaired Characters. Rebuilt: {keptStr}. SAVE THE SCENE (Cmd+S).");
            EditorUtility.DisplayDialog("Repair Characters",
                $"Done. Rebuilt characters:\n  {keptStr}\n\nDefault articulation = GLB.\n\n" +
                "Remember to SAVE THE SCENE (Cmd+S).", "OK");
        }

        [MenuItem("TENDOR/Characters/Use GLB Characters Only")]
        public static void SetupGlbOnly()
        {
            var switcher = Object.FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            if (switcher == null)
            {
                EditorUtility.DisplayDialog("GLB Character Setup",
                    "No CharacterSwitcher found in the open scene. Open the scene that contains your Characters " +
                    "object and try again.", "OK");
                return;
            }

            Transform parent = switcher.transform; // the switcher lives on the "Characters" object
            var kept = new List<string>();
            var disabled = new List<string>();

            Undo.RegisterFullObjectHierarchyUndo(parent.gameObject, "Use GLB Characters Only");

            ReparentLooseGlbs(parent, kept);
            ImportGlbAssetsUnder(parent, kept);

            for (int i = 0; i < parent.childCount; i++)
            {
                GameObject child = parent.GetChild(i).gameObject;
                string n = child.name.ToLowerInvariant().Trim();
                // FBX fallback is the exact "model"/"model 1" object (or anything carrying an FBXCharacterController).
                // Use an exact match so GLB characters like "modelART" are NOT mistaken for the FBX.
                bool isFbxFallback = n == "model" || n == "model 1" ||
                                     child.GetComponentInChildren<FBXCharacterController>(true) != null;
                bool isMoveSource = n.Contains("source") || n.Contains("moveai");

                if (isFbxFallback || isMoveSource)
                {
                    if (child.activeSelf) child.SetActive(false);
                    disabled.Add(child.name);
                }
                else
                {
                    if (!child.activeSelf) child.SetActive(true);
                    kept.Add(child.name);
                }
            }

            var so = new SerializedObject(switcher);
            SetObjectRef(so, "charactersParent", parent);
            ClearArray(so, "characters");                 // empty -> auto-collect active children of the parent
            SetInt(so, "startIndex", 0);
            SetEnum(so, "defaultArticulation", (int)FusedCharacterPlayer.BodyArticulationSource.MoveGlb);
            ClearArray(so, "proceduralCharacters");
            SetObjectRef(so, "fbxCharacterController", null);
            so.ApplyModifiedProperties();

            // Stop FusedCharacterPlayer from auto-spawning the FBX at playback time.
            var fusedPlayer = Object.FindFirstObjectByType<FusedCharacterPlayer>(FindObjectsInactive.Include);
            if (fusedPlayer != null)
            {
                var fp = new SerializedObject(fusedPlayer);
                SetObjectRef(fp, "fbxCharacterController", null);
                SetBool(fp, "autoFindCharacter", false);
                fp.ApplyModifiedProperties();
                EditorUtility.SetDirty(fusedPlayer);
            }

            EditorUtility.SetDirty(switcher);
            EditorSceneManager.MarkSceneDirty(switcher.gameObject.scene);

            string keptStr = kept.Count > 0 ? string.Join(", ", kept) : "<none — add GLB characters under 'Characters'>";
            string disStr = disabled.Count > 0 ? string.Join(", ", disabled) : "<none>";
            Debug.Log($"[GlbCharacterSetup] GLB display characters: {keptStr}. Disabled (not displayed): {disStr}. " +
                      "Default articulation = MoveGlb. SAVE THE SCENE to keep this wiring.");
            EditorUtility.DisplayDialog("GLB Character Setup",
                $"Done.\n\nGLB display characters (cycled by the toggle):\n  {keptStr}\n\n" +
                $"Disabled (kept as fallback, not shown):\n  {disStr}\n\n" +
                "Default articulation = GLB.\n\nRemember to SAVE THE SCENE (Cmd+S).", "OK");
        }

        // Instantiates the GLB model assets in Assets/DeepMotion/Characters (priyal, modelART, …) as
        // children of "Characters" if they aren't already present. glTFast/Unity imports each .glb as a
        // model prefab whose root carries a SkinnedMeshRenderer, so we can drop it straight in.
        static void ImportGlbAssetsUnder(Transform parent, List<string> kept)
        {
            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/DeepMotion/Characters" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                    continue;

                // Skip if an instance with this name already lives under Characters.
                bool already = false;
                for (int i = 0; i < parent.childCount; i++)
                {
                    if (parent.GetChild(i).name == asset.name)
                    {
                        already = true;
                        break;
                    }
                }
                if (already)
                    continue;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent.gameObject.scene);
                if (instance == null)
                    continue;

                Undo.RegisterCreatedObjectUndo(instance, "Import GLB Character");
                instance.name = asset.name;
                instance.transform.SetParent(parent, false);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                if (!kept.Contains(instance.name))
                    kept.Add(instance.name);
                Debug.Log($"[GlbCharacterSetup] Instantiated GLB character '{instance.name}' under Characters.");
            }
        }

        // Shared switcher/player wiring for GLB-only playback.
        static void WireSwitcher(CharacterSwitcher switcher, Transform parent)
        {
            var so = new SerializedObject(switcher);
            SetObjectRef(so, "charactersParent", parent);
            ClearArray(so, "characters");
            SetInt(so, "startIndex", 0);
            SetEnum(so, "defaultArticulation", (int)FusedCharacterPlayer.BodyArticulationSource.MoveGlb);
            ClearArray(so, "proceduralCharacters");
            SetObjectRef(so, "fbxCharacterController", null);
            so.ApplyModifiedProperties();

            var fusedPlayer = Object.FindFirstObjectByType<FusedCharacterPlayer>(FindObjectsInactive.Include);
            if (fusedPlayer != null)
            {
                var fp = new SerializedObject(fusedPlayer);
                SetObjectRef(fp, "fbxCharacterController", null);
                SetBool(fp, "autoFindCharacter", false);
                fp.ApplyModifiedProperties();
                EditorUtility.SetDirty(fusedPlayer);
            }

            EditorUtility.SetDirty(switcher);
        }

        static void ReparentLooseGlbs(Transform parent, List<string> kept)
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || !go.scene.IsValid() || go.hideFlags != HideFlags.None)
                    continue;
                // Only consider whole GLB characters sitting at the SCENE ROOT. Without this guard the
                // method would also grab deep sub-meshes inside existing characters (Eyelash_Mesh,
                // Teeth_Mesh, finger link nodes…) and flatten them out as bogus "characters".
                if (go.transform.parent != null)
                    continue;

                string n = go.name.ToLowerInvariant().Trim();
                bool isFbxFallback = n == "model" || n == "model 1" ||
                                     go.GetComponentInChildren<FBXCharacterController>(true) != null;
                bool isMoveSource = n.Contains("source") || n.Contains("moveai");
                if (isFbxFallback || isMoveSource)
                    continue;
                if (go.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                    continue;

                go.transform.SetParent(parent, false);
                if (!kept.Contains(go.name))
                    kept.Add(go.name);
            }
        }

        static void SetBool(SerializedObject so, string prop, bool value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.boolValue = value;
        }

        static void SetObjectRef(SerializedObject so, string prop, Object value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.objectReferenceValue = value;
        }

        static void SetInt(SerializedObject so, string prop, int value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.intValue = value;
        }

        static void SetEnum(SerializedObject so, string prop, int enumValueIndex)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.enumValueIndex = enumValueIndex;
        }

        static void ClearArray(SerializedObject so, string prop)
        {
            var p = so.FindProperty(prop);
            if (p != null && p.isArray) p.ClearArray();
        }
    }
}
