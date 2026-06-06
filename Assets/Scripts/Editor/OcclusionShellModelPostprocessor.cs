using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Avaturn / Ready-Player-Me character FBX files ship an eye ambient-occlusion shell mesh (e.g.
    /// "EyeAO_Mesh"). It is a dark dome that sits around the eyeball and, rendered opaque while borrowing the
    /// eye material, reads as a heavy black ring around the eyes. It carries no useful texture of its own, so
    /// the clean fix is to not render it.
    ///
    /// Disabling it via runtime code only hides it in Play mode / AR, not in the Scene view. Doing it here in
    /// the model post-processor bakes the change into the imported model itself, so the dome is gone everywhere
    /// (Scene view, Play, AR) and the fix survives reimport (which regenerates the .fbm + model contents).
    /// </summary>
    public class OcclusionShellModelPostprocessor : AssetPostprocessor
    {
        // Set false to keep eyelashes. The eyelash card renders as black strands (real lashes are black); if that
        // reads as a heavy dark smear on this model, hiding the mesh is the simplest clean look.
        const bool HideEyelashes = true;

        static bool IsOcclusionShell(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("eyeao") || n.Contains("eye_ao") || n.Contains("occlusion") || n.Contains("cornea");
        }

        static bool IsEyelash(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("eyelash") || n.Contains("lash");
        }

        static bool ShouldHide(string name)
        {
            return IsOcclusionShell(name) || (HideEyelashes && IsEyelash(name));
        }

        void OnPostprocessModel(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool match = ShouldHide(r.name);
                if (!match)
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m != null && ShouldHide(m.name)) { match = true; break; }
                    }
                }

                if (match)
                    r.enabled = false;
            }
        }
    }
}
