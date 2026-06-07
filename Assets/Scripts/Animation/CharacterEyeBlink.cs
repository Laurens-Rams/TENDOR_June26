using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.Animation
{
    /// <summary>
    /// Drives realistic, performant eye blinking on the active climbing character by animating its blink
    /// blendshapes (ARKit-style <c>eyeBlinkLeft</c>/<c>eyeBlinkRight</c>, or <c>eyesClosed</c>).
    ///
    /// This is deliberately independent of the body retarget: <see cref="Playback.FusedCharacterPlayer"/> drives
    /// the rig's BONES procedurally (with the Animator disabled) and never touches blendshapes, so a blink layered
    /// on top can't fight it and costs almost nothing — the skinned mesh is already re-skinned every frame, so we
    /// only change a couple of blendshape inputs a few times per second.
    ///
    /// Drop this on any scene GameObject (e.g. the one with <see cref="FBXCharacterController"/>). It auto-finds the
    /// currently shown character — including after a <see cref="CharacterSwitcher"/> swap — rescans its blink
    /// blendshapes, and keeps blinking. All timing/feel is exposed below for tuning. Uses unscaled time so blinks
    /// continue while playback is paused.
    /// </summary>
    [DefaultExecutionOrder(250)]
    public class CharacterEyeBlink : MonoBehaviour
    {
        [Header("Target (optional — auto-resolved if empty)")]
        [Tooltip("Explicit character root to blink. Leave empty to auto-track the active character from the " +
                 "CharacterSwitcher / FBXCharacterController in the scene (handles runtime spawn + switching).")]
        [SerializeField] private Transform characterRoot;
        [Tooltip("Auto-find CharacterSwitcher/FBXCharacterController so the blink follows the active character " +
                 "even when it is spawned or swapped at runtime.")]
        [SerializeField] private bool autoTrackActiveCharacter = true;

        [Header("Blink Blendshapes")]
        [Tooltip("Blendshape names to drive when blinking. Matched case-insensitively (exact name first, then " +
                 "a 'contains' fallback). Defaults to the ARKit per-eye blink shapes for the most natural look.")]
        [SerializeField] private string[] blinkBlendshapeNames = { "eyeBlinkLeft", "eyeBlinkRight" };
        [Tooltip("Also drive a single combined 'eyes closed' shape if the per-eye blink shapes aren't present " +
                 "(some rigs only ship 'eyesClosed').")]
        [SerializeField] private bool fallbackToEyesClosed = true;
        [Tooltip("Name of the combined eyes-closed shape used by the fallback above.")]
        [SerializeField] private string eyesClosedBlendshapeName = "eyesClosed";
        [Tooltip("Fully-closed blendshape weight (Avaturn/ARKit shapes are 0–100).")]
        [Range(0f, 100f)] [SerializeField] private float closedWeight = 100f;

        [Header("Timing — interval between blinks")]
        [Tooltip("Shortest gap between blinks (seconds). Human resting average is ~2–6 s.")]
        [SerializeField] private float minInterval = 2.5f;
        [Tooltip("Longest gap between blinks (seconds).")]
        [SerializeField] private float maxInterval = 6.5f;

        [Header("Timing — the blink itself (asymmetric = realistic)")]
        [Tooltip("Time for the lid to close (seconds). Real lids snap shut fast (~0.05–0.1 s).")]
        [SerializeField] private float closeDuration = 0.06f;
        [Tooltip("Time held fully closed (seconds).")]
        [SerializeField] private float closedHold = 0.04f;
        [Tooltip("Time for the lid to re-open (seconds). Re-opening is noticeably slower than closing (~0.1–0.2 s).")]
        [SerializeField] private float openDuration = 0.14f;
        [Tooltip("Random ± jitter applied to each phase duration (fraction, 0–1) so no two blinks look identical.")]
        [Range(0f, 0.6f)] [SerializeField] private float durationJitter = 0.25f;

        [Header("Double blink")]
        [Tooltip("Chance (0–1) a blink is immediately followed by a second quick blink — common in humans.")]
        [Range(0f, 1f)] [SerializeField] private float doubleBlinkChance = 0.12f;
        [Tooltip("Gap between the two blinks of a double blink (seconds).")]
        [SerializeField] private float doubleBlinkGap = 0.12f;

        [Header("Shape")]
        [Tooltip("Override the lid motion with a custom curve. X = normalized phase time (0→1), Y = closed amount " +
                 "(0→1). Used for both close and open phases. When off, smoothstep easing is used.")]
        [SerializeField] private bool useCustomCurve = false;
        [SerializeField] private AnimationCurve blinkCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Behaviour")]
        [Tooltip("Skip blinking while the target character is hidden/inactive (saves the tiny cost and avoids " +
                 "blinking an off-screen model). The lids are left open when it reappears.")]
        [SerializeField] private bool onlyWhenActive = true;
        [Tooltip("Log resolved renderers/blendshapes once on bind (development aid).")]
        [SerializeField] private bool verboseLogging = false;

        // --- resolved binding ---------------------------------------------------------------------------------
        // fullWeight = the blendshape's own max frame weight. FBX/Maya shapes are authored 0–100, but glTF morph
        // targets imported by glTFast are authored 0–1, so driving them to 100 over-applies the shape 100× and
        // balloons the eye. Scaling by the shape's real max keeps the blink at its intended full-closed amount.
        private struct ShapeRef { public SkinnedMeshRenderer smr; public int index; public float fullWeight; }
        private readonly List<ShapeRef> blinkShapes = new List<ShapeRef>();
        private Transform boundRoot;

        // --- scene wiring -------------------------------------------------------------------------------------
        private CharacterSwitcher switcher;
        private FBXCharacterController fbxController;

        // --- blink state machine ------------------------------------------------------------------------------
        private enum Phase { Waiting, Closing, Closed, Opening }
        private Phase phase = Phase.Waiting;
        private float phaseTimer;
        private float phaseLength;
        private float nextInterval;
        private bool secondBlinkQueued;
        private float currentWeight01;

        private void OnEnable()
        {
            ResolveSceneWiring();
            ScheduleNextBlink(Random.Range(minInterval, maxInterval) * Random.value); // stagger first blink
        }

        private void OnDisable()
        {
            // Leave the eyes open if we get disabled mid-blink.
            ApplyWeight(0f);
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
            if (blinkShapes.Count == 0) return;

            if (onlyWhenActive && !root.gameObject.activeInHierarchy) return;

            AdvanceBlink(Time.unscaledDeltaTime);
        }

        private Transform ResolveActiveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (switcher != null && switcher.Current != null) return switcher.Current.transform;
            if (fbxController != null && fbxController.CharacterRoot != null) return fbxController.CharacterRoot.transform;
            return null;
        }

        /// <summary>Resolve the blink blendshapes on a (new) character root and reset the blink state.</summary>
        public void Bind(Transform root)
        {
            boundRoot = root;
            blinkShapes.Clear();
            currentWeight01 = 0f;
            if (root == null) return;

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bool foundPerEye = false;

            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                foreach (var name in blinkBlendshapeNames)
                {
                    int idx = FindBlendShape(smr.sharedMesh, name);
                    if (idx >= 0)
                    {
                        blinkShapes.Add(new ShapeRef { smr = smr, index = idx, fullWeight = MaxFrameWeight(smr.sharedMesh, idx) });
                        foundPerEye = true;
                    }
                }
            }

            // Fallback: combined eyes-closed shape, only if no per-eye blink shapes were found.
            if (!foundPerEye && fallbackToEyesClosed && !string.IsNullOrEmpty(eyesClosedBlendshapeName))
            {
                foreach (var smr in renderers)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    int idx = FindBlendShape(smr.sharedMesh, eyesClosedBlendshapeName);
                    if (idx >= 0)
                        blinkShapes.Add(new ShapeRef { smr = smr, index = idx, fullWeight = MaxFrameWeight(smr.sharedMesh, idx) });
                }
            }

            // Start fresh so a swap doesn't leave half-closed lids.
            ApplyWeight(0f);
            ScheduleNextBlink(Random.Range(minInterval, maxInterval));

            if (verboseLogging)
            {
                if (blinkShapes.Count > 0)
                    Debug.Log($"[CharacterEyeBlink] Bound to '{root.name}': {blinkShapes.Count} blink shape(s) resolved.");
                else
                    Debug.LogWarning($"[CharacterEyeBlink] Bound to '{root.name}' but found no blink blendshapes " +
                                     $"(looked for: {string.Join(", ", blinkBlendshapeNames)}" +
                                     (fallbackToEyesClosed ? $", {eyesClosedBlendshapeName}" : "") + ").");
            }
        }

        // The blendshape's authored full-strength weight (last frame). glTFast morph targets => 1.0,
        // FBX/Maya shapes => 100. Falling back to the inspector's closedWeight only if the mesh reports 0.
        private float MaxFrameWeight(Mesh mesh, int shapeIndex)
        {
            int frames = mesh.GetBlendShapeFrameCount(shapeIndex);
            if (frames <= 0) return closedWeight;
            float w = mesh.GetBlendShapeFrameWeight(shapeIndex, frames - 1);
            return w > 0.0001f ? w : closedWeight;
        }

        private static int FindBlendShape(Mesh mesh, string name)
        {
            int idx = mesh.GetBlendShapeIndex(name);
            if (idx >= 0) return idx;
            // Case-insensitive / contains fallback (handles "eyeBlinkLeftS", "EyeBlink_L", etc.).
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string bn = mesh.GetBlendShapeName(i);
                if (bn.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return -1;
        }

        private void AdvanceBlink(float dt)
        {
            phaseTimer += dt;

            switch (phase)
            {
                case Phase.Waiting:
                    if (phaseTimer >= nextInterval)
                        EnterPhase(Phase.Closing);
                    break;

                case Phase.Closing:
                    currentWeight01 = Evaluate(Mathf.Clamp01(phaseTimer / phaseLength));
                    if (phaseTimer >= phaseLength)
                    {
                        currentWeight01 = 1f;
                        EnterPhase(Phase.Closed);
                    }
                    break;

                case Phase.Closed:
                    currentWeight01 = 1f;
                    if (phaseTimer >= phaseLength)
                        EnterPhase(Phase.Opening);
                    break;

                case Phase.Opening:
                    currentWeight01 = 1f - Evaluate(Mathf.Clamp01(phaseTimer / phaseLength));
                    if (phaseTimer >= phaseLength)
                    {
                        currentWeight01 = 0f;
                        if (secondBlinkQueued)
                        {
                            secondBlinkQueued = false;
                            phase = Phase.Waiting;
                            phaseTimer = 0f;
                            nextInterval = Mathf.Max(0f, doubleBlinkGap);
                        }
                        else
                        {
                            ScheduleNextBlink(Random.Range(minInterval, maxInterval));
                        }
                    }
                    break;
            }

            ApplyWeight(currentWeight01);
        }

        private void EnterPhase(Phase next)
        {
            phase = next;
            phaseTimer = 0f;
            switch (next)
            {
                case Phase.Closing:
                    // Decide up-front whether this becomes a double blink.
                    if (!secondBlinkQueued && Random.value < doubleBlinkChance)
                        secondBlinkQueued = true;
                    phaseLength = Jittered(closeDuration);
                    break;
                case Phase.Closed:
                    phaseLength = Jittered(closedHold);
                    break;
                case Phase.Opening:
                    phaseLength = Jittered(openDuration);
                    break;
            }
            phaseLength = Mathf.Max(0.0001f, phaseLength);
        }

        private void ScheduleNextBlink(float interval)
        {
            phase = Phase.Waiting;
            phaseTimer = 0f;
            nextInterval = Mathf.Max(0f, interval);
        }

        private float Jittered(float baseValue)
        {
            if (durationJitter <= 0f) return baseValue;
            return baseValue * (1f + Random.Range(-durationJitter, durationJitter));
        }

        private float Evaluate(float t)
        {
            if (useCustomCurve && blinkCurve != null)
                return Mathf.Clamp01(blinkCurve.Evaluate(t));
            return Mathf.SmoothStep(0f, 1f, t);
        }

        private void ApplyWeight(float weight01)
        {
            float t = Mathf.Clamp01(weight01);
            for (int i = 0; i < blinkShapes.Count; i++)
            {
                var s = blinkShapes[i];
                // Scale to each shape's own authored max so glTF (0–1) and FBX (0–100) rigs both close fully
                // without over-driving (which previously scaled the eye geometry 100×).
                if (s.smr != null) s.smr.SetBlendShapeWeight(s.index, t * s.fullWeight);
            }
        }

        /// <summary>Force a single blink right now (also hookable from a UI Button for testing).</summary>
        [ContextMenu("Test Blink")]
        public void TriggerBlink()
        {
            if (boundRoot == null) Bind(ResolveActiveRoot());
            if (blinkShapes.Count == 0) return;
            secondBlinkQueued = false;
            EnterPhase(Phase.Closing);
        }
    }
}
