using UnityEngine;
using BodyTracking.AR;
using BodyTracking.Playback;
using BodyTracking.Playback.PostProcess;
using BodyTracking.Spatial;
using BodyTracking.Utils;

namespace BodyTracking.Diagnostics
{
    /// <summary>
    /// On-device debug overlay for the flat-wall climbing system. Draws what the system "thinks" is going on so you
    /// can see it on the phone (not just in the Editor):
    ///   • a semi-transparent BLUE quad on the wall plane (RouteRoot Z = wall offset),
    ///   • a semi-transparent GREEN quad on the detected floor plane,
    ///   • small spheres on each hand/foot that turn GREEN when that limb is locked onto a hold,
    ///   • a colored status HUD (localized / wall / floor / playing / per-limb contact).
    /// Everything is built from runtime meshes + materials (works in URP on device). Toggle via <see cref="ShowVisuals"/>.
    /// Attach to any always-on GameObject; it auto-finds the player + surface probe.
    /// </summary>
    public class WallFloorDebugVisualizer : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private FusedCharacterPlayer player;
        [SerializeField] private ARSurfaceProbe surfaceProbe;

        [Header("Visibility")]
        [Tooltip("Master switch for the 3D planes/markers AND the status HUD.")]
        [SerializeField] private bool showVisuals = true;
        [Tooltip("Draw the on-screen colored status panel.")]
        [SerializeField] private bool showStatusHud = false;
        [Tooltip("Draw the semi-transparent wall + floor planes.")]
        [SerializeField] private bool showPlanes = true;
        [Tooltip("Draw the per-limb hold-contact markers.")]
        [SerializeField] private bool showContactMarkers = true;
        [Tooltip("Draw ALL inferred climbing holds on the wall (hands = spheres, feet = cubes), colored by confidence.")]
        [SerializeField] private bool showHolds = true;

        [Header("Appearance")]
        [SerializeField] private float planeSize = 3f;
        [SerializeField] private float markerRadius = 0.06f;
        [SerializeField] private Color wallColor = new Color(0.15f, 0.6f, 1f, 0.22f);
        [SerializeField] private Color floorColor = new Color(0.3f, 1f, 0.4f, 0.22f);
        [Tooltip("Limb anchored to a known hold.")]
        [SerializeField] private Color contactOnColor = new Color(0.2f, 1f, 0.3f, 0.95f);
        [Tooltip("Limb pushed out of the wall (no hold under it).")]
        [SerializeField] private Color contactPushColor = new Color(1f, 0.85f, 0.1f, 0.95f);
        [Tooltip("Limb still inside the wall after correction (failed to reach the surface).")]
        [SerializeField] private Color contactInsideColor = new Color(1f, 0.2f, 0.15f, 0.95f);

        [Header("Hold overlay")]
        [Tooltip("Marker size (m) for an inferred hold.")]
        [SerializeField] private float holdMarkerSize = 0.05f;
        [Tooltip("Color for a low-confidence (just-guessed) hold.")]
        [SerializeField] private Color holdLowConfidenceColor = new Color(1f, 0.35f, 0.2f, 1f);
        [Tooltip("Color for a high-confidence (often-used) hold.")]
        [SerializeField] private Color holdHighConfidenceColor = new Color(0.2f, 1f, 0.4f, 1f);

        public bool ShowVisuals { get => showVisuals; set { showVisuals = value; if (!value) HideAll(); } }
        public bool ShowStatusHud { get => showStatusHud; set => showStatusHud = value; }
        public bool ShowHolds { get => showHolds; set { showHolds = value; if (!value) HideHoldMarkers(); } }
        public bool ShowPlanes { get => showPlanes; set => showPlanes = value; }
        public bool ShowContactMarkers { get => showContactMarkers; set => showContactMarkers = value; }

        private GameObject wallQuad;
        private GameObject floorQuad;
        private GameObject[] markers;
        private Material wallMat, floorMat;
        private Material contactOnMat, contactPushMat, contactInsideMat;

        // Hold overlay marker pool (one GameObject per inferred hold; sphere for hands, cube for feet).
        private readonly System.Collections.Generic.List<GameObject> holdMarkers = new System.Collections.Generic.List<GameObject>();
        private readonly System.Collections.Generic.List<bool> holdMarkerIsHand = new System.Collections.Generic.List<bool>();

        // Cached status for the HUD (refreshed in LateUpdate so the planes/markers and text agree).
        private bool stLocalized, stWall, stFloor, stPlaying;
        private readonly bool[] stContacts = new bool[4];
        private string stWallInfo = "";
        private int stHoldHands, stHoldFeet;

        FusedCharacterPlayer Player()
        {
            if (player == null) player = FindFirstObjectByType<FusedCharacterPlayer>();
            return player;
        }

        ARSurfaceProbe Probe()
        {
            if (surfaceProbe == null)
            {
                var p = Player();
                surfaceProbe = (p != null && p.SurfaceProbe != null) ? p.SurfaceProbe : FindAnyObjectByType<ARSurfaceProbe>();
            }
            return surfaceProbe;
        }

        void LateUpdate()
        {
            if (!showVisuals)
            {
                HideAll();
                return;
            }

            var p = Player();
            var probe = Probe();
            Transform routeRoot = probe != null ? probe.RouteRoot : null;

            stPlaying = p != null && p.IsPlayingBack;
            stLocalized = p != null && p.RouteRootProvider != null && p.RouteRootProvider.IsLocalized;
            stWall = probe != null && probe.TryGetWallPlane(out _, out _);
            float floorY = 0f;
            stFloor = probe != null && probe.HasFloor(out floorY);

            if (probe != null)
            {
                string src = probe.WallSource == ARSurfaceProbe.WallSourceMode.ARVerticalPlane
                    ? (probe.HasArWall ? "AR plane" : "AR plane (none yet)")
                    : "RouteRoot";
                stWallInfo = $"Wall: {src}  Z {probe.WallLocalZOffset:F2} m";
            }
            else stWallInfo = "Wall: n/a";

            UpdateWallQuad(routeRoot, probe);
            UpdateFloorQuad(routeRoot, stFloor, floorY);
            UpdateContactMarkers(p);
            UpdateHoldMarkers(p, routeRoot);
        }

        void UpdateWallQuad(Transform routeRoot, ARSurfaceProbe probe)
        {
            // Use the probe's resolved wall plane (AR-detected vertical plane if active, else the RouteRoot plane) so
            // the blue quad shows EXACTLY the plane the slab/contact logic uses, including AR tilt.
            if (!showPlanes || probe == null || !probe.TryGetWallPlane(out Vector3 wallPoint, out Vector3 wallNormal)
                || wallNormal.sqrMagnitude < 1e-8f)
            {
                SetActive(wallQuad, false);
                return;
            }
            EnsureWallQuad();
            wallNormal.Normalize();
            Vector3 up = Mathf.Abs(Vector3.Dot(wallNormal, Vector3.up)) < 0.95f ? Vector3.up : Vector3.forward;
            wallQuad.transform.position = wallPoint;
            wallQuad.transform.rotation = Quaternion.LookRotation(wallNormal, up);
            wallQuad.transform.localScale = new Vector3(planeSize, planeSize, 1f);
            SetActive(wallQuad, true);
        }

        void UpdateFloorQuad(Transform routeRoot, bool hasFloor, float floorY)
        {
            if (!showPlanes || !hasFloor)
            {
                SetActive(floorQuad, false);
                return;
            }
            EnsureFloorQuad();
            // Center the floor patch under the wall frame (or world origin) at the detected height, lying flat.
            Vector3 center = routeRoot != null
                ? new Vector3(routeRoot.position.x, floorY, routeRoot.position.z)
                : new Vector3(0f, floorY, 0f);
            floorQuad.transform.position = center;
            floorQuad.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // XY quad -> horizontal
            floorQuad.transform.localScale = new Vector3(planeSize * 1.5f, planeSize * 1.5f, 1f);
            SetActive(floorQuad, true);
        }

        void UpdateContactMarkers(FusedCharacterPlayer p)
        {
            for (int i = 0; i < stContacts.Length; i++) stContacts[i] = false;

            var push = p != null ? p.ContactPushStatus : null;
            if (!showContactMarkers || push == null)
            {
                if (markers != null)
                    foreach (var m in markers) SetActive(m, false);
                return;
            }

            EnsureMarkers();
            // green = anchored to a hold, yellow = pushed out of the wall (no hold), red = still inside after the fix.
            for (int i = 0; i < markers.Length; i++)
            {
                var m = markers[i];
                if (m == null) continue;
                var ps = i < push.Count ? push[i] : default;
                stContacts[i] = ps.status == PosePenetrationResolver.LimbPushStatus.Anchored;

                Material mat = null;
                switch (ps.status)
                {
                    case PosePenetrationResolver.LimbPushStatus.Anchored: mat = contactOnMat; break;
                    case PosePenetrationResolver.LimbPushStatus.PushedOut: mat = contactPushMat; break;
                    case PosePenetrationResolver.LimbPushStatus.Inside: mat = contactInsideMat; break;
                }

                if (mat != null)
                {
                    m.transform.position = ps.world;
                    SetRendererMaterial(m, mat);
                    m.transform.localScale = Vector3.one * markerRadius * 2f;
                    SetActive(m, true);
                }
                else
                {
                    SetActive(m, false);
                }
            }
        }

        void UpdateHoldMarkers(FusedCharacterPlayer p, Transform routeRoot)
        {
            stHoldHands = 0;
            stHoldFeet = 0;

            var map = p != null ? p.HoldMap : null;
            if (!showHolds || map == null)
            {
                HideHoldMarkers();
                return;
            }

            var holds = map.Holds;
            for (int i = 0; i < holds.Count; i++)
            {
                var hold = holds[i];
                if (hold.isHand) stHoldHands++; else stHoldFeet++;

                var marker = EnsureHoldMarker(i, hold.isHand);
                if (marker == null) continue;

                marker.transform.position = hold.ToWorld(routeRoot);
                marker.transform.localScale = Vector3.one * holdMarkerSize;
                SetMarkerColor(marker, ConfidenceColor(hold.Confidence));
                SetActive(marker, true);
            }

            // Hide any pooled markers beyond the current hold count.
            for (int i = holds.Count; i < holdMarkers.Count; i++)
                SetActive(holdMarkers[i], false);
        }

        GameObject EnsureHoldMarker(int index, bool isHand)
        {
            // Grow the pool as needed.
            while (holdMarkers.Count <= index)
            {
                holdMarkers.Add(null);
                holdMarkerIsHand.Add(false);
            }
            // (Re)create the primitive if missing or the hand/foot shape doesn't match.
            if (holdMarkers[index] == null || holdMarkerIsHand[index] != isHand)
            {
                if (holdMarkers[index] != null) Destroy(holdMarkers[index]);
                holdMarkers[index] = MakeHoldMarker(isHand);
                holdMarkerIsHand[index] = isHand;
            }
            return holdMarkers[index];
        }

        GameObject MakeHoldMarker(bool isHand)
        {
            // Hands = spheres, feet = cubes, so foot-holds read as visually distinct from hand-holds.
            var go = GameObject.CreatePrimitive(isHand ? PrimitiveType.Sphere : PrimitiveType.Cube);
            go.name = isHand ? "Hold_Hand" : "Hold_Foot";
            go.transform.SetParent(transform, false);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            SetRendererMaterial(go, DebugVisualizationMaterials.CreateTransparentColorMaterial(Color.white));
            go.SetActive(false);
            return go;
        }

        Color ConfidenceColor(float confidence)
        {
            float c = Mathf.Clamp01(confidence);
            var col = Color.Lerp(holdLowConfidenceColor, holdHighConfidenceColor, c);
            col.a = Mathf.Lerp(0.3f, 0.9f, c); // faint guesses stay translucent; sure holds read solid
            return col;
        }

        static void SetMarkerColor(GameObject go, Color color)
        {
            if (go == null) return;
            var r = go.GetComponent<Renderer>();
            var m = r != null ? r.sharedMaterial : null;
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        }

        void HideHoldMarkers()
        {
            for (int i = 0; i < holdMarkers.Count; i++)
                SetActive(holdMarkers[i], false);
        }

        // ---- HUD ----------------------------------------------------------------------------------

        GUIStyle hudStyle;

        void OnGUI()
        {
            if (!showVisuals || !showStatusHud) return;

            if (hudStyle == null)
                hudStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.022f)), fontStyle = FontStyle.Bold };

            float lineH = hudStyle.fontSize * 1.5f;
            float pad = lineH * 0.5f;
            float w = Screen.width * 0.5f;
            float x = pad;
            float y = pad;

            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 6f, y - 6f, w, lineH * 9f + 12f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            DrawStatus("Localized (RouteRoot)", stLocalized, ref x, ref y, lineH);
            DrawStatus("Wall plane", stWall, ref x, ref y, lineH);
            GUI.color = Color.white;
            GUI.Label(new Rect(x + lineH, y, Screen.width * 0.5f, lineH), stWallInfo, hudStyle);
            y += lineH;
            DrawStatus("Floor plane", stFloor, ref x, ref y, lineH);
            DrawStatus("Playing", stPlaying, ref x, ref y, lineH);
            DrawStatus("Contact L-hand", stContacts[0], ref x, ref y, lineH);
            DrawStatus("Contact R-hand", stContacts[1], ref x, ref y, lineH);
            DrawStatus("Contact L-foot / R-foot", stContacts[2] || stContacts[3], ref x, ref y, lineH);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, Screen.width * 0.5f, lineH),
                $"Holds: {stHoldHands + stHoldFeet} (hands {stHoldHands} / feet {stHoldFeet})", hudStyle);
            y += lineH;
        }

        void DrawStatus(string label, bool on, ref float x, ref float y, float lineH)
        {
            GUI.color = on ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.45f, 0.4f);
            GUI.Label(new Rect(x, y, Screen.width * 0.5f, lineH), (on ? "\u25CF " : "\u25CB ") + label, hudStyle);
            y += lineH;
        }

        // ---- object/material lifecycle ------------------------------------------------------------

        void EnsureWallQuad()
        {
            if (wallQuad != null) return;
            if (wallMat == null) wallMat = MakePlaneMaterial(wallColor);
            wallQuad = MakeQuad("WallDebugPlane", wallMat);
        }

        void EnsureFloorQuad()
        {
            if (floorQuad != null) return;
            if (floorMat == null) floorMat = MakePlaneMaterial(floorColor);
            floorQuad = MakeQuad("FloorDebugPlane", floorMat);
        }

        void EnsureMarkers()
        {
            if (markers != null) return;
            if (contactOnMat == null) contactOnMat = DebugVisualizationMaterials.CreateSolidColorMaterial(contactOnColor);
            if (contactPushMat == null) contactPushMat = DebugVisualizationMaterials.CreateSolidColorMaterial(contactPushColor);
            if (contactInsideMat == null) contactInsideMat = DebugVisualizationMaterials.CreateSolidColorMaterial(contactInsideColor);
            string[] names = { "Contact_LeftHand", "Contact_RightHand", "Contact_LeftFoot", "Contact_RightFoot" };
            markers = new GameObject[4];
            for (int i = 0; i < 4; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = names[i];
                s.transform.SetParent(transform, false);
                var col = s.GetComponent<Collider>();
                if (col != null) Destroy(col);
                SetRendererMaterial(s, contactOnMat);
                s.transform.localScale = Vector3.one * markerRadius * 2f;
                s.SetActive(false);
                markers[i] = s;
            }
        }

        GameObject MakeQuad(string name, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            q.transform.SetParent(transform, false);
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            SetRendererMaterial(q, mat);
            q.SetActive(false);
            return q;
        }

        static Material MakePlaneMaterial(Color color)
        {
            var m = DebugVisualizationMaterials.CreateTransparentColorMaterial(color);
            // Visible from both sides regardless of which way the quad faces.
            if (m != null && m.HasProperty("_Cull"))
                m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            return m;
        }

        static void SetRendererMaterial(GameObject go, Material mat)
        {
            if (go == null || mat == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }

        void HideAll()
        {
            SetActive(wallQuad, false);
            SetActive(floorQuad, false);
            if (markers != null)
                foreach (var m in markers) SetActive(m, false);
            HideHoldMarkers();
        }

        void OnDestroy()
        {
            if (wallQuad != null) Destroy(wallQuad);
            if (floorQuad != null) Destroy(floorQuad);
            if (markers != null)
                foreach (var m in markers) if (m != null) Destroy(m);
            foreach (var m in holdMarkers) if (m != null) Destroy(m);
        }
    }
}
