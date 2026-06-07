using System.Collections.Generic;
using BodyTracking;
using UnityEngine;

namespace BodyTracking.Animation
{
    /// <summary>
    /// Adds subtle, natural eye motion (saccades + fixations + micro-tremor) to the active climbing character.
    /// Companion to <see cref="CharacterEyeBlink"/>; runs in LateUpdate after body retarget so gaze is not overwritten.
    ///
    /// Avaturn/RPM characters drive the visible eyes via ARKit <c>eyeLook*</c> blendshapes on the head mesh — not via
    /// humanoid eye bones (those are often dummy transforms). This component prefers blendshapes when present.
    /// Keeps animating while playback is paused (uses unscaled time).
    /// </summary>
    [DefaultExecutionOrder(250)]
    public class CharacterEyeMovement : MonoBehaviour
    {
        [Header("Target (optional — auto-resolved if empty)")]
        [SerializeField] private Transform characterRoot;
        [SerializeField] private bool autoTrackActiveCharacter = true;

        [Header("Gaze range (degrees from straight-ahead)")]
        [Range(0f, 30f)] [SerializeField] private float maxYaw = 9f;
        [Range(0f, 30f)] [SerializeField] private float maxPitch = 6f;
        [Range(0f, 1f)] [SerializeField] private float centerBias = 0.45f;

        [Header("Timing")]
        [SerializeField] private float minFixation = 0.6f;
        [SerializeField] private float maxFixation = 2.4f;
        [SerializeField] private float saccadeDuration = 0.045f;

        [Header("Micro-tremor")]
        [Range(0f, 1f)] [SerializeField] private float microTremorAmplitude = 0.25f;
        [SerializeField] private float microTremorSpeed = 7f;

        [Header("Coupling with blinking")]
        [SerializeField] private bool blinkOnLargeSaccades = true;
        [Range(0f, 1f)] [SerializeField] private float largeSaccadeThreshold = 0.6f;
        [Range(0f, 1f)] [SerializeField] private float blinkOnSaccadeChance = 0.35f;

        [Header("Axis tuning (flip if an eye looks the wrong way)")]
        [SerializeField] private bool invertYaw = false;
        [SerializeField] private bool invertPitch = false;
        [SerializeField] private bool swapYawPitch = false;

        [Header("Binding")]
        [Tooltip("Prefer ARKit eye-look blendshapes (Avaturn/RPM). Bone rotation is fallback only.")]
        [SerializeField] private bool preferLookBlendshapes = true;
        [SerializeField] private bool fallbackToEyeBones = true;
        [Tooltip("Re-scan the character if no eyes were resolved on the first bind (runtime spawn delay).")]
        [SerializeField] private float rebindInterval = 1f;

        [Header("Behaviour")]
        [SerializeField] private bool onlyWhenActive = true;
        [Tooltip("Keep eyes alive while playback is paused (recommended).")]
        [SerializeField] private bool animateWhilePlaybackPaused = true;
        [SerializeField] private bool verboseLogging = false;

        private struct EyeBone { public Transform t; public Quaternion bind; }
        private readonly List<EyeBone> eyeBones = new List<EyeBone>();

        private struct ShapeRef { public SkinnedMeshRenderer smr; public int index; public float fullWeight; }
        private readonly List<ShapeRef> lookOutLeft = new List<ShapeRef>();
        private readonly List<ShapeRef> lookInLeft = new List<ShapeRef>();
        private readonly List<ShapeRef> lookOutRight = new List<ShapeRef>();
        private readonly List<ShapeRef> lookInRight = new List<ShapeRef>();
        private readonly List<ShapeRef> lookUpLeft = new List<ShapeRef>();
        private readonly List<ShapeRef> lookUpRight = new List<ShapeRef>();
        private readonly List<ShapeRef> lookDownLeft = new List<ShapeRef>();
        private readonly List<ShapeRef> lookDownRight = new List<ShapeRef>();
        private bool usingBlendshapes;

        private Transform boundRoot;
        private CharacterSwitcher switcher;
        private FBXCharacterController fbxController;
        private BodyTrackingController bodyController;
        private CharacterEyeBlink blink;

        private Vector2 fromGaze, toGaze, currentGaze;
        private float saccadeTimer, fixationTimer, fixationLength;
        private bool inSaccade;
        private Vector2 noiseSeed;
        private float rebindTimer;

        private void OnEnable()
        {
            noiseSeed = new Vector2(Random.value * 1000f, Random.value * 1000f);
            ResolveSceneWiring();
            fromGaze = toGaze = currentGaze = Vector2.zero;
            inSaccade = false;
            fixationTimer = 0f;
            fixationLength = Random.Range(minFixation, maxFixation);
            rebindTimer = 0f;
        }

        private void OnDisable()
        {
            ApplyGaze(Vector2.zero);
            if (switcher != null) switcher.OnCharacterChanged -= OnCharacterChanged;
        }

        private void ResolveSceneWiring()
        {
            if (blink == null) blink = GetComponent<CharacterEyeBlink>();
            if (bodyController == null)
                bodyController = FindFirstObjectByType<BodyTrackingController>(FindObjectsInactive.Include);
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

        private Transform ResolveActiveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (switcher != null && switcher.Current != null) return switcher.Current.transform;
            if (fbxController != null && fbxController.CharacterRoot != null) return fbxController.CharacterRoot.transform;
            return null;
        }

        public void Bind(Transform root)
        {
            boundRoot = root;
            eyeBones.Clear();
            ClearLookShapes();
            usingBlendshapes = false;
            currentGaze = fromGaze = toGaze = Vector2.zero;
            if (root == null) return;

            if (preferLookBlendshapes)
                BindLookBlendshapes(root);

            if (!usingBlendshapes && fallbackToEyeBones)
                BindEyeBones(root);

            inSaccade = false;
            fixationTimer = 0f;
            fixationLength = Random.Range(minFixation, maxFixation);
            ApplyGaze(Vector2.zero);

            if (verboseLogging)
            {
                if (usingBlendshapes)
                    Debug.Log($"[CharacterEyeMovement] Bound to '{root.name}': ARKit eye-look blendshapes " +
                              $"(L out={lookOutLeft.Count} in={lookInLeft.Count}, R out={lookOutRight.Count} in={lookInRight.Count}).");
                else if (eyeBones.Count > 0)
                    Debug.Log($"[CharacterEyeMovement] Bound to '{root.name}': {eyeBones.Count} eye bone(s).");
                else
                    Debug.LogWarning($"[CharacterEyeMovement] Bound to '{root.name}' but found no eye-look blendshapes " +
                                     "or eye bones — eyes will stay still.");
            }
        }

        private void ClearLookShapes()
        {
            lookOutLeft.Clear(); lookInLeft.Clear(); lookOutRight.Clear(); lookInRight.Clear();
            lookUpLeft.Clear(); lookUpRight.Clear(); lookDownLeft.Clear(); lookDownRight.Clear();
        }

        private void BindEyeBones(Transform root)
        {
            var animator = root.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                AddEyeBone(animator.GetBoneTransform(HumanBodyBones.LeftEye));
                AddEyeBone(animator.GetBoneTransform(HumanBodyBones.RightEye));
            }

            if (eyeBones.Count == 0)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name.ToLowerInvariant();
                    if (!n.Contains("eye") || n.Contains("eyelash") || n.Contains("eyebrow") || n.Contains("brow") ||
                        n.Contains("ao") || n.Contains("lid")) continue;
                    if (n.Contains("lefteye") || n.Contains("righteye") || n.Contains("eye_l") || n.Contains("eye_r") ||
                        n.Contains("eyel") || n.Contains("eyer") || n.Contains("l_eye") || n.Contains("r_eye"))
                        AddEyeBone(t);
                }
            }
        }

        private void AddEyeBone(Transform t)
        {
            if (t == null) return;
            for (int i = 0; i < eyeBones.Count; i++) if (eyeBones[i].t == t) return;
            eyeBones.Add(new EyeBone { t = t, bind = t.localRotation });
        }

        private void BindLookBlendshapes(Transform root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (name.IndexOf("eyeLook", System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var shape = new ShapeRef { smr = smr, index = i, fullWeight = MaxFrameWeight(mesh, i) };
                    string lower = name.ToLowerInvariant();
                    bool isLeft = lower.Contains("left");
                    bool isRight = lower.Contains("right");

                    if (lower.Contains("lookout"))
                    {
                        if (isLeft) lookOutLeft.Add(shape);
                        else if (isRight) lookOutRight.Add(shape);
                        else { lookOutLeft.Add(shape); lookOutRight.Add(shape); }
                    }
                    else if (lower.Contains("lookin"))
                    {
                        if (isLeft) lookInLeft.Add(shape);
                        else if (isRight) lookInRight.Add(shape);
                        else { lookInLeft.Add(shape); lookInRight.Add(shape); }
                    }
                    else if (lower.Contains("lookup"))
                    {
                        if (isLeft) lookUpLeft.Add(shape);
                        else if (isRight) lookUpRight.Add(shape);
                        else { lookUpLeft.Add(shape); lookUpRight.Add(shape); }
                    }
                    else if (lower.Contains("lookdown"))
                    {
                        if (isLeft) lookDownLeft.Add(shape);
                        else if (isRight) lookDownRight.Add(shape);
                        else { lookDownLeft.Add(shape); lookDownRight.Add(shape); }
                    }
                }
            }

            usingBlendshapes = lookOutLeft.Count + lookInLeft.Count + lookOutRight.Count + lookInRight.Count +
                               lookUpLeft.Count + lookUpRight.Count + lookDownLeft.Count + lookDownRight.Count > 0;
        }

        private static float MaxFrameWeight(Mesh mesh, int shapeIndex)
        {
            int frames = mesh.GetBlendShapeFrameCount(shapeIndex);
            if (frames <= 0) return 100f;
            float w = mesh.GetBlendShapeFrameWeight(shapeIndex, frames - 1);
            return w > 0.0001f ? w : 100f;
        }

        private void LateUpdate()
        {
            Transform root = ResolveActiveRoot();
            if (root == null) return;
            if (root != boundRoot) Bind(root);

            if (!IsEyeAnimationAllowed(root)) return;

            if (!usingBlendshapes && eyeBones.Count == 0)
            {
                rebindTimer -= GetDeltaTime();
                if (rebindTimer <= 0f)
                {
                    rebindTimer = rebindInterval;
                    Bind(root);
                }
                if (!usingBlendshapes && eyeBones.Count == 0) return;
            }

            Step(GetDeltaTime());
        }

        private bool IsEyeAnimationAllowed(Transform root)
        {
            if (onlyWhenActive && !root.gameObject.activeInHierarchy) return false;
            if (bodyController != null && bodyController.IsPlaying && bodyController.IsPaused && !animateWhilePlaybackPaused)
                return false;
            return true;
        }

        /// <summary>Unscaled so eyes keep moving while playback is paused (timeline frozen).</summary>
        private static float GetDeltaTime() => Time.unscaledDeltaTime;

        private void Step(float dt)
        {
            if (inSaccade)
            {
                saccadeTimer += dt;
                float t = saccadeDuration <= 0f ? 1f : Mathf.Clamp01(saccadeTimer / saccadeDuration);
                currentGaze = Vector2.Lerp(fromGaze, toGaze, Mathf.SmoothStep(0f, 1f, t));
                if (t >= 1f)
                {
                    currentGaze = toGaze;
                    inSaccade = false;
                    fixationTimer = 0f;
                    fixationLength = Random.Range(minFixation, maxFixation);
                }
            }
            else
            {
                fixationTimer += dt;
                if (fixationTimer >= fixationLength)
                    StartSaccade();
            }

            Vector2 gaze = currentGaze;
            if (microTremorAmplitude > 0f)
            {
                float ts = Time.unscaledTime * microTremorSpeed;
                float jx = (Mathf.PerlinNoise(noiseSeed.x + ts, 0f) - 0.5f) * 2f;
                float jy = (Mathf.PerlinNoise(0f, noiseSeed.y + ts) - 0.5f) * 2f;
                gaze += new Vector2(jx, jy) * microTremorAmplitude;
            }

            ApplyGaze(gaze);
        }

        private void StartSaccade()
        {
            fromGaze = currentGaze;
            toGaze = PickGazeTarget();
            saccadeTimer = 0f;
            inSaccade = true;

            if (blinkOnLargeSaccades && blink != null)
            {
                float range = Mathf.Max(0.001f, new Vector2(maxYaw, maxPitch).magnitude);
                float size = (toGaze - fromGaze).magnitude / range;
                if (size >= largeSaccadeThreshold && Random.value < blinkOnSaccadeChance)
                    blink.TriggerBlink();
            }
        }

        private Vector2 PickGazeTarget()
        {
            float yaw = Random.Range(-maxYaw, maxYaw) * (1f - centerBias * Random.value);
            float pitch = Random.Range(-maxPitch, maxPitch) * (1f - centerBias * Random.value);
            return new Vector2(yaw, pitch);
        }

        private void ApplyGaze(Vector2 gaze)
        {
            float yaw = (invertYaw ? -1f : 1f) * gaze.x;
            float pitch = (invertPitch ? -1f : 1f) * gaze.y;

            if (usingBlendshapes)
            {
                ApplyLookBlendshapes(yaw, pitch);
                return;
            }

            if (eyeBones.Count > 0)
            {
                Quaternion offset = swapYawPitch
                    ? Quaternion.Euler(yaw, pitch, 0f)
                    : Quaternion.Euler(pitch, yaw, 0f);
                for (int i = 0; i < eyeBones.Count; i++)
                    if (eyeBones[i].t != null)
                        eyeBones[i].t.localRotation = eyeBones[i].bind * offset;
            }
        }

        private void ApplyLookBlendshapes(float yaw, float pitch)
        {
            float h = Mathf.Clamp(yaw / Mathf.Max(0.001f, maxYaw), -1f, 1f);
            float v = Mathf.Clamp(pitch / Mathf.Max(0.001f, maxPitch), -1f, 1f);

            // ARKit: positive yaw = look to the character's left — left eye Out, right eye In.
            ZeroLookShapes();
            if (h >= 0f)
            {
                SetShapes(lookOutLeft, h);
                SetShapes(lookInRight, h);
            }
            else
            {
                float a = -h;
                SetShapes(lookInLeft, a);
                SetShapes(lookOutRight, a);
            }

            if (v >= 0f)
            {
                SetShapes(lookDownLeft, v);
                SetShapes(lookDownRight, v);
            }
            else
            {
                float a = -v;
                SetShapes(lookUpLeft, a);
                SetShapes(lookUpRight, a);
            }
        }

        private void ZeroLookShapes()
        {
            SetShapes(lookOutLeft, 0f); SetShapes(lookInLeft, 0f);
            SetShapes(lookOutRight, 0f); SetShapes(lookInRight, 0f);
            SetShapes(lookUpLeft, 0f); SetShapes(lookUpRight, 0f);
            SetShapes(lookDownLeft, 0f); SetShapes(lookDownRight, 0f);
        }

        private static void SetShapes(List<ShapeRef> shapes, float amount01)
        {
            float a = Mathf.Clamp01(amount01);
            for (int i = 0; i < shapes.Count; i++)
                if (shapes[i].smr != null)
                    shapes[i].smr.SetBlendShapeWeight(shapes[i].index, a * shapes[i].fullWeight);
        }
    }
}
