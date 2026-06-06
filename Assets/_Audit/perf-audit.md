# TENDOR Performance and Logic Audit

Phase 1 discovery only. No code, scene, prefab, project setting, SDK, or package files were changed.

Severity key: P0 = critical/security/data integrity, P1 = high frame-budget or correctness risk, P2 = medium risk, P3 = lower priority cleanup.

## Fix Order

1. Gate idle ARKit pose polling in `BodyTrackingRecorder` - same-file recorder hot path, high idle/recording CPU impact, low regression risk.
2. Fix Move AI resume polling in `MoveApiClient` - same API system, de-risks interrupted jobs before broader retry work, low contract risk.
3. Guard/remove hot-path debug session logging - grouped playback/debug files, high fused-playback frame-budget impact, low user-facing risk.
4. Throttle UI transport refresh - same UI system, independent low-risk reduction in per-frame text churn.
5. ~~Gate BlazePose/video duplicate CPU-image work~~ - resolved by deleting the BlazePose validation pipeline entirely (no second CPU-image/ML path remains).
6. Fix timestamp frame lookup in `HipRecording.GetFrameAtTime()` - de-risks playback/fusion accuracy, gated because it alters cross-system timing behavior.
7. Fix hip/video `videoStartTimeOffset` alignment - highest data-integrity risk, gated because it changes the Move AI timing/data contract.
8. Remove serialized API keys from scenes and credentials storage - security critical, gated because it edits scene/credential assets and requires key rotation outside code.
9. Tune AR/Immersal/session lifecycle and map-switch policies - grouped spatial systems, gated where serialized fields, scene state, or cross-system localization behavior changes.
10. Review project settings, cameras, physics timestep, and texture importers - grouped asset/settings work, gated because it touches project settings, scenes, or bulk import policy.

## AR Foundation

### P0 - Duplicate AR camera CPU-image work during recording

- Status: ✅ Resolved by removal - the entire BlazePose validation pipeline (scripts, ONNX models, `com.unity.ai.inference` package, and the in-scene `BlazePosePipeline` GameObject) has been deleted. There is no longer a second CPU-image/ML path competing with AVPro during recording, so the suppression workaround was removed too.
- Original issue: `VideoRecorder.RecordFrame()` and `BlazePoseRunner.Update()` could both acquire and convert `XRCpuImage` while recording, with body tracking also active in the same AR session.
- Why it mattered: CPU-image conversion, rotation, `Texture2D.Apply()`, and ML inference competed for the same mobile frame budget — one of the largest recording-time costs on iPhone.

### P0 - ARKit body pose polling runs even when not recording

- Status: ✅ Completed - `BodyTrackingRecorder.Update()` now skips ARKit pose sampling while idle unless live skeleton visualization needs it; arming still polls explicitly.
- Issue: `BodyTrackingRecorder.Update()` calls `activeSource.TryGetCurrentPose(...)` every frame before checking `isRecording`. With `showSkeletonWhenNotRecording` false, the skeleton is hidden but the ARKit body trackable scan still runs.
- Why it matters: The recorder iterates AR human-body trackables and counts joints for the app lifetime while the recorder is alive. This burns CPU even in idle/localization states.
- Proposed fix: Gate pose polling behind `isRecording`, `showSkeletonWhenNotRecording`, or an explicit one-frame detection request. Keep `PollBodyDetection()` for the arming coroutine so body-wait behavior still works.

### P1 - AR image target overlay and update events run every frame

- Status: [ ] Pending - ✅ Overlay material texture/color/cull updates are now cached and axis diagnostics are development-only; per-frame pose/event policy remains to be reviewed.
- Issue: `ARImageTargetManager.Update()` calls `UpdateReferenceImageOverlay(currentTrackedImage)` and invokes `OnImageTargetUpdated` every frame while the tracked image is usable. Overlay refresh also reassigns material properties and repositions/reparents visuals.
- Why it matters: Marker tracking is often active while the user is idle or preparing to record, so this becomes steady per-frame transform/material/event work. Downstream route-root providers may copy marker pose every frame as a result.
- Proposed fix: Split state-change work from pose-follow work. Refresh overlay material only when texture/alpha/state changes, and only fire continuous pose events when a consumer actually needs live marker following.

### P1 - Body tracking accepts `TrackingState.Limited`

- Status: ✅ Completed - `ARKitBodyPoseSource` now selects only `TrackingState.Tracking` bodies for recording/detection input.
- Issue: `ARKitBodyPoseSource.TryGetBestTrackedBody()` skips only `TrackingState.None`; `TrackingState.Limited` bodies can be selected if joints report tracked.
- Why it matters: Limited body tracking can produce unstable or partial poses. This can arm recording too early and contaminate hip/fusion data.
- Proposed fix: Require `TrackingState.Tracking` for recording by default, or make Limited acceptance an explicit serialized option with UI/status feedback.

### P1 - Missing broad `ARSession.state` guards before capture and world-map operations

- Status: [ ] Pending - ✅ `VideoRecorder` now requires `ARSessionState.SessionTracking` before video start/frame capture; world-map/session reset guards remain pending.
- Issue: Camera capture and `ARWorldMapPersistence` save/load paths rely mostly on manager availability/subsystem checks. Robust session-state gating is not consistently visible before capture, pose, or world-map reset/apply operations.
- Why it matters: ARKit interruptions, relocalization, or permissions/session startup delays can cause silent failures, partial captures, or world-map reset during a bad state.
- Proposed fix: Add centralized AR-session readiness checks around recording start, video frame capture, world-map save/load, and localization-dependent operations. Defer or fail visibly when `ARSession.state` is not tracking.

### P2 - Plane and raycast managers are enabled without a clear lifecycle policy

- Status: [ ] Pending
- Issue: `ARRaycastManager` is wired in `Globals` but no app raycasts were found. `ARPlaneManager` appears present/enabled while detection is off, and `DebugVisualsController` only disables it when clean view hides debug visuals.
- Why it matters: AR managers can still maintain subsystem state or allocate/update trackables depending on platform and configuration. Unused managers add risk and noise.
- Proposed fix: Disable unused AR managers by default in the app scene, and enable only for features that actively need them. Keep debug toggles visual-only where possible.

### P2 - ARKit body tracking and occlusion configuration conflict remains fragile

- Status: [ ] Pending
- Issue: `BodyTrackingController.ApplyOcclusionForMode()` correctly disables `ARHumanBodyManager` during playback to allow occlusion-capable ARKit configurations, but `AROcclusionManager` remains present and may request depth during pose updates. (Note: the former `BlazePoseDepthLift` depth consumer has been removed.)
- Why it matters: ARKit selects one camera configuration satisfying active feature requests. Body tracking, depth, and occlusion requests can silently downgrade each other or fail, creating inconsistent performance and visuals.
- Proposed fix: Make mode ownership explicit: body tracking active only for recording/body-wait; occlusion/depth active only for playback or validation modes. Guard depth CPU-image requests when occlusion is unavailable or disabled.

### P3 - Scene wiring drift in older scenes

- Status: [ ] Pending
- Issue: Discovery found `TENDOR.unity` with `humanBodyManager` unwired and `NewVersion.unity` relying on runtime `GetComponent` fallback for some `ARCharacterOcclusion` references.
- Why it matters: Older or test scenes can break at runtime or produce misleading profiler results if used accidentally.
- Proposed fix: Treat `NewVersion.unity` as the canonical scene and either update, archive, or clearly label older scenes. Do not remove serialized fields without prefab/scene confirmation.

## Immersal

### P0 - Immersal developer token is serialized in scene/runtime storage

- Status: [ ] Deferred per user - do not edit serialized API keys/scenes yet.
- Issue: The SDK integration stores a developer token in scene YAML and the Immersal SDK/editor tooling can persist it to PlayerPrefs.
- Why it matters: Tokens in source control or PlayerPrefs are extractable and should be considered compromised for production.
- Proposed fix: Rotate exposed tokens, remove raw tokens from committed scenes, and load credentials from secure storage or a backend/proxy. For MVP builds, document the risk and keep secrets out of git.

### P1 - Localization state is polled every frame

- Status: [ ] Pending
- Issue: `ImmersalRouteRootProvider.Update()` calls `UpdateLocalizationState()` and reads SDK state every frame, including `ImmersalSDK.Instance`, tracking quality, success counts, pose agreement, and anchor correction logic.
- Why it matters: The confidence gate is sensible, but per-frame SDK polling and pose math are unnecessary when localization updates arrive slower than the render loop.
- Proposed fix: Throttle localization evaluation to a short interval, or update on new localization success count. Keep per-frame blend motion only while a realign blend is active.

### P1 - Runtime map switching can create main-thread pressure

- Status: [ ] Pending
- Issue: `ImmersalMapSwitcher` uses async SDK jobs, but waits with `yield return null`, removes all maps, then creates/downloads map data and visualization at runtime.
- Why it matters: Map creation and visualization download can hitch or drop localization during an active session, especially on mobile.
- Proposed fix: Keep the existing metadata pre-check, then show a blocking UI state during switching. Avoid switching while recording/playback is active, and consider disabling visualization download for production unless debug visuals are needed.

### P1 - Auto/manual realign can affect cross-system spatial consistency

- Status: ✅ Completed - `RealignToImmersal()` and map Load now refuse while recording or playback is active.
- Issue: `ImmersalRouteRootProvider` can move the room anchor via manual realign or configured stability modes. The controller suppresses auto-realign outside Ready mode, but manual realign remains available from UI.
- Why it matters: Anchor movement during recording/playback would invalidate timestamped pose data and make fused playback appear offset.
- Proposed fix: Keep auto-realign suppressed during recording/playback, and disable or confirm manual realign while any recording/playback/fusion state is active.

### P2 - `IsAvailable` and provider selection can trigger scene searches

- Status: [ ] Pending - ✅ `RouteRootManager` provider resolution is now throttled; Immersal map availability caching remains separate.
- Issue: Immersal availability and `RouteRootManager` provider selection can fall back to `FindAnyObjectByType` / `FindObjectsByType` when references are missing.
- Why it matters: Scene-wide searches are expensive if evaluated in frequent UI/status paths or per-frame manager updates.
- Proposed fix: Cache provider and map references in `Awake`/initialization, invalidate only on map switch, and avoid repeated scene searches in properties.

### P2 - Confidence thresholds may delay usable UX

- Status: [ ] Pending
- Issue: The provider requires strong tracking quality, several success counts, and pose agreement before reporting localized.
- Why it matters: This protects correctness, but users may spend extra time scanning without enough progress feedback.
- Proposed fix: Keep the gate for recording correctness, but expose progress signals in UI: tracking quality, successful localization count, and pose agreement state.

## AVPro

### P0 - Video capture conversion and rotation happen on the main thread

- Status: [ ] Pending
- Issue: `VideoRecorder.RecordFrame()` runs from `ARCameraManager.frameReceived`, acquires `XRCpuImage`, converts to RGBA, optionally rotates a full frame buffer, calls `Texture2D.Apply()`, then feeds AVPro.
- Why it matters: This is heavy per captured frame. At 30 fps and camera resolutions around 1920x1440, it can dominate CPU time and cause dropped AR frames.
- Proposed fix: Reduce capture resolution/frame rate where acceptable, skip rotation when not needed by the downstream service, and consider Unity jobs/Burst or native plugin support for rotation if capture must stay high resolution. Avoid running other CPU-image consumers at the same time.

### P1 - Start/stop guards exist, but prewarm can race first recording

- Status: [ ] Pending
- Issue: `VideoRecorder.StartRecording()` guards double-starts, but can fail when AVPro/prewarm is still busy and returns "try again".
- Why it matters: A failed video start while body recording continues creates missing or misaligned Move AI input.
- Proposed fix: Disable the record button or block body recording until `IsEncoderPrewarmed` is true or prewarm has definitively failed. If video capture fails, make the user-visible state explicit.

### P1 - MP4 timeout fallback can upload an incomplete file

- Status: ✅ Completed - `VideoRecorder` now waits for fallback MP4 file size stability before passing the path to upload callbacks.
- Issue: `VideoRecorder` waits for `CompletedFileWritingAction`, but after timeout it falls back to best-known paths. `MoveAIFusionCoordinator` then reads the file for upload.
- Why it matters: A partially flushed MP4 can fail Move ingestion with vague synchronization errors and waste upload time.
- Proposed fix: Before upload, validate file-size stability over two checks and, ideally, confirm the MP4 has a `moov` atom. Extend timeout for long recordings.

### P1 - Capture buffers stay allocated after stop

- Status: [ ] Pending
- Issue: `Texture2D` and `NativeArray` buffers are intentionally reused and only released on destroy or resolution change.
- Why it matters: This avoids churn, but keeps a large RGBA texture and rotation buffer resident after recording. On memory-constrained devices this can contribute to pressure after upload/playback.
- Proposed fix: Add a memory policy: keep buffers for a short reuse window or release them after upload/fusion completion when not recording soon.

### P2 - Photos export and Move upload are decoupled but still share storage pressure

- Status: [ ] Pending
- Issue: The app keeps the persistentDataPath MP4 for upload and optionally exports to Photos.
- Why it matters: Long recordings can quickly consume storage, especially alongside Move debug dumps and raw GLB outputs.
- Proposed fix: Add retention controls for old videos/debug assets and surface storage usage in debug UI.

### P2 - AVPro component disposal is mostly handled

- Status: [ ] Pending
- Issue: `VideoRecorder` unsubscribes frame callbacks, disposes `NativeArray`, and destroys textures in `OnDestroy`, but does not explicitly dispose the AVPro capture component beyond stopping capture.
- Why it matters: Scene unload is probably safe, but native encoder state can linger if capture teardown happens during app pause/quit.
- Proposed fix: Add pause/quit handling that stops capture cleanly and records failure state before scene unload or app suspension.

## Move AI

### P0 - Move API key is serialized in scene data

- Status: [ ] Deferred per user - do not edit serialized API keys/scenes yet.
- Issue: `MoveApiClient.apiKey` is a serialized field and discovery found a key serialized in `NewVersion.unity`.
- Why it matters: Any committed client-side API key should be treated as exposed. It can be extracted from repository history or device builds.
- Proposed fix: Rotate the key, remove it from committed scenes/prefabs, and proxy Move API calls through a backend for production. For internal builds, use an untracked local config and avoid logging credentials.

### P0 - Hip/video alignment metadata is not populated

- Status: ✅ Completed - leading ARKit trim now accumulates `videoStartTimeOffset`, Move processing includes `offset + duration`, and fusion samples motion at `recordingTime + offset`.
- Issue: `videoStartTimeOffset` exists in pose data and is used by fusion, but no assignment was found. `TrimRecordingLeadingInvalid()` re-zeros hip timestamps after removing leading invalid frames, while Move upload still uses a clip window starting at zero.
- Why it matters: Move motion and ARKit hip samples can be shifted relative to each other, causing fused replay to apply the wrong motion to the wrong AR frame.
- Proposed fix: Preserve the removed leading time as `recording.videoStartTimeOffset` or send `clipWindow.startTime` matching the hip trim. Log the offset during upload and bake.

### P1 - Resume polling requests outputs before jobs finish

- Status: ✅ Completed - `RedownloadPipeline()` now polls job status first and fetches output URLs only after `FINISHED`, matching the submit path.
- Issue: `RunPipeline()` correctly polls job status before requesting outputs, but `RedownloadPipeline()` queries `outputs { file { presignedUrl } }` while waiting.
- Why it matters: Move can return GraphQL errors for output files that do not exist yet, masking state and making resume noisy or unreliable.
- Proposed fix: Mirror the submit path: poll status-only until `FINISHED`, then request output URLs once.

### P1 - Network requests lack consistent timeout and retry policy

- Status: ✅ Completed - GraphQL, presigned PUT upload, and MOTION_DATA/GLB downloads now use explicit timeouts plus bounded exponential retry/backoff on transient network failures.
- Issue: Upload PUT has a timeout, but GraphQL POST and download requests do not consistently set request timeouts or retry transient failures with backoff.
- Why it matters: Mobile networks are lossy. A transient failure can fail the job path, leave stale status, or require manual retry.
- Proposed fix: Add bounded retry/backoff for createFile, createTake, createJob, status polls, and downloads. Set explicit timeouts and preserve resumable job IDs as early as possible.

### P1 - Full video and result payloads stay in memory

- Status: [ ] Pending
- Issue: `MoveAIFusionCoordinator` reads the whole MP4 into `byte[]`, then `UploadHandlerRaw` holds upload data. Motion ZIP and optional GLB are also stored as full byte arrays.
- Why it matters: Long/high-resolution climbs can create large memory spikes and possible OOM on mobile.
- Proposed fix: Add file-size limits and user feedback before upload. Investigate streaming upload support for presigned PUTs, or lower capture resolution/duration for mobile.

### P1 - Move motion parsing and debug extraction run on the main thread

- Status: [ ] Pending - ✅ Production builds no longer write MOTION_DATA debug dumps or JSON previews; moving parsing off-thread remains an architectural follow-up.
- Issue: `MoveMotionParser.ParseMotionDataZip()` unzips, reads JSON, parses, logs previews, and writes debug dumps from the main flow.
- Why it matters: Large response JSON can stall the app and generate storage churn on device.
- Proposed fix: Move unzip/parse/debug extraction to a background worker where Unity APIs are not required. Gate debug dumps behind a development flag or explicit user action.

### P2 - FAILED jobs are terminal without a user retry path

- Status: [ ] Pending
- Issue: Interrupted jobs resume, but failed jobs are skipped on future resume and there is no obvious runtime retry action.
- Why it matters: Transient network or server failures can strand a recording without fusion unless the user re-records or uses debug tools.
- Proposed fix: Add a "retry fusion" action that can either redownload by job ID or re-upload the paired video when available.

### P2 - Fused playback can use the wrong asset key

- Status: ✅ Completed - `BodyTrackingController.StartPlayback(recording)` now uses a fused asset only when `recording` is the current `lastRecording` associated with `lastRecordingFileName`.
- Issue: `BodyTrackingController.StartPlayback(recording)` checks `MoveAIFusionCoordinator.HasFusionAsset(lastRecordingFileName)` and passes `lastRecordingFileName`, even when a different recording argument is supplied.
- Why it matters: A mismatched recording/fusion asset can apply motion to the wrong clip and make debugging alignment impossible.
- Proposed fix: Thread the selected recording filename through playback APIs, or only allow fused playback when the requested recording matches `lastRecordingFileName`.

## General Unity

### P0 - API secrets are present in project assets

- Status: [ ] Deferred per user - do not edit serialized API keys/scenes yet.
- Issue: Move AI and Immersal credentials were found serialized in scene/project-owned assets.
- Why it matters: This is a security and billing risk independent of runtime performance.
- Proposed fix: Rotate exposed credentials immediately. Remove secrets from committed YAML and add a workflow for local-only credentials or backend proxying.

### P0 - Timestamp-based playback lookup is missing

- Status: ✅ Completed - `HipRecording.GetFrameAtTime()` now binary-searches actual frame timestamps and returns the nearest recorded frame.
- Issue: `HipRecording.GetFrameAtTime()` uses `floor(time * frameRate)` rather than searching actual frame timestamps. Recordings store real elapsed timestamps, and trimming/dropped frames make the nominal index inaccurate.
- Why it matters: Playback, scrub, compare overlays, and fused pose anchoring can select the wrong ARKit frame, especially after trims or frame drops.
- Proposed fix: Replace index math with a timestamp lookup, preferably binary search over `frames[i].timestamp`, returning the nearest or interpolated frame.

### P1 - Fused playback allocates and recomputes pose data per frame

- Status: [ ] Pending
- Issue: `FusedCharacterPlayer`, `PlaybackCompareVisualizer`, and `FusedPoseSolver` allocate arrays and recompute forward kinematics separately during fused playback.
- Why it matters: Per-frame allocations and duplicate FK work create GC pressure and hitches in the most visually important playback mode.
- Proposed fix: Reuse scratch arrays and share one solved pose per frame between character playback and compare visualization.

### P1 - Runtime debug logging remains in hot or near-hot paths

- Status: ✅ Completed - `DBG5f9dd8`/`DebugSessionLog` instrumentation now compiles only in editor/development builds, including the heavier fused playback diagnostic blocks.
- Issue: `Debug.Log` and `DebugSessionLog` calls remain in update/playback/solver code, including diagnostic string construction.
- Why it matters: Logging allocates and can be very expensive on device, even when throttled.
- Proposed fix: Wrap diagnostics in `#if DEVELOPMENT_BUILD || UNITY_EDITOR` or a runtime debug flag that defaults off for device builds.

### P1 - Default physics timestep is still 0.02

- Status: [ ] Pending
- Issue: `ProjectSettings/TimeManager.asset` uses Unity's default `Fixed Timestep: 0.02`.
- Why it matters: The app appears AR/animation driven with no meaningful `FixedUpdate` hot paths. A 50 Hz physics step is often unnecessary on mobile.
- Proposed fix: If physics is not gameplay-critical, consider a coarser timestep such as 0.0333 after testing. This touches project settings and should be approved before applying.

### P1 - Mobile rendering settings need targeted review

- Status: [ ] Pending
- Issue: Scene/settings discovery found HDR/MSAA-enabled cameras in some scenes, duplicate active cameras in `TENDOR.unity`, GPU skinning reported off, and Gamma color space.
- Why it matters: Mobile AR benefits from minimal camera/render passes, GPU skinning for characters, and carefully chosen color/quality settings.
- Proposed fix: Audit the canonical build scene only before changing serialized settings. Consider enabling GPU skinning, removing unused cameras, and validating Linear color space/quality settings on device.

### P1 - RouteRoot and provider selection do scene searches when references are null

- Status: ✅ Completed - `RouteRootManager` now resolves missing providers at startup and then retries missing references at most once per second.
- Issue: `RouteRootManager` and related providers use `FindAnyObjectByType` fallbacks in runtime paths.
- Why it matters: Occasional searches are acceptable at init, but per-frame or status-path searches can add CPU spikes and hide scene wiring problems.
- Proposed fix: Resolve dependencies once during initialization and fail visibly if required providers are missing. Re-resolve only after map switch or scene reload.

### P2 - UI updates run every frame for transport state

- Status: ✅ Completed - `BodyTrackingUI` now refreshes transport text/scrub state at 10 Hz instead of every frame.
- Issue: `BodyTrackingUI.Update()` updates transport time every frame and refreshes broader UI every tenth frame.
- Why it matters: This is likely lower cost than AR/video work, but it adds text/string churn while recording/playback is active.
- Proposed fix: Update time labels at a fixed UI rate such as 5-10 Hz, unless the scrubber is actively dragged.

### P2 - Texture/import settings and duplicate character assets need asset-level audit

- Status: [ ] Pending
- Issue: Texture meta scans show many assets at default compression settings, duplicate character texture folders, and no texture streaming on sampled character textures.
- Why it matters: Build size and runtime texture memory can be significant for mobile AR with skinned characters.
- Proposed fix: Do not bulk-edit importers blindly. Identify runtime-used character/material textures first, then apply mobile compression, max-size, mipmap, and Read/Write policies with visual QA.

### P2 - Object creation/destruction is mostly setup-time but can hitch

- Status: [ ] Pending
- Issue: Skeleton visualizers create primitives, line renderers, materials, and destroy colliders during setup or joint-count changes.
- Why it matters: Not a steady hot path, but repeated toggling or character switches can hitch on mobile.
- Proposed fix: Keep these debug visuals disabled in production or prewarm/pool them when debug overlays are required.

### P2 - Localization timeline behavior differs between players

- Status: [ ] Pending
- Issue: `BodyTrackingPlayer` continues advancing timeline while delocalized, while `FusedCharacterPlayer` freezes before advancing `playbackTime`.
- Why it matters: Users see different playback behavior depending on whether a fused asset exists.
- Proposed fix: Choose one policy and apply it consistently. For review before changing behavior, document whether playback should pause during localization loss or keep time moving while hidden.

## Phase 2 Approval Gate

No fixes have been applied. Recommended first fixes, in severity order:

1. Rotate/remove serialized API keys and move credentials out of committed assets.
2. Fix `videoStartTimeOffset` / clip-window alignment.
3. Replace `GetFrameAtTime()` index math with timestamp lookup.
4. Gate `BodyTrackingRecorder` pose polling.
5. Disable duplicate CPU-image consumers during recording.
6. Move/guard hot-path debug logging.
7. Fix Move AI resume polling and add network retry/timeouts.

Items touching public APIs, serialized fields, project settings, scenes, or cross-system behavior should be confirmed before Phase 2 changes.
