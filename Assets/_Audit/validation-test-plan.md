# Validation Test Plan — Perf Audit Fixes

**Build under test:** Development, Unity 6000.0.49f1, iOS device  
**Date started:** 2026-06-05  
**Tester:** _______________

Use this document step by step. Do not start the recording/playback tests until **Phase 0** passes — Immersal localization is required for the app's primary spatial frame.

---

## Quick status

| Phase | Name | Status |
|-------|------|--------|
| 0 | Localization + map switch | [x] **PASS** |
| 1 | Deploy / compile | [x] PASS |
| 2 | Recording smoke | [x] **PASS** — `hip_recording_20260605_230355` |
| 3 | Leading-trim / video offset | [~] **Trusted** — code in place; not re-testing |
| 6 | AR session video guard | [x] PASS |

**Focus now:** Priority checks below (unexpected failures that look fine until you hit them).

---

## Priority checks — unexpected & high impact

Work through in order. Each takes 2–5 minutes except Move fusion wait.

### P1 — Move AI fusion completes (your latest recording)

**Why:** Upload started in session 2; silent failure = no fused replay.

- [ ] Wait for Move AI strip: `Fused replay ready for 'hip_recording_20260605_230355'`
- [ ] Or console: `[MoveAIFusionCoordinator] … Fused replay ready`
- [ ] If failed: note `Move AI failed:` message (network retry should auto-retry transient errors)

**Unexpected failure:** Job stuck at 0% / timeout / FAILED with no fused file.  
**Result:** [ ] Pass  [ ] Fail  [ ] Still running

---

### P1 — Fused playback stays on the wall when you walk

**Why:** Most visible correctness bug — character drifts with camera instead of room.

**Prereq:** P1 fusion ready (or use skeleton-only playback as baseline).

1. [ ] **Locked — ready** on map 147190
2. [ ] Play `hip_recording_20260605_230355`
3. [ ] Walk 2–3 m left/right while replay runs
4. [ ] Ghost/character stays on the wall — does **not** follow the camera like a screen overlay

**Unexpected failure:** Replay slides along the wall or floats away when you move.  
**Result:** [ ] Pass  [ ] Fail

---

### P1 — Playback scrub (timestamp lookup)

**Why:** Wrong frame selection only shows when scrubbing — easy to miss in normal play.

1. [ ] Play `hip_recording_20260605_230355`
2. [ ] Drag scrub bar slowly through 0s → 5s → end
3. [ ] Pause, resume, scrub backward
4. [ ] Skeleton/hip does not **jump** to a clearly wrong pose mid-scrub

**Result:** [ ] Pass  [ ] Fail

---

### P1 — Wrong fusion asset not applied

**Why:** Fixed in code; wrong pairing would show another climber's motion on your recording.

1. [ ] In recording selector, pick an **older** recording (different session/map if possible)
2. [ ] Play it
3. [ ] Console should **not** use fused replay for a mismatched file (skeleton fallback is OK)
4. [ ] Fused replay only when `.fusion.json` exists **for that exact filename**

**Result:** [ ] Pass  [ ] Fail

---

### P2 — Map switch blocked during record/playback

**Why:** Switching maps mid-session invalidates spatial frame silently.

1. [ ] Start recording (or playback)
2. [ ] Try **Load map** with a different ID
3. [ ] Status should say: `Stop recording/playback before switching maps`
4. [ ] Active map unchanged

**Result:** [ ] Pass  [ ] Fail *(guard added in latest build — rebuild required)*

---

### P2 — Re-align blocked during record/playback

**Why:** Manual re-align moves the room anchor — would make replay look offset.

1. [ ] During playback, confirm **Re-align** button hidden or tap does nothing useful
2. [ ] If visible and tapped: `Stop recording/playback before re-aligning`

**Result:** [ ] Pass  [ ] Fail *(guard added in latest build — rebuild required)*

---

### P2 — App background during Move upload (optional)

**Why:** `runInBackground` should let polling continue; easy to break on iOS.

1. [ ] Start a new recording, stop, wait until Move shows `Uploading` or `Processing`
2. [ ] Home button / switch app 30s, return
3. [ ] Fusion either completes or shows clear failed status (not frozen forever)

**Result:** [ ] Pass  [ ] Fail  [ ] Skipped

---

### P3 — Skipped (low risk / trusted)

| Item | Status |
|------|--------|
| Leading-trim / `videoStartTimeOffset` | Trusted — not re-testing |
| BlazePose suppression during capture | Removed — BlazePose pipeline deleted entirely |
| Phase 1 batch Unity compile | Optional |

---

## Session log — 2026-06-05 ~22:41 (FAIL)

**What happened:**
| Observation | Meaning |
|-------------|---------|
| `Looking up map 147177…` at launch | App auto-restored **147177** from `PlayerPrefs` key `tendor_immersal_map_id` (saved from a prior session). Scene default is **147158**. |
| No `Looking up map 147190…` anywhere | Map **147190 was never submitted** in this log. Typing alone does not switch maps. |
| `ARSession did not reach SessionTracking; skipping Immersal init` | Immersal SDK never fully initialized — localization will be unreliable. |
| `Could not acquire camera intrinsics` | Camera busy/unstable (AVPro prewarm + AR reconfig at same time). |
| `Too few matches: 10 < 12` / `11 < 12` | Immersal almost matched but failed threshold; these attempts don't count toward lock. |
| `applicationWillResignActive` + `UIKeyboardImpl` | Opening the map ID keyboard paused AR — avoid typing until after initial localization attempt. |

**Phase 0 result:** FAIL — do not proceed to Phases 2–6 yet.

---

## Session log — 2026-06-05 ~23:00 (PASS)

| Observation | Result |
|-------------|--------|
| `Map 147190 loaded — scan to localize` | Map switch UI fix confirmed |
| `Saved map 147190 to PlayerPrefs` | Persistence confirmed |
| `Room anchor locked … successes=4` | Localization **PASS** |
| `hip_recording_20260605_230355` saved | Recording smoke **PASS** |
| `Video ready` + Move AI upload started | Video finalize **PASS** |
| `fusion offset 0.00s` | Expected — wait-for-body meant no leading trim |

**Phase 0 + 2 result:** PASS

**Corrected retry for map 147190:**
1. Force-quit app, relaunch.
2. **Wait 30s** without touching UI — point camera at textured wall. Confirm log shows `Immersal SDK initialized after AR tracking was ready` (NOT the SessionTracking skip warning).
3. Wait until top-left map label stops showing `…` (startup restore to 147177 finishes).
4. Open map panel → type `147190` → tap **Load map** (button enabled only when not switching).
5. Confirm console: `Looking up map 147190…` then `Map 147190 loaded — scan to localize`.
6. **Dismiss keyboard**, keep app foreground, scan 30–60s.

---

## Phase 0 — Unblock Immersal localization (BLOCKER)

> **Goal:** UI shows localization progressing and eventually **Locked — ready** (or at minimum stable "Aligning…" with successful matches). Without this, recording/playback tests run in the wrong spatial frame.

### 0.1 Pre-flight (device)

- [ ] **Fresh app launch** — force-quit the app, relaunch (do not resume from background).
- [ ] **Good lighting** — avoid glare, very dark corners, or pointing at blank walls.
- [ ] **Same physical room** as when map `147190` was scanned in Immersal Mapper.
- [ ] **Camera permissions** granted.
- [ ] **Network** available (map download + Immersal cloud lookup).

### 0.2 Confirm AR session reaches tracking

Watch Xcode / device console for ~30 seconds after launch.

- [ ] No persistent `FigCaptureSourceRemote` / `err=-12784` spam after the first few seconds.
- [ ] **Must NOT see** (after ~20s):  
  `[ImmersalDelayedInitializer] ARSession did not reach SessionTracking; skipping Immersal init.`
- [ ] **Should see** (if AR is healthy):  
  `[ImmersalDelayedInitializer] Immersal SDK initialized after AR tracking was ready.`

**If SessionTracking never arrives:**
1. Point phone at a textured wall or the printed image target for 10–15s.
2. Walk slowly left/right so ARKit can build features.
3. Toggle airplane mode off/on and relaunch if camera was stuck.
4. Retry in a brighter area of the same room.

**Result:** AR session healthy?  [ ] Yes  [ ] No  
**Notes:** _______________________________________________

### 0.3 Switch to map `147190`

> **Important:** On every launch the app auto-restores the last saved map from `PlayerPrefs` (`tendor_immersal_map_id`). That is why you saw **147177** even after typing 147190 in a prior session — you must tap **Load map** and see the log confirm 147190.

In the in-app map panel (top-left **Map —** button):

- [ ] Wait until startup map switch finishes (Load map button enabled; label not ending in `…`).
- [ ] Type `147190` in the input field.
- [ ] Tap **Load map** (not just Enter/keyboard dismiss).
- [ ] Status progresses: `Connecting to Immersal…` → `Looking up map 147190…` → `Downloading map…` → **`Map 147190 loaded — scan to localize`**
- [ ] **No error** like `Map 147190 not found for this account/token`
- [ ] Dismiss keyboard and keep app in foreground before scanning.

**If lookup fails:** the map id may belong to a different Immersal developer token than the one in the scene. Confirm the map exists in your Immersal cloud dashboard under the same account.

**Result:** Map 147190 loaded?  [ ] Yes  [ ] No  
**Notes:** _______________________________________________

### 0.4 Scan to localize

The app requires **multiple successful** Immersal localizations before locking (`minSuccessfulLocalizations = 4`, tracking quality ≥ 3). Failed attempts show as `Too few matches: X < 12` and **do not count**.

**Scan technique:**
1. Stand in the **same area** you stood when creating the map.
2. Hold phone at chest height, portrait.
3. **Pan slowly** across walls, corners, and distinctive features (not the floor or ceiling).
4. Cover roughly the same viewpoints as the original Immersal scan.
5. Keep moving for **30–60 seconds**; do not stand still on a blank surface.

**Console — good signs:**
- [ ] `Too few matches` stops appearing (or becomes rare).
- [ ] Immersal tracking quality rises (SDK-internal; UI pill may show "Aligning…").

**UI — success criteria:**
- [ ] Localization pill: **"Aligning…"** (amber dot) or better.
- [ ] Eventually: **"Locked — ready"** (green dot) — ideal for recording tests.

**If still stuck on scanning after 60s:**
- [ ] Verify you are in the **correct room** (map 147190's scan location).
- [ ] Try map `147177` temporarily — your log showed that map **did** download; if 147177 localizes but 147190 does not, the wrong map id or wrong room is likely.
- [ ] Rescan the room in Immersal Mapper and create a fresh map id.
- [ ] Check for `[ARFoundationSupport] Could not acquire camera intrinsics` — if persistent, AR session is unstable; fix Phase 0.2 first.

**Result:** Localized / locked?  [ ] Locked  [ ] Aligning only  [ ] Still scanning  
**Time to lock:** _______ seconds  
**Notes:** _______________________________________________

### Phase 0 gate

**Proceed to Phase 1 only if:**  [ ] Locked — ready  **OR**  [ ] Aligning with no SessionTracking / init errors

---

## Phase 1 — Automated / editor checks

### 1.1 Unity compile (editor)

- [ ] Open project in Unity 6000.0.49f1 (only one instance).
- [ ] Wait for script compile to finish.
- [ ] Console: **zero compile errors**.

**Optional — batch compile from terminal** (Unity must be closed):

```bash
"/Applications/Unity/Hub/Editor/6000.0.49f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -quit \
  -projectPath "/Users/laurensart/Desktop/TENDOR-climbing copy/TENDOR-climbing" \
  -logFile -
```

- [ ] Batch compile exits 0 with no `error CS` lines.

### 1.2 Development build deploy

- [ ] Build & Run to iPhone (Development build, as now).
- [ ] App launches without crash.
- [ ] Ignore `The referenced script (Unknown) on this Behaviour is missing!` **only if** it does not block AR/Immersal (note for cleanup later).

**Result:**  [ ] Pass  [ ] Fail  
**Notes:** _______________________________________________

---

## Phase 2 — Recording smoke test

**Validates:** general regression after perf fixes; save + video finalize path.

**Prereq:** Phase 0 gate passed.

1. [ ] Wait for **Locked — ready** (or stable Aligning).
2. [ ] Start recording; perform 5–10s of movement with body visible.
3. [ ] Stop recording.
4. [ ] Confirm status: `Finalizing video…` then save success.
5. [ ] Console: `[BodyTrackingController] Hip recording saved: …`
6. [ ] Console: `[BodyTrackingController] Video ready: …` (if video capture enabled)
7. [ ] No freeze/crash during stop.

**Result:**  [ ] Pass  [ ] Fail  
**Recording file:** _______________________________________________

---

## Phase 3 — Leading-trim + video offset *(trusted — skip)*

Code verified in audit; session 2 used wait-for-body (`fusion offset 0.00s` expected). Re-test only if alignment bugs appear in fused replay.

---

## Phase 4 — Timestamp playback lookup

*Moved to **Priority P1 — Playback scrub** above.*

---

## Phase 5 — BlazePose suppression *(removed)*

The BlazePose validation pipeline has been deleted from the project (scripts, ONNX models,
`com.unity.ai.inference` package, and the in-scene `BlazePosePipeline` GameObject). Nothing to test.

---

## Phase 6 — AR session video guard

**Validates:** `VideoRecorder` refuses capture when AR is not `SessionTracking`.

**Hard to trigger deliberately; optional check:**

1. [ ] If you ever see video fail to start right at launch, console should show:  
   `[VideoRecorder] AR session is …; wait for tracking before recording video.`
2. [ ] Once tracking is stable, video recording starts normally (Phase 2).

**Result:**  [ ] Pass  [ ] Fail / Not observed

---

## Log cheat sheet

Filter Xcode console by these strings:

| String | Meaning |
|--------|---------|
| `Immersal SDK initialized after AR tracking` | Good — Immersal started correctly |
| `ARSession did not reach SessionTracking` | **Blocker** — fix Phase 0.2 |
| `Map 147190 loaded — scan to localize` | Map download OK |
| `Map 147190 not found for this account/token` | Wrong id or token |
| `Too few matches` | Scanning but no successful localization yet |
| `Trimmed first N untracked frames` | Leading-trim fix fired |
| `video offset` / `fusion offset` | Offset alignment fix fired |
| `Video ready` | AVPro finished encoding the mp4 |
| `AR session is` + `wait for tracking` | Video guard blocked capture |

---

## Issues found during testing

| # | Phase | Issue | Severity | Notes |
|---|-------|-------|----------|-------|
| 1 | 0 | ARSession never SessionTracking; Immersal init skipped | Blocker | 2026-06-05 session — likely AVPro prewarm + AR reconfig race |
| 2 | 0 | Too few matches during scan (10–11/12) | Blocker | Map 147177 loaded; almost localizing |
| 3 | 0 | 147190 not actually loaded | Blocker | PlayerPrefs restored 147177; no `Looking up map 147190` in log |
| 4 | 0 | Keyboard backgrounded AR session | Warning | `UIKeyboardImpl` + `applicationDidEnterBackground` |
| 5 | | | | |

---

## Sign-off

| Area | Pass? | Tester initials | Date |
|------|-------|-----------------|------|
| P1 — Move fusion completes | [ ] | | |
| P1 — Replay stays on wall | [ ] | | |
| P1 — Scrub / timestamps | [ ] | | |
| P1 — Wrong fusion guard | [ ] | | |
| P2 — Map switch blocked | [ ] | | |
| P2 — Re-align blocked | [ ] | | |
| P2 — Background upload | [ ] | | |
