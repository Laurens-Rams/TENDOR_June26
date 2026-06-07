using BodyTracking.Animation;
using BodyTracking;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AR
{
    /// <summary>
    /// Fast AR contact shadow: one or two soft transparent quads projected onto detected AR planes (floor +
    /// optional wall). No shadow maps — just aligns a radial-gradient blob to the nearest <see cref="ARPlane"/>
    /// under the character's feet and, optionally, onto a nearby vertical plane in the light direction.
    ///
    /// This complements disabling real-time shadows on the avatar (which caused face self-shadowing): the
    /// character stays evenly lit while a cheap blob grounds it on the real floor/wall. For a real body
    /// silhouette on the floor, use <see cref="ARCharacterPlanarShadow"/> instead (disable this component).
    ///
    /// Drop on any scene object (e.g. alongside <see cref="ARCharacterOcclusion"/>). Auto-finds
    /// <see cref="ARPlaneManager"/>, the active character, and the gym key-light direction. Intended for
    /// playback only — mirrors the occlusion lifecycle.
    /// </summary>
    public class ARCharacterContactShadow : MonoBehaviour
    {
        [Header("Target (optional — auto-resolved)")]
        [Tooltip("Character root. Leave empty to follow CharacterSwitcher / FBXCharacterController.")]
        [SerializeField] private Transform characterRoot;
        [SerializeField] private bool autoTrackActiveCharacter = true;

        [Header("AR planes")]
        [Tooltip("Plane manager on the XR Origin. Auto-found if empty.")]
        [SerializeField] private ARPlaneManager planeManager;

        [Header("Light (shadow cast direction)")]
        [Tooltip("World-space direction the key light shines. Auto-read from TendorLighting key if present.")]
        [SerializeField] private Vector3 lightDirection = new Vector3(0.35f, -0.85f, -0.38f);
        [SerializeField] private bool autoResolveLightDirection = true;

        [Header("Floor shadow")]
        [SerializeField] private bool floorShadow = true;
        [Tooltip("Base blob radius at ~1.7 m character height (metres).")]
        [SerializeField] private float floorRadius = 0.42f;
        [Tooltip("Extra radius per metre of character height.")]
        [SerializeField] private float floorRadiusPerHeight = 0.12f;
        [Tooltip("Slide the blob slightly opposite the light so it reads as cast, not centered.")]
        [SerializeField] private float floorLightOffset = 0.08f;
        [Tooltip("Lift above the plane to avoid z-fighting.")]
        [SerializeField] private float floorSurfacePadding = 0.008f;
        [Tooltip("Max vertical gap between feet and candidate floor plane (metres).")]
        [SerializeField] private float maxFloorDistance = 2.5f;

        [Header("Wall shadow")]
        [SerializeField] private bool wallShadow = true;
        [Tooltip("Max distance from the character anchor to a vertical wall plane (metres).")]
        [SerializeField] private float maxWallDistance = 2.0f;
        [SerializeField] private float wallRadius = 0.35f;
        [SerializeField] private float wallHeightScale = 1.6f;
        [SerializeField] private float wallSurfacePadding = 0.01f;

        [Header("Look")]
        [Range(0f, 1f)] [SerializeField] private float shadowAlpha = 0.38f;
        [SerializeField] private Color shadowColor = Color.black;

        [Header("Behaviour")]
        [Tooltip("Only show during BodyTrackingController playback (same window as AR occlusion).")]
        [SerializeField] private bool onlyDuringPlayback = true;
        [Tooltip("Re-scan AR planes at this interval instead of every frame.")]
        [SerializeField] private float planeScanInterval = 0.2f;
        [SerializeField] private bool verboseLogging = false;

        // --- scene wiring ---
        private CharacterSwitcher switcher;
        private FBXCharacterController fbxController;
        private BodyTrackingController bodyController;

        // --- shadow objects ---
        private Transform floorShadowRoot;
        private Transform wallShadowRoot;
        private Material shadowMaterial;
        private Texture2D softCircleTexture;

        // --- cached plane picks ---
        private ARPlane cachedFloorPlane;
        private ARPlane cachedWallPlane;
        private float planeScanTimer;
        private Animator boundAnimator;
        private Transform boundRoot;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void OnEnable()
        {
            ResolveSceneWiring();
            EnsureShadowObjects();
            planeScanTimer = 0f;
        }

        private void OnDisable()
        {
            SetShadowsVisible(false);
            if (switcher != null) switcher.OnCharacterChanged -= OnCharacterChanged;
        }

        private void ResolveSceneWiring()
        {
            if (planeManager == null)
                planeManager = FindFirstObjectByType<ARPlaneManager>(FindObjectsInactive.Include);

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

            if (autoResolveLightDirection)
                TryResolveLightDirection();
        }

        private void OnCharacterChanged(int _, GameObject newRoot)
        {
            BindCharacter(newRoot != null ? newRoot.transform : null);
        }

        private void TryResolveLightDirection()
        {
            var group = GameObject.Find("TendorLighting");
            if (group == null) return;
            var key = group.transform.Find("Key Light (Skylights)");
            if (key == null) return;
            lightDirection = key.forward.normalized;
        }

        private void BindCharacter(Transform root)
        {
            boundRoot = root;
            boundAnimator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
            cachedFloorPlane = null;
            cachedWallPlane = null;
            planeScanTimer = 0f;
        }

        private Transform ResolveActiveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (switcher != null && switcher.Current != null) return switcher.Current.transform;
            if (fbxController != null && fbxController.CharacterRoot != null) return fbxController.CharacterRoot.transform;
            return null;
        }

        private bool explicitEnabled = true;

        /// <summary>Runtime on/off (e.g. tied to playback mode). Pass false to hide regardless of playback.</summary>
        public void SetShadowsEnabled(bool enabled)
        {
            explicitEnabled = enabled;
            if (!enabled) SetShadowsVisible(false);
        }

        private void LateUpdate()
        {
            if (!explicitEnabled)
            {
                SetShadowsVisible(false);
                return;
            }

            if (onlyDuringPlayback && (bodyController == null || !bodyController.IsPlaying))
            {
                SetShadowsVisible(false);
                return;
            }

            if (planeManager == null)
            {
                SetShadowsVisible(false);
                return;
            }

            Transform root = ResolveActiveRoot();
            if (root == null)
            {
                SetShadowsVisible(false);
                return;
            }
            if (root != boundRoot) BindCharacter(root);

            EnsureShadowObjects();
            if (!TryGetAnchorPose(out Vector3 anchor, out float height))
            {
                SetShadowsVisible(false);
                return;
            }

            planeScanTimer -= Time.deltaTime;
            if (planeScanTimer <= 0f)
            {
                planeScanTimer = Mathf.Max(0.05f, planeScanInterval);
                RefreshPlaneCache(anchor);
            }

            bool anyVisible = false;

            if (floorShadow && cachedFloorPlane != null &&
                TryProjectOnPlane(cachedFloorPlane, anchor, maxFloorDistance, out Vector3 floorPoint, out Vector3 floorNormal))
            {
                Vector3 horizontalLight = Vector3.ProjectOnPlane(-lightDirection.normalized, floorNormal);
                if (horizontalLight.sqrMagnitude > 0.0001f)
                    floorPoint += horizontalLight.normalized * (floorLightOffset * height);

                float radius = floorRadius + floorRadiusPerHeight * height;
                PlaceShadow(floorShadowRoot, floorPoint + floorNormal * floorSurfacePadding, floorNormal, horizontalLight,
                    new Vector2(radius * 2f, radius * 2f));
                anyVisible = true;
            }
            else if (floorShadowRoot != null)
                floorShadowRoot.gameObject.SetActive(false);

            if (wallShadow && cachedWallPlane != null &&
                TryProjectOnPlane(cachedWallPlane, anchor, maxWallDistance, out Vector3 wallPoint, out Vector3 wallNormal))
            {
                float radius = wallRadius + floorRadiusPerHeight * height * 0.5f;
                float wallHeight = radius * wallHeightScale;
                PlaceShadow(wallShadowRoot, wallPoint + wallNormal * wallSurfacePadding, wallNormal, Vector3.up,
                    new Vector2(radius * 2f, wallHeight));
                anyVisible = true;
            }
            else if (wallShadowRoot != null)
                wallShadowRoot.gameObject.SetActive(false);

            if (!anyVisible && verboseLogging && Time.frameCount % 120 == 0)
                Debug.Log("[ARCharacterContactShadow] No suitable AR plane under/near character — shadow hidden.");
        }

        private bool TryGetAnchorPose(out Vector3 anchor, out float height)
        {
            anchor = Vector3.zero;
            height = 1.7f;

            Transform leftFoot = null;
            Transform rightFoot = null;
            Transform head = null;

            if (boundAnimator != null && boundAnimator.isHuman)
            {
                leftFoot = boundAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
                rightFoot = boundAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
                head = boundAnimator.GetBoneTransform(HumanBodyBones.Head);
            }

            if (leftFoot != null && rightFoot != null)
            {
                anchor = (leftFoot.position + rightFoot.position) * 0.5f;
                if (head != null)
                    height = Mathf.Max(1.2f, head.position.y - anchor.y);
                else if (boundRoot != null)
                    height = Mathf.Max(1.2f, boundRoot.position.y - anchor.y + 1.6f);
                return true;
            }

            if (boundRoot != null)
            {
                anchor = boundRoot.position;
                height = 1.7f;
                return true;
            }

            return false;
        }

        private void RefreshPlaneCache(Vector3 anchor)
        {
            cachedFloorPlane = null;
            cachedWallPlane = null;
            float bestFloor = float.MaxValue;
            float bestWall = float.MaxValue;

            foreach (var trackable in planeManager.trackables)
            {
                var plane = trackable as ARPlane;
                if (plane == null) continue;

                if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (!TryProjectOnPlane(plane, anchor, maxFloorDistance, out _, out Vector3 normal)) continue;
                    float dist = Mathf.Abs(Vector3.Dot(anchor - plane.transform.position, normal));
                    if (dist < bestFloor)
                    {
                        bestFloor = dist;
                        cachedFloorPlane = plane;
                    }
                }
                else if (wallShadow && plane.alignment == PlaneAlignment.Vertical)
                {
                    if (!TryProjectOnPlane(plane, anchor, maxWallDistance, out _, out _)) continue;
                    float dist = HorizontalDistance(plane.transform.position, anchor);
                    if (dist < bestWall)
                    {
                        bestWall = dist;
                        cachedWallPlane = plane;
                    }
                }
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static bool TryProjectOnPlane(ARPlane plane, Vector3 worldPoint, float maxDistance, out Vector3 onPlane,
            out Vector3 normal)
        {
            onPlane = worldPoint;
            normal = plane.normal;
            float signed = Vector3.Dot(worldPoint - plane.transform.position, normal);
            if (Mathf.Abs(signed) > maxDistance) return false;

            onPlane = worldPoint - normal * signed;
            return PointInPlaneBoundary(plane, onPlane);
        }

        private static bool PointInPlaneBoundary(ARPlane plane, Vector3 worldPoint)
        {
            Vector3 local = plane.transform.InverseTransformPoint(worldPoint);
            Vector2 p = new Vector2(local.x, local.z);

            NativeArray<Vector2> boundary = plane.boundary;
            if (!boundary.IsCreated || boundary.Length < 3)
            {
                // Fallback: axis-aligned bounds from ARPlane.size (x × z in plane space).
                return Mathf.Abs(local.x) <= plane.size.x * 0.5f && Mathf.Abs(local.z) <= plane.size.y * 0.5f;
            }

            bool inside = false;
            int count = boundary.Length;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 a = boundary[i];
                Vector2 b = boundary[j];
                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-6f) + a.x))
                    inside = !inside;
            }
            return inside;
        }

        private static void PlaceShadow(Transform root, Vector3 position, Vector3 planeNormal, Vector3 tangentHint,
            Vector2 size)
        {
            if (root == null) return;
            root.gameObject.SetActive(true);
            root.position = position;

            Vector3 tangent = Vector3.ProjectOnPlane(tangentHint.sqrMagnitude > 0.0001f ? tangentHint : Vector3.forward, planeNormal);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.ProjectOnPlane(Vector3.right, planeNormal);
            tangent.Normalize();
            root.rotation = Quaternion.LookRotation(tangent, planeNormal);
            root.localScale = new Vector3(size.x, size.y, 1f);
        }

        private void EnsureShadowObjects()
        {
            if (shadowMaterial == null)
                shadowMaterial = CreateShadowMaterial();

            if (floorShadow && floorShadowRoot == null)
                floorShadowRoot = CreateShadowQuad("AR Contact Shadow (Floor)");
            if (wallShadow && wallShadowRoot == null)
                wallShadowRoot = CreateShadowQuad("AR Contact Shadow (Wall)");
        }

        private Transform CreateShadowQuad(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = shadowMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            go.transform.SetParent(transform, false);
            go.SetActive(false);
            return go.transform;
        }

        private Material CreateShadowMaterial()
        {
            softCircleTexture = CreateSoftCircleTexture(64);
            var shader = FindShader(
                "Universal Render Pipeline/Unlit",
                "Unlit/Transparent",
                "Sprites/Default");
            var mat = new Material(shader);
            Color c = shadowColor;
            c.a = shadowAlpha;
            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, c);
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, c);
            if (mat.HasProperty(BaseMapId)) mat.SetTexture(BaseMapId, softCircleTexture);
            if (mat.HasProperty(MainTexId)) mat.SetTexture(MainTexId, softCircleTexture);
            ConfigureTransparent(mat);
            return mat;
        }

        private static Texture2D CreateSoftCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ContactShadowSoftCircle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = 1f - Mathf.SmoothStep(0.25f, 1f, r);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        private static void ConfigureTransparent(Material mat)
        {
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
            }
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        private static Shader FindShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                if (shader != null) return shader;
            }
            Debug.LogWarning("[ARCharacterContactShadow] No unlit shader found — shadow will not render.");
            return Shader.Find("Hidden/InternalErrorShader");
        }

        private void SetShadowsVisible(bool visible)
        {
            if (floorShadowRoot != null) floorShadowRoot.gameObject.SetActive(visible);
            if (wallShadowRoot != null) wallShadowRoot.gameObject.SetActive(visible);
        }
    }
}
