using System.Collections.Generic;
using UnityEngine;
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
        [Tooltip("Which character is shown first (clamped to the list).")]
        [SerializeField] private int startIndex = 0;

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
            CollectCharacters();

            if (fusedPlayer == null)
                fusedPlayer = FindObjectOfType<FusedCharacterPlayer>(true);

            if (characters.Count == 0)
            {
                Debug.LogWarning("[CharacterSwitcher] No characters configured. Assign a Characters Parent with child models, or fill the Characters list.");
                return;
            }

            // All hidden to start; the active player reveals the selected one during playback.
            foreach (var c in characters)
                if (c != null) c.SetActive(false);

            ApplySelection(Mathf.Clamp(startIndex, 0, characters.Count - 1));
        }

        /// <summary>Advance to the next character (wraps around). Hook UI Button OnClick here.</summary>
        public void CycleCharacter()
        {
            if (characters.Count == 0) return;
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

            // Re-point the fused playback driver at the new rig. Works whether idle or mid-playback.
            if (fusedPlayer != null)
                fusedPlayer.RebindCharacter(next.transform);

            // Keep the legacy FBX path in sync if it's being used.
            if (fbxCharacterController != null)
            {
                if (!next.activeSelf) next.SetActive(true);
                fbxCharacterController.SetCharacter(next);
            }

            if (verboseLogging)
                Debug.Log($"[CharacterSwitcher] Selected character {index + 1}/{characters.Count}: '{next.name}'.");

            OnCharacterChanged?.Invoke(index, next);
        }

        private void CollectCharacters()
        {
            if (charactersParent == null) return;

            characters.Clear();
            for (int i = 0; i < charactersParent.childCount; i++)
                characters.Add(charactersParent.GetChild(i).gameObject);
        }
    }
}
