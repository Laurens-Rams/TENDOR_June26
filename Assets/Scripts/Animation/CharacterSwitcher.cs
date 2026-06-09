using System.Collections.Generic;
using UnityEngine;
using BodyTracking.LookDev;
using BodyTracking.Playback;

namespace BodyTracking.Animation
{
    /// <summary>
    /// Cycles the playback character between several FBX models on a button press. Put all candidate character
    /// rigs under one parent (or list them explicitly), wire a UI Button's OnClick to <see cref="CycleCharacter"/>,
    /// and each click advances to the next model. The selected rig is bound to the <see cref="FusedCharacterPlayer"/>
    /// (the Move AI playback driver), so switching works both while idle and mid-playback — the retarget is
    /// rig-agnostic, so any Humanoid character animates without extra setup.
    ///
    /// Only one model is active at a time; the rest are disabled. During playback the player itself shows/hides
    /// the active model, so while idle the character stays hidden (matching the existing behaviour).
    /// </summary>
    public class CharacterSwitcher : MonoBehaviour
    {
        [Header("Characters")]
        [Tooltip("Parent whose direct children are the character models to cycle through (in sibling order). " +
                 "If set, the children are collected automatically. Leave empty to use the explicit list below.")]
        [SerializeField] private Transform charactersParent;
        [Tooltip("Explicit, ordered list of character root GameObjects. Used only when Characters Parent is empty.")]
        [SerializeField] private List<GameObject> characters = new List<GameObject>();
        [Tooltip("Which character is shown first (clamped to the list). Point this at the GLB character to make " +
                 "GLB playback the default.")]
        [SerializeField] private int startIndex = 0;

        [Header("Articulation")]
        [Tooltip("Default articulation for every character. We now use GLB (Move AI muscle retarget: body + " +
                 "fingers) for all characters — drop your GLB characters under the Characters Parent and the " +
                 "toggle cycles through them, each driven by the GLB.")]
        [SerializeField] private FusedCharacterPlayer.BodyArticulationSource defaultArticulation =
            FusedCharacterPlayer.BodyArticulationSource.MoveGlb;
        [Tooltip("Fallback escape hatch: characters listed here use the legacy FBX procedural retarget instead of " +
                 "the GLB path. Leave empty for GLB-only. Kept in case a GLB character won't animate correctly.")]
        [SerializeField] private List<GameObject> proceduralCharacters = new List<GameObject>();

        [Header("Playback wiring")]
        [Tooltip("The Move AI fused player that drives the character. Auto-found if left empty.")]
        [SerializeField] private FusedCharacterPlayer fusedPlayer;
        [Tooltip("Optional: keep this legacy FBX controller pointed at the selected model too (only needed if you " +
                 "also use the non-fused BodyTrackingPlayer path).")]
        [SerializeField] private FBXCharacterController fbxCharacterController;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;

        /// <summary>Index of the currently selected character.</summary>
        public int CurrentIndex { get; private set; } = -1;
        /// <summary>Number of characters available to cycle through.</summary>
        public int Count => characters.Count;
        /// <summary>The currently selected character root, or null if none.</summary>
        public GameObject Current =>
            (CurrentIndex >= 0 && CurrentIndex < characters.Count) ? characters[CurrentIndex] : null;

        /// <summary>Fired after a switch with the new index and character root.</summary>
        public event System.Action<int, GameObject> OnCharacterChanged;

        private void Awake()
        {
            if (fusedPlayer == null)
                fusedPlayer = FindFirstObjectByType<FusedCharacterPlayer>(FindObjectsInactive.Include);

            if (!EnsureBound())
            {
                Debug.LogWarning("[CharacterSwitcher] No GLB characters found. Put Avaturn/priyal/modelART under " +
                                 "'Characters', or run TENDOR ▸ Characters ▸ Use GLB Characters Only.");
                return;
            }

            // All hidden to start; the active player reveals the selected one during playback.
            foreach (var c in characters)
                if (c != null && c != Current) c.SetActive(false);
        }

        /// <summary>Ensure a GLB display character is bound. Safe to call before playback if Awake found nothing
        /// (e.g. all characters were disabled in the scene, or a GLB was left at scene root).</summary>
        public bool EnsureBound()
        {
            CollectCharacters();
            if (characters.Count == 0)
                DiscoverLooseGlbs();

            if (characters.Count == 0)
                return false;

            if (CurrentIndex < 0 || Current == null)
            {
                foreach (var c in characters)
                    if (c != null) c.SetActive(false);
                ApplySelection(Mathf.Clamp(startIndex, 0, characters.Count - 1));
            }

            return Current != null;
        }

        /// <summary>
        /// Instantiate a standalone clone of a display character so the multi-recording overlap engine can give
        /// each overlaid recording its own visible rig. The clone is independent of the cycle list (cloning the
        /// shared scene rigs would make them flicker as the engine shows/hides them). Returns the clone's root,
        /// or null when no GLB character is available to copy.
        /// </summary>
        public Transform CreateRigInstance(Transform parent) => CreateRigInstance(parent, -1);

        /// <summary>
        /// Clone a specific display character by index (so the recordings list can give each recording its own
        /// chosen character). Pass -1 to use the currently selected character. Out-of-range indices wrap.
        /// </summary>
        public Transform CreateRigInstance(Transform parent, int index)
        {
            if (!EnsureBound())
                return null;

            GameObject template = GetCharacter(index);
            if (template == null)
                template = Current != null ? Current : (characters.Count > 0 ? characters[0] : null);
            if (template == null)
                return null;

            // The template may be hidden (it is only shown during playback); force the clone active so the
            // overlay player can drive + reveal it.
            bool wasActive = template.activeSelf;
            if (!wasActive) template.SetActive(true);
            var clone = Object.Instantiate(template, parent != null ? parent : template.transform.parent);
            if (!wasActive) template.SetActive(false);

            clone.name = template.name + "_Overlay";
            clone.SetActive(true);

            // Strip the legacy FBX controller from the clone — overlays are driven directly by their own
            // FusedCharacterPlayer, and a stray controller would fight for the rig.
            var fbx = clone.GetComponentInChildren<FBXCharacterController>(true);
            if (fbx != null)
                Object.Destroy(fbx);

            CharacterLookLab.PrepareForDisplay(clone.transform);
            return clone.transform;
        }

        /// <summary>Default articulation mode the engine should use for cloned overlay rigs.</summary>
        public FusedCharacterPlayer.BodyArticulationSource DefaultArticulation => defaultArticulation;

        /// <summary>Character root at an index (wrapping), or null when there are no characters.</summary>
        public GameObject GetCharacter(int index)
        {
            if (characters.Count == 0 && !EnsureBound())
                return null;
            if (characters.Count == 0)
                return null;
            if (index < 0)
                return Current;
            return characters[((index % characters.Count) + characters.Count) % characters.Count];
        }

        /// <summary>Advance to the next character (wraps around). Hook UI Button OnClick here.</summary>
        public void CycleCharacter()
        {
            if (characters.Count == 0 && !EnsureBound()) return;
            int next = (CurrentIndex + 1) % characters.Count;
            ApplySelection(next);
        }

        /// <summary>Go to the previous character (wraps around).</summary>
        public void CyclePrevious()
        {
            if (characters.Count == 0) return;
            int prev = (CurrentIndex - 1 + characters.Count) % characters.Count;
            ApplySelection(prev);
        }

        /// <summary>Select a specific character by index (clamped/ignored if out of range).</summary>
        public void SelectCharacter(int index)
        {
            if (index < 0 || index >= characters.Count) return;
            ApplySelection(index);
        }

        private void ApplySelection(int index)
        {
            GameObject previous = Current;
            GameObject next = characters[index];
            if (next == null)
            {
                Debug.LogWarning($"[CharacterSwitcher] Character at index {index} is null — skipping.");
                return;
            }

            // Hide the outgoing model (the player will show the incoming one as needed).
            if (previous != null && previous != next)
                previous.SetActive(false);

            CurrentIndex = index;

            // Re-point the fused playback driver at the new rig and pick its articulation mode based on the
            // character: GLB characters use the Move AI muscle retarget; everything else uses the legacy FBX
            // procedural path. Set the mode BEFORE rebinding so the player builds the right pose handler.
            if (fusedPlayer != null)
            {
                var mode = proceduralCharacters.Contains(next)
                    ? FusedCharacterPlayer.BodyArticulationSource.Procedural
                    : defaultArticulation;
                fusedPlayer.SetArticulationSource(mode);
                fusedPlayer.RebindCharacter(next.transform);

                if (verboseLogging)
                    Debug.Log($"[CharacterSwitcher] '{next.name}' → {mode} articulation.");
            }

            // Keep the legacy FBX path in sync if it's being used.
            if (fbxCharacterController != null)
            {
                if (!next.activeSelf) next.SetActive(true);
                fbxCharacterController.SetCharacter(next);
            }

            CharacterLookLab.PrepareForDisplay(next.transform);

            if (verboseLogging)
                Debug.Log($"[CharacterSwitcher] Selected character {index + 1}/{characters.Count}: '{next.name}'.");

            OnCharacterChanged?.Invoke(index, next);
        }

        private void CollectCharacters()
        {
            if (charactersParent == null) return;

            // Collect ALL non-fallback children (active or inactive). GLB characters start disabled and are
            // revealed during playback; excluding inactive ones left the list empty and forced an FBX fallback.
            characters.Clear();
            for (int i = 0; i < charactersParent.childCount; i++)
            {
                var child = charactersParent.GetChild(i).gameObject;
                if (!IsExcludedFallback(child))
                    characters.Add(child);
            }
        }

        /// <summary>Find GLB display characters placed outside the Characters parent (e.g. Avaturn_Target at
        /// scene root) and reparent them so the toggle can cycle them.</summary>
        void DiscoverLooseGlbs()
        {
            if (charactersParent == null) return;

            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || !go.scene.IsValid() || go.hideFlags != HideFlags.None)
                    continue;
                if (IsExcludedFallback(go) || characters.Contains(go))
                    continue;
                if (!LooksLikeGlbDisplayCharacter(go))
                    continue;

                if (go.transform.parent != charactersParent)
                    go.transform.SetParent(charactersParent, false);
                characters.Add(go);

                if (verboseLogging)
                    Debug.Log($"[CharacterSwitcher] Discovered GLB character '{go.name}' and parented under '{charactersParent.name}'.");
            }
        }

        static bool IsExcludedFallback(GameObject go)
        {
            if (go == null) return true;
            string n = go.name.ToLowerInvariant().Trim();
            if (n == "model" || n == "model 1") return true;
            if (n.Contains("source") || n.Contains("moveai")) return true;
            return go.GetComponentInChildren<FBXCharacterController>(true) != null;
        }

        static bool LooksLikeGlbDisplayCharacter(GameObject go)
        {
            // Avaturn / glTFast GLB instances: skinned mesh + skeleton, no FBX controller.
            if (go.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                return false;
            if (go.GetComponentInChildren<Animator>(true) == null &&
                go.GetComponentsInChildren<Transform>(true).Length < 5)
                return false;
            return true;
        }
    }
}
