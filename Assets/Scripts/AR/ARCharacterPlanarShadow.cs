using System.Collections.Generic;
using BodyTracking;
using BodyTracking.Animation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AR
{
    /// <summary>
    /// Grey semi-transparent floor shadow for AR playback. Skinned meshes are baked each frame, projected onto
    /// the AR floor in world space, merged into a single silhouette mesh, and drawn once so overlapping body
    /// parts do not stack transparent layers.
    /// </summary>
    [DefaultExecutionOrder(400)]
    public class ARCharacterPlanarShadow : MonoBehaviour
    {
        [Header("Target (optional — auto-resolved)")]
        [SerializeField] private Transform characterRoot;
        [SerializeField] private bool autoTrackActiveCharacter = true;

        [Header("AR floor")]
        [SerializeField] private ARPlaneManager planeManager;
        [SerializeField] private float maxFloorDistance = 2.5f;
        [SerializeField] private float planeScanInterval = 0.2f;

        [Header("Projection")]
        [Tooltip("Project straight down onto the floor. Most reliable for horizontal AR planes.")]
        [SerializeField] private bool projectDownward = true;
        [SerializeField] private Vector3 lightDirection = new Vector3(0f, -1f, 0f);
        [SerializeField] private bool autoResolveLightDirection = false;

        [Header("Look")]
        [Range(0f, 1f)] [SerializeField] private float shadowAlpha = 0.45f;
        [SerializeField] private Color shadowColor = new Color(0.22f, 0.22f, 0.22f, 1f);
        [SerializeField] private float surfacePadding = 0.015f;

        [Header("Meshes")]
        [SerializeField] private bool skipNonBodyMeshes = true;
        [SerializeField] private float rebindInterval = 0.5f;

        [Header("Behaviour")]
        [SerializeField] private bool onlyDuringPlayback = true;
        [SerializeField] private bool verboseLogging = true;

        private struct ShadowSource
        {
            public SkinnedMeshRenderer skinnedSource;
            public Mesh bakedMesh;
        }

        private readonly List<ShadowSource> shadowSources = new List<ShadowSource>();
        private readonly List<Vector3> combineVerts = new List<Vector3>(8192);
        private readonly List<int> combineTris = new List<int>(16384);

        private CharacterSwitcher switcher;
        private FBXCharacterController fbxController;
        private BodyTrackingController bodyController;

        private struct FloorSnapshot
        {
            public Vector3 planePoint;
            public Vector3 planeNormal;
        }

        private Transform shadowContainer;
        private Transform combinedTransform;
        private MeshFilter combinedFilter;
        private MeshRenderer combinedRenderer;
        private Mesh combinedMesh;
        private Transform boundRoot;
        private ARPlane cachedFloorPlane;
        private FloorSnapshot floorSnapshot;
        private float planeScanTimer;
        private float rebindTimer;
        private Material shadowMaterial;
        private bool explicitEnabled = true;
        private bool loggedReady;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void OnEnable()
        {
            loggedReady = false;
            EnsureShadowContainer();
            ResolveSceneWiring();
            planeScanTimer = 0f;
            rebindTimer = 0f;
        }

        private void OnDisable()
        {
            SetShadowsVisible(false);
            if (switcher != null) switcher.OnCharacterChanged -= OnCharacterChanged;
        }

        private void OnDestroy()
        {
            ClearShadowSources();
            if (combinedMesh != null) Destroy(combinedMesh);
            if (combinedRenderer != null) Destroy(combinedRenderer.gameObject);
            if (shadowMaterial != null) Destroy(shadowMaterial);
        }

        /// <summary>Runtime on/off (e.g. tied to playback mode). Pass false to hide regardless of playback.</summary>
        public void SetShadowsEnabled(bool enabled)
        {
            explicitEnabled = enabled;
            if (!enabled) SetShadowsVisible(false);
        }

        private void EnsureShadowContainer()
        {
            if (shadowContainer != null) return;

            var existing = transform.Find("_PlanarShadowRoot");
            if (existing != null)
            {
                shadowContainer = existing;
                combinedTransform = shadowContainer.Find("CombinedPlanarShadow");
                if (combinedTransform != null)
                {
                    combinedFilter = combinedTransform.GetComponent<MeshFilter>();
                    combinedRenderer = combinedTransform.GetComponent<MeshRenderer>();
                    combinedMesh = combinedFilter != null ? combinedFilter.sharedMesh : null;
                }
                return;
            }

            var rootGo = new GameObject("_PlanarShadowRoot");
            rootGo.transform.SetParent(transform, false);
            shadowContainer = rootGo.transform;

            var combineGo = new GameObject("CombinedPlanarShadow");
            combineGo.AddComponent<ARPresentationShadow>();
            combineGo.transform.SetParent(shadowContainer, false);
            combineGo.transform.localPosition = Vector3.zero;
            combineGo.transform.localRotation = Quaternion.identity;
            combineGo.transform.localScale = Vector3.one;
            combinedTransform = combineGo.transform;

            combinedMesh = new Mesh { name = "PlanarShadowCombined" };
            combinedMesh.MarkDynamic();
            combinedFilter = combineGo.AddComponent<MeshFilter>();
            combinedFilter.sharedMesh = combinedMesh;
            combinedRenderer = combineGo.AddComponent<MeshRenderer>();
            combinedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            combinedRenderer.receiveShadows = false;
            combineGo.SetActive(false);
        }

        private void ResolveSceneWiring()
        {
            if (planeManager == null)
                planeManager = FindFirstObjectByType<ARPlaneManager>(FindObjectsInactive.Include);
            if (planeManager != null && !planeManager.enabled)
                planeManager.enabled = true;

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

            if (autoResolveLightDirection && !projectDownward)
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

        private Transform ResolveActiveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (switcher != null)
            {
                if (switcher.Current != null) return switcher.Current.transform;
                switcher.EnsureBound();
                if (switcher.Current != null) return switcher.Current.transform;
            }
            if (fbxController != null && fbxController.CharacterRoot != null)
                return fbxController.CharacterRoot.transform;
            return null;
        }

        private void BindCharacter(Transform root)
        {
            boundRoot = root;
            cachedFloorPlane = null;
            floorSnapshot = default;
            planeScanTimer = 0f;
            rebindTimer = 0f;
            loggedReady = false;
            RebuildShadowSources(root);
        }

        private void LateUpdate()
        {
            if (!explicitEnabled) { SetShadowsVisible(false); return; }

            if (onlyDuringPlayback && (bodyController == null || !bodyController.IsPlaying))
            {
                SetShadowsVisible(false);
                return;
            }

            if (planeManager == null)
            {
                if (verboseLogging && Time.frameCount % 180 == 0)
                    Debug.LogWarning("[ARCharacterPlanarShadow] No ARPlaneManager found.");
                SetShadowsVisible(false);
                return;
            }

            Transform root = ResolveActiveRoot();
            if (root == null)
            {
                if (verboseLogging && Time.frameCount % 180 == 0)
                    Debug.LogWarning("[ARCharacterPlanarShadow] No character root yet.");
                SetShadowsVisible(false);
                return;
            }

            if (root != boundRoot) BindCharacter(root);

            EnsureShadowMaterial();
            if (shadowMaterial == null || combinedRenderer == null) { SetShadowsVisible(false); return; }

            if (shadowSources.Count == 0)
            {
                rebindTimer -= Time.deltaTime;
                if (rebindTimer <= 0f)
                {
                    rebindTimer = rebindInterval;
                    RebuildShadowSources(root);
                }
                if (shadowSources.Count == 0)
                {
                    if (verboseLogging && Time.frameCount % 180 == 0)
                        Debug.LogWarning($"[ARCharacterPlanarShadow] No skinned meshes on '{root.name}' — shadow hidden.");
                    SetShadowsVisible(false);
                    return;
                }
            }

            if (!TryGetFootAnchor(out Vector3 anchor)) { SetShadowsVisible(false); return; }

            planeScanTimer -= Time.deltaTime;
            if (planeScanTimer <= 0f)
            {
                planeScanTimer = Mathf.Max(0.05f, planeScanInterval);
                RefreshFloorPlane(anchor);
            }

            FloorSnapshot floor = cachedFloorPlane != null
                ? CaptureSnapshot(cachedFloorPlane, anchor)
                : floorSnapshot.planeNormal.sqrMagnitude > 0.001f
                    ? floorSnapshot
                    : FallbackFloor(anchor);

            floorSnapshot = floor;
            ApplyMaterialColor();

            if (!RebuildCombinedSilhouette(floor))
            {
                SetShadowsVisible(false);
                return;
            }

            SetShadowsVisible(true);

            if (!loggedReady)
            {
                loggedReady = true;
                Debug.Log($"[ARCharacterPlanarShadow] Active on '{root.name}': {shadowSources.Count} source mesh(es) " +
                          $"→ 1 combined silhouette, floor Y≈{floor.planePoint.y:F2}, anchor Y={anchor.y:F2}.");
            }
        }

        private bool RebuildCombinedSilhouette(FloorSnapshot floor)
        {
            combineVerts.Clear();
            combineTris.Clear();
            Vector3 projDir = projectDownward ? Vector3.down : lightDirection.normalized;
            var worldToLocal = combinedTransform.worldToLocalMatrix;

            for (int i = 0; i < shadowSources.Count; i++)
            {
                var source = shadowSources[i];
                if (source.skinnedSource == null || source.bakedMesh == null) continue;

                source.skinnedSource.BakeMesh(source.bakedMesh, true);

                var srcVerts = source.bakedMesh.vertices;
                var srcTris = source.bakedMesh.triangles;
                if (srcVerts == null || srcVerts.Length == 0 || srcTris == null || srcTris.Length == 0) continue;

                var localToWorld = source.skinnedSource.transform.localToWorldMatrix;
                int vertOffset = combineVerts.Count;

                for (int v = 0; v < srcVerts.Length; v++)
                {
                    Vector3 world = localToWorld.MultiplyPoint3x4(srcVerts[v]);
                    Vector3 projected = ProjectWorldPointOntoPlane(world, floor, projDir);
                    combineVerts.Add(worldToLocal.MultiplyPoint3x4(projected));
                }

                for (int t = 0; t < srcTris.Length; t++)
                    combineTris.Add(srcTris[t] + vertOffset);
            }

            if (combineVerts.Count == 0 || combineTris.Count == 0)
                return false;

            combinedMesh.Clear();
            combinedMesh.SetVertices(combineVerts);
            combinedMesh.SetTriangles(combineTris, 0);
            combinedMesh.RecalculateBounds();
            return true;
        }

        private Vector3 ProjectWorldPointOntoPlane(Vector3 world, FloorSnapshot floor, Vector3 lightDir)
        {
            Vector3 planePoint = floor.planePoint;
            Vector3 planeNormal = floor.planeNormal.normalized;
            lightDir = lightDir.normalized;
            float denom = Vector3.Dot(planeNormal, lightDir);

            if (Mathf.Abs(denom) < 1e-4f)
                return world + planeNormal * surfacePadding;

            float t = Vector3.Dot(planePoint - world, planeNormal) / denom;
            return t < 0f
                ? world + planeNormal * surfacePadding
                : world + lightDir * t + planeNormal * surfacePadding;
        }

        private static FloorSnapshot CaptureSnapshot(ARPlane plane, Vector3 anchor)
        {
            Vector3 normal = plane.normal;
            float signed = Vector3.Dot(anchor - plane.transform.position, normal);
            return new FloorSnapshot
            {
                planePoint = anchor - normal * signed,
                planeNormal = normal
            };
        }

        private static FloorSnapshot FallbackFloor(Vector3 anchor)
        {
            float floorY = anchor.y - 0.04f;
            return new FloorSnapshot
            {
                planePoint = new Vector3(anchor.x, floorY, anchor.z),
                planeNormal = Vector3.up
            };
        }

        private void ApplyMaterialColor()
        {
            Color c = shadowColor;
            c.a = shadowAlpha;
            if (shadowMaterial.HasProperty(BaseColorId)) shadowMaterial.SetColor(BaseColorId, c);
            if (shadowMaterial.HasProperty(ColorId)) shadowMaterial.SetColor(ColorId, c);
            combinedRenderer.sharedMaterial = shadowMaterial;
        }

        private void RebuildShadowSources(Transform root)
        {
            ClearShadowSources();
            if (root == null) return;

            EnsureShadowContainer();
            EnsureShadowMaterial();
            if (shadowMaterial == null) return;

            BodyTracking.LookDev.CharacterLookLab.DisableOcclusionShells(root);

            int built = 0;
            foreach (var srcSmr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (srcSmr == null || srcSmr.sharedMesh == null) continue;
                if (skipNonBodyMeshes && ShouldSkipMesh(srcSmr.gameObject.name)) continue;

                var baked = new Mesh { name = srcSmr.gameObject.name + "_PlanarShadowBake" };
                baked.MarkDynamic();
                shadowSources.Add(new ShadowSource
                {
                    skinnedSource = srcSmr,
                    bakedMesh = baked
                });
                built++;
            }

            if (verboseLogging)
                Debug.Log($"[ARCharacterPlanarShadow] Rebuilt on '{root.name}': {built} source mesh(es) → combined silhouette.");
        }

        private static bool ShouldSkipMesh(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("eyelash") || n.Contains("eyebrow") || n.Contains("eyeao") || n.Contains("eye_ao") ||
                   n.Contains("occlusion") || n.Contains("cornea") || n.Contains("hair") ||
                   n.Contains("glass") || n.Contains("teeth") || n.Contains("tongue") ||
                   n.Contains("shoe") || n.Contains("boot") || n.Contains("sock") ||
                   n.Contains("accessory") || n.Contains("jewel") || n.Contains("watch") ||
                   (n.Contains("eye") && !n.Contains("brow"));
        }

        private void ClearShadowSources()
        {
            for (int i = 0; i < shadowSources.Count; i++)
            {
                if (shadowSources[i].bakedMesh != null)
                    Destroy(shadowSources[i].bakedMesh);
            }
            shadowSources.Clear();
        }

        private void EnsureShadowMaterial()
        {
            if (shadowMaterial != null) return;

            var shader = FindShader(
                "Universal Render Pipeline/Unlit",
                "Unlit/Transparent",
                "Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("[ARCharacterPlanarShadow] No unlit transparent shader found — shadow will not render.");
                return;
            }

            shadowMaterial = new Material(shader) { name = "PlanarShadow (runtime)" };
            ConfigureTransparent(shadowMaterial);
            ApplyMaterialColor();
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
            mat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            mat.renderQueue = (int)RenderQueue.Transparent + 10;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        private static Shader FindShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                if (shader != null) return shader;
            }
            return null;
        }

        private void SetShadowsVisible(bool visible)
        {
            if (shadowContainer != null && shadowContainer.gameObject.activeSelf != visible)
                shadowContainer.gameObject.SetActive(visible);

            if (combinedRenderer != null)
            {
                combinedRenderer.gameObject.SetActive(visible);
                combinedRenderer.enabled = visible;
            }
        }

        private bool TryGetFootAnchor(out Vector3 anchor)
        {
            anchor = Vector3.zero;
            if (boundRoot == null) return false;

            var animator = boundRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                if (left != null && right != null)
                {
                    anchor = (left.position + right.position) * 0.5f;
                    return true;
                }
            }

            anchor = boundRoot.position;
            return true;
        }

        private void RefreshFloorPlane(Vector3 anchor)
        {
            cachedFloorPlane = null;
            float best = float.MaxValue;

            foreach (var trackable in planeManager.trackables)
            {
                var plane = trackable as ARPlane;
                if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp) continue;

                Vector3 normal = plane.normal;
                float signed = Vector3.Dot(anchor - plane.transform.position, normal);
                if (signed < -0.35f || signed > maxFloorDistance) continue;

                float dist = Mathf.Abs(signed);
                if (dist < best)
                {
                    best = dist;
                    cachedFloorPlane = plane;
                    floorSnapshot = CaptureSnapshot(plane, anchor);
                }
            }
        }
    }
}
