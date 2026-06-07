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

        [Header("Skin offsets (m kept outside the surface)")]
        [SerializeField] private float wallSkinOffset = 0.02f;
        [SerializeField] private float floorSkinOffset = 0.0f;

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

        public float WallSkinOffset => wallSkinOffset;
        public float FloorSkinOffset => floorSkinOffset;

        public void SetRouteRoot(Transform t) => routeRoot = t;

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

            if (routeRoot == null) return false;

            // RouteRoot plane: wall surface at local Z = 0, normal = +Z (out of wall).
            Vector3 origin = routeRoot.position;
            Vector3 fwd = routeRoot.forward;
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
    }
}
