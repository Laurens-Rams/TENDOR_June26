using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Builds a natural, well-spread "climbing gym" lighting rig in the active scene.
    ///
    /// The project renders with URP (HDR off, additional lights cannot cast shadows,
    /// per-object additional light limit = 4) and composites a moving avatar over the AR
    /// camera feed. Because the avatar moves around the space, the rig is built entirely
    /// from DIRECTIONAL lights (position-independent, so the avatar is lit evenly wherever
    /// it stands) plus a bright gradient ambient that fakes the bounced fill you get under
    /// a high gym ceiling.
    ///
    /// Run via TENDOR > Lighting > Setup Climbing Gym Lighting. Re-running is idempotent
    /// (lights are matched by name under the "TendorLighting" group and re-tuned).
    /// </summary>
    public static class GymLightingSetup
    {
        const string GroupName = "TendorLighting";
        const string KeyName = "Key Light (Skylights)";
        const string FillName = "Fill Light (Windows)";
        const string SideFillName = "Side Fill (Bounce)";
        const string RimName = "Rim Light (Back)";

        [MenuItem("TENDOR/Lighting/Setup Climbing Gym Lighting")]
        public static void SetupMenu()
        {
            if (Setup())
            {
                EditorUtility.DisplayDialog(
                    "Gym Lighting",
                    "Climbing-gym lighting rig is set up in the active scene.\n\n" +
                    "Tune to taste on the 'TendorLighting' group:\n" +
                    "- Key Light intensity/rotation = main 'skylight' direction\n" +
                    "- Fill / Side Fill = how flat/even the shadows look\n" +
                    "- Rim Light = separation from the background\n" +
                    "- Environment ambient (Window > Rendering > Lighting) = overall lift",
                    "OK");
            }
        }

        public static bool Setup()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[GymLightingSetup] No active scene.");
                return false;
            }

            var group = GameObject.Find(GroupName);
            if (group == null)
            {
                group = new GameObject(GroupName);
                Undo.RegisterCreatedObjectUndo(group, "Create Lighting Group");
            }

            // KEY: the dominant "skylight" coming through the roof. Slightly warm, no shadows —
            // directional shadows self-shadow the avatar's face (nose/cheek) as harsh dark patches in AR, and
            // the fill/rim rig already provides shape. Skipping the shadow map is also cheaper on device.
            var key = EnsureLight(group, KeyName, reuseExistingSun: true);
            key.type = LightType.Directional;
            key.color = new Color(1.00f, 0.97f, 0.90f);
            key.intensity = 1.0f;
            key.shadows = LightShadows.None;
            key.bounceIntensity = 1f;
            key.transform.localEulerAngles = new Vector3(50f, -35f, 0f);

            // FILL: cool daylight from the opposite side (big gym windows). No shadows.
            var fill = EnsureLight(group, FillName, reuseExistingSun: false);
            fill.type = LightType.Directional;
            fill.color = new Color(0.82f, 0.89f, 1.00f);
            fill.intensity = 0.40f;
            fill.shadows = LightShadows.None;
            fill.transform.localEulerAngles = new Vector3(35f, 150f, 0f);

            // SIDE FILL: low, neutral bounce light to open up the undersides (floor bounce).
            var sideFill = EnsureLight(group, SideFillName, reuseExistingSun: false);
            sideFill.type = LightType.Directional;
            sideFill.color = new Color(0.96f, 0.96f, 1.00f);
            sideFill.intensity = 0.25f;
            sideFill.shadows = LightShadows.None;
            sideFill.transform.localEulerAngles = new Vector3(-12f, 60f, 0f);

            // RIM: cool backlight high from behind for silhouette separation.
            var rim = EnsureLight(group, RimName, reuseExistingSun: false);
            rim.type = LightType.Directional;
            rim.color = new Color(0.90f, 0.94f, 1.00f);
            rim.intensity = 0.55f;
            rim.shadows = LightShadows.None;
            rim.transform.localEulerAngles = new Vector3(55f, 205f, 0f);

            ConfigureEnvironment(key);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = group;
            Debug.Log("[GymLightingSetup] Climbing-gym lighting rig configured (key + fill + side fill + rim + ambient).");
            return true;
        }

        [MenuItem("TENDOR/Lighting/Remove Gym Lighting Rig")]
        public static void Remove()
        {
            var group = GameObject.Find(GroupName);
            if (group != null)
                Undo.DestroyObjectImmediate(group);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        static Light EnsureLight(GameObject group, string name, bool reuseExistingSun)
        {
            // Already created under the group? Reuse it.
            var existing = group.transform.Find(name);
            if (existing != null)
                return existing.GetComponent<Light>() ?? Undo.AddComponent<Light>(existing.gameObject);

            // For the key light, repurpose the scene's existing sun/first directional light
            // so we don't end up with a duplicate dominant light.
            if (reuseExistingSun)
            {
                var sun = RenderSettings.sun;
                if (sun == null)
                {
                    foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    {
                        if (l.type == LightType.Directional)
                        {
                            sun = l;
                            break;
                        }
                    }
                }

                if (sun != null)
                {
                    Undo.RecordObject(sun.transform, "Reparent Key Light");
                    sun.gameObject.name = name;
                    sun.transform.SetParent(group.transform, false);
                    return sun;
                }
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(group.transform, false);
            return Undo.AddComponent<Light>(go);
        }

        static void ConfigureEnvironment(Light key)
        {
            // Bright neutral gradient ambient = the soft, omnidirectional fill you feel under
            // a big diffuse gym ceiling. Sky cool-neutral, ground slightly warm (floor bounce).
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.58f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.44f, 0.44f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.28f, 0.24f);
            RenderSettings.ambientIntensity = 1.0f;
            RenderSettings.reflectionIntensity = 1.0f;
            RenderSettings.fog = false;
            RenderSettings.sun = key;
        }
    }
}
