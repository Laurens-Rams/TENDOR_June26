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

        [Header("Hold contact lock (climbing foot-lock)")]
        [Tooltip("Detect hands/feet that are on holds (near the wall + barely moving) and lock them to the wall " +
                 "surface for the duration of the contact, like a foot-plant in walking mocap. Removes hold jitter.")]
        public bool enableContactLock;
        [Tooltip("Snap the world joint onto the locked surface point (drives the procedural bone path directly). The " +
                 "GLB path is corrected with two-bone IK using the same target regardless of this flag.")]
        public bool snapWorldJoints;
        [Tooltip("A hand/foot counts as 'on a hold' only when within this depth (m) of the wall surface.")]
        public float contactDepthBand;
        [Tooltip("Stillness speed (m/s): below this (and near the wall) a limb latches onto a hold.")]
        public float contactStillnessSpeed;
        [Tooltip("Release speed (m/s): a latched limb lets go of the hold once it moves faster than this. Must be " +
                 "above the stillness speed (hysteresis band) so a gripping hand doesn't flicker on/off.")]
        public float contactReleaseSpeed;
        [Tooltip("Seconds to ease a contact lock / IK weight in, so grabbing a hold slides on smoothly.")]
        public float contactEaseSeconds;
        [Range(0f, 1f)]
        [Tooltip("Maximum strength of the contact lock / IK pull (1 = pin exactly to the hold).")]
        public float maxContactWeight;

        public static WallProjectionSettings Default => new WallProjectionSettings
        {
            enableSlabClamp = true,
            wallDepthOffset = 0f,
            wallSurfaceDepth = 0.04f,
            maxBodyDepth = 0.5f,
            enableContactLock = true,
            snapWorldJoints = true,
            contactDepthBand = 0.14f,
            contactStillnessSpeed = 0.08f,
            contactReleaseSpeed = 0.25f,
            contactEaseSeconds = 0.12f,
            maxContactWeight = 1f,
        };
    }
}
