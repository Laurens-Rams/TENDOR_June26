# TENDOR test plan

Two layers: **(A) automated editor checks** (fast, no device) and **(B) on-device manual flow** (the things that only happen at runtime — ARKit, async GLB load, world placement).

---

## A. Automated editor checks (run before every build)

| Menu | What it proves |
|------|----------------|
| `TENDOR ▸ GLB ▸ Validate Pipeline` | glTFast present; each `.glb` builds a valid Humanoid avatar; Move clip exists & has length; body + finger bones resolve; a muscle-space retarget runs and **moves the target's fingers**; scene has `FusedCharacterPlayer` + `MoveAIFusionCoordinator`. |
| `TENDOR ▸ GLB ▸ Retarget Test...` | Visual check: builds Move-source + Avaturn in the scene, press Play to see body + fingers retargeted at matched height. |
| `TENDOR ▸ Move AI ▸ Parse + Bake Bundled AppData Sample` | Move motion parsing + fusion bake work on a known-good sample (no API call). |

Pass criteria: `Validate Pipeline` reports **0 failed**. Warnings are OK to triage (e.g. "no scene open").

---

## B. On-device manual flow

Runs on iPhone (ARKit + Immersal/world map + Move AI). Covers what edit mode can't.

### B1. Record → fuse
1. Open the app, let it localize (RouteRoot/world map ready).
2. Record a short climb (~10–20 s) with the paired video.
3. Stop. Confirm status moves through `Submitting → Move AI % → Baking fusion → Fused replay ready`.
4. Confirm on device: `MoveAIFusion/<rec>.fusion.json` **and** `MoveAIGLB/<rec>.glb` both exist (Xcode ▸ Devices ▸ app container).

### B2. GLB retarget playback (the new path)
5. Play the recording. Watch the Console / status: `Loading Move AI GLB…` → `Playing fused replay`.
6. Expect log: `[MoveGlbSource] Loaded ... (clip ..., Ns)` and `[FusedCharacterPlayer] Move GLB articulation active`.
7. **Body**: limbs follow the climb; no T-pose, no exploded/flipped joints.
8. **Fingers**: hands open/close/grip (this is the GLB win over the procedural path).
9. **Facing**: character faces the same way as the orange/cyan compare skeletons. If reversed/yawed, that's the one-time Move→RouteRoot yaw alignment — see Tuning below.
10. **Position + scale**: hips track the orange skeleton; height matches; character stays anchored on the wall as you move the phone.

### B3. ARKit-dropout resilience
11. During playback, briefly occlude / move so ARKit tracking degrades.
12. Character keeps moving (Move motion continues) and re-anchors cleanly when tracking returns — it must not freeze or jump to the origin.

### B3b. Anchor mode: legs don't drift while still
1. Replay a clip with a clearly STILL moment (a rest/standing pose).
2. On `FusedCharacterPlayer`, set `Anchor Settings ▸ Mode = FollowArkit` (old behavior). Watch the feet/legs against a fixed wall feature during the still moment — note any sliding/creeping.
3. Switch `Anchor Settings ▸ Mode = MoveAIDriftCorrected`. The feet/legs should stop drifting while still, and still track real motion when the climber moves; loop restarts still snap (no fling).
4. If the character lags its true position or pops on a re-sync, raise `Resync Blend Seconds` / lower `Max Drift Meters`; if it ignores real position too long, raise `Stillness Velocity` or lower `Resync Interval Seconds`. `Correct XZ Only` keeps Move's vertical (ground contact) while still fixing horizontal drift.

### B3c. Pose smoothing (jitter vs jumps)
1. On `FusedCharacterPlayer`, toggle `Enable Pose Smoothing`.
2. ON: a held/still pose should lose its high-frequency shimmer (hands, head, fingers). A quick reach or a jump should stay crisp — not laggy or "swimmy".
3. Tune `Post Process Settings ▸ Min Cutoff` (lower = less jitter, more lag while slow) and `Beta` (higher = less lag while fast). `Jump Velocity Threshold` / `Jump Beta Scale` control how aggressively smoothing backs off during a jump.

### B3d. Wall/floor penetration + closest-hand IK
1. Run `TENDOR ▸ Post-Processing ▸ Setup Pose Post-Processing` once to add + wire the `ARSurfaceProbe`, then save the scene.
2. `Enable Penetration Fix` ON. Replay a climb where the closest hand clipped INTO the wall: the hand should sit on the wall surface while the rest of the body stays put (two-bone IK on that arm only). Toggle off to compare.
3. Wall IK uses the RouteRoot Z=0 plane by default; for real geometry set the probe's `AR Mesh Layer Mask` to an ARMeshManager mesh-collider layer.
4. Floor fix is OFF by default — first calibrate the probe's `Floor Local Y` to the real floor height in RouteRoot space, then enable `Penetration Settings ▸ Enable Floor Fix`. Standing feet should then rest on the floor (not sunk in / floating).
5. `Penetration Settings ▸ Debug Draw` shows red penetration vectors, yellow surface normals, and a green/grey floor line in the Scene view.

### B4. Fallback precedence (must all still work)
13. **No GLB, has fusion**: delete/rename the `.glb`, replay → procedural fused retarget (body only, no fingers). No errors.
14. **No fusion**: a recording with neither → dot/line skeleton playback (legacy `BodyTrackingPlayer`).
15. **FBX character**: select the FBX model in `CharacterSwitcher` → plays via the same path (procedural retarget). The FBX path is unchanged.

### B5. Character switching
16. Mid-playback, cycle characters (FBX ↔ Avaturn GLB). Each rebinds live, keeps playing, re-fits height, and re-applies GLB articulation if a Move GLB is loaded.

### B6. Pause / seek / loop
17. Pause holds the pose anchored; seek jumps correctly; loop wraps without drift or accumulating twist.

---

## Tuning knobs (if B2 looks off)

On `FusedCharacterPlayer`:
- `Articulation Source`: `MoveGlb` (default) vs `Procedural` (force the old path).
- `Invert Facing` / `Flip Character Forward`: facing parity vs the compare skeletons.
- `Height Fit Mode` + `Skeleton Fit Scale`: size against the orange skeleton.
- `Anchor Settings ▸ Mode`: `MoveAIDriftCorrected` (default; legs don't drift while still) vs `FollowArkit` (old). Knobs: `Resync Interval Seconds`, `Max Drift Meters`, `Resync Blend Seconds`, `Stillness Velocity`, `Correct XZ Only`.

On `MoveAIFusionCoordinator`:
- `Prefer Glb Articulation`: master switch for the GLB path.
- `Show Compare Skeletons`: cyan ARKit + orange Move overlay for visual diffing.

---

## Known runtime-only risks to watch

- **Compression**: if a future Move GLB ships Draco/KTX/meshopt, runtime load fails — add `com.unity.cloud.draco` / `com.unity.cloud.ktx`. (Current Move + Avaturn GLBs need neither.)
- **Yaw double-apply**: facing comes from the fused basis with a one-time yaw align; validate against the orange skeleton on the first GLB replay.
- **Async/IL2CPP**: playback start is gated on the GLB load `Task` completing; confirm no hang on a cold first load.
