using BodyTracking.Glb;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-stop tester for the GLB retarget path: pick the Move AI GLB (animation source) and the Avaturn GLB
    /// (character), report their bone names to verify the humanoid mapping, then drop a wired
    /// <see cref="GlbPoseRetargeter"/> into the open scene so you can press Play and see the Avaturn character
    /// perform the Move motion (body + fingers) at matched height with pinned hips.
    /// </summary>
    public class GlbRetargetTestWindow : EditorWindow
    {
        GameObject movePrefab;
        GameObject avaturnPrefab;
        Vector2 scroll;
        string report = "";

        [MenuItem("TENDOR/GLB/Retarget Test...", priority = 0)]
        public static void Open() => GetWindow<GlbRetargetTestWindow>("GLB Retarget Test");

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "1) Drop the Move AI .glb and your Avaturn .glb into the project (glTFast imports them).\n" +
                "2) Assign both below.\n" +
                "3) 'Report Bones' to confirm the humanoid mapping resolves (esp. fingers).\n" +
                "4) 'Build Test In Scene', then press Play.", MessageType.Info);

            movePrefab = (GameObject)EditorGUILayout.ObjectField("Move AI GLB (animation)", movePrefab, typeof(GameObject), false);
            avaturnPrefab = (GameObject)EditorGUILayout.ObjectField("Avaturn GLB (character)", avaturnPrefab, typeof(GameObject), false);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Report Bones: Move")) report = ReportAndBuild(movePrefab, "Move AI GLB");
                if (GUILayout.Button("Report Bones: Avaturn")) report = ReportAndBuild(avaturnPrefab, "Avaturn GLB");
            }

            using (new EditorGUI.DisabledScope(movePrefab == null || avaturnPrefab == null))
            {
                if (GUILayout.Button("Build Test In Scene", GUILayout.Height(28)))
                    BuildTest();
            }

            if (!string.IsNullOrEmpty(report))
            {
                EditorGUILayout.Space();
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(180));
                EditorGUILayout.TextArea(report, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        static string ReportAndBuild(GameObject prefab, string label)
        {
            if (prefab == null) return $"Assign the {label} first.";
            var temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                string bones = HumanoidAvatarFactory.ReportBones(temp);
                HumanoidAvatarFactory.Build(temp, out string buildReport);
                return $"=== {label} ===\n{buildReport}\n{bones}";
            }
            finally
            {
                DestroyImmediate(temp);
            }
        }

        void BuildTest()
        {
            var moveInstance = (GameObject)PrefabUtility.InstantiatePrefab(movePrefab);
            moveInstance.name = "MoveAI_Source";
            // Hide the source mesh; we only need its skeleton/animation as the retarget driver.
            foreach (var r in moveInstance.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;

            var avaturnInstance = (GameObject)PrefabUtility.InstantiatePrefab(avaturnPrefab);
            avaturnInstance.name = "Avaturn_Target";
            var targetAnimator = avaturnInstance.GetComponentInChildren<Animator>();
            if (targetAnimator == null)
                targetAnimator = avaturnInstance.AddComponent<Animator>();

            var host = new GameObject("GlbRetargetTest");
            var rt = host.AddComponent<GlbPoseRetargeter>();
            rt.sourceInstance = moveInstance;
            rt.targetAnimator = targetAnimator;
            rt.sourceClip = LoadClipFromAsset(movePrefab);
            if (rt.sourceClip == null)
                Debug.LogWarning("[GlbRetargetTestWindow] No AnimationClip found as a sub-asset of the Move GLB — " +
                                 "check that the .glb actually contains animation.");

            Selection.activeGameObject = host;
            report = "Test built. Press Play. If the character T-poses or a bone is unmatched, use 'Report Bones' " +
                     "to find the rig's actual names and extend HumanoidAvatarFactory.Candidates.";
            Debug.Log("[GlbRetargetTestWindow] " + report);
        }

        static AnimationClip LoadClipFromAsset(GameObject prefab)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview"))
                    return clip;
            return null;
        }
    }
}
