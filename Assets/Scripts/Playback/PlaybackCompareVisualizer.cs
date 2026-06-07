using System.Collections.Generic;
using UnityEngine;
using BodyTracking.Data;
using BodyTracking.MoveAI;
using BodyTracking.Spatial;
using BodyTracking.Utils;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Overlaid compare view during fused replay: ARKit recording (cyan) and Move AI (orange) drawn at the
    /// same RouteRoot positions so alignment can be checked in-place before trusting the character.
    /// </summary>
    public class PlaybackCompareVisualizer : MonoBehaviour
    {
        [Header("Visualization")]
        [Tooltip("Flip the Move skeleton 180° in yaw so it faces the same way as the ARKit (cyan) skeleton.")]
        [SerializeField] private bool invertFacing = false;
        [SerializeField] private float jointRadius = 0.03f;
        [SerializeField] private float boneWidth = 0.022f;
        [SerializeField] private float moveJointRadiusScale = 0.85f;

        [Header("Colors")]
        [SerializeField] private Color arkitJointColor = new Color(0f, 0.85f, 1f, 1f);
        [SerializeField] private Color arkitBoneColor = new Color(0f, 0.55f, 1f, 0.75f);
        [SerializeField] private Color moveJointColor = new Color(1f, 0.45f, 0.05f, 1f);
        [SerializeField] private Color moveBoneColor = new Color(1f, 0.35f, 0f, 0.75f);

        HipRecording recording;
        MoveAIFusionAsset fusion;
        IRouteRootProvider routeRootProvider;
        CoordinateFrame referenceFrame;
        FusedCharacterPlayer fusedPlayer;
        bool active;
        float localPlaybackTime;
        FusedPoseSolver.AnchorState moveAnchorState; // last-good anchor/facing through ARKit dropouts

        SkeletonLayer arkitLayer;
        SkeletonLayer moveLayer;
        bool suppressed;

        public bool IsActive => active;

        /// <summary>
        /// Force-hide the compare overlay (orange/cyan skeletons) regardless of playback state. Used by the
        /// clean-view toggle so only the final character remains.
        /// </summary>
        public void SetSuppressed(bool value)
        {
            suppressed = value;
            if (suppressed)
            {
                arkitLayer?.HideAll();
                moveLayer?.HideAll();
            }
        }

        public void Begin(HipRecording hipRecording, MoveAIFusionAsset asset, IRouteRootProvider provider, FusedCharacterPlayer player)
        {
            recording = hipRecording;
            fusion = asset;
            routeRootProvider = provider;
            fusedPlayer = player;
            // Lock the orange skeleton's facing to the character's: both reconstruct from FusedPoseSolver, so a
            // mismatched invertFacing rotates one 180° in yaw and makes the character appear to face the wrong way.
            if (player != null)
                invertFacing = player.InvertFacing;
            active = recording != null && fusion != null && fusion.FrameCount > 0;

            if (!active) return;

            if (provider?.RouteRoot != null)
                referenceFrame = new CoordinateFrame(provider.RouteRoot);

            arkitLayer ??= new SkeletonLayer("ARKitCompare", arkitJointColor, arkitBoneColor, jointRadius, boneWidth);
            moveLayer ??= new SkeletonLayer(
                "MoveCompare",
                moveJointColor,
                moveBoneColor,
                jointRadius * moveJointRadiusScale,
                boneWidth * moveJointRadiusScale);

            localPlaybackTime = 0f;
            moveAnchorState = default;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            string playerGo = player != null ? player.gameObject.name : "null";
            bool playerActive = player != null && player.gameObject.activeInHierarchy;
            bool playerEnabled = player != null && player.isActiveAndEnabled;
            BodyTracking.DebugTools.DebugSessionLog.Log("E", "PlaybackCompareVisualizer.cs:Begin",
                "compare Begin GO state",
                "{\"active\":" + (active ? "true" : "false") +
                ",\"thisGO\":\"" + gameObject.name + "\"" +
                ",\"thisActiveInHierarchy\":" + (gameObject.activeInHierarchy ? "true" : "false") +
                ",\"thisIsActiveAndEnabled\":" + (isActiveAndEnabled ? "true" : "false") +
                ",\"playerGO\":\"" + playerGo + "\"" +
                ",\"playerActiveInHierarchy\":" + (playerActive ? "true" : "false") +
                ",\"playerIsActiveAndEnabled\":" + (playerEnabled ? "true" : "false") +
                ",\"isLocalized\":" + (provider != null && provider.IsLocalized ? "true" : "false") + "}");
            // #endregion
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        int dbgFrame;

        // #region agent log
        void OnEnable()
        {
            BodyTracking.DebugTools.DebugSessionLog.Log("E", "PlaybackCompareVisualizer.cs:OnEnable",
                "compare OnEnable", "{\"go\":\"" + gameObject.name + "\",\"activeInHierarchy\":" + (gameObject.activeInHierarchy ? "true" : "false") + "}");
        }
        // #endregion
#endif

        public void Stop()
        {
            active = false;
            localPlaybackTime = 0f;
            moveAnchorState = default;
            arkitLayer?.HideAll();
            moveLayer?.HideAll();
        }

        void Update()
        {
            if (suppressed)
            {
                arkitLayer?.HideAll();
                moveLayer?.HideAll();
                return;
            }

            if (!active || recording == null || fusion == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // #region agent log
                if ((dbgFrame++ % 60) == 0)
                    BodyTracking.DebugTools.DebugSessionLog.Log("B", "PlaybackCompareVisualizer.cs:Update",
                        "compare Update inactive-guard",
                        "{\"active\":" + (active ? "true" : "false") +
                        ",\"recordingNull\":" + (recording == null ? "true" : "false") +
                        ",\"fusionNull\":" + (fusion == null ? "true" : "false") + "}");
                // #endregion
#endif
                return;
            }

            bool localized = routeRootProvider == null || routeRootProvider.IsLocalized;
            if (!localized)
            {
                arkitLayer?.HideAll();
                moveLayer?.HideAll();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // #region agent log
                if ((dbgFrame++ % 60) == 0)
                    BodyTracking.DebugTools.DebugSessionLog.Log("A", "PlaybackCompareVisualizer.cs:Update",
                        "compare Update NOT localized -> hidden", "{}");
                // #endregion
#endif
                return;
            }

            if (routeRootProvider?.RouteRoot != null)
                referenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);

            float t;
            if (fusedPlayer != null && fusedPlayer.IsPlaying)
                t = fusedPlayer.CurrentTime;
            else
            {
                localPlaybackTime += Time.deltaTime;
                float dur = fusion.Duration;
                if (dur > 0f && localPlaybackTime >= dur)
                    localPlaybackTime %= dur;
                t = localPlaybackTime;
            }

            DrawArkitSkeleton(t);
            DrawMoveSkeleton(t);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            if ((dbgFrame++ % 60) == 0)
            {
                var camTr = Camera.main != null ? Camera.main.transform : null;
                Vector3 rr = referenceFrame.position;
                var moveFrame = fusion.pose.FrameAtTime(t);
                Vector3[] fk = moveFrame != null ? fusion.pose.ForwardKinematics(moveFrame) : null;
                Vector3 j0World = (fk != null && fk.Length > 0) ? referenceFrame.TransformPoint(fk[0]) : Vector3.zero;
                float camDist = camTr != null ? Vector3.Distance(camTr.position, j0World) : -1f;
                // Spread = farthest joint from root (in local FK space). ~0 means collapsed; >0 means real skeleton.
                float spread = 0f;
                if (fk != null && fk.Length > 0)
                    for (int i = 1; i < fk.Length; i++)
                        spread = Mathf.Max(spread, Vector3.Distance(fk[i], fk[0]));
                BodyTracking.DebugTools.DebugSessionLog.Log("C", "PlaybackCompareVisualizer.cs:Update",
                    "compare drawing",
                    "{\"t\":" + t.ToString("F2") +
                    ",\"fkLen\":" + (fk != null ? fk.Length : -1) +
                    ",\"spread\":" + spread.ToString("F3") +
                    ",\"j0World\":[" + j0World.x.ToString("F2") + "," + j0World.y.ToString("F2") + "," + j0World.z.ToString("F2") + "]" +
                    ",\"camDistToJ0\":" + camDist.ToString("F2") + "}");
            }
            // #endregion
#endif
        }

        void DrawArkitSkeleton(float t)
        {
            var frame = recording.GetFrameAtTime(t);

            if (frame.hipJoint.IsValid)
            {
                arkitLayer.Ensure(1);
                Vector3 hipWorld = referenceFrame.TransformPoint(frame.hipJoint.position);
                arkitLayer.SetJoint(0, hipWorld, true);
            }

            if (!frame.HasSkeleton)
            {
                arkitLayer.HideBones();
                return;
            }

            if (frame.HasRecordedSkeleton)
            {
                int maxIndex = 0;
                foreach (var j in frame.recordedJoints)
                    maxIndex = Mathf.Max(maxIndex, j.jointIndex);
                arkitLayer.Ensure(maxIndex + 1);
                arkitLayer.HideAll();

                foreach (var j in frame.recordedJoints)
                {
                    if (!j.isTracked || j.jointIndex < 0) continue;
                    Vector3 w = referenceFrame.TransformPoint(j.positionReference);
                    arkitLayer.SetJoint(j.jointIndex, w, true);
                    if (j.parentIndex >= 0 &&
                        TryGetSample(frame, j.parentIndex, out var parent))
                    {
                        Vector3 pw = referenceFrame.TransformPoint(parent.positionReference);
                        arkitLayer.SetBone(j.jointIndex, pw, w, true);
                    }
                }
            }
        }

        void DrawMoveSkeleton(float t)
        {
            // Shared solver: same ARKit-anchored, facing-aligned positions the character uses, and it holds the
            // last-good anchor/facing through ARKit dropouts so the overlay no longer spins when the body is lost.
            var anchorSettings = fusedPlayer != null ? fusedPlayer.AnchorSettings : FusedPoseSolver.AnchorSettings.Default;
            Vector3[] local = FusedPoseSolver.ComputeLocalJoints(fusion, recording, t, ref moveAnchorState, invertFacing, anchorSettings);
            if (local == null) return;

            int n = local.Length;
            moveLayer.Ensure(n);
            moveLayer.HideAll();

            for (int i = 0; i < n; i++)
            {
                Vector3 w = referenceFrame.TransformPoint(local[i]);
                moveLayer.SetJoint(i, w, true);

                int parent = i < fusion.pose.jointParents.Count ? fusion.pose.jointParents[i] : -1;
                if (parent >= 0 && parent < n)
                {
                    Vector3 pw = referenceFrame.TransformPoint(local[parent]);
                    moveLayer.SetBone(i, pw, w, true);
                }
            }
        }

        static bool TryGetSample(HipFrame frame, int jointIndex, out RecordedJointSample sample)
        {
            sample = null;
            if (frame.recordedJoints == null) return false;
            foreach (var j in frame.recordedJoints)
            {
                if (j.jointIndex == jointIndex && j.isTracked)
                {
                    sample = j;
                    return true;
                }
            }
            return false;
        }

        sealed class SkeletonLayer
        {
            readonly string label;
            readonly Color jointColor;
            readonly Color boneColor;
            readonly float jointRadius;
            readonly float boneWidth;
            GameObject root;
            GameObject[] spheres;
            LineRenderer[] bones;

            public SkeletonLayer(string label, Color jointColor, Color boneColor, float jointRadius, float boneWidth)
            {
                this.label = label;
                this.jointColor = jointColor;
                this.boneColor = boneColor;
                this.jointRadius = jointRadius;
                this.boneWidth = boneWidth;
            }

            public void Ensure(int count)
            {
                if (root == null)
                    root = new GameObject(label);

                if (spheres != null && spheres.Length >= count) return;

                HideAll();
                if (spheres != null)
                {
                    foreach (var s in spheres) if (s != null) Destroy(s);
                    foreach (var b in bones) if (b != null) Destroy(b.gameObject);
                }

                spheres = new GameObject[count];
                bones = new LineRenderer[count];
                var jointMat = DebugVisualizationMaterials.CreateSolidColorMaterial(jointColor);
                var boneMat = DebugVisualizationMaterials.CreateLineMaterial(boneColor);

                for (int i = 0; i < count; i++)
                {
                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name = $"{label}_J{i}";
                    sphere.transform.SetParent(root.transform);
                    sphere.transform.localScale = Vector3.one * jointRadius;
                    if (sphere.TryGetComponent<Collider>(out var col)) Destroy(col);
                    var r = sphere.GetComponent<Renderer>();
                    if (r != null) r.material = jointMat;
                    spheres[i] = sphere;

                    var boneGo = new GameObject($"{label}_B{i}");
                    boneGo.transform.SetParent(root.transform);
                    var line = boneGo.AddComponent<LineRenderer>();
                    line.material = boneMat;
                    line.startWidth = boneWidth;
                    line.endWidth = boneWidth;
                    line.positionCount = 2;
                    line.useWorldSpace = true;
                    line.startColor = boneColor;
                    line.endColor = boneColor;
                    bones[i] = line;
                }
            }

            public void SetJoint(int index, Vector3 world, bool visible)
            {
                if (spheres == null || index < 0 || index >= spheres.Length) return;
                spheres[index].transform.position = world;
                spheres[index].SetActive(visible);
            }

            public void SetBone(int index, Vector3 from, Vector3 to, bool visible)
            {
                if (bones == null || index < 0 || index >= bones.Length) return;
                bones[index].SetPosition(0, from);
                bones[index].SetPosition(1, to);
                bones[index].gameObject.SetActive(visible);
            }

            public void HideBones()
            {
                if (bones == null) return;
                foreach (var b in bones)
                    if (b != null) b.gameObject.SetActive(false);
            }

            public void HideAll()
            {
                if (spheres != null)
                    foreach (var s in spheres) if (s != null) s.SetActive(false);
                HideBones();
            }
        }
    }
}
