using System.IO;
using BodyTracking.Animation;
using BodyTracking.Editor;       // GymLightingSetup
using BodyTracking.LookDev;      // CharacterLookLab
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Builds and opens a dedicated "Character Look Lab" scene: the same directional gym lighting + ambient as the
    /// main scene, a ground plane, a framed camera, and an instance of the character with a <see cref="CharacterLookLab"/>
    /// tuning component. Tune materials/lighting/HDRI live, then push the lighting to the main scene with one click
    /// (the character's materials are shared assets, so those changes already carry over).
    /// </summary>
    public static class CharacterLabSceneBuilder
    {
        const string LabScenePath = "Assets/Scenes/CharacterLab.unity";
        const string MainScenePath = "Assets/Scenes/NewVersion.unity";
        const string CharactersFolder = "Assets/DeepMotion/Characters";
        const string ModelArtPath = "Assets/DeepMotion/Characters/modelART.glb";

        [MenuItem("TENDOR/Characters/Open Character Look Lab", priority = 2)]
        public static void OpenLab()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Scene scene = File.Exists(LabScenePath)
                ? EditorSceneManager.OpenScene(LabScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLabContents();

            EditorSceneManager.MarkSceneDirty(scene);
            EnsureScenesFolder();
            EditorSceneManager.SaveScene(scene, LabScenePath);
            EditorSceneManager.OpenScene(LabScenePath, OpenSceneMode.Single);

            var lab = Object.FindAnyObjectByType<CharacterLookLab>();
            if (lab != null) Selection.activeGameObject = lab.gameObject;

            Debug.Log($"[CharacterLab] Look Lab ready at '{LabScenePath}'. Tune on the CharacterLookLab component, " +
                      "then use 'Apply Lighting To Main Scene'.");
        }

        static void BuildLabContents()
        {
            // Lighting rig + ambient (same builder the main scene uses).
            GymLightingSetup.Setup();

            // Camera framed on the character.
            var camGo = GameObject.Find("Lab Camera");
            if (camGo == null)
            {
                camGo = new GameObject("Lab Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
            }
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.1f, 2.6f), Quaternion.Euler(6f, 180f, 0f));

            // Ground plane so contact + shadows read.
            var ground = GameObject.Find("Lab Ground");
            if (ground == null)
            {
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Lab Ground";
                ground.transform.localScale = Vector3.one * 1.2f;
            }

            // Character instance — always the GLB display avatar so material tuning hits the same shared
            // .mat assets the main scene / AR playback uses (modelART.glb, not the legacy FBX).
            GameObject character = EnsureLabCharacter();

            // Tuning component on a manager object.
            var labGo = GameObject.Find("LookLab") ?? new GameObject("LookLab");
            var lab = labGo.GetComponent<CharacterLookLab>() ?? labGo.AddComponent<CharacterLookLab>();
            if (character != null) lab.character = character.transform;
            lab.ApplyAll();
        }

        /// <summary>Ensure a modelART.glb instance named "LabCharacter" exists; replace legacy FBX lab rigs.</summary>
        static GameObject EnsureLabCharacter()
        {
            var prefab = LoadModelArtPrefab();
            if (prefab == null)
            {
                Debug.LogWarning($"[CharacterLab] modelART.glb not found at '{ModelArtPath}'. " +
                                 "Import it, then assign a character on the CharacterLookLab 'Character' field.");
                return GameObject.Find("LabCharacter") ?? GameObject.Find("modelART");
            }

            var existing = GameObject.Find("LabCharacter") ?? GameObject.Find("modelART");
            if (existing != null && PrefabUtility.GetCorrespondingObjectFromSource(existing) == prefab)
            {
                if (existing.name != "LabCharacter") existing.name = "LabCharacter";
                return existing;
            }

            if (existing != null)
                Object.DestroyImmediate(existing);

            var character = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            character.name = "LabCharacter";
            character.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return character;
        }

        static GameObject LoadModelArtPrefab()
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(ModelArtPath);
        }

        /// <summary>Find the active GLB display character in the main scene for material prep during apply.</summary>
        static Transform FindMainSceneCharacter()
        {
            var switcher = Object.FindAnyObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            if (switcher != null && switcher.EnsureBound() && switcher.Current != null)
                return switcher.Current.transform;

            var modelArt = GameObject.Find("modelART");
            if (modelArt != null) return modelArt.transform;

            var avaturn = GameObject.Find("Avaturn_Target");
            if (avaturn != null) return avaturn.transform;

            return Object.FindAnyObjectByType<FBXCharacterController>()?.CharacterRoot?.transform;
        }

        static void EnsureScenesFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
        }

        /// <summary>
        /// Copy the lab's lighting + environment (HDRI/ambient) settings into the main scene. Materials are shared
        /// assets so they already match; this just brings the light rig + ambient/skybox across.
        /// </summary>
        public static void ApplyLightingToMainScene(CharacterLookLab lab)
        {
            if (lab == null) return;
            if (!File.Exists(MainScenePath))
            {
                EditorUtility.DisplayDialog("Apply Lighting", $"Main scene not found at '{MainScenePath}'.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            // Snapshot the tunable values + the HDRI reference (an asset, so it survives the scene switch).
            var snap = new LabSnapshot(lab);
            Cubemap hdri = lab.hdri;

            EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            Scene main = EditorSceneManager.GetActiveScene();

            // Ensure the rig exists, then re-apply the snapshot through a temporary tuner in the main scene.
            GymLightingSetup.Setup();
            var holder = new GameObject("~LookLabApply");
            var applier = holder.AddComponent<CharacterLookLab>();
            snap.Into(applier);
            applier.hdri = hdri;
            applier.character = FindMainSceneCharacter();
            applier.ApplyAll();
            Object.DestroyImmediate(holder);

            EditorSceneManager.MarkSceneDirty(main);
            EditorSceneManager.SaveScene(main);
            Debug.Log("[CharacterLab] Applied lab lighting + environment to the main scene.");

            EditorSceneManager.OpenScene(LabScenePath, OpenSceneMode.Single);
            EditorUtility.DisplayDialog("Apply Lighting",
                "Lighting + environment copied to the main scene and saved.\n\n" +
                "Character materials are shared assets, so the matte look already matches.", "OK");
        }

        /// <summary>Plain copy of the tunable fields so they survive a scene switch.</summary>
        class LabSnapshot
        {
            readonly float smoothness, metallic, eyeSmoothness;
            readonly bool specularHighlights, environmentReflections;
            readonly float keyIntensity, fillIntensity, sideFillIntensity, rimIntensity, ambientIntensity;
            readonly bool useHdri;
            readonly float hdriExposure, hdriRotation, hdriAmbientIntensity;
            readonly Color hdriTint;

            public LabSnapshot(CharacterLookLab l)
            {
                smoothness = l.smoothness; metallic = l.metallic; eyeSmoothness = l.eyeSmoothness;
                specularHighlights = l.specularHighlights; environmentReflections = l.environmentReflections;
                keyIntensity = l.keyIntensity; fillIntensity = l.fillIntensity;
                sideFillIntensity = l.sideFillIntensity; rimIntensity = l.rimIntensity;
                ambientIntensity = l.ambientIntensity;
                useHdri = l.useHdri; hdriExposure = l.hdriExposure; hdriRotation = l.hdriRotation;
                hdriAmbientIntensity = l.hdriAmbientIntensity; hdriTint = l.hdriTint;
            }

            public void Into(CharacterLookLab l)
            {
                l.smoothness = smoothness; l.metallic = metallic; l.eyeSmoothness = eyeSmoothness;
                l.specularHighlights = specularHighlights; l.environmentReflections = environmentReflections;
                l.keyIntensity = keyIntensity; l.fillIntensity = fillIntensity;
                l.sideFillIntensity = sideFillIntensity; l.rimIntensity = rimIntensity;
                l.ambientIntensity = ambientIntensity;
                l.useHdri = useHdri; l.hdriExposure = hdriExposure; l.hdriRotation = hdriRotation;
                l.hdriAmbientIntensity = hdriAmbientIntensity; l.hdriTint = hdriTint;
            }
        }
    }

    [CustomEditor(typeof(CharacterLookLab))]
    public class CharacterLookLabEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var lab = (CharacterLookLab)target;

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Apply Now", GUILayout.Height(26)))
                lab.ApplyAll();

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Materials are shared assets — tuning them here already changes the main scene's character. " +
                "Use the button below to also copy the LIGHTING + environment (HDRI/ambient) to the main scene.",
                MessageType.Info);

            if (GUILayout.Button("Apply Lighting To Main Scene (NewVersion)", GUILayout.Height(30)))
                CharacterLabSceneBuilder.ApplyLightingToMainScene(lab);
        }
    }
}
