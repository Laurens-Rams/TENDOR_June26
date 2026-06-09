using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// Tunables for <see cref="WallProjectionResolver"/> — the flat-wall depth slab + hold-contact locking that keeps
    /// the climbing character pinned to the wall instead of clipping into it or floating off it.
    /// </summary>
    [System.Serializable]
    public struct WallProjectionSettings
    {
        [Header("Depth slab (flat-wall prior)")]
        [Tooltip("Project every joint into a thin depth slab parallel to the wall, so nothing sinks behind the wall " +
                 "or floats unrealistically far out from it. The single biggest fix for 'clipping into the wall'.")]
        public bool enableSlabClamp;
        [Tooltip("Fine-tune (m) where the physical wall sits in RouteRoot-local Z. 0 = the RouteRoot Z=0 plane is the " +
                 "wall (the system's convention). Nudge if the map origin is slightly in front of / behind the real " +
                 "wall so the whole climber is offset in depth. Keep this matched to the ARSurfaceProbe wall offset.")]
        public float wallDepthOffset;
        [Tooltip("Wall skin depth (m): the surface holds sit on, in RouteRoot-local Z (out from the wall). Joints are " +
                 "never pushed closer to the wall than this, and contacts are snapped onto it.")]
        public float wallSurfaceDepth;
        [Tooltip("Maximum body depth (m): how far the FURTHEST joint (usually the hips) may sit out from the wall. " +
                 "~0.5 m suits general climbing; raise for steep/dynamic moves, lower for technical face climbing.")]
        public float maxBodyDepth;

        [Header("Snap onto holds (pull hands/feet onto the wall at real holds)")]
        [Tooltip("Pull a hand/foot ONTO the wall surface — but only where there is a real (pre-detected) hold under " +
                 "it. A limb with no hold beneath it is left where it is (floating), never stuck to a blank wall. " +
                 "This is purely geometric against the precomputed hold map: NO live speed/stillness gate.")]
        public bool enableContactLock;
        [Tooltip("Snap the world joint onto the locked hold point (drives the procedural bone path directly). The " +
                 "GLB path is corrected with two-bone IK using the same target regardless of this flag.")]
        public bool snapWorldJoints;
        [Tooltip("Snap depth band (m): a limb only latches onto a nearby hold when it is within this depth of the " +
                 "wall surface. A limb floating further out than this (e.g. reaching to the next hold) is left free.")]
        public float contactDepthBand;
        [Tooltip("Seconds to ease a hold snap / IK weight in, so grabbing a hold slides on smoothly.")]
        public float contactEaseSeconds;
        [Range(0f, 1f)]
        [Tooltip("Maximum strength of the hold snap / IK pull (1 = pin exactly to the hold).")]
        public float maxContactWeight;

        [Header("Walk-away release (leave the wall)")]
        [Tooltip("Let the climber step off the wall: when the WHOLE body moves farther than the release distance " +
                 "out from the wall, the slab clamp and contact lock turn OFF so a climber who walks away isn't " +
                 "snapped back onto the wall. The fix resumes once they come back near the wall.")]
        public bool enableWalkAwayRelease;
        [Tooltip("Release distance (m): once the CLOSEST body joint is farther than this out from the wall, the " +
                 "climber is treated as off the wall and the wall fix stops. Should be above Max body depth.")]
        public float wallReleaseDepth;
        [Tooltip("Re-engage distance (m): the closest body joint must come back within this depth for the wall fix " +
                 "to resume. Keep below the release distance so engaging/releasing doesn't flicker (hysteresis).")]
        public float wallReengageDepth;
        [Tooltip("Seconds to EASE the whole-body depth slab on/off as the climber engages or leaves the wall. The slab " +
                 "weight ramps over this time instead of snapping, so the body glides onto / off the wall instead of " +
                 "popping. 0 = instant (old behavior).")]
        public float wallEngageEaseSeconds;
        [Tooltip("Don't pin the body to the wall while the climber is STANDING ON THE FLOOR in front of it (feet at " +
                 "floor level and the hips sitting out past Max body depth = upright, not climbing). Stops the body " +
                 "snapping onto the wall while you just walk in front of it; the slab only engages once they're " +
                 "actually on the wall (hips close to it / feet off the ground).")]
        public bool enableFloorStandRelease;

        [Header("Hold map (OFFLINE pre-detection of holds)")]
        [Tooltip("Build the map of climbing holds AHEAD OF TIME by scanning the whole recording: wherever a hand/foot " +
                 "rests still on the wall long enough, record a hold there (reinforced on repeat grabs, scored by " +
                 "confidence). This is the only place speed/stillness is used — to find rests offline. Playback then " +
                 "just reads this map. A future ML/image detector can feed the very same map.")]
        public bool enableHoldDetection;
        [Tooltip("Detection stillness speed (m/s): while scanning the recording, a limb slower than this (and near " +
                 "the wall) is treated as resting on a hold. OFFLINE only — playback never uses limb speed.")]
        public float contactStillnessSpeed;
        [Tooltip("Detection release speed (m/s): a resting limb stops being a hold candidate once it moves faster " +
                 "than this. Kept above the stillness speed (hysteresis). OFFLINE only.")]
        public float contactReleaseSpeed;
        [Tooltip("Seconds a limb must stay still on the wall before it registers (or reinforces) a hold. Filters " +
                 "out brief touches / pass-throughs so only real rests become holds.")]
        public float holdDwellSeconds;
        [Tooltip("Merge radius (m, along the wall): a new observation within this distance of an existing hold " +
                 "reinforces it instead of creating a duplicate. ~ a hold's physical size.")]
        public float holdMergeRadius;
        [Tooltip("How far (m) from the wall a hand/foot may be and still count as a candidate hold during the OFFLINE " +
                 "full-recording detector. Intentionally wide so detection tolerates AR/Move depth error and shake.")]
        public float holdDetectionDepthBand;
        [Tooltip("Don't record a FOOT hold when the foot is within this height (m) of the detected floor — a foot on " +
                 "the ground is a floor stand, not a wall hold.")]
        public float holdFloorExclusionBand;
        [Tooltip("Search radius (m, along the wall): at playback a limb snaps onto the nearest known hold within this " +
                 "distance. About a hold's physical size — too big and a limb grabs a hold it isn't really on.")]
        public float holdSnapRadius;

        public static WallProjectionSettings Default => new WallProjectionSettings
        {
            enableSlabClamp = true,
            wallDepthOffset = 0f,
            wallSurfaceDepth = 0.04f,
            maxBodyDepth = 0.5f,
            enableContactLock = true,
            snapWorldJoints = true,
            contactDepthBand = 0.18f,
            contactEaseSeconds = 0.12f,
            maxContactWeight = 1f,
            enableWalkAwayRelease = true,
            wallReleaseDepth = 0.7f,
            wallReengageDepth = 0.5f,
            wallEngageEaseSeconds = 0.3f,
            enableFloorStandRelease = true,
            enableHoldDetection = true,
            // Offline detection is intentionally permissive: a few extra candidate holds are cheap (playback only
            // snaps when a limb is actually over one and close to the wall), while MISSING a hold means a real grab
            // can never snap. So keep dwell short and the detection band wide to catch more rests.
            contactStillnessSpeed = 0.12f,
            contactReleaseSpeed = 0.3f,
            holdDwellSeconds = 0.18f,
            holdMergeRadius = 0.12f,
            holdDetectionDepthBand = 0.45f,
            holdFloorExclusionBand = 0.18f,
            holdSnapRadius = 0.12f,
        };
    }
}
