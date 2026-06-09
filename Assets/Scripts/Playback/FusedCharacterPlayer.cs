using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using BodyTracking.Animation;
using BodyTracking.Data;
using BodyTracking.LookDev;
using BodyTracking.Spatial;
using BodyTracking.MoveAI;
using BodyTracking.AR;
using BodyTracking.Playback.PostProcess;
using BodyTracking.Diagnostics;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Replays a baked <see cref="MoveAIFusionAsset"/>: drives a rigged character's bone local rotations from
    /// the Move AI pose, places the character root each frame from the baked RouteRoot-local trajectory (mapped
    /// through the live RouteRoot so it inherits Immersal/world-map drift correction), and applies one uniform
    /// height scale from the recording so the FBX matches the orange skeleton size. Hidden until localized.
    /// </summary>
    public class FusedCharacterPlayer : MonoBehaviour
    {
        [Header("Character")]
        [Tooltip("Root of the rigged character to drive. Bones are resolved by name under this transform.")]
        [SerializeField] private Transform characterRoot;
        [SerializeField] private FBXCharacterController fbxCharacterController;
        [SerializeField] private bool autoFindCharacter = true;
        [SerializeField] private MoveJointMap jointMap = MoveJointMap.CreateDefaultMixamo();

        [Header("Playback")]
        [Tooltip("Flip the Move pose 180° in yaw (affects pose anchoring). Keep matched to the compare overlay.")]
        [SerializeField] private bool invertFacing = false;
        [Tooltip("Spin the character root 180° about its up axis. Use when the model's authored forward is -Z so " +
                 "its torso faces the same way as the recorded climber (limbs are aimed separately and stay correct).")]
        [SerializeField] private bool flipCharacterForward = true;
        [SerializeField] private bool loop = true;
        [SerializeField] private float playbackSpeed = 1f;
        [Tooltip("Lateral offset in RouteRoot-local space (used for side-by-side compare with ARKit).")]
        [SerializeField] private Vector3 routeRootLocalOffset = Vector3.zero;

        /// <summary>How the single uniform character scale is chosen.</summary>
        public enum HeightFitMode
        {
            // Fit the character's RENDERED height — the crown of the mesh (hair included) down to the soles — to
            // the recorded climber height. The orange/cyan skeletons only reach the Head JOINT (well below the
            // crown) and the toe JOINT (above the sole), so this stops the visible mesh overshooting top/bottom.
            RenderedMeshHeight,
            // Fit the foot->head BONE-CHAIN (joint to joint) to the skeleton. Lands the character's joints on the
            // orange joints, but lets the mesh crown/soles stick out past the skeleton (character looks taller).
            BoneChain,
            // Scale to the orange skeleton's head-to-foot span and pin feet each frame (not hips) so fit-scale
            // tweaks height without making the feet float.
            SkeletonJoints,
        }

        /// <summary>Where the per-frame body + finger articulation comes from.</summary>
        public enum BodyArticulationSource
        {
            // Legacy path: aim each bone from the Move joint POSITIONS (no fingers; solved in FusedPoseSolver).
            Procedural,
            // Retarget a Move AI GLB onto the character in muscle space (body + fingers). Positioning/scale still
            // come from the fused trajectory below; only the pose changes. Falls back to Procedural if no GLB.
            MoveGlb,
        }

        [Header("Articulation")]
        [Tooltip("Procedural = position-based bone aiming (no fingers). MoveGlb = retarget the Move AI GLB in " +
                 "muscle space (body + fingers); positioning/scale still come from the fused trajectory. Falls " +
                 "back to Procedural automatically when no GLB is supplied for the recording.")]
        [SerializeField] private BodyArticulationSource articulationSource = BodyArticulationSource.MoveGlb;

        [Header("Anchoring (ARKit drift vs Move AI stability)")]
        [Tooltip("FollowArkit = pin the pelvis to ARKit every frame (original; inherits ARKit drift). " +
                 "MoveAIDriftCorrected = ride Move AI's steadier root motion and only re-sync to ARKit occasionally, " +
                 "so the legs stop drifting while the climber is still.")]
        [SerializeField] private FusedPoseSolver.AnchorSettings anchorSettings = new FusedPoseSolver.AnchorSettings
        {
            mode = FusedPoseSolver.AnchorMode.FollowBakedRoot,
            stillnessVelocity = 0.06f,
            fullMotionVelocity = 0.2f,
            followSeconds = 0.1f,
            correctXZOnly = false,
            moveDrivenFacing = true,
            facingCorrectionSeconds = 1.141143f,
            moveAutoRealign = false,
            moveRealignDriftThreshold = 0.2f,
            moveRealignEaseSeconds = 0.4f,
        };

        [Tooltip("World position source during PLAYBACK. FollowMoveGlbRoot = Move GLB root motion (aligned once to ARKit; " +
                 "use Re-align button or enable auto re-align in tuning). FollowBakedRoot = baked Move+ARKit fused trajectory. FollowArkit = " +
                 "recorded ARKit pelvis every frame (original).")]
        [SerializeField] private FusedPoseSolver.AnchorMode playbackAnchorMode = FusedPoseSolver.AnchorMode.FollowMoveGlbRoot;

        /// <summary>Live-tunable anchoring settings (also handed to the compare overlay so it matches).</summary>
        public FusedPoseSolver.AnchorSettings AnchorSettings => anchorSettings;

        /// <summary>Playback world-position mode; live-settable from the Tuning UI for A/B.</summary>
        public FusedPoseSolver.AnchorMode PlaybackAnchorMode
        {
            get => playbackAnchorMode;
            set
            {
                if (playbackAnchorMode == value) return;
                playbackAnchorMode = value;
                // Rebake the one-shot GLB yaw offset when switching Baked vs Live mid-playback.
                anchorState.hasFacing = false;
                // Re-capture the move-driven anchor so the test mode re-anchors cleanly each time it's enabled.
                anchorState.hasMoveDrivenAnchor = false;
                anchorState.requestRealign = false;
            }
        }

        /// <summary>Bumped each time <see cref="RealignMoveMovement"/> is called, so the compare overlay (which
        /// keeps its own anchor state) can mirror the re-align and stay aligned with the character.</summary>
        public int MoveRealignEpoch { get; private set; }

        /// <summary>Trigger hook (UI button / future automatic triggers): re-anchor the Move-driven test mode at
        /// the current frame. The solver captures a fresh ARKit anchor + yaw and continues from there exactly.</summary>
        public void RealignMoveMovement()
        {
            anchorState.requestRealign = true;
            MoveRealignEpoch++;
        }

        /// <summary>
        /// Anchor settings used during fused replay. The mode is taken from <see cref="playbackAnchorMode"/>
        /// (FollowArkit by default; FollowBakedRoot rides the baked fused trajectory). MoveAIDriftCorrected is a
        /// live-AR mode and is coerced to FollowArkit here so playback never lerps/ lags the GLB.
        /// </summary>
        public FusedPoseSolver.AnchorSettings EffectivePlaybackAnchorSettings()
        {
            var s = anchorSettings;
            s.mode = (playbackAnchorMode == FusedPoseSolver.AnchorMode.FollowBakedRoot
                      || playbackAnchorMode == FusedPoseSolver.AnchorMode.FollowMoveGlbRoot)
                ? playbackAnchorMode
                : FusedPoseSolver.AnchorMode.FollowArkit;
            // Guard against zero values deserialized on objects saved before these fields existed (would cause a
            // divide-by-zero ease or a re-align every frame). Fall back to the sane defaults.
            if (s.moveRealignDriftThreshold <= 0f) s.moveRealignDriftThreshold = 0.2f;
            if (s.moveRealignEaseSeconds <= 0f) s.moveRealignEaseSeconds = 0.4f;
            return s;
        }

        /// <summary>Live Move GLB clip when GLB articulation is active — used to drive compare overlay pose.</summary>
        public MoveGlbSource ActiveGlbSource => glbActive && glbSource != null && glbSource.IsReady ? glbSource : null;

        [Header("Post-processing")]
        [Tooltip("Master switch for jitter smoothing on the GLB muscles + joint overlay. Glitch guard can stay on when this is off.")]
        [SerializeField] private bool enablePoseSmoothing = true;
        [SerializeField] private PosePostProcessSettings postProcessSettings = new PosePostProcessSettings
        {
            enableGlitchGuard = true,
            maxJointSpeed = 6f,
            boneLengthTolerance = 0.25f,
            enableSmoothing = true,
            minCutoff = 0.35f,
            beta = 0.1143093f,
            smoothRootTranslation = false,
            jumpVelocityThreshold = 1.5f,
            jumpBetaScale = 8f,
        };

        [Header("Root-motion guard (final placement safety net)")]
        [Tooltip("Clamp how fast the placed character ROOT can travel per frame so neither the procedural nor the GLB " +
                 "path can ever lurch/teleport across the room from an anchor spike or re-align. The pose (muscle) " +
                 "glitch guard above only cleans joints RELATIVE to the pelvis; this is the only thing that guards the " +
                 "body's WORLD position. Works in RouteRoot-local space, so moving the phone is never clamped.")]
        [SerializeField] private bool enableRootMotionGuard = true;
        [Tooltip("Top speed (m/s) the character root may move at 1x playback. Travel faster than this is eased to this " +
                 "speed instead of snapping. Set just above a fast climb/jump (~4-8 m/s) so real motion passes but " +
                 "single-frame teleports are absorbed.")]
        [SerializeField] private float maxRootSpeed = 6f;
        [Tooltip("A single-frame root jump larger than this (m) is treated as a genuine relocation (loop restart / " +
                 "re-anchor to a far pelvis) and allowed through 1:1 so the body recovers instead of crawling. Keep " +
                 "above the largest legitimate per-frame move but below a full across-the-wall jump. 0 = always clamp.")]
        [SerializeField] private float rootTeleportSnapDistance = 1.25f;
        [Tooltip("Top turn rate (deg/s) the character's FACING may change at. Smooths out sudden body spins from a " +
                 "noisy/flipped facing without lagging a normal climb turn. 0 = no rotation clamp.")]
        [SerializeField] private float maxRootTurnSpeed = 540f;
        [Tooltip("A single-frame facing change larger than this (deg) is treated as a genuine re-facing (loop wrap) " +
                 "and allowed through 1:1 so it recovers instead of slowly spinning. 0 = always clamp.")]
        [SerializeField] private float rootTurnSnapDegrees = 135f;
        [Tooltip("Master switch for wall/floor penetration correction + closest-hand IK (Step 4). On by default with " +
                 "feet-on-floor only (wall IK stays off) to complement Move GLB root motion.")]
        [SerializeField] private bool enablePenetrationFix = true;
        [SerializeField] private PenetrationSettings penetrationSettings = new PenetrationSettings
        {
            enableFloorFix = true,
            floorContactBand = 0.12f,
            maxStandingHipHeightAboveFloor = 1.3f,
            maxFloorSnapMeters = 0.5f,
            enableWallHandIK = false,
            enableWallFootIK = false,
            maxIkWeight = 1f,
            penetrationForFullWeight = 0.08f,
            enableWholeBodyPush = false,
            minWholeBodyPenetration = 0.04f,
            wholeBodyPenetrationFraction = 0.9188983f,
            maxWholeBodyPushMeters = 0.12f,
            skipDuringJump = false,
            debugDraw = false,
        };
        [Tooltip("AR surface probe used by the penetration fix. Auto-found if left empty.")]
        [SerializeField] private ARSurfaceProbe surfaceProbe;

        [Header("Wall constraint (flat-wall climbing)")]
        [Tooltip("Keep the climber pinned to the flat wall: project joints into a thin depth slab (no clipping into / " +
                 "floating off the wall) and lock hands/feet onto holds while they grip. Exploits the strong priors " +
                 "of climbing a flat vertical wall, where ARKit's estimated depth is the least reliable axis.")]
        [SerializeField] private bool enableWallProjection = true;
        [Tooltip("On playback start, estimate the wall plane from the recording (the climb's closest hand/foot " +
                 "contacts) and set the wall depth offset automatically. Fixes the case where the RouteRoot origin " +
                 "(and so the debug wall plane) sits off the physical wall. Turn off to keep a manually-set offset.")]
        [SerializeField] private bool autoCalibrateWallOnPlay = true;
        [SerializeField] private WallProjectionSettings wallProjectionSettings = WallProjectionSettings.Default;

        // Runtime post-processing helpers (Step 2-4).
        private readonly PosePostProcessor postProcessor = new PosePostProcessor();
        private readonly PosePenetrationResolver penetrationResolver = new PosePenetrationResolver();
        private readonly WallProjectionResolver wallProjection = new WallProjectionResolver();
        private WallProjectionResolver.ContactJoint[] contactJointTable = System.Array.Empty<WallProjectionResolver.ContactJoint>();
        private WallProjectionResolver.WallContact[] lastWallContacts;
        // Wall plane used this frame (slab + contact push-out), cached so the post-pose rig IK can reuse it on both
        // the procedural and GLB paths.
        private Vector3 lastWallPoint;
        private Vector3 lastWallNormal = Vector3.forward;
        private bool lastWallValid;
        private int[] footWorldIndices = System.Array.Empty<int>();
        private int[] wallContactWorldIndices = System.Array.Empty<int>();

        // Inferred climbing holds for the current spatial map. Generated once by scanning the complete fused
        // recording (or loaded from disk), then consumed during playback for overlay/snap. Playback itself does not
        // add observations, so pausing/scrubbing cannot create duplicate holds.
        private readonly ClimbingHoldMap holdMap = new ClimbingHoldMap();
        private string loadedHoldsMapId;
        private bool holdsDirty;

        [Header("Fit")]
        [Tooltip("How the one uniform character scale is chosen. RenderedMeshHeight (default) matches the visible " +
                 "mesh — hair/crown down to the soles — to the recorded climber height, so the character stops " +
                 "looking too tall. BoneChain matches joint-to-joint and lets the mesh overshoot the skeleton.")]
        [SerializeField] private HeightFitMode heightFitMode = HeightFitMode.SkeletonJoints;
        [Tooltip("Optional fine-tune multiplier on the auto-computed height scale. Leave at 1 for an exact fit; " +
                 "nudge down/up if the mesh still reads slightly large/small. Live-adjustable in play mode.")]
        [SerializeField, Range(0.5f, 1.5f)] private float skeletonFitScale = 0.88f;
        private float lastAppliedFitScale = float.NaN;

        [Header("Dev")]
        [Tooltip("Every 15s, log all live tuning values in Unity scene YAML form (Xcode console) for copy/paste.")]
        [SerializeField] private bool logTuningSettingsEvery15s = true;
        Coroutine tuningSnapshotLogRoutine;

        private IRouteRootProvider routeRootProvider;
        private CoordinateFrame referenceFrame;

        private MoveAIFusionAsset asset;
        private HipRecording recording; // source ARKit recording, for pelvis anchor + facing
        private bool isPlaying;
        private bool isPaused;
        private float playbackTime;
        private bool waitingForLocalization;
        private bool segmentLoopEnabled;
        private float segmentLoopStart;
        private float segmentLoopEnd;
        private bool readyToApply; // set in Update, consumed in LateUpdate so we win over the rig Animator
        private bool loggedPlacement; // one-shot placement diagnostic per playback
        private bool pendingPosedHeightRefit; // re-measure head-foot span after first GLB pose is applied
        private Animator characterAnimator; // disabled while we drive bones procedurally
        private FusedPoseSolver.AnchorState anchorState; // last-good ARKit anchor/facing through dropouts

        // --- Move GLB articulation (muscle-space retarget) ---
        private MoveGlbSource glbSource;            // supplies per-frame body+finger HumanPose
        private HumanPoseHandler targetPoseHandler; // writes that pose onto the character
        private HumanPose glbPose = new HumanPose();
        private bool glbActive;                     // MoveGlb mode + source ready + target handler built
        private Transform poseRoot;                  // transform SetHumanPose drives (the avatar/animator root)

        // Resolved bone transforms keyed by Move joint index.
        private readonly Dictionary<int, Transform> boneByJoint = new Dictionary<int, Transform>();
        // Bind-pose local rotation per bound bone, captured at resolve. The retarget resets to this each frame so
        // the per-frame swing doesn't accumulate roll drift (which looked like progressive twisting).
        private readonly Dictionary<int, Quaternion> bindLocalRotation = new Dictionary<int, Quaternion>();
        // Bind-space aim/up basis for each driven bone. The up axis gives elbows/knees/feet a stable bend plane,
        // avoiding the 180-degree limb flips that happen when only the parent-child direction is constrained.
        private readonly Dictionary<int, Vector3> bindAimLocal = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Vector3> bindUpLocal = new Dictionary<int, Vector3>();
        // Primary child joint per Move joint (defines that bone's aim direction), -1 if none mapped.
        private readonly Dictionary<int, int> primaryChild = new Dictionary<int, int>();
        private int rootJointIndex, spineJointIndex = -1, lHipJointIndex = -1, rHipJointIndex = -1;
        private float recordingHeightMeters;
        // The rig's anatomical (forward,up) basis expressed in characterRoot-local space at bind. Used to align
        // the character's facing to the Move body each frame WITHOUT a manual flip, so it can't end up reversed
        // regardless of how the model was authored (its visual front may be +Z or -Z).
        private Quaternion rigBindLocalAnatomical = Quaternion.identity;
        private bool hasRigBindAnatomical;
        // Cached body basis when hip/spine vectors degenerate — stops forward flipping to referenceFrame.up.
        private BodyBasis lastGoodBodyBasis;
        private bool hasLastGoodBodyBasis;
        // Facing: when move-driven, the root yaw follows the steady Move body forward (set per frame from settings).
        private bool useMoveDrivenFacing;
        private Vector3 lastFacingForward;
        private bool hasLastFacingForward;
        // Root pose at bind so StopPlayback can restore a stable idle pose instead of leaving a spun root.
        private Quaternion bindCharacterRootLocalRotation = Quaternion.identity;
        private Vector3 bindCharacterRootLocalPosition;
        private bool hasBindRootPose;

        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;
        /// <summary>Where per-frame articulation comes from. Procedural = legacy FBX position solver (no GLB);
        /// MoveGlb = muscle-space retarget of the Move AI GLB (body + fingers).</summary>
        public BodyArticulationSource ArticulationMode => articulationSource;
        /// <summary>The 180° yaw flip applied to the Move pose. The compare overlay reads this so the orange
        /// skeleton and the driven character always share one facing (they both feed off FusedPoseSolver).</summary>
        public bool InvertFacing => invertFacing;
        public bool IsWaitingForLocalization => waitingForLocalization;
        public float Duration => asset?.Duration ?? 0f;
        public float CurrentTime => playbackTime;
        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set => playbackSpeed = Mathf.Max(0.01f, value);
        }
        public bool LoopPlayback
        {
            get => loop;
            set => loop = value;
        }
        public float FrameRate => recording != null && recording.frameRate > 0f
            ? recording.frameRate
            : (asset != null && asset.Duration > 0f ? 30f : 30f);

        // --- Live-tunable settings (read every frame in ApplyFrame, so setters take effect immediately) ---
        /// <summary>Anchor + facing tunables. World position mode is selected via <see cref="PlaybackAnchorMode"/>
        /// (Baked vs Live); stillness/motion knobs apply to live AR drift correction only.</summary>
        public FusedPoseSolver.AnchorSettings AnchorSettingsLive
        {
            get => anchorSettings;
            set => anchorSettings = value;
        }
        public bool EnablePoseSmoothing
        {
            get => enablePoseSmoothing;
            set
            {
                enablePoseSmoothing = value;
                if (value)
                {
                    var s = postProcessSettings;
                    s.enableSmoothing = true;
                    postProcessSettings = s;
                }
                RefreshGlbPostProcess();
            }
        }
        public PosePostProcessSettings PostProcessSettings
        {
            get => postProcessSettings;
            set
            {
                postProcessSettings = value;
                RefreshGlbPostProcess();
            }
        }
        /// <summary>Final world-placement safety net: clamp the character root's per-frame travel so it can never
        /// teleport/lurch (the muscle glitch guard only cleans the pose, not the body's world position).</summary>
        public bool EnableRootMotionGuard
        {
            get => enableRootMotionGuard;
            set => enableRootMotionGuard = value;
        }
        /// <summary>Top speed (m/s, at 1x playback) the placed character root may move; faster travel is eased.</summary>
        public float MaxRootSpeed
        {
            get => maxRootSpeed;
            set => maxRootSpeed = Mathf.Max(0f, value);
        }
        /// <summary>Single-frame root jump (m) above which motion is treated as a genuine relocation and passed 1:1.</summary>
        public float RootTeleportSnapDistance
        {
            get => rootTeleportSnapDistance;
            set => rootTeleportSnapDistance = Mathf.Max(0f, value);
        }
        /// <summary>Top turn rate (deg/s) the character facing may change at (anti body-spin). 0 = no clamp.</summary>
        public float MaxRootTurnSpeed
        {
            get => maxRootTurnSpeed;
            set => maxRootTurnSpeed = Mathf.Max(0f, value);
        }
        /// <summary>Single-frame facing change (deg) above which a turn is treated as a genuine re-facing and passed 1:1.</summary>
        public float RootTurnSnapDegrees
        {
            get => rootTurnSnapDegrees;
            set => rootTurnSnapDegrees = Mathf.Max(0f, value);
        }
        public bool EnablePenetrationFix
        {
            get => enablePenetrationFix;
            set => enablePenetrationFix = value;
        }
        public PenetrationSettings PenetrationSettingsLive
        {
            get => penetrationSettings;
            set => penetrationSettings = value;
        }
        /// <summary>Master switch for the flat-wall depth slab + hold-contact lock (read every frame).</summary>
        public bool EnableWallProjection
        {
            get => enableWallProjection;
            set => enableWallProjection = value;
        }
        /// <summary>Flat-wall depth slab + contact-lock tunables (read every frame).</summary>
        public WallProjectionSettings WallProjectionSettingsLive
        {
            get => wallProjectionSettings;
            set => wallProjectionSettings = value;
        }
        /// <summary>Auto-estimate the wall depth from the recording when playback starts.</summary>
        public bool AutoCalibrateWallOnPlay
        {
            get => autoCalibrateWallOnPlay;
            set => autoCalibrateWallOnPlay = value;
        }
        /// <summary>Per-limb wall-contact state from the last applied frame (null until a frame is applied). For
        /// debug visualizers that draw which hands/feet are currently locked to a hold.</summary>
        public WallProjectionResolver.WallContact[] LastWallContacts => lastWallContacts;
        /// <summary>Per-limb push-out status from the last applied frame (anchored / pushed out / still inside). For
        /// the debug overlay to color limbs green/yellow/red. Index = WallProjectionResolver.ClimbLimb.</summary>
        public System.Collections.Generic.IReadOnlyList<PostProcess.PosePenetrationResolver.LimbPush> ContactPushStatus
            => penetrationResolver.LastLimbPush;
        /// <summary>AR surface probe (wall plane + floor) the player is driving. For debug visualizers.</summary>
        public ARSurfaceProbe SurfaceProbe => surfaceProbe;

        /// <summary>Inferred climbing holds for the current map (drives the hold overlay and optional snapping).</summary>
        public ClimbingHoldMap HoldMap => holdMap;

        /// <summary>Spatial map id (Immersal map / image target) the holds are keyed to.</summary>
        public string HoldsMapId
        {
            get
            {
                string providerMap = routeRootProvider != null ? routeRootProvider.MapId : "";
                if (!string.IsNullOrEmpty(providerMap)) return providerMap;
                return asset != null ? asset.mapId : "";
            }
        }

        /// <summary>
        /// Load the persisted holds for the active map into <see cref="holdMap"/>. If no JSON exists yet, build the
        /// hold list by scanning the full recording immediately and save it, so playback starts from a complete map.
        /// </summary>
        private void EnsureHoldsLoaded()
        {
            string mapId = HoldsMapId;
            if (mapId == loadedHoldsMapId) return;
            loadedHoldsMapId = mapId;
            holdsDirty = false;
            bool loaded = BodyTracking.Storage.ClimbingHoldStorage.LoadInto(mapId, holdMap);
            if (!loaded)
                RegenerateHoldsFromRecording();
        }

        /// <summary>Persist the current holds for the active map (no-op when nothing changed).</summary>
        public void SaveHolds()
        {
            if (!holdsDirty) return;
            if (BodyTracking.Storage.ClimbingHoldStorage.Save(loadedHoldsMapId ?? HoldsMapId, holdMap))
                holdsDirty = false;
        }

        /// <summary>Forget all inferred holds for the active map (clears memory + the persisted file).</summary>
        public void ClearHolds()
        {
            holdMap.Clear();
            string mapId = loadedHoldsMapId ?? HoldsMapId;
            // Save an empty map rather than deleting the file; otherwise the next load would auto-generate again.
            BodyTracking.Storage.ClimbingHoldStorage.Save(mapId, holdMap);
            holdsDirty = false;
        }

        /// <summary>Force a fresh full-recording hold detection pass and save the resulting map.</summary>
        public bool RegenerateHoldsFromRecording()
        {
            if (asset?.pose == null)
                return false;
            if (contactJointTable == null || contactJointTable.Length == 0)
                ConfigurePostProcessing(asset.pose);

            if (!wallProjectionSettings.enableHoldDetection)
            {
                holdMap.Clear();
                holdsDirty = true;
                SaveHolds();
                return true;
            }

            bool ok = ClimbingHoldDetector.GenerateFromRecording(asset, recording, contactJointTable,
                wallProjectionSettings, EffectivePlaybackAnchorSettings(), invertFacing, routeRootLocalOffset, out var generated);
            if (!ok)
                return false;

            holdMap.LoadFromJson(generated.ToJson());
            holdsDirty = true;
            SaveHolds();
            Debug.Log($"[FusedCharacterPlayer] Generated {holdMap.Count} inferred climbing holds for map '{HoldsMapId}'.");
            return true;
        }

        /// <summary>
        /// Calibrate the wall plane from the loaded recording (the climbing prior: the wall is where hands/feet get
        /// closest). Sets both the wall-projection depth offset AND the penetration probe's wall plane so the slab,
        /// contact lock, IK and debug plane all agree with the real wall — even when the RouteRoot origin does not
        /// sit on the wall. Safe to call repeatedly. Returns the estimated wall depth (RouteRoot-local Z), or NaN if
        /// there isn't enough data. Auto-run at <see cref="StartPlayback"/>.
        /// </summary>
        /// <summary>
        /// Resolve the wall plane to use this frame (world point on the surface + outward normal). Prefers the
        /// AR surface probe (which can return a real AR-detected vertical plane, tilt included); falls back to the
        /// RouteRoot Z=offset plane. Returns false when neither is available.
        /// </summary>
        bool TryGetWallPlane(out Vector3 point, out Vector3 normal)
        {
            if (surfaceProbe != null)
            {
                if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                    surfaceProbe.SetRouteRoot(routeRootProvider.RouteRoot);
                if (surfaceProbe.TryGetWallPlane(out point, out normal))
                    return true;
            }

            if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
            {
                Transform rr = routeRootProvider.RouteRoot;
                normal = rr.forward;
                point = rr.position + normal * wallProjectionSettings.wallDepthOffset;
                return true;
            }

            point = Vector3.zero;
            normal = Vector3.forward;
            return false;
        }

        /// <summary>
        /// Detect ARKit's front-facing vertical wall plane and use it as the wall (real position + tilt). Sets the
        /// probe to AR-plane mode on success. Returns true if a wall plane was found. Needs the AR camera.
        /// </summary>
        public bool CalibrateWallFromArPlane()
        {
            if (surfaceProbe == null)
            {
                Debug.LogWarning("[FusedCharacterPlayer] No ARSurfaceProbe — can't calibrate wall from an AR plane.");
                return false;
            }
            if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                surfaceProbe.SetRouteRoot(routeRootProvider.RouteRoot);

            Transform cam = Globals.CameraManager != null ? Globals.CameraManager.transform
                : (Camera.main != null ? Camera.main.transform : null);
            if (!surfaceProbe.TryCalibrateWallFromArPlane(cam))
            {
                Debug.LogWarning("[FusedCharacterPlayer] No front-facing AR vertical plane detected yet — point the phone at the wall and wait for tracking.");
                return false;
            }

            surfaceProbe.WallSource = ARSurfaceProbe.WallSourceMode.ARVerticalPlane;
            wallProjectionSettings.wallDepthOffset = surfaceProbe.WallLocalZOffset;
            return true;
        }

        /// <summary>
        /// Startup calibration should match the manual "Calibrate wall to AR plane (front)" button first. If ARKit
        /// has not detected a usable vertical plane yet, fall back to the older recording-depth estimate so playback
        /// still has a reasonable wall instead of doing nothing.
        /// </summary>
        bool AutoCalibrateWallForPlayback()
        {
            if (CalibrateWallFromArPlane())
                return true;

            return !float.IsNaN(AutoCalibrateWallDepth());
        }

        public float AutoCalibrateWallDepth()
        {
            if (!WallProjectionResolver.TryEstimateWallDepth(recording, out float wallDepth))
            {
                Debug.LogWarning("[FusedCharacterPlayer] Auto wall calibration skipped — not enough tracked joints in the recording.");
                return float.NaN;
            }

            wallProjectionSettings.wallDepthOffset = wallDepth;
            if (surfaceProbe != null)
                surfaceProbe.WallLocalZOffset = wallDepth;

            Debug.Log($"[FusedCharacterPlayer] Auto-calibrated wall plane to RouteRoot-local Z = {wallDepth:F3} m " +
                      "(estimated from the climb's closest hand/foot contacts).");
            return wallDepth;
        }
        /// <summary>RouteRoot provider supplying the live wall frame. For debug visualizers / status HUD.</summary>
        public IRouteRootProvider RouteRootProvider => routeRootProvider;
        /// <summary>True while a recording is actively playing back.</summary>
        public bool IsPlayingBack => isPlaying;
        public void SetInvertFacing(bool value)
        {
            if (invertFacing == value) return;
            invertFacing = value;
            hasLastFacingForward = false;
            hasLastGoodBodyBasis = false;
        }
        /// <summary>Fine-tune multiplier on the auto-fit character scale; re-applies the fit immediately.</summary>
        public float SkeletonFitScale
        {
            get => skeletonFitScale;
            set
            {
                skeletonFitScale = Mathf.Clamp(value, 0.5f, 1.5f);
                ApplyRecordingHeightScale();
            }
        }

        public void SetRouteRootLocalOffset(Vector3 offset) => routeRootLocalOffset = offset;

        void OnEnable()
        {
            if (logTuningSettingsEvery15s)
                tuningSnapshotLogRoutine = StartCoroutine(TuningSnapshotLogLoop());
        }

        void OnDisable()
        {
            if (tuningSnapshotLogRoutine != null)
            {
                StopCoroutine(tuningSnapshotLogRoutine);
                tuningSnapshotLogRoutine = null;
            }
            SaveHolds();
        }

        IEnumerator TuningSnapshotLogLoop()
        {
            var wait = new WaitForSecondsRealtime(15f);
            while (true)
            {
                yield return wait;
                LogTuningSnapshotForCopy();
            }
        }

        /// <summary>Emit all tunables in Unity scene YAML form for copy/paste into NewVersion.unity.</summary>
        public void LogTuningSnapshotForCopy()
        {
            var a = anchorSettings;
            var pp = postProcessSettings;
            var pen = penetrationSettings;
            var bake = Object.FindFirstObjectByType<MoveAIFusionCoordinator>()?.BakeSettings
                       ?? MoveAIFusionBaker.Settings.Default;

            var sb = new StringBuilder(2048);
            sb.AppendLine("[FusedCharacterPlayer] === Tuning snapshot — copy block below into scene YAML ===");
            sb.AppendLine("  invertFacing: " + YBool(invertFacing));
            sb.AppendLine("  flipCharacterForward: " + YBool(flipCharacterForward));
            sb.AppendLine("  loop: " + YBool(loop));
            sb.AppendLine("  playbackSpeed: " + YFloat(playbackSpeed));
            sb.AppendLine("  routeRootLocalOffset: {x: " + YFloat(routeRootLocalOffset.x) + ", y: " + YFloat(routeRootLocalOffset.y) + ", z: " + YFloat(routeRootLocalOffset.z) + "}");
            sb.AppendLine("  articulationSource: " + (int)articulationSource + "  # " + articulationSource);
            sb.AppendLine("  anchorSettings:");
            sb.AppendLine("    mode: " + (int)a.mode + "  # " + a.mode);
            sb.AppendLine("    stillnessVelocity: " + YFloat(a.stillnessVelocity));
            sb.AppendLine("    fullMotionVelocity: " + YFloat(a.fullMotionVelocity));
            sb.AppendLine("    followSeconds: " + YFloat(a.followSeconds));
            sb.AppendLine("    correctXZOnly: " + YBool(a.correctXZOnly));
            sb.AppendLine("    moveDrivenFacing: " + YBool(a.moveDrivenFacing));
            sb.AppendLine("    facingCorrectionSeconds: " + YFloat(a.facingCorrectionSeconds));
            sb.AppendLine("  playbackAnchorMode: " + (int)playbackAnchorMode + "  # " + playbackAnchorMode);
            sb.AppendLine("  enablePoseSmoothing: " + YBool(enablePoseSmoothing));
            sb.AppendLine("  postProcessSettings:");
            sb.AppendLine("    enableGlitchGuard: " + YBool(pp.enableGlitchGuard));
            sb.AppendLine("    maxJointSpeed: " + YFloat(pp.maxJointSpeed));
            sb.AppendLine("    boneLengthTolerance: " + YFloat(pp.boneLengthTolerance));
            sb.AppendLine("    enableSmoothing: " + YBool(pp.enableSmoothing));
            sb.AppendLine("    minCutoff: " + YFloat(pp.minCutoff));
            sb.AppendLine("    beta: " + YFloat(pp.beta));
            sb.AppendLine("    smoothRootTranslation: " + YBool(pp.smoothRootTranslation));
            sb.AppendLine("    jumpVelocityThreshold: " + YFloat(pp.jumpVelocityThreshold));
            sb.AppendLine("    jumpBetaScale: " + YFloat(pp.jumpBetaScale));
            sb.AppendLine("  enableRootMotionGuard: " + YBool(enableRootMotionGuard));
            sb.AppendLine("  maxRootSpeed: " + YFloat(maxRootSpeed));
            sb.AppendLine("  rootTeleportSnapDistance: " + YFloat(rootTeleportSnapDistance));
            sb.AppendLine("  maxRootTurnSpeed: " + YFloat(maxRootTurnSpeed));
            sb.AppendLine("  rootTurnSnapDegrees: " + YFloat(rootTurnSnapDegrees));
            sb.AppendLine("  enablePenetrationFix: " + YBool(enablePenetrationFix));
            sb.AppendLine("  penetrationSettings:");
            sb.AppendLine("    enableFloorFix: " + YBool(pen.enableFloorFix));
            sb.AppendLine("    floorContactBand: " + YFloat(pen.floorContactBand));
            sb.AppendLine("    enableWallHandIK: " + YBool(pen.enableWallHandIK));
            sb.AppendLine("    enableWallFootIK: " + YBool(pen.enableWallFootIK));
            sb.AppendLine("    maxIkWeight: " + YFloat(pen.maxIkWeight));
            sb.AppendLine("    penetrationForFullWeight: " + YFloat(pen.penetrationForFullWeight));
            sb.AppendLine("    enableWholeBodyPush: " + YBool(pen.enableWholeBodyPush));
            sb.AppendLine("    minWholeBodyPenetration: " + YFloat(pen.minWholeBodyPenetration));
            sb.AppendLine("    wholeBodyPenetrationFraction: " + YFloat(pen.wholeBodyPenetrationFraction));
            sb.AppendLine("    maxWholeBodyPushMeters: " + YFloat(pen.maxWholeBodyPushMeters));
            sb.AppendLine("    skipDuringJump: " + YBool(pen.skipDuringJump));
            sb.AppendLine("    debugDraw: " + YBool(pen.debugDraw));
            sb.AppendLine("  heightFitMode: " + (int)heightFitMode + "  # " + heightFitMode);
            sb.AppendLine("  skeletonFitScale: " + YFloat(skeletonFitScale));
            sb.AppendLine("--- MoveAIFusionCoordinator (fusion bake) ---");
            sb.AppendLine("  axisWeights: {x: " + YFloat(bake.axisWeights.x) + ", y: " + YFloat(bake.axisWeights.y) + ", z: " + YFloat(bake.axisWeights.z) + "}");
            sb.AppendLine("  smoothingTau: " + YFloat(bake.smoothingTau));
            sb.AppendLine("  outlierMeters: " + YFloat(bake.outlierMeters));
            sb.AppendLine("[FusedCharacterPlayer] === End tuning snapshot ===");
            Debug.Log(sb.ToString());
        }

        static int YBool(bool v) => v ? 1 : 0;

        static string YFloat(float v) => v.ToString("G", CultureInfo.InvariantCulture);

        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;

        public void SetRouteRootProvider(IRouteRootProvider provider)
        {
            routeRootProvider = provider;
            if (provider?.RouteRoot != null)
                referenceFrame = new CoordinateFrame(provider.RouteRoot);
        }

        public void SetCharacterRoot(Transform root)
        {
            characterRoot = root;
            boneByJoint.Clear();
        }

        /// <summary>
        /// Switch the articulation path at runtime (used by the FBX⇄GLB toggle). Procedural restores the original
        /// position-based FBX retarget and fully detaches the GLB muscle path so it can't touch the FBX rig. MoveGlb
        /// re-builds the character's pose handler when a ready GLB source is present (otherwise it activates lazily
        /// once <see cref="SetMoveGlbSource"/> supplies one). Safe to call while idle or mid-playback.
        /// </summary>
        public void SetArticulationSource(BodyArticulationSource mode)
        {
            articulationSource = mode;

            if (mode == BodyArticulationSource.Procedural)
            {
                // Detach GLB entirely so the legacy procedural solver owns the rig (no leftover muscle pose/rotation).
                glbActive = false;
                targetPoseHandler?.Dispose();
                targetPoseHandler = null;
                poseRoot = null;
            }
            else // MoveGlb
            {
                if (glbSource != null && glbSource.IsReady && characterRoot != null)
                {
                    BuildTargetPoseHandler();
                    glbActive = targetPoseHandler != null;
                }
                else
                {
                    glbActive = false; // activates later when a ready source arrives via SetMoveGlbSource
                }
            }
        }

        /// <summary>
        /// Supply (or clear) the Move AI GLB source for muscle-space body + finger articulation. Pass a ready
        /// source to drive the character from the GLB; pass null to fall back to the procedural position solver.
        /// Builds the character's Humanoid pose handler (and a runtime avatar if the rig is generic) on demand.
        /// </summary>
        void RefreshGlbPostProcess()
        {
            if (glbSource == null) return;
            glbSource.SetPostProcess(
                enablePoseSmoothing && postProcessSettings.enableSmoothing,
                postProcessSettings.minCutoff,
                postProcessSettings.beta,
                postProcessSettings.enableGlitchGuard,
                postProcessSettings.maxJointSpeed);
        }

        public void SetMoveGlbSource(MoveGlbSource source)
        {
            glbSource = source;

            // Step 2b: smooth the GLB's muscle-space body/finger jitter (the world-joint smoothing can't see it).
            if (source != null)
            {
                RefreshGlbPostProcess();
                source.ResetSmoothing();
            }

            if (source != null && source.IsReady && articulationSource == BodyArticulationSource.MoveGlb)
            {
                BuildTargetPoseHandler();
                glbActive = targetPoseHandler != null;
                if (!glbActive)
                    Debug.LogWarning("[FusedCharacterPlayer] GLB source ready but the character has no usable " +
                                     "Humanoid avatar — falling back to the procedural retarget.");
                else
                    Debug.Log("[FusedCharacterPlayer] Move GLB articulation active (body + fingers in muscle space).");
            }
            else
            {
                glbActive = false;
            }
        }

        /// <summary>Re-apply a preloaded GLB source after LoadAsset/RebindCharacter (e.g. WarmGlb finished before Play).</summary>
        public void RefreshGlbArticulation()
        {
            if (glbSource != null && articulationSource == BodyArticulationSource.MoveGlb)
                SetMoveGlbSource(glbSource);
        }

        /// <summary>Build the <see cref="HumanPoseHandler"/> used to write the Move GLB pose onto the character,
        /// constructing a Humanoid avatar at runtime when the rig is generic (e.g. a glTFast-imported GLB).</summary>
        void BuildTargetPoseHandler()
        {
            targetPoseHandler?.Dispose();
            targetPoseHandler = null;
            poseRoot = null;
            if (characterRoot == null) return;

            if (characterAnimator == null)
                characterAnimator = characterRoot.GetComponentInChildren<Animator>(true);
            if (characterAnimator == null)
                characterAnimator = characterRoot.gameObject.AddComponent<Animator>();

            if (!(characterAnimator.isHuman && characterAnimator.avatar != null && characterAnimator.avatar.isValid))
            {
                var built = Glb.HumanoidAvatarFactory.Build(characterRoot.gameObject, out string report);
                if (built == null)
                {
                    Debug.LogWarning("[FusedCharacterPlayer] Could not build a Humanoid avatar for the character: " + report);
                    return;
                }
                characterAnimator.avatar = built;
                Debug.Log("[FusedCharacterPlayer] Built runtime Humanoid avatar for '" + characterRoot.name + "'.");
            }

            try
            {
                targetPoseHandler = new HumanPoseHandler(characterAnimator.avatar, characterAnimator.transform);
                poseRoot = characterAnimator.transform;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FusedCharacterPlayer] HumanPoseHandler construction failed: " + e.Message);
                targetPoseHandler = null;
            }
        }

        /// <summary>
        /// Swap the driven character to a different rig at runtime (used by <see cref="CharacterSwitcher"/>).
        /// Safe to call mid-playback: re-resolves bones, re-captures the bind pose, disables the new rig's
        /// Animator, re-applies the skeleton-fit scale, and (if playing) shows the new model immediately.
        /// The previous model is left untouched (the switcher hides it); pass null only to detach.
        /// </summary>
        public void RebindCharacter(Transform newRoot)
        {
            // Hide the old rig if we're swapping it out during playback so two characters never show at once.
            if (characterRoot != null && characterRoot != newRoot)
                SetCharacterVisible(false);

            characterRoot = newRoot;

            // Drop all cached rig state so nothing from the previous model leaks into the new one.
            boneByJoint.Clear();
            bindLocalRotation.Clear();
            bindAimLocal.Clear();
            bindUpLocal.Clear();
            primaryChild.Clear();
            characterAnimator = null;
            hasRigBindAnatomical = false;
            hasLastGoodBodyBasis = false;
            hasBindRootPose = false;
            loggedPlacement = false;
            // Drop the GLB pose handler bound to the old rig; it is rebuilt below for the new character.
            targetPoseHandler?.Dispose();
            targetPoseHandler = null;
            poseRoot = null;
            glbActive = false;

            if (newRoot == null)
                return;

            CharacterLookLab.PrepareForDisplay(newRoot);

            // Bones can only be resolved once we know the pose (asset). Before the first asset loads we just
            // remember the root; LoadAsset/ResolveCharacterRoot will keep it because it is now non-null.
            if (asset?.pose != null)
            {
                ResolveBones();
                if (boneByJoint.Count == 0)
                    Debug.LogWarning($"[FusedCharacterPlayer] Rebind to '{newRoot.name}' mapped 0 bones — check the rig is Humanoid or its bone names match.");
                DisableRigAnimator();
                if (isPlaying)
                    ApplyRecordingHeightScale();
                if (articulationSource == BodyArticulationSource.MoveGlb)
                    EnsureCharacterHumanoid();
            }

            // Rebind the GLB articulation onto the new rig if a source is loaded.
            if (glbSource != null && glbSource.IsReady && articulationSource == BodyArticulationSource.MoveGlb)
                SetMoveGlbSource(glbSource);

            SetCharacterVisible(isPlaying && !waitingForLocalization);
            Debug.Log($"[FusedCharacterPlayer] Rebound character to '{newRoot.name}' (playing={isPlaying}).");
        }

        /// <summary>The ARKit recording the asset was baked from; used to anchor the pelvis, align facing, and pre-detect holds.</summary>
        public void SetSourceRecording(HipRecording sourceRecording)
        {
            recording = sourceRecording;
            if (asset != null)
            {
                if (autoCalibrateWallOnPlay && enableWallProjection)
                    AutoCalibrateWallForPlayback();
                loadedHoldsMapId = null;
                EnsureHoldsLoaded();
            }
        }

        public bool LoadAsset(MoveAIFusionAsset fusionAsset)
        {
            if (fusionAsset == null || fusionAsset.pose == null || fusionAsset.FrameCount == 0)
            {
                Debug.LogError("[FusedCharacterPlayer] Invalid fusion asset");
                return false;
            }
            asset = fusionAsset;
            playbackTime = 0f;
            recordingHeightMeters = EstimateRecordingHeight(recording);
            UpdatePoseScaleFromRecording();
            ResolveCharacterRoot();
            if (characterRoot == null)
                Debug.LogWarning("[FusedCharacterPlayer] No character rig — timeline + compare skeletons only.");
            else
            {
                ResolveBones();
                if (boneByJoint.Count == 0)
                    Debug.LogWarning("[FusedCharacterPlayer] No bones mapped — check MoveJointMap vs your rig bone names.");
                DisableRigAnimator();
                if (articulationSource == BodyArticulationSource.MoveGlb)
                    EnsureCharacterHumanoid();
                ApplyRecordingHeightScale();
            }
            if (asset.pose != null && (contactJointTable == null || contactJointTable.Length == 0))
                ConfigurePostProcessing(asset.pose);
            if (autoCalibrateWallOnPlay && enableWallProjection)
                AutoCalibrateWallForPlayback();
            loadedHoldsMapId = null;
            EnsureHoldsLoaded();
            return true;
        }

        /// <summary>Prepare a Humanoid avatar on the display character before the Move GLB source arrives.</summary>
        public void EnsureCharacterHumanoid()
        {
            if (characterRoot == null || articulationSource != BodyArticulationSource.MoveGlb) return;
            if (targetPoseHandler != null) return;
            BuildTargetPoseHandler();
            if (characterAnimator != null && characterAnimator.avatar != null && characterAnimator.avatar.isValid)
                Debug.Log("[FusedCharacterPlayer] Display character Humanoid avatar ready for GLB retarget.");
        }

        /// <summary>
        /// The FBX rig's Animator (added/enabled by FBXCharacterController) overwrites local bone rotations in
        /// the animation pass, which runs after Update and clobbers our retarget. Disable it so the procedural
        /// pose we write in LateUpdate sticks.
        /// </summary>
        void DisableRigAnimator()
        {
            if (characterRoot == null) return;
            characterAnimator = characterRoot.GetComponentInChildren<Animator>(true);
            if (characterAnimator != null && characterAnimator.enabled)
            {
                characterAnimator.enabled = false;
                Debug.Log("[FusedCharacterPlayer] Disabled rig Animator so procedural retarget can drive bones.");
            }
        }

        public void StartPlayback()
        {
            if (asset == null)
            {
                Debug.LogError("[FusedCharacterPlayer] No asset loaded");
                return;
            }
            if (routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
                Debug.LogError("[FusedCharacterPlayer] No RouteRoot provider; wait for localization");
                return;
            }

            if (characterRoot != null)
                ApplyRecordingHeightScale();

            DisableRigAnimator();
            isPlaying = true;
            isPaused = false;
            playbackTime = 0f;
            readyToApply = false;
            loggedPlacement = false;
            anchorState = default;
            postProcessor.Reset();
            ResetRootMotionGuard();
            wallProjection.Reset();
            if (autoCalibrateWallOnPlay && enableWallProjection)
                AutoCalibrateWallForPlayback();
            EnsureHoldsLoaded();
            RefreshGlbPostProcess();
            glbSource?.ResetSmoothing();
            pendingPosedHeightRefit = glbActive;
            hasLastGoodBodyBasis = false;
            waitingForLocalization = !routeRootProvider.IsLocalized;
            SetCharacterVisible(characterRoot != null && !waitingForLocalization);
            OnPlaybackStarted?.Invoke();
        }

        public void StopPlayback()
        {
            if (!isPlaying) return;
            isPlaying = false;
            isPaused = false;
            readyToApply = false;
            loggedPlacement = false;
            SaveHolds();
            RestoreBindRootPose();
            SetCharacterVisible(false);
            OnPlaybackStopped?.Invoke();
        }

        /// <summary>Pause the fused replay in place without resetting the timeline.</summary>
        public void PausePlayback()
        {
            if (!isPlaying || isPaused) return;
            isPaused = true;
        }

        /// <summary>Resume the fused replay from where it was paused.</summary>
        public void ResumePlayback()
        {
            if (!isPlaying || !isPaused) return;
            isPaused = false;
        }

        /// <summary>Seek the fused replay to a specific time (seconds).</summary>
        public void SeekToTime(float time)
        {
            if (asset == null) return;
            playbackTime = Mathf.Clamp(time, 0f, asset.Duration);
            // A seek is a deliberate relocation — let the body snap there instead of gliding from the old spot.
            ResetRootMotionGuard();
            // Re-apply the frozen frame next LateUpdate so the character snaps to the new time even when paused.
            readyToApply = isPlaying && !waitingForLocalization;
        }

        /// <summary>Configure looping between two times (inclusive start, exclusive end).</summary>
        public void SetSegmentLoop(float start, float end, bool enabled)
        {
            segmentLoopEnabled = enabled && end > start;
            segmentLoopStart = Mathf.Max(0f, start);
            segmentLoopEnd = end;
        }

        /// <summary>Step the timeline by whole frames (positive = forward).</summary>
        public void StepFrames(int deltaFrames)
        {
            if (asset == null || deltaFrames == 0) return;
            float step = deltaFrames * (1f / FrameRate);
            SeekToTime(playbackTime + step);
        }

        void Update()
        {
            readyToApply = false;
            if (!isPlaying || asset == null) return;

            // Live calibration: if the fit slider was nudged in the inspector during play mode, re-apply the
            // uniform height scale (the ratio math is scale-invariant, so re-applying is stable, not cumulative).
            if (characterRoot != null && !Mathf.Approximately(skeletonFitScale, lastAppliedFitScale))
                ApplyRecordingHeightScale();

            bool localized = routeRootProvider == null || routeRootProvider.IsLocalized;
            if (!localized)
            {
                if (!waitingForLocalization)
                {
                    waitingForLocalization = true;
                    SetCharacterVisible(false);
                }
                return;
            }
            if (waitingForLocalization)
            {
                waitingForLocalization = false;
                if (characterRoot != null)
                    SetCharacterVisible(true);
            }

            // Track the live RouteRoot pose each frame (Immersal scene updates / image-target follow).
            if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                referenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);

            // While paused, keep re-applying the frozen frame (so it stays anchored if the phone moves) but
            // do not advance the timeline.
            if (isPaused)
            {
                readyToApply = true;
                return;
            }

            playbackTime += Time.deltaTime * playbackSpeed;
            float duration = asset.Duration;
            if (segmentLoopEnabled && segmentLoopEnd > segmentLoopStart)
            {
                if (playbackTime >= segmentLoopEnd)
                {
                    playbackTime = segmentLoopStart;
                    ResetRootMotionGuard(); // loop wrap teleports the pelvis back to the start — snap, don't glide
                }
            }
            else if (playbackTime >= duration)
            {
                if (loop) { playbackTime %= duration; ResetRootMotionGuard(); }
                else { StopPlayback(); return; }
            }

            // Defer the actual bone write to LateUpdate so it lands after the rig's animation pass.
            readyToApply = true;
        }

        void LateUpdate()
        {
            if (!isPlaying || !readyToApply || characterRoot == null) return;
            ApplyFrame(playbackTime);
        }

        void ApplyFrame(float t)
        {
            if (characterRoot == null) return;
            // #region agent log
            PerfSampler.Begin("Frame.ApplyFrame");
            // #endregion

            // Positions-based retarget: drive the rig from Move's reliable joint POSITIONS rather than its
            // source local rotations. Each bone aims at its Move child and uses a captured bind-pose up axis
            // to keep elbows, knees, hands, and feet from twisting/flipping around the segment direction.
            var effAnchorSettings = EffectivePlaybackAnchorSettings();
            useMoveDrivenFacing = effAnchorSettings.moveDrivenFacing;
            // #region agent log
            PerfSampler.Begin("Solve.ComputeLocal");
            // #endregion
            Vector3[] local = FusedPoseSolver.ComputeLocalJoints(asset, recording, t, ref anchorState, out Quaternion effectiveFacingLocal, invertFacing, effAnchorSettings, ActiveGlbSource);
            // #region agent log
            PerfSampler.End("Solve.ComputeLocal");
            // #endregion
            if (local == null)
            {
                // #region agent log
                PerfSampler.End("Frame.ApplyFrame");
                // #endregion
                return;
            }

            // Step 2/3: joint-array cleanup for the procedural path. GLB replay handles muscles in MoveGlbSource.
            // #region agent log
            PerfSampler.Begin("PostProcess");
            // #endregion
            if (!glbActive && (enablePoseSmoothing || postProcessSettings.enableGlitchGuard))
                postProcessor.Process(local, rootJointIndex, Time.deltaTime, postProcessSettings);
            // #region agent log
            PerfSampler.End("PostProcess");
            // #endregion
            bool jumping = postProcessor.LastJumping;

            int n = local.Length;
            var world = new Vector3[n];
            for (int i = 0; i < n; i++)
                world[i] = referenceFrame.TransformPoint(local[i] + routeRootLocalOffset);

            // Step 4 (world phase): keep the body out of the wall and lift a standing pose onto the floor BEFORE the
            // hips are pinned, so both the procedural and GLB paths inherit the correction.
            if (enablePenetrationFix && surfaceProbe != null)
            {
                // #region agent log
                PerfSampler.Begin("Penetration.BodyFloor");
                // #endregion
                if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                    surfaceProbe.SetRouteRoot(routeRootProvider.RouteRoot);
                penetrationResolver.ResolveBodyAndFloor(world, rootJointIndex, footWorldIndices, wallContactWorldIndices,
                    jumping, penetrationSettings);
                // #region agent log
                PerfSampler.End("Penetration.BodyFloor");
                // #endregion
            }

            // Flat-wall constraint: clamp every joint into the wall depth slab (no clipping in / floating off) and
            // lock hands/feet that are gripping holds onto the wall surface. Runs on the shared world array so both
            // the procedural and GLB paths inherit it; the per-limb contacts also drive a rig-IK contact pass below.
            lastWallContacts = null;
            lastWallValid = false;
            if (enableWallProjection && TryGetWallPlane(out Vector3 wallPoint, out Vector3 wallNormal))
            {
                // #region agent log
                PerfSampler.Begin("WallProjection");
                // #endregion
                // Only constrain to the wall while the climber is actually on it. If they walk away — or are just
                // standing on the floor in front of it — let the raw pose pass through (eased) so they aren't snapped
                // onto the wall. Floor data is needed by the engagement gate, so resolve it first.
                Transform routeRoot = routeRootProvider != null ? routeRootProvider.RouteRoot : null;
                float floorWorldY = 0f;
                bool hasFloor = surfaceProbe != null && surfaceProbe.HasFloor(out floorWorldY);
                if (wallProjection.UpdateWallEngagement(world, wallPoint, wallNormal, hasFloor, floorWorldY,
                        footWorldIndices, rootJointIndex, Time.deltaTime, wallProjectionSettings))
                {
                    wallProjection.ProjectIntoSlab(world, wallPoint, wallNormal, wallProjectionSettings);
                    float contactDt = Mathf.Max(1f / FrameRate, Time.deltaTime * Mathf.Max(0.01f, playbackSpeed));
                    lastWallContacts = wallProjection.ResolveContacts(world, contactJointTable, wallPoint, wallNormal,
                        routeRoot, contactDt, hasFloor, floorWorldY, holdMap, wallProjectionSettings);
                    // Cache the plane so the post-pose rig IK push-out (procedural + GLB) uses the exact same wall.
                    lastWallPoint = wallPoint;
                    lastWallNormal = wallNormal;
                    lastWallValid = true;
                }
                // #region agent log
                PerfSampler.End("WallProjection");
                // #endregion
            }

            // GLB articulation path: body + fingers come from the Move GLB (muscle space); positioning + facing
            // still come from the fused world joints above. Returns early so the procedural bone solver is skipped.
            if (glbActive && ApplyGlbFrame(t, world, effectiveFacingLocal))
            {
                // #region agent log
                PerfSampler.End("Frame.ApplyFrame");
                // #endregion
                return;
            }

            // Reset every bound bone to its bind pose so this frame's swing is computed fresh (no roll drift).
            foreach (var kv in boneByJoint)
                if (kv.Value != null && bindLocalRotation.TryGetValue(kv.Key, out var bind))
                    kv.Value.localRotation = bind;

            // Place + face the whole character from the Move pelvis basis.
            OrientAndPlaceRoot(world, effectiveFacingLocal);
            BodyBasis bodyBasis = ComputeBodyBasis(world);

            if (!loggedPlacement)
            {
                loggedPlacement = true;
                Vector3 rr = referenceFrame.position;
                Vector3 hipW = world[rootJointIndex];
                Debug.Log($"[FusedCharacterPlayer] Placement: RouteRoot world={rr:F3} rot={referenceFrame.rotation.eulerAngles:F1} " +
                          $"| hip world={hipW:F3} | moveBodyForward={bodyBasis.forward:F3} | localized={(routeRootProvider == null || routeRootProvider.IsLocalized)}. " +
                          "moveBodyForward should be the climber's anatomical front; the per-bone twist reference matches it to the rig bind forward.");
            }

            // Aim each mapped bone toward its primary mapped child (parents already iterate first because
            // Move topology is parent-ordered), while preserving the target limb bend plane.
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                if (!boneByJoint.TryGetValue(j, out var bone) || bone == null) continue;
                if (!primaryChild.TryGetValue(j, out int child) || child < 0) continue;
                if (!boneByJoint.TryGetValue(child, out var childBone) || childBone == null) continue;
                if (childBone == bone) continue;

                ApplyBoneBasis(j, child, bone, world, bodyBasis);
            }

            // Step 4 (bone phase): pin any hand/foot that sank through the wall back onto the surface (climbing).
            if (enablePenetrationFix && surfaceProbe != null)
            {
                // #region agent log
                PerfSampler.Begin("Penetration.Limbs");
                // #endregion
                penetrationResolver.ResolveLimbs(characterAnimator, jumping, penetrationSettings);
                // #region agent log
                PerfSampler.End("Penetration.Limbs");
                // #endregion
            }

            // Pull any gripping hand/foot onto its locked hold.
            if (enableWallProjection && lastWallContacts != null)
                penetrationResolver.ResolveContactLimbs(characterAnimator, lastWallContacts, jumping, penetrationSettings);
            // Push any hand/foot still behind the wall skin back onto the surface (hold-independent), using the same
            // flat wall plane as the slab so it works even where the AR probe / standing classification would skip it.
            if (enableWallProjection && lastWallValid)
                penetrationResolver.ResolveContactPlane(characterAnimator, lastWallContacts, lastWallPoint, lastWallNormal,
                    wallProjectionSettings.wallSurfaceDepth, jumping, penetrationSettings);
            // #region agent log
            PerfSampler.End("Frame.ApplyFrame");
            // #endregion
        }

        /// <summary>
        /// Drive the character from the Move GLB in muscle space (body + fingers), then place + face it using the
        /// fused world joints exactly like the procedural path: yaw-align the Move capture to the fused facing once,
        /// then pin the hips onto the fused world hip every frame. Returns false to let the caller fall back to the
        /// procedural solver if the GLB pose can't be sampled this frame.
        /// </summary>
        bool ApplyGlbFrame(float t, Vector3[] world, Quaternion effectiveFacingLocal)
        {
            if (targetPoseHandler == null || glbSource == null || poseRoot == null)
                return false;
            // #region agent log
            PerfSampler.Begin("Glb.Sample");
            // #endregion
            bool sampled = glbSource.SampleHumanPose(t, ref glbPose);
            // #region agent log
            PerfSampler.End("Glb.Sample");
            // #endregion
            if (!sampled)
                return false;

            // Strip GLB root motion — the fused trajectory owns world placement and facing. Keeping the Move
            // capture's bodyPosition/bodyRotation makes the mesh follow only part of the ARKit path (~30% travel)
            // and the one-shot yaw align drifts as the body turns during the walk.
            glbPose.bodyPosition = Vector3.zero;
            glbPose.bodyRotation = Quaternion.identity;

            targetPoseHandler.SetHumanPose(ref glbPose);

            if (pendingPosedHeightRefit)
            {
                pendingPosedHeightRefit = false;
                ApplyRecordingHeightScale();
            }

            // Same root placement + per-frame facing as the procedural path (characterRoot, not poseRoot alone).
            OrientAndPlaceRoot(world, effectiveFacingLocal);

            if (enablePenetrationFix && surfaceProbe != null)
            {
                // #region agent log
                PerfSampler.Begin("Penetration.Limbs");
                // #endregion
                penetrationResolver.ResolveLimbs(characterAnimator, postProcessor.LastJumping, penetrationSettings);
                // #region agent log
                PerfSampler.End("Penetration.Limbs");
                // #endregion
            }

            // Pull any gripping hand/foot onto its locked hold (works in GLB muscle space too).
            if (enableWallProjection && lastWallContacts != null)
                penetrationResolver.ResolveContactLimbs(characterAnimator, lastWallContacts, postProcessor.LastJumping, penetrationSettings);
            // Push any hand/foot still behind the wall skin back onto the surface — this is the path that fixes the
            // GLB mesh limbs the slab can't reach (the slab only moves the world joints, not the muscle-space mesh).
            if (enableWallProjection && lastWallValid)
                penetrationResolver.ResolveContactPlane(characterAnimator, lastWallContacts, lastWallPoint, lastWallNormal,
                    wallProjectionSettings.wallSurfaceDepth, postProcessor.LastJumping, penetrationSettings);

            if (!loggedPlacement)
            {
                loggedPlacement = true;
                BodyBasis basis = ComputeBodyBasis(world);
                Vector3 hipsW = world[rootJointIndex];
                Debug.Log($"[FusedCharacterPlayer] GLB retarget: hip world={hipsW:F3}, " +
                          $"fused forward={basis.forward:F3}. Muscles/fingers from Move GLB; root from fused trajectory.");
            }
            return true;
        }

        /// <summary>Position the character: rotate to face, then pin feet (skeleton-joint fit) or hips (legacy).</summary>
        void OrientAndPlaceRoot(Vector3[] world, Quaternion effectiveFacingLocal)
        {
            Vector3 hipsW = world[rootJointIndex];
            BodyBasis basis = ComputeBodyBasis(world);

            Vector3 forward;
            if (useMoveDrivenFacing)
            {
                forward = basis.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f && hasLastFacingForward)
                    forward = lastFacingForward;
            }
            else
            {
                // Exact same yaw the solver applied to the orange skeleton (includes Invert facing 180°).
                forward = referenceFrame.rotation * (effectiveFacingLocal * Vector3.forward);
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f)
                {
                    forward = basis.forward;
                    forward.y = 0f;
                }
            }
            if (forward.sqrMagnitude < 1e-6f)
                forward = hasLastFacingForward ? lastFacingForward : Vector3.forward;
            forward.Normalize();
            lastFacingForward = forward;
            hasLastFacingForward = true;

            Quaternion targetAnatomical = Quaternion.LookRotation(forward, Vector3.up);
            Quaternion desiredRotation = hasRigBindAnatomical
                ? targetAnatomical * Quaternion.Inverse(rigBindLocalAnatomical)
                : targetAnatomical;
            characterRoot.rotation = ClampRootRotation(desiredRotation);

            // Foot-anchored placement: scale is head-to-foot, so pin feet to the orange skeleton — not hips.
            // Hip-only pinning made the feet look right but the head too high; shrinking fit-scale then floated the feet.
            Vector3 targetPos;
            if (heightFitMode == HeightFitMode.SkeletonJoints && TryComputeFootPlacementCorrection(world, out Vector3 footCorrection))
                targetPos = characterRoot.position + footCorrection;
            else if (boneByJoint.TryGetValue(rootJointIndex, out var hipsBone) && hipsBone != null)
                targetPos = characterRoot.position + (hipsW - hipsBone.position);
            else
                targetPos = hipsW;

            characterRoot.position = ClampRootMotion(targetPos);
        }

        // --- Final root-motion safety net --------------------------------------------------------------------
        // The muscle-space glitch guard / smoothing only clean the POSE (joints relative to the pelvis), and the GLB
        // path even strips the clip's root motion entirely — so the body's WORLD placement (the fused anchor) is the
        // one thing nothing else guards. A tracking spike, an anchor re-align, or the solver accepting a sustained
        // pelvis jump can therefore make the body lurch "from one position to another". This caps the per-frame travel
        // of the placed root to a sane top speed so neither the procedural nor the GLB path can ever teleport. It runs
        // in RouteRoot-local space so that moving the phone (which moves referenceFrame) is never mistaken for the body
        // moving, and a jump beyond rootTeleportSnapDistance (loop restart / genuine relocation) is allowed through 1:1
        // so the guard recovers instead of crawling across the wall.
        private Vector3 lastRootLocal;
        private bool hasLastRootLocal;
        private Quaternion lastRootLocalRot;
        private bool hasLastRootLocalRot;

        /// <summary>Forget the last placed root so the next frame seeds fresh (no glide from a stale spot). Call on
        /// (re)start, seek, and loop wrap.</summary>
        void ResetRootMotionGuard()
        {
            hasLastRootLocal = false;
            hasLastRootLocalRot = false;
        }

        /// <summary>Cap how fast the character's facing can turn per frame so a noisy/flipped facing can't snap the
        /// whole body around. Worked in RouteRoot-local space (like the position clamp) so turning the phone is never
        /// clamped. A jump beyond rootTurnSnapDegrees (loop wrap / genuine re-facing) is allowed through 1:1.</summary>
        Quaternion ClampRootRotation(Quaternion targetWorld)
        {
            if (!enableRootMotionGuard || maxRootTurnSpeed <= 0f)
                return targetWorld;

            Quaternion targetLocal = Quaternion.Inverse(referenceFrame.rotation) * targetWorld;
            if (!hasLastRootLocalRot)
            {
                lastRootLocalRot = targetLocal;
                hasLastRootLocalRot = true;
                return targetWorld;
            }

            float ang = Quaternion.Angle(lastRootLocalRot, targetLocal);
            if (rootTurnSnapDegrees > 0f && ang > rootTurnSnapDegrees)
            {
                lastRootLocalRot = targetLocal;
                return targetWorld;
            }

            float dt = Mathf.Max(1e-4f, Time.deltaTime) * Mathf.Max(0.01f, playbackSpeed);
            float maxDeg = maxRootTurnSpeed * dt;
            if (maxDeg > 0f && ang > maxDeg)
                targetLocal = Quaternion.RotateTowards(lastRootLocalRot, targetLocal, maxDeg);

            lastRootLocalRot = targetLocal;
            return referenceFrame.rotation * targetLocal;
        }

        Vector3 ClampRootMotion(Vector3 targetWorld)
        {
            if (!enableRootMotionGuard)
                return targetWorld;

            Vector3 targetLocal = referenceFrame.InverseTransformPoint(targetWorld);
            if (!hasLastRootLocal)
            {
                lastRootLocal = targetLocal;
                hasLastRootLocal = true;
                return targetWorld;
            }

            Vector3 delta = targetLocal - lastRootLocal;
            float dist = delta.magnitude;

            // Genuine relocation (loop wrap / re-anchor to a far pelvis): accept it so we never crawl across the room.
            if (rootTeleportSnapDistance > 0f && dist > rootTeleportSnapDistance)
            {
                lastRootLocal = targetLocal;
                return targetWorld;
            }

            // Scale the budget by playbackSpeed so fast-forward isn't throttled, but a single-frame spike still is.
            float dt = Mathf.Max(1e-4f, Time.deltaTime) * Mathf.Max(0.01f, playbackSpeed);
            float maxStep = Mathf.Max(0f, maxRootSpeed) * dt;
            if (maxStep > 0f && dist > maxStep)
            {
                targetLocal = lastRootLocal + delta * (maxStep / dist);
                targetWorld = referenceFrame.TransformPoint(targetLocal);
            }

            lastRootLocal = targetLocal;
            return targetWorld;
        }

        /// <summary>Average left/right foot error so both land on the orange skeleton after uniform scale.</summary>
        bool TryComputeFootPlacementCorrection(Vector3[] world, out Vector3 correction)
        {
            correction = Vector3.zero;
            if (asset?.pose == null || world == null) return false;

            int count = 0;
            foreach (int idx in GetPlacementFootJointIndices())
            {
                if (idx < 0 || idx >= world.Length) continue;
                if (!boneByJoint.TryGetValue(idx, out var bone) || bone == null) continue;
                correction += world[idx] - bone.position;
                count++;
            }

            if (count == 0) return false;
            correction /= count;
            return true;
        }

        /// <summary>Left/right ankle (or toe) indices used for foot pinning.</summary>
        IEnumerable<int> GetPlacementFootJointIndices()
        {
            var pose = asset.pose;
            int left = pose.IndexOfJoint("Left_ankle");
            if (left < 0) left = pose.IndexOfJoint("Left_toe");
            int right = pose.IndexOfJoint("Right_ankle");
            if (right < 0) right = pose.IndexOfJoint("Right_toe");
            if (left >= 0) yield return left;
            if (right >= 0) yield return right;
        }

        struct BodyBasis
        {
            public Vector3 up;
            public Vector3 right;
            public Vector3 forward;
        }

        BodyBasis ComputeBodyBasis(Vector3[] world)
        {
            Vector3 hipsW = world[rootJointIndex];
            Vector3 up = spineJointIndex >= 0 ? world[spineJointIndex] - hipsW : Vector3.zero;
            Vector3 right = (lHipJointIndex >= 0 && rHipJointIndex >= 0)
                ? world[rHipJointIndex] - world[lHipJointIndex]
                : Vector3.zero;

            if (up.sqrMagnitude < 1e-8f)
            {
                if (hasLastGoodBodyBasis)
                    up = lastGoodBodyBasis.up;
                else
                    up = referenceFrame.rotation * Vector3.up;
            }
            if (right.sqrMagnitude < 1e-8f)
            {
                if (hasLastGoodBodyBasis)
                    right = lastGoodBodyBasis.right;
                else
                    right = referenceFrame.rotation * Vector3.right;
            }

            Vector3 forward = Vector3.Cross(right.normalized, up.normalized);
            if (forward.sqrMagnitude < 1e-8f)
            {
                if (hasLastGoodBodyBasis)
                    return lastGoodBodyBasis;
                forward = referenceFrame.rotation * Vector3.forward;
            }

            var basis = new BodyBasis
            {
                up = up.normalized,
                right = right.normalized,
                forward = forward.normalized
            };
            lastGoodBodyBasis = basis;
            hasLastGoodBodyBasis = true;
            return basis;
        }

        void ApplyBoneBasis(int joint, int child, Transform bone, Vector3[] world, BodyBasis bodyBasis)
        {
            if (!bindAimLocal.TryGetValue(joint, out var bindAim) ||
                !bindUpLocal.TryGetValue(joint, out var bindUp))
                return;

            Vector3 currentAim = bone.TransformDirection(bindAim);
            Vector3 currentUp = bone.TransformDirection(bindUp);
            Vector3 targetAim = world[child] - world[joint];
            // Twist reference is the Move body's anatomical FRONT — the SAME reference captured at bind time
            // (see CaptureBindAxes / ComputeRigBindBasis). Using one consistent reference for both is what
            // stops limbs from rolling 180° (flipping) on rigs whose authored forward differs from cross(right, up).
            Vector3 targetUp = bodyBasis.forward;

            if (!TryBuildRotation(currentAim, currentUp, out var from) ||
                !TryBuildRotation(targetAim, targetUp, out var to))
                return;

            bone.rotation = to * Quaternion.Inverse(from) * bone.rotation;
        }

        static bool TryBuildRotation(Vector3 aim, Vector3 upHint, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (aim.sqrMagnitude < 1e-8f)
                return false;

            Vector3 forward = aim.normalized;
            if (!TryProjectUp(forward, upHint, out var up))
                return false;

            rotation = Quaternion.LookRotation(forward, up);
            return true;
        }

        static bool TryProjectUp(Vector3 forward, Vector3 upHint, out Vector3 up)
        {
            up = Vector3.ProjectOnPlane(upHint, forward);
            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.ProjectOnPlane(Vector3.up, forward);
            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.ProjectOnPlane(Vector3.forward, forward);
            if (up.sqrMagnitude < 1e-8f)
                return false;

            up.Normalize();
            return true;
        }

        void UpdatePoseScaleFromRecording()
        {
            if (asset?.pose == null) return;
            if (recordingHeightMeters <= 0.1f) return;

            float poseHeight = EstimatePoseHeight(asset.pose);
            if (poseHeight <= 0.1f) return;

            asset.scale = Mathf.Max(0.01f, recordingHeightMeters / poseHeight);
            Debug.Log($"[FusedCharacterPlayer] Pose height scale={asset.scale:F3} " +
                      $"(recording {recordingHeightMeters:F2}m / Move pose {poseHeight:F2}m)");
        }

        void ApplyRecordingHeightScale()
        {
            if (characterRoot == null) return;

            float fit = Mathf.Max(0.01f, skeletonFitScale);
            lastAppliedFitScale = skeletonFitScale;

            float currentScale = Mathf.Max(0.01f, (characterRoot.localScale.x + characterRoot.localScale.y + characterRoot.localScale.z) / 3f);

            // Default: match mapped head + foot bones to the orange skeleton joints (same asset.scale the overlay uses).
            if (heightFitMode == HeightFitMode.SkeletonJoints && TryComputeSkeletonJointScale(currentScale, fit, out float jointScale))
            {
                characterRoot.localScale = Vector3.one * jointScale;
                Debug.Log($"[FusedCharacterPlayer] Character skeleton-joint fit scale={jointScale:F3} (head→foot span, feet pinned, fit={fit:F2})");
                return;
            }

            // Preferred (legacy): match the character's RENDERED height — the crown of the mesh (hair included)
            // down to the soles — to the recorded climber's height. The orange/cyan skeletons only reach the Head
            // JOINT (well below the crown) and the toe JOINT (above the sole), so a joint-to-joint chain fit leaves
            // the mesh overshooting top and bottom and the character looks too tall. Fitting the mesh extent makes
            // the visible character sit inside the skeleton instead. Hips stay pinned every frame in OrientAndPlaceRoot.
            if (heightFitMode == HeightFitMode.RenderedMeshHeight)
            {
                float meshTarget = recordingHeightMeters;
                if (meshTarget <= 0.1f)
                    meshTarget = EstimatePoseHeight(asset?.pose) * Mathf.Max(0.01f, asset?.scale ?? 1f);

                if (meshTarget > 0.1f && TryEstimateCharacterMeshHeight(out float meshHeight) && meshHeight > 0.1f)
                {
                    float meshScale = Mathf.Max(0.01f, currentScale * meshTarget / meshHeight * fit);
                    characterRoot.localScale = Vector3.one * meshScale;
                    Debug.Log($"[FusedCharacterPlayer] Character mesh-height fit scale={meshScale:F3} " +
                              $"(climber {meshTarget:F2}m / mesh crown-to-sole {meshHeight:F2}m, fit={fit:F2})");
                    return;
                }
                // Mesh bounds couldn't be measured — fall through to the bone-chain / extent estimate below.
            }

            // Preferred fit: match the FBX's foot->head BONE-CHAIN length to the orange (Move) skeleton's, both
            // measured over the SAME mapped joints. Chain lengths are pose-invariant (segment lengths are fixed),
            // so with roughly-matched proportions a single uniform scale lands BOTH the head and the feet on the
            // orange joints automatically — the hips are already pinned every frame in OrientAndPlaceRoot.
            if (TryMatchChainHeight(out float chainScale))
            {
                float chainTarget = Mathf.Max(0.01f, chainScale * fit);
                characterRoot.localScale = Vector3.one * chainTarget;
                Debug.Log($"[FusedCharacterPlayer] Character chain-fit scale={chainTarget:F3} " +
                          $"(foot->head chain match, fit={fit:F2})");
                return;
            }

            // Fallback: overall head-to-foot extent vs the solved Move height (used when the foot/head joints
            // can't be resolved on this rig).
            float targetHeight = EstimatePoseHeight(asset?.pose) * Mathf.Max(0.01f, asset?.scale ?? 1f);
            if (targetHeight <= 0.1f)
                targetHeight = recordingHeightMeters;
            if (targetHeight <= 0.1f)
            {
                characterRoot.localScale = Vector3.one * Mathf.Max(0.01f, (asset?.scale ?? 1f) * fit);
                return;
            }

            bool usingBoneHeight = TryEstimateCharacterBoneHeight(out float currentHeight);
            if (!usingBoneHeight || currentHeight <= 0.1f)
                currentHeight = EstimateCharacterHeight(characterRoot);
            if (currentHeight <= 0.1f)
            {
                characterRoot.localScale = Vector3.one * Mathf.Max(0.01f, (asset?.scale ?? 1f) * fit);
                return;
            }

            float targetScale = Mathf.Max(0.01f, currentScale * targetHeight / currentHeight * fit);
            characterRoot.localScale = Vector3.one * targetScale;
            Debug.Log($"[FusedCharacterPlayer] Character height scale={targetScale:F3} " +
                      $"(target {targetHeight:F2}m / model {currentHeight:F2}m, " +
                      $"fit={fit:F2}, measure={(usingBoneHeight ? "joints" : "mesh-bounds")})");
        }

        static readonly string[] FootJointCandidates = { "Left_toe", "Right_toe", "Left_ankle", "Right_ankle" };

        /// <summary>
        /// Uniform scale from the orange skeleton's head-to-foot span (same <see cref="MoveAIFusionAsset.scale"/> as
        /// the overlay). Feet are pinned each frame in <see cref="OrientAndPlaceRoot"/>, so <see cref="skeletonFitScale"/>
        /// can nudge height without floating the feet.
        /// </summary>
        bool TryComputeSkeletonJointScale(float currentUniformScale, float fitMultiplier, out float scale)
        {
            scale = 0f;
            if (asset?.pose == null || asset.pose.FrameCount == 0 || boneByJoint.Count == 0)
                return false;

            var pose = asset.pose;
            int headIdx = pose.IndexOfJoint("Head");
            if (headIdx < 0) headIdx = pose.IndexOfJoint("Neck");
            int footIdx = FindMappedExtremityJoint(pose, rootJointIndex, FootJointCandidates);
            if (headIdx < 0 || footIdx < 0) return false;
            if (!boneByJoint.TryGetValue(headIdx, out var headBone) || headBone == null) return false;
            if (!boneByJoint.TryGetValue(footIdx, out var footBone) || footBone == null) return false;

            var fk = pose.ForwardKinematics(pose.frames[0]);
            if (fk == null || headIdx >= fk.Length || footIdx >= fk.Length)
                return false;

            float poseScale = Mathf.Max(0.01f, asset.scale);
            float moveSpan = Vector3.Distance(fk[headIdx], fk[footIdx]) * poseScale;
            if (moveSpan < 0.1f) return false;

            float charSpan = Vector3.Distance(headBone.position, footBone.position) / currentUniformScale;
            if (charSpan < 0.1f) return false;

            scale = Mathf.Max(0.01f, moveSpan / charSpan * fitMultiplier);
            return true;
        }

        int FindMappedExtremityJoint(MoveMotion pose, int rootIdx, string[] jointNames)
        {
            if (pose == null || pose.FrameCount == 0 || rootIdx < 0) return -1;
            var fk = pose.ForwardKinematics(pose.frames[0]);
            if (fk == null || rootIdx >= fk.Length) return -1;

            int best = -1;
            float bestDist = 0f;
            foreach (var name in jointNames)
            {
                int idx = pose.IndexOfJoint(name);
                if (idx < 0 || idx >= fk.Length || !boneByJoint.ContainsKey(idx)) continue;
                float dist = Vector3.Distance(fk[idx], fk[rootIdx]);
                if (dist > bestDist)
                {
                    bestDist = dist;
                    best = idx;
                }
            }
            return best;
        }

        /// <summary>
        /// Uniform localScale that makes the character's foot->head bone-chain length equal the orange (Move)
        /// skeleton's, measured over the same mapped joints. Returns false if the head/foot joints or enough of
        /// the chain can't be resolved on this rig (caller then falls back to the extent-based estimate).
        /// </summary>
        bool TryMatchChainHeight(out float scale)
        {
            scale = 0f;
            if (asset?.pose == null || boneByJoint.Count == 0 || asset.pose.FrameCount == 0)
                return false;

            var pose = asset.pose;
            int headIdx = pose.IndexOfJoint("Head");
            if (headIdx < 0) headIdx = pose.IndexOfJoint("Neck");
            int footIdx = pose.IndexOfJoint("Left_toe");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Left_ankle");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Right_toe");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Right_ankle");
            if (headIdx < 0 || footIdx < 0) return false;

            // Move joint world positions (rest is fine — bone lengths are constant), scaled the same way the
            // orange skeleton is drawn (asset.scale).
            Vector3[] fk = pose.ForwardKinematics(pose.frames[0]);
            if (fk == null || fk.Length == 0) return false;
            float poseScale = Mathf.Max(0.01f, asset.scale);

            float moveLen = 0f, charLen = 0f;
            bool ok = AccumulateChain(headIdx, fk, ref moveLen, ref charLen) &
                      AccumulateChain(footIdx, fk, ref moveLen, ref charLen);
            if (!ok || moveLen <= 0.05f || charLen <= 0.05f) return false;

            float currentScale = Mathf.Max(0.01f, (characterRoot.localScale.x + characterRoot.localScale.y + characterRoot.localScale.z) / 3f);
            scale = Mathf.Max(0.01f, currentScale * (moveLen * poseScale) / charLen);
            return true;
        }

        /// <summary>
        /// Walk from <paramref name="leaf"/> up the Move parent chain to the root, summing segment lengths over
        /// joints that are mapped to a character bone — accumulating the Move length (from <paramref name="fk"/>)
        /// and the character length (from the bound bone world positions) over the SAME consecutive joints so the
        /// two are directly comparable. Returns false if fewer than two chain joints are mapped.
        /// </summary>
        bool AccumulateChain(int leaf, Vector3[] fk, ref float moveLen, ref float charLen)
        {
            var pose = asset.pose;
            int prev = -1;
            int counted = 0;
            int guard = 0;
            for (int j = leaf; j >= 0 && guard < 128; guard++)
            {
                if (boneByJoint.TryGetValue(j, out var bone) && bone != null && j < fk.Length)
                {
                    if (prev >= 0)
                    {
                        moveLen += Vector3.Distance(fk[prev], fk[j]);
                        charLen += Vector3.Distance(boneByJoint[prev].position, bone.position);
                        counted++;
                    }
                    prev = j;
                }
                j = j < pose.jointParents.Count ? pose.jointParents[j] : -1;
            }
            return counted > 0;
        }

        /// <summary>
        /// Vertical extent of the character's MAPPED bones in its current (bind) pose — head bone down to the
        /// lowest foot/toe bone. This mirrors <see cref="EstimatePoseHeight"/> (min/max joint Y of the Move pose),
        /// so scaling the character to the recorded height by this metric makes its joints land exactly on the
        /// orange skeleton instead of falling short because the mesh box is taller than the skeleton.
        /// </summary>
        bool TryEstimateCharacterBoneHeight(out float height)
        {
            height = 0f;
            if (boneByJoint.Count == 0) return false;

            float min = float.MaxValue;
            float max = float.MinValue;
            int counted = 0;
            foreach (var kv in boneByJoint)
            {
                if (kv.Value == null) continue;
                float y = kv.Value.position.y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
                counted++;
            }

            if (counted < 2 || max <= min) return false;
            height = max - min;
            return height > 0.1f;
        }

        /// <summary>
        /// The character's RENDERED vertical extent (crown of the mesh/hair down to the soles) in world meters at
        /// the current scale. Uses each skinned mesh's bind-pose <c>sharedMesh.bounds</c> (pose-invariant and valid
        /// even while the model is hidden), so it captures the hair/crown that sits above the Head joint — the part
        /// the bone-chain fit ignores and that makes the character look too tall.
        /// </summary>
        bool TryEstimateCharacterMeshHeight(out float height)
        {
            height = 0f;
            if (characterRoot == null) return false;

            var renderers = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0) return false;

            float min = float.MaxValue;
            float max = float.MinValue;
            bool any = false;

            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                Bounds b = smr.sharedMesh.bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;
                Matrix4x4 m = smr.transform.localToWorldMatrix;

                // Transform all 8 corners so non-axis-aligned rigs are measured correctly.
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 w = m.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                    min = Mathf.Min(min, w.y);
                    max = Mathf.Max(max, w.y);
                    any = true;
                }
            }

            if (!any || max <= min) return false;
            height = max - min;
            return height > 0.1f;
        }

        static float EstimateCharacterHeight(Transform root)
        {
            if (root == null) return 0f;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds && bounds.size.y > 0.1f)
                return bounds.size.y;

            var bones = root.GetComponentsInChildren<Transform>(true);
            if (bones == null || bones.Length == 0) return 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (var bone in bones)
            {
                min = Mathf.Min(min, bone.position.y);
                max = Mathf.Max(max, bone.position.y);
            }
            return max > min ? max - min : 0f;
        }

        static float EstimatePoseHeight(MoveMotion pose)
        {
            if (pose == null || pose.FrameCount == 0) return 0f;

            var heights = new List<float>();
            int stride = Mathf.Max(1, pose.FrameCount / 60);
            for (int i = 0; i < pose.FrameCount; i += stride)
            {
                var fk = pose.ForwardKinematics(pose.frames[i]);
                if (fk == null || fk.Length == 0) continue;
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int j = 0; j < fk.Length; j++)
                {
                    min = Mathf.Min(min, fk[j].y);
                    max = Mathf.Max(max, fk[j].y);
                }
                if (max > min) heights.Add(max - min);
            }

            return Percentile(heights, 0.95f);
        }

        static float EstimateRecordingHeight(HipRecording source)
        {
            if (source?.frames == null || source.frames.Count == 0) return 0f;

            var chainHeights = new List<float>();
            var extentHeights = new List<float>();
            foreach (var frame in source.frames)
            {
                if (frame.recordedJoints == null || frame.recordedJoints.Count == 0) continue;
                if (TryEstimateArkitChainHeight(frame, out float chainHeight))
                    chainHeights.Add(chainHeight);
                if (TryEstimateHeadFootExtent(frame, out float extentHeight))
                    extentHeights.Add(extentHeight);
            }

            float height = Median(chainHeights);
            if (height > 0.1f) return height;
            return Percentile(extentHeights, 0.95f);
        }

        static bool TryEstimateArkitChainHeight(HipFrame frame, out float height)
        {
            height = 0f;

            // ARKit 3D body chain: head -> neck/spine -> hips plus the longer leg to toes/foot.
            int[] torso3D = { 51, 50, 49, 48, 47, 18, 17, 16, 15, 14, 13, 12, 1 };
            int[] leftLeg3D = { 1, 2, 3, 4, 5 };
            int[] rightLeg3D = { 1, 7, 8, 9, 10 };
            if (TryChainLength(frame, torso3D, out float torso) &&
                (TryChainLength(frame, leftLeg3D, out float leftLeg) | TryChainLength(frame, rightLeg3D, out float rightLeg)))
            {
                height = torso + Mathf.Max(leftLeg, rightLeg);
                return height > 0.5f;
            }

            // ARKit 2D fallback used by older recordings.
            int[] torso2D = { 0, 1, 16 };
            int[] leftLeg2D = { 16, 11, 12, 13 };
            int[] rightLeg2D = { 16, 8, 9, 10 };
            if (TryChainLength(frame, torso2D, out torso) &&
                (TryChainLength(frame, leftLeg2D, out leftLeg) | TryChainLength(frame, rightLeg2D, out rightLeg)))
            {
                height = torso + Mathf.Max(leftLeg, rightLeg);
                return height > 0.5f;
            }

            return false;
        }

        static bool TryEstimateHeadFootExtent(HipFrame frame, out float height)
        {
            height = 0f;
            int[] headCandidates = { 51, 58, 54, 59, 0, 14, 15 };
            int[] footCandidates = { 5, 6, 10, 11, 4, 9, 13, 10 };
            bool gotHead = false;
            bool gotFoot = false;
            float headY = float.MinValue;
            float footY = float.MaxValue;

            foreach (int idx in headCandidates)
            {
                if (TryGetSample(frame, idx, out var sample))
                {
                    gotHead = true;
                    headY = Mathf.Max(headY, sample.positionReference.y);
                }
            }

            foreach (int idx in footCandidates)
            {
                if (TryGetSample(frame, idx, out var sample))
                {
                    gotFoot = true;
                    footY = Mathf.Min(footY, sample.positionReference.y);
                }
            }

            if (!gotHead || !gotFoot || headY <= footY)
                return false;

            height = headY - footY;
            return height > 0.1f;
        }

        static bool TryChainLength(HipFrame frame, int[] indices, out float length)
        {
            length = 0f;
            if (indices == null || indices.Length < 2) return false;

            bool gotAny = false;
            for (int i = 1; i < indices.Length; i++)
            {
                if (!TryGetSample(frame, indices[i - 1], out var a) ||
                    !TryGetSample(frame, indices[i], out var b))
                    return false;

                length += Vector3.Distance(a.positionReference, b.positionReference);
                gotAny = true;
            }

            return gotAny && length > 0.1f;
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

        static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            return values[values.Count / 2];
        }

        static float Percentile(List<float> values, float percentile)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int index = Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * percentile), 0, values.Count - 1);
            return values[index];
        }

        void ResolveCharacterRoot()
        {
            if (characterRoot != null) return;

            // Prefer the CharacterSwitcher's GLB display character (Avaturn, priyal, modelART, etc.).
            var switcher = Object.FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            if (switcher != null && switcher.EnsureBound() && switcher.Current != null)
            {
                characterRoot = switcher.Current.transform;
                return;
            }

            // GLB mode must never fall back to spawning the legacy FBX — that caused the wrong character,
            // massive main-thread log spam, and freezes on device.
            if (articulationSource == BodyArticulationSource.MoveGlb)
            {
                Debug.LogWarning("[FusedCharacterPlayer] No GLB character bound. Put GLB character(s) under the " +
                                 "'Characters' object (or run TENDOR ▸ Characters ▸ Use GLB Characters Only).");
                return;
            }

            if (fbxCharacterController == null && autoFindCharacter)
                fbxCharacterController = FindFirstObjectByType<FBXCharacterController>();

            if (fbxCharacterController != null)
            {
                // Recording uses skeleton-only mode; spawn the rig on demand for fused replay (procedural mode only).
                if (fbxCharacterController.CharacterRoot == null)
                    fbxCharacterController.Initialize();
                if (fbxCharacterController.CharacterRoot != null)
                    characterRoot = fbxCharacterController.CharacterRoot.transform;
            }
        }

        void ResolveBones()
        {
            boneByJoint.Clear();
            if (characterRoot == null || asset?.pose == null) return;

            // Prefer the Humanoid avatar mapping: it is rig-name independent, so any humanoid robot/character
            // resolves correctly even when its bone names don't match Mixamo. Fall back to name matching.
            if (characterAnimator == null)
                characterAnimator = characterRoot.GetComponentInChildren<Animator>(true);
            bool humanoid = characterAnimator != null && characterAnimator.isHuman && characterAnimator.avatar != null;

            var transforms = characterRoot.GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, Transform>();
            foreach (var tr in transforms)
            {
                if (!byName.ContainsKey(tr.name))
                    byName[tr.name] = tr;
            }

            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                string moveName = asset.pose.jointNames[j];
                Transform bone = null;

                if (humanoid && MoveToHumanBone.TryGetValue(moveName, out var hbb))
                    bone = characterAnimator.GetBoneTransform(hbb);

                if (bone == null && jointMap.TryGetBone(moveName, out string boneName))
                    bone = FindBone(byName, boneName);

                if (bone != null)
                    boneByJoint[j] = bone;
            }

            // Fallback for non-humanoid / non-Mixamo rigs: bind remaining joints by common bone-name conventions
            // (UE/Blender/Daz/Unity etc.), so any rig animates without requiring an exact name match.
            ResolveBonesByConvention(transforms);

            // Capture the bind/T-pose local rotations now (rig is at bind here) for the non-accumulating retarget.
            bindLocalRotation.Clear();
            foreach (var kv in boneByJoint)
                if (kv.Value != null)
                    bindLocalRotation[kv.Key] = kv.Value.localRotation;

            BuildRetargetTopology();
            CaptureBindAxes();
            Debug.Log($"[FusedCharacterPlayer] Resolved {boneByJoint.Count}/{asset.pose.jointNames.Count} bones " +
                      $"(humanoid avatar: {humanoid})");
            VerifyRig(humanoid);
        }

        void CaptureBindAxes()
        {
            bindAimLocal.Clear();
            bindUpLocal.Clear();

            // The bind twist reference MUST be the rig's anatomical FRONT computed the SAME way the Move body
            // front is computed at runtime (cross of the hip axis and the up/spine axis). Deriving it from the
            // rig's own bind-pose bones — rather than characterRoot.forward, which is the model's authored facing
            // and can be 180° off — is what keeps every limb's roll consistent so they don't flip.
            hasRigBindAnatomical = ComputeRigBindBasis(out Vector3 rigForward, out Vector3 rigUp);
            if (hasRigBindAnatomical && characterRoot != null)
            {
                // Store the anatomical basis relative to the root so OrientAndPlaceRoot can map it onto the Move
                // facing directly. Use the HORIZONTAL rig forward + world up to match the runtime target (which is
                // a pure yaw about world up), so the mapping is a clean yaw with no constant tilt and no degeneracy.
                Vector3 flatRigForward = rigForward;
                flatRigForward.y = 0f;
                if (flatRigForward.sqrMagnitude < 1e-6f)
                    flatRigForward = characterRoot.forward;
                flatRigForward.y = 0f;
                if (flatRigForward.sqrMagnitude < 1e-6f)
                    flatRigForward = Vector3.forward;
                Quaternion worldAnatomical = Quaternion.LookRotation(flatRigForward.normalized, Vector3.up);
                rigBindLocalAnatomical = Quaternion.Inverse(characterRoot.rotation) * worldAnatomical;
                Debug.Log($"[FusedCharacterPlayer] Rig bind anatomical forward={rigForward:F3} up={rigUp:F3} " +
                          $"(authored characterRoot.forward={characterRoot.forward:F3}). Facing is auto-aligned to the Move body.");
            }

            if (characterRoot != null)
            {
                bindCharacterRootLocalRotation = characterRoot.localRotation;
                bindCharacterRootLocalPosition = characterRoot.localPosition;
                hasBindRootPose = true;
            }

            foreach (var kv in primaryChild)
            {
                int joint = kv.Key;
                int child = kv.Value;
                if (child < 0) continue;
                if (!boneByJoint.TryGetValue(joint, out var bone) || bone == null) continue;
                if (!boneByJoint.TryGetValue(child, out var childBone) || childBone == null) continue;
                if (childBone == bone) continue;

                Vector3 aim = childBone.position - bone.position;
                if (aim.sqrMagnitude < 1e-8f) continue;
                if (!TryProjectUp(aim.normalized, rigForward, out var projectedUp))
                    continue;

                bindAimLocal[joint] = bone.InverseTransformDirection(aim.normalized);
                bindUpLocal[joint] = bone.InverseTransformDirection(projectedUp);
            }
        }

        void RestoreBindRootPose()
        {
            if (characterRoot == null || !hasBindRootPose) return;
            characterRoot.localRotation = bindCharacterRootLocalRotation;
            characterRoot.localPosition = bindCharacterRootLocalPosition;
            foreach (var kv in boneByJoint)
            {
                if (kv.Value != null && bindLocalRotation.TryGetValue(kv.Key, out var bind))
                    kv.Value.localRotation = bind;
            }
        }

        /// <summary>
        /// The rig's anatomical (forward,up) basis in its current bind pose, from its own hip/spine bones.
        /// Mirrors <see cref="ComputeBodyBasis"/> (which derives the Move basis) so bind and runtime share one
        /// reference for both per-bone twist and whole-body facing. Returns false if the bones aren't resolved.
        /// </summary>
        bool ComputeRigBindBasis(out Vector3 forward, out Vector3 up)
        {
            forward = characterRoot != null ? characterRoot.forward : Vector3.forward;
            up = characterRoot != null ? characterRoot.up : Vector3.up;

            if (!boneByJoint.TryGetValue(rootJointIndex, out var hips) || hips == null)
                return false;

            Vector3 rigUp = Vector3.up;
            if (spineJointIndex >= 0 && boneByJoint.TryGetValue(spineJointIndex, out var spine) && spine != null)
                rigUp = spine.position - hips.position;

            Vector3 right = Vector3.right;
            if (lHipJointIndex >= 0 && rHipJointIndex >= 0 &&
                boneByJoint.TryGetValue(lHipJointIndex, out var lHip) && lHip != null &&
                boneByJoint.TryGetValue(rHipJointIndex, out var rHip) && rHip != null)
                right = rHip.position - lHip.position;

            if (rigUp.sqrMagnitude < 1e-8f) rigUp = Vector3.up;
            if (right.sqrMagnitude < 1e-8f) right = Vector3.right;

            Vector3 rigForward = Vector3.Cross(right.normalized, rigUp.normalized);
            if (rigForward.sqrMagnitude < 1e-8f)
                return false;

            forward = rigForward.normalized;
            up = rigUp.normalized;
            return true;
        }

        /// <summary>
        /// One-shot rig sanity report so any FBX character can be validated at a glance: avatar/humanoid status,
        /// which Move joints failed to bind, and any required humanoid bones the avatar is missing.
        /// </summary>
        void VerifyRig(bool humanoid)
        {
            if (characterAnimator == null)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: no Animator on character — using bone-name matching only. " +
                                 "Set the FBX Rig → Animation Type = Humanoid for reliable retargeting on any character.");
            else if (!humanoid)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: Animator present but not Humanoid (avatar null or generic). " +
                                 "Set Rig → Animation Type = Humanoid and Apply so bones map regardless of names.");

            // Move joints that should drive a bone but didn't bind.
            var unbound = new List<string>();
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                string n = asset.pose.jointNames[j];
                if (MoveToHumanBone.ContainsKey(n) && !boneByJoint.ContainsKey(j))
                    unbound.Add(n);
            }
            if (unbound.Count > 0)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: Move joints with no bound bone (rig is missing these): " +
                                 string.Join(", ", unbound));

            // Required humanoid bones the avatar itself lacks (only meaningful for humanoid rigs).
            if (humanoid)
            {
                var missing = new List<string>();
                foreach (var kv in MoveToHumanBone)
                    if (characterAnimator.GetBoneTransform(kv.Value) == null)
                        missing.Add(kv.Value.ToString());
                if (missing.Count > 0)
                    Debug.Log("[FusedCharacterPlayer] Rig check: optional humanoid bones not present on this avatar: " +
                              string.Join(", ", missing));
                else
                    Debug.Log("[FusedCharacterPlayer] Rig check: humanoid avatar has all mapped bones. Good to go.");
            }
        }

        /// <summary>
        /// Bind any still-unbound Move joints by matching common bone-name conventions (rig-name independent).
        /// Joints are processed most-specific first and each rig transform is claimed once, so e.g. "forearm"
        /// can't get stolen by the upper-arm match.
        /// </summary>
        void ResolveBonesByConvention(Transform[] transforms)
        {
            // Already-claimed transforms (from humanoid / name-map pass) must not be reused.
            var claimed = new HashSet<Transform>();
            foreach (var kv in boneByJoint) claimed.Add(kv.Value);

            // Normalized (lowercase, separators stripped) name for each candidate transform.
            var norm = new List<KeyValuePair<string, Transform>>(transforms.Length);
            foreach (var tr in transforms)
                norm.Add(new KeyValuePair<string, Transform>(Norm(tr.name), tr));

            // Map Move joint name -> index, for quick "is this already bound" checks.
            var indexByName = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
                if (!indexByName.ContainsKey(asset.pose.jointNames[j]))
                    indexByName[asset.pose.jointNames[j]] = j;

            foreach (var moveName in ConventionOrder)
            {
                if (!indexByName.TryGetValue(moveName, out int j)) continue;
                if (boneByJoint.ContainsKey(j)) continue;
                if (!ConventionAliases.TryGetValue(moveName, out var aliases)) continue;

                Transform best = MatchByAliases(norm, claimed, aliases);
                if (best != null)
                {
                    boneByJoint[j] = best;
                    claimed.Add(best);
                }
            }
        }

        static Transform MatchByAliases(List<KeyValuePair<string, Transform>> norm, HashSet<Transform> claimed, string[] aliases)
        {
            // Pass 1: exact normalized equality. Pass 2: endswith (prefix rigs). Pass 3: contains (suffix rigs).
            foreach (var a in aliases)
                foreach (var kv in norm)
                    if (!claimed.Contains(kv.Value) && kv.Key == a) return kv.Value;
            foreach (var a in aliases)
                foreach (var kv in norm)
                    if (!claimed.Contains(kv.Value) && kv.Key.EndsWith(a, System.StringComparison.Ordinal)) return kv.Value;
            foreach (var a in aliases)
                if (a.Length >= 6)
                    foreach (var kv in norm)
                        if (!claimed.Contains(kv.Value) && kv.Key.Contains(a)) return kv.Value;
            return null;
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Most-specific first so longer joints claim their transform before shorter/ambiguous ones.
        static readonly string[] ConventionOrder =
        {
            "Left_toe","Right_toe","Left_ankle","Right_ankle","Left_knee","Right_knee","Left_hip","Right_hip",
            "Left_wrist","Right_wrist","Left_elbow","Right_elbow","Left_shoulder","Right_shoulder",
            "Left_shoulder_rotation","Right_shoulder_rotation","Left_clavicle","Right_clavicle",
            "Spine3","Spine2","Spine1","Neck","Head","Root",
        };

        static readonly Dictionary<string, string[]> ConventionAliases = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Root", new[]{ "hips","pelvis","root","cog" } },
            { "Spine1", new[]{ "spine1","spine01","spine","abdomenlower" } },
            { "Spine2", new[]{ "spine2","spine02","chest","abdomenupper" } },
            { "Spine3", new[]{ "spine3","spine03","upperchest" } },
            { "Neck", new[]{ "neck1","neck" } },
            { "Head", new[]{ "head" } },
            { "Left_clavicle", new[]{ "leftshoulder","leftclavicle","claviclel","leftcollar","collarleft","lcollar" } },
            { "Right_clavicle", new[]{ "rightshoulder","rightclavicle","clavicler","rightcollar","collarright","rcollar" } },
            { "Left_shoulder", new[]{ "leftupperarm","leftarm","upperarmleft","upperarml","luparm","armleft" } },
            { "Right_shoulder", new[]{ "rightupperarm","rightarm","upperarmright","upperarmr","ruparm","armright" } },
            { "Left_shoulder_rotation", new[]{ "leftupperarm","leftarm","upperarmleft","upperarml" } },
            { "Right_shoulder_rotation", new[]{ "rightupperarm","rightarm","upperarmright","upperarmr" } },
            { "Left_elbow", new[]{ "leftforearm","leftlowerarm","lowerarmleft","forearmleft","forearml","loarml" } },
            { "Right_elbow", new[]{ "rightforearm","rightlowerarm","lowerarmright","forearmright","forearmr" } },
            { "Left_wrist", new[]{ "lefthand","handleft","leftwrist","wristleft","handl","wristl" } },
            { "Right_wrist", new[]{ "righthand","handright","rightwrist","wristright","handr","wristr" } },
            { "Left_hip", new[]{ "leftupleg","leftupperleg","upperlegleft","leftthigh","thighleft","upperlegl","thighl","lthigh" } },
            { "Right_hip", new[]{ "rightupleg","rightupperleg","upperlegright","rightthigh","thighright","upperlegr","thighr" } },
            { "Left_knee", new[]{ "leftlowerleg","lowerlegleft","leftcalf","calfleft","leftshin","shinleft","leftleg","lowerlegl","calfl","shinl" } },
            { "Right_knee", new[]{ "rightlowerleg","lowerlegright","rightcalf","calfright","rightshin","shinright","rightleg","lowerlegr" } },
            { "Left_ankle", new[]{ "leftfoot","footleft","leftankle","ankleleft","footl","anklel" } },
            { "Right_ankle", new[]{ "rightfoot","footright","rightankle","ankleright","footr","ankler" } },
            { "Left_toe", new[]{ "lefttoebase","lefttoes","lefttoe","toebaseleft","toeleft","ltoe","balll" } },
            { "Right_toe", new[]{ "righttoebase","righttoes","righttoe","toebaseright","toeright","rtoe","ballr" } },
        };

        // Move biomechanical joint -> Unity Humanoid bone. Drives any humanoid rig regardless of bone names.
        static readonly Dictionary<string, HumanBodyBones> MoveToHumanBone = new Dictionary<string, HumanBodyBones>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Root", HumanBodyBones.Hips },
            { "Left_hip", HumanBodyBones.LeftUpperLeg }, { "Left_knee", HumanBodyBones.LeftLowerLeg }, { "Left_ankle", HumanBodyBones.LeftFoot }, { "Left_toe", HumanBodyBones.LeftToes },
            { "Right_hip", HumanBodyBones.RightUpperLeg }, { "Right_knee", HumanBodyBones.RightLowerLeg }, { "Right_ankle", HumanBodyBones.RightFoot }, { "Right_toe", HumanBodyBones.RightToes },
            { "Spine1", HumanBodyBones.Spine }, { "Spine2", HumanBodyBones.Chest }, { "Spine3", HumanBodyBones.UpperChest }, { "Neck", HumanBodyBones.Neck }, { "Head", HumanBodyBones.Head },
            { "Left_clavicle", HumanBodyBones.LeftShoulder }, { "Left_shoulder", HumanBodyBones.LeftUpperArm }, { "Left_shoulder_rotation", HumanBodyBones.LeftUpperArm },
            { "Left_elbow", HumanBodyBones.LeftLowerArm }, { "Left_wrist", HumanBodyBones.LeftHand },
            { "Right_clavicle", HumanBodyBones.RightShoulder }, { "Right_shoulder", HumanBodyBones.RightUpperArm }, { "Right_shoulder_rotation", HumanBodyBones.RightUpperArm },
            { "Right_elbow", HumanBodyBones.RightLowerArm }, { "Right_wrist", HumanBodyBones.RightHand },
        };

        /// <summary>Cache pelvis/spine/hip indices and the primary mapped child per joint for the swing solver.</summary>
        void BuildRetargetTopology()
        {
            primaryChild.Clear();
            var pose = asset.pose;
            rootJointIndex = Mathf.Max(0, pose.IndexOfJoint("Root"));
            spineJointIndex = pose.IndexOfJoint("Spine1");
            if (spineJointIndex < 0) spineJointIndex = pose.IndexOfJoint("Spine");
            lHipJointIndex = pose.IndexOfJoint("Left_hip");
            rHipJointIndex = pose.IndexOfJoint("Right_hip");

            for (int j = 0; j < pose.jointNames.Count; j++)
            {
                int best = -1;
                bool bestPreferred = false;
                for (int c = 0; c < pose.jointParents.Count; c++)
                {
                    if (pose.jointParents[c] != j) continue;
                    if (!boneByJoint.ContainsKey(c)) continue;
                    // Prefer the spine/neck/head continuation so the torso aims up, not into an arm.
                    string cn = pose.jointNames[c];
                    bool preferred = cn.IndexOf("Spine", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     cn.IndexOf("Neck", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     cn.IndexOf("Head", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (best < 0 || (preferred && !bestPreferred))
                    {
                        best = c;
                        bestPreferred = preferred;
                    }
                }
                primaryChild[j] = best;
            }

            ConfigurePostProcessing(pose);
        }

        /// <summary>Allocate the glitch/smoothing filters for this skeleton, cache foot joint indices, and wire the
        /// AR surface probe. Called whenever the rig/asset is (re)bound.</summary>
        void ConfigurePostProcessing(MoveMotion pose)
        {
            postProcessor.Configure(pose.JointCount, pose.jointParents);
            postProcessor.Reset();

            var feet = new List<int>();
            var wallContacts = new List<int>();
            foreach (var name in new[] { "Left_ankle", "Right_ankle", "Left_toe", "Right_toe" })
            {
                int idx = pose.IndexOfJoint(name);
                if (idx >= 0)
                {
                    feet.Add(idx);
                    wallContacts.Add(idx);
                }
            }
            foreach (var name in new[] { "Left_wrist", "Right_wrist", "Left_hand", "Right_hand" })
            {
                int idx = pose.IndexOfJoint(name);
                if (idx >= 0 && !wallContacts.Contains(idx))
                    wallContacts.Add(idx);
            }
            footWorldIndices = feet.ToArray();
            wallContactWorldIndices = wallContacts.ToArray();

            // Map each limb tip to its world-joint index for the wall-contact lock (wrist/ankle, with toe/hand fallbacks).
            contactJointTable = new[]
            {
                new WallProjectionResolver.ContactJoint { limb = WallProjectionResolver.ClimbLimb.LeftHand, worldIndex = FirstJointIndex(pose, "Left_wrist", "Left_hand") },
                new WallProjectionResolver.ContactJoint { limb = WallProjectionResolver.ClimbLimb.RightHand, worldIndex = FirstJointIndex(pose, "Right_wrist", "Right_hand") },
                new WallProjectionResolver.ContactJoint { limb = WallProjectionResolver.ClimbLimb.LeftFoot, worldIndex = FirstJointIndex(pose, "Left_toe", "Left_ankle") },
                new WallProjectionResolver.ContactJoint { limb = WallProjectionResolver.ClimbLimb.RightFoot, worldIndex = FirstJointIndex(pose, "Right_toe", "Right_ankle") },
            };
            wallProjection.Reset();

            if (surfaceProbe == null)
                surfaceProbe = FindAnyObjectByType<ARSurfaceProbe>();
            penetrationResolver.SetProbe(surfaceProbe);
            if (surfaceProbe != null && routeRootProvider != null && routeRootProvider.RouteRoot != null)
                surfaceProbe.SetRouteRoot(routeRootProvider.RouteRoot);
        }

        /// <summary>First matching joint index for the given names, or -1 when none are present in the pose.</summary>
        static int FirstJointIndex(MoveMotion pose, params string[] names)
        {
            foreach (var n in names)
            {
                int idx = pose.IndexOfJoint(n);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        static Transform FindBone(Dictionary<string, Transform> byName, string boneName)
        {
            if (byName.TryGetValue(boneName, out var exact)) return exact;
            // Suffix match (e.g. "mixamorig:Hips" vs "Hips").
            foreach (var kv in byName)
            {
                if (kv.Key.EndsWith(boneName, System.StringComparison.OrdinalIgnoreCase) ||
                    boneName.EndsWith(kv.Key, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        void SetCharacterVisible(bool visible)
        {
            if (characterRoot != null && characterRoot.gameObject.activeSelf != visible)
                characterRoot.gameObject.SetActive(visible);
        }
    }
}
