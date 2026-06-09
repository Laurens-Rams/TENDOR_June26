using UnityEngine;
using BodyTracking.Animation;
using BodyTracking.Data;
using BodyTracking.MoveAI;
using BodyTracking.Spatial;
using BodyTracking.Storage;

namespace BodyTracking.Playback
{
    /// <summary>
    /// One overlaid recording rendered as its own full character during multi-recording playback. Owns a
    /// dedicated <see cref="FusedCharacterPlayer"/> plus a cloned display rig, driven by the master clock in
    /// <see cref="MultiRecordingPlayback"/> (the instance is kept internally paused and seeked every frame so
    /// it stays locked to the primary timeline and holds its last frame when shorter than the primary clip).
    ///
    /// Overlays require a baked Move AI fusion asset (full character). Recordings without one are skipped by
    /// the engine — the dot-skeleton fallback only exists for the single primary playback path.
    /// </summary>
    public class PlaybackInstance
    {
        public string FileName { get; private set; }
        public bool IsActive { get; private set; }
        /// <summary>Which GLB character index this overlay was cloned from (-1 = switcher default).</summary>
        public int CharacterIndex { get; private set; }

        private FusedCharacterPlayer player;
        private GameObject hostGo;
        private Transform rig;

        /// <summary>
        /// Build a ready, started overlay instance, or null when it can't be created (no fusion asset, no
        /// recording on disk, or no display rig available to clone).
        /// </summary>
        public static PlaybackInstance Create(string fileName, Transform parent,
            MoveAIFusionCoordinator coordinator, CharacterSwitcher switcher, IRouteRootProvider provider,
            int characterIndex)
        {
            if (string.IsNullOrEmpty(fileName) || coordinator == null)
                return null;
            if (!MoveAIFusionCoordinator.CanPlayFused(fileName))
            {
                Debug.Log($"[PlaybackInstance] '{fileName}' has no fused asset — overlay character skipped.");
                return null;
            }

            var recording = RecordingStorage.LoadRecording(fileName);
            if (recording == null || !recording.IsValid)
                return null;

            Transform clonedRig = switcher != null ? switcher.CreateRigInstance(parent, characterIndex) : null;
            if (clonedRig == null)
            {
                Debug.LogWarning("[PlaybackInstance] No display rig available to clone for the overlay character.");
                return null;
            }

            var host = new GameObject("OverlayPlayer_" + fileName);
            host.transform.SetParent(parent, false);
            var fused = host.AddComponent<FusedCharacterPlayer>();

            // Match the primary character's full correction/look settings so overlays render identically — this
            // includes the wall/floor penetration + IK fix. The ARSurfaceProbe is a shared, stateless scene
            // service (auto-found per player), so overlays can run the same fix as the primary.
            var primary = coordinator.PrimaryPlayer;
            if (primary != null)
            {
                ApplyPrimarySettings(fused, primary);
            }
            else if (switcher != null)
            {
                fused.SetArticulationSource(switcher.DefaultArticulation);
            }
            fused.SetCharacterRoot(clonedRig);

            if (!coordinator.ConfigureAndStartFusedOn(fused, fileName, provider, recording))
            {
                Object.Destroy(host);
                Object.Destroy(clonedRig.gameObject);
                return null;
            }

            // The master clock owns time: freeze the player's own advance and drive it purely via Seek so it
            // stays synced to the primary and clamps to its own last frame when shorter.
            fused.PausePlayback();

            return new PlaybackInstance
            {
                FileName = fileName,
                CharacterIndex = characterIndex,
                player = fused,
                hostGo = host,
                rig = clonedRig,
                IsActive = true
            };
        }

        /// <summary>Lock the overlay to a master timeline position (clamped to this clip's last frame).</summary>
        public void Sync(float time)
        {
            if (player != null)
                player.SeekToTime(time);
        }

        /// <summary>
        /// Re-apply the primary character's correction/look settings to this overlay so every character on
        /// screen shares one consistent set of settings. Called whenever the live tuning UI edits the primary
        /// (the overlays would otherwise keep the stale copy taken at creation time).
        /// </summary>
        public void ApplyPrimarySettings(FusedCharacterPlayer primary)
        {
            if (player != null)
                ApplyPrimarySettings(player, primary);
        }

        /// <summary>
        /// Copy every tunable correction/look setting from <paramref name="primary"/> onto <paramref name="target"/>
        /// so every character on screen behaves identically — including the wall/floor penetration + IK fix (the
        /// AR surface probe is a shared scene service each player auto-finds). Playback speed is the only thing left
        /// out: it is owned by the master clock that drives all overlays.
        /// </summary>
        private static void ApplyPrimarySettings(FusedCharacterPlayer target, FusedCharacterPlayer primary)
        {
            if (target == null || primary == null)
                return;
            target.SetArticulationSource(primary.ArticulationMode);
            target.SetInvertFacing(primary.InvertFacing);
            target.PlaybackAnchorMode = primary.PlaybackAnchorMode;
            target.AnchorSettingsLive = primary.AnchorSettingsLive;
            target.EnablePoseSmoothing = primary.EnablePoseSmoothing;
            target.PostProcessSettings = primary.PostProcessSettings;
            target.SkeletonFitScale = primary.SkeletonFitScale;
            target.EnablePenetrationFix = primary.EnablePenetrationFix;
            target.PenetrationSettingsLive = primary.PenetrationSettingsLive;
        }

        /// <summary>Stop, hide and destroy the overlay character and its player.</summary>
        public void Dispose()
        {
            IsActive = false;
            if (player != null)
                player.StopPlayback();
            if (hostGo != null)
                Object.Destroy(hostGo);
            if (rig != null)
                Object.Destroy(rig.gameObject);
            player = null;
            hostGo = null;
            rig = null;
        }
    }
}
