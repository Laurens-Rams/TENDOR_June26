using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BodyTracking.AR;
using BodyTracking.Playback;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-click wiring for the pose post-processing layer. Ensures an <see cref="ARSurfaceProbe"/> exists in the
    /// open scene and links it to the <see cref="FusedCharacterPlayer"/> so the penetration fix (wall/floor + hand
    /// IK) has a surface to query. The anchor mode, smoothing and penetration toggles use their code defaults, so
    /// no other scene change is required — this only adds the probe.
    ///
    /// Optional: the probe queries real AR geometry if you point its layer mask at an ARMeshManager whose mesh
    /// prefab has a MeshCollider; otherwise it falls back to the RouteRoot Z=0 wall plane + a calibrated floor Y.
    /// </summary>
    public static class PostProcessSetupTool
    {
        [MenuItem("TENDOR/Post-Processing/Setup Pose Post-Processing")]
        public static void Setup()
        {
            var player = Object.FindFirstObjectByType<FusedCharacterPlayer>(FindObjectsInactive.Include);
            if (player == null)
            {
                EditorUtility.DisplayDialog("Pose Post-Processing",
                    "No FusedCharacterPlayer found in the open scene. Open the gameplay scene and try again.", "OK");
                return;
            }

            var probe = Object.FindFirstObjectByType<ARSurfaceProbe>(FindObjectsInactive.Include);
            if (probe == null)
            {
                var go = new GameObject("PosePostProcessing");
                Undo.RegisterCreatedObjectUndo(go, "Create PosePostProcessing");
                probe = Undo.AddComponent<ARSurfaceProbe>(go);
            }

            var so = new SerializedObject(player);
            var prop = so.FindProperty("surfaceProbe");
            if (prop != null)
            {
                prop.objectReferenceValue = probe;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(player);
            }

            EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
            Debug.Log("[PostProcessSetup] ARSurfaceProbe wired to FusedCharacterPlayer. " +
                      "Anchor mode = MoveAIDriftCorrected, smoothing + penetration fix ON by default. SAVE THE SCENE (Cmd+S). " +
                      "For real AR geometry, set the probe's AR Mesh Layer Mask to an ARMeshManager mesh-collider layer.");
            EditorUtility.DisplayDialog("Pose Post-Processing",
                "Done. ARSurfaceProbe added and wired to FusedCharacterPlayer.\n\n" +
                "Defaults: Anchor = MoveAIDriftCorrected, Smoothing ON, Penetration fix ON (RouteRoot plane).\n\n" +
                "Remember to SAVE THE SCENE (Cmd+S).", "OK");
        }
    }
}
