using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AR
{
    /// <summary>
    /// Queries the real environment for the climbing WALL surface (and a FLOOR level) so the penetration resolver
    /// can keep hands/feet from sinking through them. Tiered, with graceful fallback:
    ///   1. AR-detected geometry — raycast against AR mesh colliders on <see cref="arMeshLayerMask"/> (e.g. an
    ///      <c>ARMeshManager</c> whose mesh prefab carries a <c>MeshCollider</c> on that layer). Gives a real
    ///      surface point + normal.
    ///   2. RouteRoot plane — the wall is the RouteRoot Z=0 plane (Z out from wall).
    /// The FLOOR is resolved separately, preferring the AR-detected horizontal floor plane (works in ANY room/scan
    /// with no manual calibration), and falling back to a RouteRoot-local Y only if you opt in.
    /// All positions are world-space. Set the RouteRoot transform once localized via <see cref="SetRouteRoot"/>.
    /// </summary>
    public class ARSurfaceProbe : MonoBehaviour
    {
        /// <summary>How the wall plane is defined.</summary>
        public enum WallSourceMode
        {
            RouteRootPlane,   // RouteRoot Z = wallLocalZOffset plane (assumes map origin near the wall)
            ARVerticalPlane,  // ARKit's detected front-facing vertical plane (real geometry)
        }

        public struct SurfaceHit
        {
            public bool valid;        // a surface was resolved
            public bool inside;       // the queried point is inside the wall (or within the skin offset)
            public Vector3 surfacePoint; // point on the skin surface to pin the joint to
            public Vector3 normal;    // surface normal pointing OUT of the wall
            public float penetration; // metres the point is inside the skin surface (>0 => needs push out)
        }

        [Header("AR mesh (optional, best quality)")]
        [Tooltip("Raycast against real geometry on these layers (set your ARMeshManager mesh prefab's MeshCollider to one of them). If nothing is hit we fall back to the RouteRoot plane.")]
        [SerializeField] private LayerMask arMeshLayerMask = 0;
        [Tooltip("Use AR mesh raycasts when available. Off = always use the RouteRoot plane.")]
        [SerializeField] private bool useArMesh = true;
        [Tooltip("How far (m) to probe on each side of a joint when raycasting the AR mesh.")]
        [SerializeField] private float maxProbeDistance = 0.5f;

        [Header("Wall source")]
        [Tooltip("How the wall plane is defined. RouteRootPlane = the RouteRoot Z=offset plane (assumes the map " +
                 "origin is on the wall). ARVerticalPlane = use ARKit's real detected vertical wall plane (the one " +
                 "the white feature dots sit on, facing the phone) — fixes both depth offset AND tilt, no map " +
                 "dependence. Falls back to the RouteRoot plane until a vertical plane is detected.")]
        [SerializeField] private WallSourceMode wallSource = WallSourceMode.RouteRootPlane;

        [Header("Wall plane calibration")]
        [Tooltip("Where the physical wall sits in RouteRoot-local Z (out from the wall). 0 = the RouteRoot Z=0 plane " +
                 "is the wall. If the map origin is in front of / behind the real wall, calibrate this (auto from the " +
                 "climb, from a point on the wall, or from a detected AR vertical plane) so the whole climber stops " +
                 "clipping into or floating off the surface. Keep matched to the player's WallProjection offset.")]
        [SerializeField] private float wallLocalZOffset = 0f;

        [Header("Wall: AR-detected vertical plane")]
        [Tooltip("Only accept vertical planes at least this big (m^2) as the wall, so a small clutter plane isn't used.")]
        [SerializeField] private float minWallPlaneArea = 0.4f;
        [Tooltip("Reject vertical planes whose normal is more than this many degrees off the RouteRoot wall normal, " +
                 "so a side wall / pillar facing a different way isn't picked.")]
        [SerializeField] private float maxWallNormalDeviationDeg = 45f;

        [Header("Skin offsets (m kept outside the surface)")]
        [SerializeField] private float wallSkinOffset = 0.02f;
        [SerializeField] private float floorSkinOffset = 0.0f;

        [Header("Debug")]
        [Tooltip("Draw the wall plane + floor level as gizmos so you can confirm RouteRoot Z=0 lies on the real wall.")]
        [SerializeField] private bool drawWallGizmo = true;

        [Header("Floor: AR-detected plane (works in any room/scan)")]
        [Tooltip("Use the AR-detected horizontal floor plane for the floor height. Recommended — no per-room calibration.")]
        [SerializeField] private bool useArPlaneFloor = true;
        [Tooltip("AR plane manager. Auto-found in the scene if left empty.")]
        [SerializeField] private ARPlaneManager planeManager;
        [Tooltip("Ignore tiny horizontal planes (m^2) so a stool/ledge isn't mistaken for the floor.")]
        [SerializeField] private float minFloorPlaneArea = 0.5f;
        [Tooltip("Reject 'floor' planes more than this far (m) below the character — usually a spurious low plane.")]
        [SerializeField] private float maxFloorDropBelowQuery = 3.0f;

        [Header("Floor: manual fallback (only if no AR plane)")]
        [Tooltip("Fall back to a RouteRoot-local floor Y when no AR floor plane is found. Off = skip the floor fix until a plane exists (safer across scans).")]
        [SerializeField] private bool useRouteRootFloorFallback = false;
        [Tooltip("Floor height in RouteRoot-local Y (the wall frame). 0 = RouteRoot origin height. Only used by the fallback above.")]
        [SerializeField] private float floorLocalY = 0f;

        Transform routeRoot;

        // Cached AR-detected wall plane (world space), refreshed by TryCalibrateWallFromArPlane / live detection.
        bool hasArWall;
        Vector3 arWallPoint;
        Vector3 arWallNormal;

        public float WallSkinOffset => wallSkinOffset;
        public float FloorSkinOffset => floorSkinOffset;

        /// <summary>RouteRoot-local Z the physical wall is calibrated to (0 = the RouteRoot plane).</summary>
        public float WallLocalZOffset { get => wallLocalZOffset; set => wallLocalZOffset = value; }

        /// <summary>Active wall source (RouteRoot plane vs AR detected vertical plane).</summary>
        public WallSourceMode WallSource { get => wallSource; set => wallSource = value; }

        /// <summary>True once an AR vertical wall plane has been detected/cached.</summary>
        public bool HasArWall => hasArWall;

        public void SetRouteRoot(Transform t) => routeRoot = t;

        /// <summary>The wall frame currently driving the probe (set during playback). Null until localized.</summary>
        public Transform RouteRoot => routeRoot;

        /// <summary>
        /// The wall plane the system should use right now, as a world-space point (on the surface) + outward normal
        /// (pointing away from the wall, toward the climber). In ARVerticalPlane mode this is the detected AR plane
        /// (real position + tilt); otherwise it's the RouteRoot Z=offset plane. Returns false only when neither is
        /// available (no RouteRoot and no detected plane).
        /// </summary>
        public bool TryGetWallPlane(out Vector3 point, out Vector3 normal)
        {
            if (wallSource == WallSourceMode.ARVerticalPlane && hasArWall)
            {
                point = arWallPoint;
                normal = arWallNormal;
                return true;
            }

            if (routeRoot != null)
            {
                normal = routeRoot.forward;
                point = routeRoot.position + normal * wallLocalZOffset;
                return true;
            }

            point = Vector3.zero;
            normal = Vector3.forward;
            return false;
        }

        /// <summary>
        /// Detect ARKit's front-facing vertical wall plane (the one the white feature dots sit on, whose normal
        /// points back toward the phone) and cache it as the wall. Also updates <see cref="wallLocalZOffset"/> so the
        /// RouteRoot-plane fallback and debug gizmo agree. <paramref name="cameraTransform"/> is used to pick the
        /// plane that faces the phone and is in front of it; pass the AR camera. Returns true when a wall was found.
        /// </summary>
        public bool TryCalibrateWallFromArPlane(Transform cameraTransform)
        {
            if (planeManager == null)
                planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (planeManager == null || cameraTransform == null)
                return false;

            Vector3 camPos = cameraTransform.position;
            Vector3 camFwd = cameraTransform.forward;
            Vector3 routeNormal = routeRoot != null ? routeRoot.forward : camFwd;

            ARPlane best = null;
            float bestScore = float.NegativeInfinity;
            foreach (var plane in planeManager.trackables)
            {
                if (plane == null) continue;
                if (plane.alignment != PlaneAlignment.Vertical) continue;
                if (plane.trackingState == TrackingState.None) continue;

                Vector2 sz = plane.size;
                if (sz.x * sz.y < minWallPlaneArea) continue;

                Vector3 center = plane.center;
                // Plane must be IN FRONT of the phone.
                if (Vector3.Dot(center - camPos, camFwd) <= 0f) continue;

                // Outward normal = the plane normal flipped to face the phone.
                Vector3 n = plane.normal;
                if (Vector3.Dot(n, camPos - center) < 0f) n = -n;

                // Reject side walls: normal must roughly oppose the way the phone looks (i.e. face the camera).
                float facing = Vector3.Dot(n, -camFwd); // 1 = squarely facing the phone
                if (facing < Mathf.Cos(maxWallNormalDeviationDeg * Mathf.Deg2Rad)) continue;

                // Prefer big, close, squarely-facing planes.
                float dist = Vector3.Distance(center, camPos);
                float score = facing * 2f + sz.x * sz.y * 0.5f - dist * 0.3f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = plane;
                }
            }

            if (best == null)
                return false;

            arWallPoint = best.center;
            arWallNormal = Vector3.Dot(best.normal, camPos - best.center) < 0f ? -best.normal : best.normal;
            hasArWall = true;

            if (routeRoot != null)
                wallLocalZOffset = Vector3.Dot(arWallPoint - routeRoot.position, routeRoot.forward);

            Debug.Log($"[ARSurfaceProbe] Wall set from AR vertical plane: point={arWallPoint:F2} normal={arWallNormal:F2} " +
                      $"(RouteRoot-local Z={wallLocalZOffset:F3} m, area={best.size.x * best.size.y:F2} m^2).");
            return true;
        }

        /// <summary>True when an AR floor plane is available; outputs its world-space Y. For debug HUD/visualizers.</summary>
        public bool HasFloor(out float worldY) => TryFloorWorldY(out worldY, float.NaN);

        /// <summary>
        /// Calibrate the wall plane from a known world-space point that lies ON the physical wall (e.g. the centre of
        /// the "Wall 1" marker, which is mounted on the wall). Sets <see cref="wallLocalZOffset"/> to that point's
        /// RouteRoot-local Z so the wall plane coincides with the real surface. Returns the offset applied (0 if no
        /// RouteRoot yet). Keep the player's WallProjection wallDepthOffset matched to this value.
        /// </summary>
        public float CalibrateWallFromWorldPoint(Vector3 worldPointOnWall)
        {
            if (routeRoot == null) return 0f;
            Vector3 fwd = routeRoot.forward;
            wallLocalZOffset = Vector3.Dot(worldPointOnWall - routeRoot.position, fwd);
            Debug.Log($"[ARSurfaceProbe] Wall plane calibrated to RouteRoot-local Z = {wallLocalZOffset:F3} m.");
            return wallLocalZOffset;
        }

        void Awake()
        {
            if (planeManager == null)
                planeManager = FindAnyObjectByType<ARPlaneManager>();
        }

        /// <summary>Resolve the wall surface near a joint. Returns false only if no reference is available at all.</summary>
        public bool TryWall(Vector3 worldPos, out SurfaceHit hit)
        {
            hit = default;
            Vector3 outwardGuess = routeRoot != null ? routeRoot.forward : Vector3.forward;

            if (useArMesh && arMeshLayerMask.value != 0)
            {
                Vector3 start = worldPos + outwardGuess * maxProbeDistance;
                if (Physics.Raycast(start, -outwardGuess, out RaycastHit rh, maxProbeDistance * 2f, arMeshLayerMask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 normal = rh.normal.sqrMagnitude > 1e-6f ? rh.normal.normalized : outwardGuess;
                    Vector3 target = rh.point + normal * wallSkinOffset;
                    float pen = Vector3.Dot(target - worldPos, normal);
                    pen = Mathf.Clamp(pen, 0f, maxProbeDistance);
                    hit = new SurfaceHit
                    {
                        valid = true,
                        inside = pen > 0f,
                        surfacePoint = target,
                        normal = normal,
                        penetration = pen,
                    };
                    return true;
                }
            }

            // Plane fallback: the wall as a point + outward normal (AR vertical plane if selected/available, else
            // the RouteRoot Z=offset plane).
            if (!TryGetWallPlane(out Vector3 origin, out Vector3 fwd)) return false;
            if (fwd.sqrMagnitude < 1e-8f) return false;
            fwd.Normalize();

            float localZ = Vector3.Dot(worldPos - origin, fwd);
            float penetration = wallSkinOffset - localZ; // >0 when the joint is inside the skin surface
            penetration = Mathf.Clamp(penetration, 0f, maxProbeDistance);
            hit = new SurfaceHit
            {
                valid = true,
                inside = penetration > 0f,
                surfacePoint = worldPos + penetration * fwd, // projected onto the skin plane
                normal = fwd,
                penetration = penetration,
            };
            return true;
        }

        /// <summary>
        /// World-space Y of the floor. Prefers the AR-detected horizontal floor plane (works in any room/scan with
        /// no calibration); falls back to the RouteRoot-local Y only if <see cref="useRouteRootFloorFallback"/> is on.
        /// Returns false (so the caller skips the floor fix) when neither is available. <paramref name="queryY"/> is
        /// the character height used to reject spurious planes far below it.
        /// </summary>
        public bool TryFloorWorldY(out float worldY, float queryY = float.NaN)
        {
            worldY = 0f;

            if (useArPlaneFloor && TryArPlaneFloorY(queryY, out float planeY))
            {
                worldY = planeY + floorSkinOffset;
                return true;
            }

            if (useRouteRootFloorFallback && routeRoot != null)
            {
                Vector3 floorWorld = routeRoot.TransformPoint(new Vector3(0f, floorLocalY, 0f));
                worldY = floorWorld.y + floorSkinOffset;
                return true;
            }

            return false;
        }

        /// <summary>Lowest qualifying AR-detected horizontal (up-facing) plane = the floor.</summary>
        bool TryArPlaneFloorY(float queryY, out float floorY)
        {
            floorY = 0f;
            if (planeManager == null) return false;

            float best = float.MaxValue;
            bool found = false;
            foreach (var plane in planeManager.trackables)
            {
                if (plane == null) continue;
                if (plane.alignment != PlaneAlignment.HorizontalUp) continue;     // floor, not walls/ceilings
                if (plane.trackingState == TrackingState.None) continue;
                Vector2 sz = plane.size;
                if (sz.x * sz.y < minFloorPlaneArea) continue;                    // ignore small ledges/stools

                float y = plane.transform.position.y;
                if (!float.IsNaN(queryY) && y < queryY - maxFloorDropBelowQuery) continue; // implausibly low artifact

                if (y < best) { best = y; found = true; }                        // floor = lowest valid plane
            }

            if (found) { floorY = best; return true; }
            return false;
        }

        void OnDrawGizmos()
        {
            if (!drawWallGizmo) return;
            if (!TryGetWallPlane(out Vector3 center, out Vector3 wallNormal)) return;
            if (wallNormal.sqrMagnitude < 1e-8f) return;
            wallNormal.Normalize();

            // Wall plane grid quad, facing out of the wall.
            Vector3 up = Mathf.Abs(Vector3.Dot(wallNormal, Vector3.up)) < 0.95f ? Vector3.up : Vector3.forward;
            Quaternion rot = Quaternion.LookRotation(wallNormal, up);
            Gizmos.color = new Color(0.1f, 0.8f, 1f, 0.9f);
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(center, rot, Vector3.one);
            const float half = 1.5f;
            for (float x = -half; x <= half + 1e-3f; x += 0.5f)
                Gizmos.DrawLine(new Vector3(x, -half, 0f), new Vector3(x, half, 0f));
            for (float y = -half; y <= half + 1e-3f; y += 0.5f)
                Gizmos.DrawLine(new Vector3(-half, y, 0f), new Vector3(half, y, 0f));
            Gizmos.matrix = prev;
            // Outward normal.
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(center, wallNormal * 0.3f);

            if (TryFloorWorldY(out float floorY, center.y))
            {
                Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.8f);
                Vector3 f = new Vector3(center.x, floorY, center.z);
                Gizmos.DrawLine(f + Vector3.left * half, f + Vector3.right * half);
                Gizmos.DrawLine(f + Vector3.forward * half, f + Vector3.back * half);
            }
        }
    }
}
