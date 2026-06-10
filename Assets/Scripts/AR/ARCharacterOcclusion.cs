using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AR
{
    /// <summary>
    /// Drives ARKit occlusion so the virtual character is correctly hidden behind real-world geometry
    /// (walls, holds, furniture) and real people, making it look like it actually occupies the space.
    ///
    /// The heavy lifting is done by AR Foundation: an <see cref="AROcclusionManager"/> produces per-pixel
    /// depth/stencil textures, and the URP "AR Background" renderer feature blits that depth into the camera
    /// depth buffer before opaque geometry is drawn. Any opaque (depth-writing) character material is then
    /// depth-tested against the real world for free. This component's job is to (a) request the best modes the
    /// device actually supports, (b) degrade gracefully on non-LiDAR phones (people-only occlusion), and
    /// (c) expose a clean runtime on/off toggle. <see cref="BodyTracking.BodyTrackingController"/> keeps this off
    /// while idle/recording (so ARKit body tracking works) and turns it on during animation playback only.
    /// </summary>
    [RequireComponent(typeof(AROcclusionManager))]
    public class ARCharacterOcclusion : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Occlusion manager on the AR camera. Auto-found on the same GameObject if left empty.")]
        [SerializeField] private AROcclusionManager occlusionManager;
        [Tooltip("AR camera manager on the same GameObject. Used to force BeforeOpaques background rendering.")]
        [SerializeField] private ARCameraManager cameraManager;
        [Tooltip("AR camera background on the same GameObject. Used to verify occlusion shader keywords at runtime.")]
        [SerializeField] private ARCameraBackground cameraBackground;

        [Header("Startup")]
        [Tooltip("Usually leave off — BodyTrackingController enables occlusion only during playback.")]
        [SerializeField] private bool occlusionEnabledOnStart = false;

        [Header("Environment depth (real objects / walls — needs LiDAR)")]
        [Tooltip("Quality of the LiDAR environment depth used to occlude the character behind real objects.")]
        [SerializeField] private EnvironmentDepthMode environmentDepthMode = EnvironmentDepthMode.Medium;
        [Tooltip("Temporally smooth environment depth to reduce edge flicker around the character. Recommended on.")]
        [SerializeField] private bool environmentDepthTemporalSmoothing = true;

        [Header("People occlusion (works on any A12+ device)")]
        [Tooltip("Stencil quality for occluding the character behind real people. Only active while occlusion is on (playback).")]
        [SerializeField] private HumanSegmentationStencilMode humanStencilMode = HumanSegmentationStencilMode.Fastest;
        [Tooltip("Depth quality for occluding the character behind real people.")]
        [SerializeField] private HumanSegmentationDepthMode humanDepthMode = HumanSegmentationDepthMode.Fastest;

        [Header("Preference")]
        [Tooltip("When both environment and human occlusion are available, which to prefer.")]
        [SerializeField] private OcclusionPreferenceMode preferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;

        [Header("Render pipeline")]
        [Tooltip("ARKit must render the camera background BeforeOpaques so LiDAR depth is in the depth buffer before the character draws. 'Any' lets the OS pick AfterOpaques, which often breaks occlusion on device.")]
        [SerializeField] private bool forceBeforeOpaquesBackground = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;
        [Tooltip("After capabilities are known, log whether the AR background material actually has occlusion keywords enabled (the usual reason nothing is hidden).")]
        [SerializeField] private bool logRenderPipelineDiagnostics = true;

        /// <summary>True while occlusion modes are being requested from the subsystem.</summary>
        public bool OcclusionEnabled { get; private set; }

        /// <summary>True while depth textures are requested without visual occlusion (LiDAR hip tracking).</summary>
        public bool DepthDataActive { get; private set; }

        private Coroutine capabilityLogRoutine;

        const string EnvironmentDepthKeyword = "ARKIT_ENVIRONMENT_DEPTH_ENABLED";
        const string HumanSegmentationKeyword = "ARKIT_HUMAN_SEGMENTATION_ENABLED";

        private void Awake()
        {
            if (occlusionManager == null)
                occlusionManager = GetComponent<AROcclusionManager>();
            if (cameraManager == null)
                cameraManager = GetComponent<ARCameraManager>();
            if (cameraBackground == null)
                cameraBackground = GetComponent<ARCameraBackground>();
        }

        private void Start()
        {
            SetOcclusionEnabled(occlusionEnabledOnStart);
        }

        private void OnDisable()
        {
            if (capabilityLogRoutine != null)
            {
                StopCoroutine(capabilityLogRoutine);
                capabilityLogRoutine = null;
            }
        }

        /// <summary>
        /// Turn character occlusion on or off at runtime. Disabling sets every mode to Disabled and the
        /// preference to NoOcclusion so the character always renders on top (useful for a clean capture).
        /// </summary>
        public void SetOcclusionEnabled(bool enabledNow)
        {
            if (occlusionManager == null)
            {
                Debug.LogWarning("[ARCharacterOcclusion] No AROcclusionManager — cannot toggle occlusion.");
                return;
            }

            OcclusionEnabled = enabledNow;
            DepthDataActive = false;

            if (enabledNow)
            {
                if (forceBeforeOpaquesBackground && cameraManager != null)
                    cameraManager.requestedBackgroundRenderingMode = CameraBackgroundRenderingMode.BeforeOpaques;

                occlusionManager.requestedEnvironmentDepthMode = environmentDepthMode;
                occlusionManager.environmentDepthTemporalSmoothingRequested = environmentDepthTemporalSmoothing;
                occlusionManager.requestedHumanStencilMode = humanStencilMode;
                occlusionManager.requestedHumanDepthMode = humanDepthMode;
                occlusionManager.requestedOcclusionPreferenceMode = preferenceMode;
            }
            else
            {
                occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;
                occlusionManager.environmentDepthTemporalSmoothingRequested = false;
                occlusionManager.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
                occlusionManager.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
                occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
            }

            if (verboseLogging)
            {
                if (capabilityLogRoutine != null)
                    StopCoroutine(capabilityLogRoutine);
                if (isActiveAndEnabled)
                    capabilityLogRoutine = StartCoroutine(LogCapabilitiesWhenReady(enabledNow));
            }
        }

        /// <summary>Convenience toggle for UI buttons.</summary>
        public void ToggleOcclusion() => SetOcclusionEnabled(!OcclusionEnabled);

        /// <summary>
        /// Request LiDAR environment depth + human segmentation CPU images WITHOUT rendering occlusion
        /// (<see cref="OcclusionPreferenceMode.NoOcclusion"/>): used while recording with the BlazePose+LiDAR
        /// hip source, which samples the depth/stencil images but must not visually occlude anything.
        /// Only valid while ARHumanBodyManager is disabled — ARKit's body-tracking camera configuration has
        /// no depth. Disable by calling <see cref="SetOcclusionEnabled"/> with the desired final state.
        /// </summary>
        public void SetDepthDataEnabled(bool enabledNow)
        {
            if (occlusionManager == null)
            {
                Debug.LogWarning("[ARCharacterOcclusion] No AROcclusionManager — cannot request depth data.");
                return;
            }

            if (!enabledNow)
            {
                SetOcclusionEnabled(false);
                return;
            }

            OcclusionEnabled = false;
            DepthDataActive = true;

            occlusionManager.requestedEnvironmentDepthMode = environmentDepthMode;
            // Raw (unsmoothed) depth: temporal smoothing trades latency for stability, and the hip source
            // medians its own samples — prefer the freshest depth for a moving climber.
            occlusionManager.environmentDepthTemporalSmoothingRequested = false;
            occlusionManager.requestedHumanStencilMode = humanStencilMode;
            occlusionManager.requestedHumanDepthMode = humanDepthMode;
            occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;

            Debug.Log("[ARCharacterOcclusion] Depth data requested without occlusion (LiDAR hip tracking).");
        }

        /// <summary>
        /// The occlusion subsystem reports its capabilities as Unknown until it has started, so we wait a few
        /// frames before logging what the device actually supports. Purely diagnostic.
        /// </summary>
        private IEnumerator LogCapabilitiesWhenReady(bool requestedEnabled)
        {
            for (int i = 0; i < 120; i++)
            {
                var descriptor = occlusionManager.descriptor;
                if (descriptor != null &&
                    descriptor.environmentDepthImageSupported != Supported.Unknown)
                {
                    bool env = descriptor.environmentDepthImageSupported == Supported.Supported;
                    bool stencil = descriptor.humanSegmentationStencilImageSupported == Supported.Supported;
                    bool humanDepth = descriptor.humanSegmentationDepthImageSupported == Supported.Supported;

                    string summary = !requestedEnabled
                        ? "requested OFF"
                        : env
                            ? "environment depth (full spatial occlusion) + " + (stencil || humanDepth ? "people occlusion" : "no people occlusion")
                            : (stencil || humanDepth)
                                ? "people occlusion only (no LiDAR environment depth on this device)"
                                : "NONE — this device cannot occlude the character";

                    Debug.Log($"[ARCharacterOcclusion] Occlusion ready: {summary}. " +
                              $"(envDepth={env}, humanStencil={stencil}, humanDepth={humanDepth})");

                    if (logRenderPipelineDiagnostics && requestedEnabled)
                    {
                        yield return LogRenderPipelineDiagnostics();
                    }

                    capabilityLogRoutine = null;
                    yield break;
                }
                yield return null;
            }

            Debug.LogWarning("[ARCharacterOcclusion] Occlusion subsystem capabilities stayed Unknown — " +
                             "running in Editor/AR Remote, or occlusion is unsupported on this device.");
            capabilityLogRoutine = null;
        }

        /// <summary>
        /// Descriptor support is not enough — occlusion only works when the AR background shader has
        /// ARKIT_ENVIRONMENT_DEPTH_ENABLED or ARKIT_HUMAN_SEGMENTATION_ENABLED at draw time.
        /// </summary>
        private IEnumerator LogRenderPipelineDiagnostics()
        {
            // Wait until ARKit has pushed at least one occlusion frame to the background material.
            for (int i = 0; i < 180; i++)
            {
                if (TryLogRenderPipelineDiagnostics())
                    yield break;
                yield return null;
            }

            Debug.LogWarning("[ARCharacterOcclusion] Render diagnostics: AR background never received occlusion " +
                             "shader keywords after 3s. Depth textures may not be bound — character will not hide behind the real world.");
        }

        private bool TryLogRenderPipelineDiagnostics()
        {
            if (cameraBackground == null || occlusionManager == null)
                return false;

            var mat = cameraBackground.material;
            if (mat == null)
                return false;

            bool envKeyword = mat.IsKeywordEnabled(EnvironmentDepthKeyword);
            bool humanKeyword = mat.IsKeywordEnabled(HumanSegmentationKeyword);
            if (!envKeyword && !humanKeyword)
                return false;

            var renderingMode = cameraManager != null
                ? cameraManager.currentRenderingMode.ToString()
                : "unknown";
            var envMode = occlusionManager.currentEnvironmentDepthMode;
            var humanDepthMode = occlusionManager.currentHumanDepthMode;
            var pref = occlusionManager.currentOcclusionPreferenceMode;

            var pipeline = GraphicsSettings.currentRenderPipeline;

            string keywordPath = envKeyword
                ? "ARKIT_ENVIRONMENT_DEPTH_ENABLED (LiDAR / walls / your hand in depth)"
                : "ARKIT_HUMAN_SEGMENTATION_ENABLED (people only — not general objects)";

            Debug.Log($"[ARCharacterOcclusion] Render path OK: backgroundMode={renderingMode}, " +
                      $"activeKeyword={keywordPath}, envDepthMode={envMode}, humanDepthMode={humanDepthMode}, " +
                      $"preference={pref}, backgroundRendering={cameraBackground.backgroundRenderingEnabled}, " +
                      $"urpPipeline={pipeline != null}. " +
                      "If the character still draws on top, check opaque materials (ZWrite) and hide debug skeletons.");

            if (renderingMode != nameof(XRCameraBackgroundRenderingMode.BeforeOpaques))
            {
                Debug.LogWarning("[ARCharacterOcclusion] Background is not BeforeOpaques — occlusion is unreliable. " +
                                 "Enable 'Force Before Opaques Background' on ARCharacterOcclusion or set ARCameraManager Rendering Mode to BeforeOpaques.");
            }

            return true;
        }
    }
}
