using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.Animation
{
    /// <summary>
    /// Drives mouth expression on the active climbing character via ARKit-style mouth blendshapes
    /// (<c>mouthSmileLeft</c>/<c>mouthSmileRight</c> for a smile, <c>jawOpen</c>/<c>mouthOpen</c> for talking).
    ///
    /// Companion to <see cref="CharacterEyeBlink"/> and <see cref="CharacterEyeMovement"/>: it uses the exact same
    /// scene-wiring (auto-finds the shown character via <see cref="CharacterSwitcher"/>/<see cref="FBXCharacterController"/>,
    /// re-binds on swap), the same blendshape resolution (case-insensitive name match + per-shape max-weight scaling so
    /// glTF 0–1 and FBX 0–100 rigs both behave), and unscaled time so it keeps animating while playback is paused.
    /// Because <see cref="Playback.FusedCharacterPlayer"/> only drives bones, layering these blendshapes is free and
    /// never fights the body retarget.
    ///
    /// This is intentionally a simple, self-contained first pass: a tunable resting smile plus a procedural "talk"
    /// jaw flap you can trigger for a fixed duration. A future pass can swap the procedural talk for real
    /// audio-driven visemes (mouthFunnel/mouthPucker/etc.) without changing the binding or scene wiring.
    /// </summary>
    [DefaultExecutionOrder(250)]
    public class CharacterMouth : MonoBehaviour
    {
        [Header("Target (optional — auto-resolved if empty)")]
        [Tooltip("Explicit character root to drive. Leave empty to auto-track the active character from the " +
                 "CharacterSwitcher / FBXCharacterController in the scene (handles runtime spawn + switching).")]
        [SerializeField] private Transform characterRoot;
        [Tooltip("Auto-find CharacterSwitcher/FBXCharacterController so the mouth follows the active character " +
                 "even when it is spawned or swapped at runtime.")]
        [SerializeField] private bool autoTrackActiveCharacter = true;

        [Header("Smile")]
        [Tooltip("Blendshape names that curl the mouth into a smile. Matched case-insensitively (exact first, then " +
                 "a 'contains' fallback). ARKit/Avaturn/RPM ship per-corner smile shapes.")]
        [SerializeField] private string[] smileBlendshapeNames = { "mouthSmileLeft", "mouthSmileRight" };
        [Tooltip("Resting smile amount (0 = neutral, 1 = full smile). Applied continuously.")]
        [Range(0f, 1f)] [SerializeField] private float smileAmount = 0.25f;
        [Tooltip("Seconds to ease toward a new smile amount (so toggling looks natural rather than snapping).")]
        [SerializeField] private float smileEaseSeconds = 0.25f;

        [Header("Talk (procedural jaw flap)")]
        [Tooltip("Blendshape names that open the mouth/jaw. The first one found is driven for the talk motion.")]
        [SerializeField] private string[] jawOpenBlendshapeNames = { "jawOpen", "mouthOpen" };
        [Tooltip("Start talking on enable (otherwise call StartTalking()/TriggerTalk() or use the context menu).")]
        [SerializeField] private bool talkOnStart = false;
        [Tooltip("How wide the jaw opens while talking (0–1 of the shape's full range).")]
        [Range(0f, 1f)] [SerializeField] private float talkOpenAmount = 0.55f;
        [Tooltip("Speed of the jaw flap while talking (syllables/sec-ish). Noise is layered for natural variation.")]
        [SerializeField] private float talkSpeed = 6.5f;
        [Tooltip("How much the smile widens while talking (added on top of the resting smile).")]
        [Range(0f, 1f)] [SerializeField] private float talkSmileBoost = 0.15f;
        [Tooltip("Default duration (seconds) for TriggerTalk(); <= 0 means talk until stopped.")]
        [SerializeField] private float defaultTalkDuration = 3f;

        [Header("Behaviour")]
        [Tooltip("Skip driving the mouth while the target character is hidden/inactive.")]
        [SerializeField] private bool onlyWhenActive = true;
        [Tooltip("Log resolved renderers/blendshapes once on bind (development aid).")]
        [SerializeField] private bool verboseLogging = false;

        // --- resolved binding ---------------------------------------------------------------------------------
        // fullWeight = the shape's authored max (FBX => 100, glTFast morph targets => 1) so we drive to the
        // intended full amount on either rig instead of over-applying 100×.
        private struct ShapeRef { public SkinnedMeshRenderer smr; public int index; public float fullWeight; }
        private readonly List<ShapeRef> smileShapes = new List<ShapeRef>();
        private readonly List<ShapeRef> jawShapes = new List<ShapeRef>();
        private Transform boundRoot;

        // --- scene wiring -------------------------------------------------------------------------------------
        private CharacterSwitcher switcher;
        private FBXCharacterController fbxController;

        // --- talk state ---------------------------------------------------------------------------------------
        private bool talking;
        private float talkRemaining;       // <= 0 + talking => indefinite
        private float talkPhase;           // accumulated phase for the jaw oscillation
        private float talkNoiseSeed;
        private float currentSmile01;      // eased resting+talk smile
        private float currentJaw01;        // current jaw open amount

        private void OnEnable()
        {
            ResolveSceneWiring();
            talkNoiseSeed = Random.value * 1000f;
            currentSmile01 = smileAmount;
            currentJaw01 = 0f;
            talking = false;
            if (talkOnStart) StartTalking();
        }

        private void OnDisable()
        {
            // Relax the mouth to neutral if we're disabled mid-talk.
            ApplyShapes(smileShapes, 0f);
            ApplyShapes(jawShapes, 0f);
            if (switcher != null) switcher.OnCharacterChanged -= OnCharacterChanged;
        }

        private void ResolveSceneWiring()
        {
            if (!autoTrackActiveCharacter) return;

            if (switcher == null)
            {
                switcher = FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
                if (switcher != null) switcher.OnCharacterChanged += OnCharacterChanged;
            }
            if (fbxController == null)
                fbxController = FindFirstObjectByType<FBXCharacterController>(FindObjectsInactive.Include);
        }

        private void OnCharacterChanged(int _, GameObject newRoot)
        {
            if (newRoot != null) Bind(newRoot.transform);
        }

        private void Update()
        {
            Transform root = ResolveActiveRoot();
            if (root == null) return;
            if (root != boundRoot) Bind(root);
            if (smileShapes.Count == 0 && jawShapes.Count == 0) return;

            if (onlyWhenActive && !root.gameObject.activeInHierarchy) return;

            Step(Time.unscaledDeltaTime);
        }

        private Transform ResolveActiveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (switcher != null && switcher.Current != null) return switcher.Current.transform;
            if (fbxController != null && fbxController.CharacterRoot != null) return fbxController.CharacterRoot.transform;
            return null;
        }

        /// <summary>Resolve the mouth blendshapes on a (new) character root and reset state.</summary>
        public void Bind(Transform root)
        {
            boundRoot = root;
            smileShapes.Clear();
            jawShapes.Clear();
            currentSmile01 = smileAmount;
            currentJaw01 = 0f;
            if (root == null) return;

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            CollectShapes(renderers, smileBlendshapeNames, smileShapes, firstMatchOnly: false);
            CollectShapes(renderers, jawOpenBlendshapeNames, jawShapes, firstMatchOnly: true);

            ApplyShapes(smileShapes, currentSmile01);
            ApplyShapes(jawShapes, 0f);

            if (verboseLogging)
            {
                if (smileShapes.Count > 0 || jawShapes.Count > 0)
                    Debug.Log($"[CharacterMouth] Bound to '{root.name}': {smileShapes.Count} smile shape(s), " +
                              $"{jawShapes.Count} jaw shape(s).");
                else
                    Debug.LogWarning($"[CharacterMouth] Bound to '{root.name}' but found no mouth blendshapes " +
                                     $"(looked for smile: {string.Join(", ", smileBlendshapeNames)}; " +
                                     $"jaw: {string.Join(", ", jawOpenBlendshapeNames)}).");
            }
        }

        // For each name, add every renderer match (smile) or just the first overall (jaw) so we don't
        // double-drive a rig that has both jawOpen and mouthOpen.
        private void CollectShapes(SkinnedMeshRenderer[] renderers, string[] names, List<ShapeRef> into, bool firstMatchOnly)
        {
            foreach (var name in names)
            {
                foreach (var smr in renderers)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    int idx = FindBlendShape(smr.sharedMesh, name);
                    if (idx >= 0)
                    {
                        into.Add(new ShapeRef { smr = smr, index = idx, fullWeight = MaxFrameWeight(smr.sharedMesh, idx) });
                        if (firstMatchOnly) return;
                    }
                }
            }
        }

        private static float MaxFrameWeight(Mesh mesh, int shapeIndex)
        {
            int frames = mesh.GetBlendShapeFrameCount(shapeIndex);
            if (frames <= 0) return 100f;
            float w = mesh.GetBlendShapeFrameWeight(shapeIndex, frames - 1);
            return w > 0.0001f ? w : 100f;
        }

        private static int FindBlendShape(Mesh mesh, string name)
        {
            int idx = mesh.GetBlendShapeIndex(name);
            if (idx >= 0) return idx;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string bn = mesh.GetBlendShapeName(i);
                if (bn.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return -1;
        }

        private void Step(float dt)
        {
            float targetSmile = smileAmount;

            if (talking)
            {
                talkPhase += dt * Mathf.Max(0.01f, talkSpeed);
                // Half-rectified sine gives an open→closed flap; Perlin noise breaks up the metronome so it
                // reads as speech rather than a chewing loop.
                float baseFlap = Mathf.Abs(Mathf.Sin(talkPhase));
                float noise = Mathf.PerlinNoise(talkNoiseSeed + talkPhase * 0.5f, 0f);
                float openTarget = Mathf.Clamp01(baseFlap * (0.65f + 0.35f * noise)) * talkOpenAmount;
                currentJaw01 = Mathf.MoveTowards(currentJaw01, openTarget, dt * 12f);

                targetSmile = Mathf.Clamp01(smileAmount + talkSmileBoost);

                if (talkRemaining > 0f)
                {
                    talkRemaining -= dt;
                    if (talkRemaining <= 0f) StopTalking();
                }
            }
            else
            {
                currentJaw01 = Mathf.MoveTowards(currentJaw01, 0f, dt * 10f);
            }

            float smileRate = smileEaseSeconds > 0.0001f ? dt / smileEaseSeconds : 1f;
            currentSmile01 = Mathf.MoveTowards(currentSmile01, targetSmile, smileRate);

            ApplyShapes(smileShapes, currentSmile01);
            ApplyShapes(jawShapes, currentJaw01);
        }

        private static void ApplyShapes(List<ShapeRef> shapes, float amount01)
        {
            float a = Mathf.Clamp01(amount01);
            for (int i = 0; i < shapes.Count; i++)
                if (shapes[i].smr != null)
                    shapes[i].smr.SetBlendShapeWeight(shapes[i].index, a * shapes[i].fullWeight);
        }

        // --- public API ---------------------------------------------------------------------------------------

        /// <summary>Set the resting smile amount (0–1). Eases over <see cref="smileEaseSeconds"/>.</summary>
        public void SetSmile(float amount01) => smileAmount = Mathf.Clamp01(amount01);

        /// <summary>Current resting smile amount (0–1).</summary>
        public float SmileAmount => smileAmount;

        /// <summary>True while the procedural talk flap is running.</summary>
        public bool IsTalking => talking;

        /// <summary>Start the procedural talk flap. Pass a duration (seconds); &lt;= 0 talks until stopped.</summary>
        public void StartTalking(float duration = -1f)
        {
            if (boundRoot == null) Bind(ResolveActiveRoot());
            talking = true;
            talkRemaining = duration;
            talkPhase = 0f;
        }

        /// <summary>Talk for the inspector's default duration (handy for UI buttons / context menu).</summary>
        public void TriggerTalk() => StartTalking(defaultTalkDuration);

        /// <summary>Stop talking and let the jaw close.</summary>
        public void StopTalking()
        {
            talking = false;
            talkRemaining = 0f;
        }

        // --- editor test hooks --------------------------------------------------------------------------------

        [ContextMenu("Test Smile")]
        private void TestSmile()
        {
            if (boundRoot == null) Bind(ResolveActiveRoot());
            SetSmile(smileAmount > 0.5f ? 0.1f : 0.85f);
        }

        [ContextMenu("Test Talk")]
        private void TestTalk() => TriggerTalk();

        [ContextMenu("Stop Talk")]
        private void TestStopTalk() => StopTalking();
    }
}
