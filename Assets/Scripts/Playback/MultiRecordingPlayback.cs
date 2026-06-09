using System.Collections.Generic;
using UnityEngine;
using BodyTracking.Animation;
using BodyTracking.MoveAI;
using BodyTracking.Spatial;
using BodyTracking.Diagnostics;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Plays several recordings overlapped in the scene. The scene's existing single-recording path drives the
    /// primary (timeline-owning) recording exactly as before; this engine adds one extra full character per
    /// additional enabled recording and keeps them locked to the primary's playhead every frame.
    ///
    /// This is the N-capable, future-proof core: it reconciles its live overlay characters to whatever the
    /// <see cref="RecordingSelection"/> model says is enabled, so new UI/automation only ever has to mutate the
    /// selection model. Overlays follow the primary playhead, so seeking/scrubbing/pausing/cycling all "just
    /// work" for every overlaid recording, and shorter clips hold their last frame.
    /// </summary>
    public class MultiRecordingPlayback : MonoBehaviour
    {
        private BodyTrackingController controller;
        private MoveAIFusionCoordinator coordinator;
        private CharacterSwitcher characterSwitcher;
        private IRouteRootProvider provider;
        private Transform overlayParent;

        private readonly List<PlaybackInstance> overlays = new List<PlaybackInstance>();

        /// <summary>Number of currently overlaid (non-primary) characters.</summary>
        public int OverlayCount => overlays.Count;

        public void Configure(BodyTrackingController owner, MoveAIFusionCoordinator fusion, CharacterSwitcher switcher)
        {
            controller = owner;
            coordinator = fusion;
            characterSwitcher = switcher;
        }

        private Transform OverlayParent()
        {
            if (overlayParent == null)
            {
                var go = new GameObject("RecordingOverlays");
                go.transform.SetParent(transform, false);
                overlayParent = go.transform;
            }
            return overlayParent;
        }

        private MoveAIFusionCoordinator Coordinator()
        {
            if (coordinator == null)
                coordinator = FindFirstObjectByType<MoveAIFusionCoordinator>(FindObjectsInactive.Include);
            return coordinator;
        }

        private CharacterSwitcher Switcher()
        {
            if (characterSwitcher == null)
                characterSwitcher = FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            return characterSwitcher;
        }

        /// <summary>
        /// Reconcile the live overlay characters to <paramref name="overlayFiles"/>: dispose ones no longer
        /// enabled and spawn ones newly enabled. Pass the route-root provider the primary is replaying under so
        /// overlays share the same wall anchor.
        /// </summary>
        public void SyncOverlays(IList<string> overlayFiles, IRouteRootProvider routeProvider)
        {
            using var _ = PerfSampler.Scope("Overlay.Reconcile");
            provider = routeProvider;
            var sel = RecordingSelection.Instance;

            // Remove overlays that are no longer enabled, or whose chosen character changed (rebuild with the
            // new rig). A character change disposes the old clone so the new index is reflected immediately.
            for (int i = overlays.Count - 1; i >= 0; i--)
            {
                bool stillEnabled = overlayFiles != null && overlayFiles.Contains(overlays[i].FileName);
                bool sameCharacter = stillEnabled &&
                    overlays[i].CharacterIndex == sel.GetCharacterIndex(overlays[i].FileName);
                if (!stillEnabled || !sameCharacter)
                {
                    overlays[i].Dispose();
                    overlays.RemoveAt(i);
                }
            }

            if (overlayFiles == null)
                return;

            // Add overlays that are newly enabled (or were just rebuilt for a character change).
            foreach (var fileName in overlayFiles)
            {
                if (HasOverlay(fileName))
                    continue;
                int characterIndex = sel.GetCharacterIndex(fileName);
                var instance = PlaybackInstance.Create(
                    fileName, OverlayParent(), Coordinator(), Switcher(), provider, characterIndex);
                if (instance != null)
                    overlays.Add(instance);
            }
        }

        /// <summary>
        /// Push the primary character's current correction/look settings onto every overlay so all characters
        /// playing at once stay in sync with live tuning edits (which only mutate the primary player directly).
        /// </summary>
        public void ApplyPrimarySettingsToOverlays()
        {
            var primary = Coordinator()?.PrimaryPlayer;
            if (primary == null)
                return;
            for (int i = 0; i < overlays.Count; i++)
                overlays[i].ApplyPrimarySettings(primary);
        }

        /// <summary>Tear down every overlay character (e.g. when playback stops).</summary>
        public void ClearOverlays()
        {
            for (int i = 0; i < overlays.Count; i++)
                overlays[i].Dispose();
            overlays.Clear();
        }

        private bool HasOverlay(string fileName)
        {
            for (int i = 0; i < overlays.Count; i++)
                if (overlays[i].FileName == fileName)
                    return true;
            return false;
        }

        void Update()
        {
            if (overlays.Count == 0)
                return;

            // Master clock = the primary playback timeline. When the primary isn't playing there is nothing to
            // follow; overlays simply hold their last seeked frame.
            if (controller == null || !controller.IsPlaying)
                return;

            float time = controller.PlaybackCurrentTime;
            // #region agent log
            PerfSampler.Begin("Overlay.Sync");
            // #endregion
            for (int i = 0; i < overlays.Count; i++)
                overlays[i].Sync(time);
            // #region agent log
            PerfSampler.End("Overlay.Sync");
            // #endregion
        }

        void OnDestroy()
        {
            ClearOverlays();
        }
    }
}
