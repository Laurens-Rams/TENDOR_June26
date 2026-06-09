# Wall constraint test plan — flat wall floating / clipping

**Scene:** `NewVersion`  
**Recording used:** _________________________________  
**Date started:** _______________  
**Tester:** _______________  
**Device:** _______________

---

## What we're fixing (from gym testing)

> On a **flat straight wall**, the GLB character often **floats in the air** and **clips into the wall**. The climb doesn't look like what was actually climbed — depth (distance from wall) is wrong and limbs don't stay on holds.

**Symptoms to watch for:**

- [ ] Character **inside the wall** (negative depth / clipping)
- [ ] Character **floating off the wall** (too far out in +Z)
- [ ] **Whole body lifts** mid-climb (floor fix misfire)
- [ ] **Hands/feet swim** near holds instead of gripping them
- [ ] **Blue debug plane** not on the real wall (RouteRoot offset wrong)

---

## Do I need to re-record?

| What | Re-record? | Notes |
|------|------------|-------|
| Wall projection (slab + contact lock) | **No** | Playback-only — use existing recording |
| Floor gate / wall IK / penetration | **No** | Playback-only |
| Debug planes + status HUD | **No** | Visual overlay |
| Baker weights (X/Y/Z) | **No** | Tuning → **Rebake from latest (no API)** |
| Record-time depth clamp | **Yes** | Only if playback fixes look good but you want cleaner source ARKit data |

**Start with an existing gym recording.** Re-record only after Step 6 passes (optional polish).

---

## Quick status

| Step | Name | Pass | Notes |
|------|------|------|-------|
| 0 | Wall plane sanity (blue plane) | [ ] | |
| 0A | AR wall plane (snap to real wall) | [ ] | recommended fix for 2 m offset / tilt |
| 1 | Baseline (old behavior) | [ ] | |
| 2 | Slab only | [ ] | |
| 3 | Contact lock (hold grabbing) | [ ] | |
| 4 | Floor gate (vertical float) | [ ] | |
| 5 | Wall IK (GLB penetration) | [ ] | |
| 6 | Full fix preset | [ ] | |
| 7 | Optional rebake | [ ] | |
| 8 | Optional re-record | [ ] | |

---

## Setup (every session)

1. [ ] Open scene **NewVersion**
2. [ ] Localize (Immersal / RouteRoot locked)
3. [ ] Load a **known-bad** gym recording (floating / clipping)
4. [ ] Start **playback**
5. [ ] Open **Tuning** sheet
6. [ ] **Debug overlay (planes/HUD)** → **On**
7. [ ] **Status HUD** → **On**

**HUD should show:**

- [ ] ● **Localized** (green)
- [ ] ● **Wall plane** (green) when RouteRoot is set
- [ ] ● **Floor plane** (green) when AR floor detected *(may stay ○ until planes appear)*
- [ ] ● **Playing** (green) during replay

**Visuals:**

- [ ] **Blue plane** = wall the system uses
- [ ] **Green plane** = detected floor
- [ ] **Green dots** on hands/feet when a hold is locked

---

## Step 0 — Wall plane sanity (CRITICAL — do first)

**Goal:** Blue plane sits on the **real** physical wall. If it's off (e.g. floating 2 m out), the RouteRoot origin isn't on the wall — and the slab/contact lock will push the climber to the WRONG plane. Fix this before judging anything else.

**Primary method — auto-calibrate from the climb:**

| Setting | Value |
|---------|-------|
| Debug overlay | On |
| Auto-calibrate wall on play | **On** |

- [ ] Start playback (auto-calibration runs) — OR tap **Calibrate wall to climb now**
- [ ] Blue plane jumps onto the real wall surface
- [ ] Console shows `Auto-calibrated wall plane to RouteRoot-local Z = … m`

**Fallback — manual:** if auto looks slightly off, nudge **Wall depth offset** (range now ±3 m).

- [ ] Blue plane overlaps physical wall surface

**Best method — snap to the real AR wall (fixes depth AND tilt):** uses ARKit's detected vertical plane (the surface the white feature dots sit on), so it doesn't depend on the map origin at all.

- [ ] Point the phone at the wall, let tracking settle (white dots cover the wall)
- [ ] Tap **Calibrate wall to AR plane (front)** — picks the front-facing vertical plane
- [ ] Blue plane snaps onto the real wall, **parallel** to it (tilt corrected too)
- [ ] HUD shows `Wall: AR plane  Z … m`
- [ ] **Wall source** toggle reads `AR plane` (flip back to `RouteRoot` to compare)

> If no plane is found, the console warns you — the wall may be too blank/untextured; aim at a hold-covered area and wait a second.

**If using RouteRoot mode and the blue plane is TILTED (not just translated)** — auto-calibrate-from-climb only fixes depth (translation). Use the AR-plane button above (it fixes tilt), or re-map / re-localize:

- [ ] Blue plane parallel to wall (not skewed)

**Pass:** [ ] Yes  [ ] No  

**Notes:**

```
Auto-calibrated offset (from console): _______ m
Wall looked: [ ] aligned  [ ] too far out  [ ] too far in  [ ] tilted/skewed
```

---

## Step 0A — AR wall plane (snap to the real wall) — RECOMMENDED

**Goal:** Put the wall on ARKit's **real detected vertical plane** (the surface the white feature dots sit on), instead of trusting the map origin. This fixes **both** the ~2 m depth offset **and** any tilt, because the AR plane carries a real position *and* orientation.

**When to use:** Your blue plane is floating ~2 m off the wall, or it's tilted/skewed vs the real wall. This is the best fix for the gym.

**How to do it (on device):**

1. [ ] Be **localized** and in **playback** (Setup above), Debug overlay + Status HUD **On**
2. [ ] **Hold the phone facing the wall**, ~1.5–3 m back, so the wall fills most of the screen
3. [ ] Wait ~1–2 s until **white feature dots cover the wall** (ARKit needs time to fit the plane)
4. [ ] Open **Tuning** → tap **`Calibrate wall to AR plane (front)`**
5. [ ] The blue plane should **snap flat onto the real wall**, parallel to it
6. [ ] HUD now reads **`Wall: AR plane  Z <value> m`**
7. [ ] **`Wall source`** toggle shows **`AR plane`**

**What "front-facing" means:** it auto-picks the vertical plane that is **in front of the phone** and whose surface **faces back toward you** (within 45°), and ignores small clutter planes — so side walls / pillars won't be chosen. Just point at the wall you're climbing.

**Verify:**

- [ ] Blue plane overlaps the physical wall surface (depth correct)
- [ ] Blue plane **parallel** to the wall (tilt correct)
- [ ] Toggling **`Wall source`** → `RouteRoot` vs `AR plane` visibly moves the plane (confirms it's live)
- [ ] Character now sits in the correct depth band (no longer 2 m out)

**Troubleshooting:**

| Symptom | Cause | Fix |
|---------|-------|-----|
| Console warns *"No front-facing AR vertical plane detected"* | Plane not fitted yet / wall too blank | Aim at a **hold-covered** area, hold steady ~2 s, re-tap |
| Blue plane snaps to a side wall | Stood at an angle | Face the climbing wall straight-on, re-tap |
| Plane is jittery / drifts | Poor tracking / glare | Move closer, avoid backlight; re-tap when dots are dense |
| Nothing happens, no warning | No `ARSurfaceProbe` / camera | Check probe is in scene; HUD `Wall:` line should show `n/a` if missing |

> Re-tap any time you reposition. The plane is taken from the **live** AR session, so calibrate while standing where you'll watch the replay.

**Pass:** [ ] Yes  [ ] No

**Notes:**

```
AR-plane offset (HUD / console): _______ m
Plane aligned: [ ] depth ok  [ ] parallel ok  [ ] picked wrong wall  [ ] never detected
```

---

## Step 1 — Baseline (old behavior)

**Goal:** Confirm the problem still reproduces without the new system.

| Setting | Value |
|---------|-------|
| Enable wall projection | **Off** |
| Enable penetration fix | On |
| Floor fix | On |
| Max standing hip height | **0** *(disables climbing guard)* |
| Wall hand IK | Off |
| Wall foot IK | Off |

- [ ] Replay same recording
- [ ] Note 2–3 worst moments (time / hold):

**Worst moments:**

| # | Time / hold | Symptom (float / clip / lift / swim) |
|---|-------------|----------------------------------------|
| 1 | | |
| 2 | | |
| 3 | | |

**Pass (problem visible):** [ ] Yes  [ ] No — if no, pick a worse recording

**Notes:**

```
```

---

## Step 2 — Slab only (biggest depth fix)

**Change only these** (keep Step 1 penetration as-is unless noted):

| Setting | Value |
|---------|-------|
| Enable wall projection | **On** |
| Depth slab clamp | **On** |
| Hold contact lock | **Off** |
| Max body depth | **0.50** |
| Wall surface depth | **0.04** |
| Wall depth offset | *(from Step 0)* |

**Pass criteria:**

- [ ] No longer **inside the wall**
- [ ] No longer **floating far off** the wall
- [ ] Body stays in a thin band parallel to wall

**Pass:** [ ] Yes  [ ] No  

**If fail, try:**

- [ ] Wall surface depth → **0.06**
- [ ] Max body depth → **0.35**
- [ ] Re-check wall depth offset (Step 0)

**Notes:**

```
Compared to Step 1 at same moments:
```

---

## Step 3 — Contact lock (hold grabbing)

**Add:**

| Setting | Value |
|---------|-------|
| Hold contact lock | **On** |
| Contact depth band | **0.14** |
| Contact stillness | **0.08** |
| Contact release | **0.25** |
| Contact ease | **0.12** |

**Pass criteria:**

- [ ] HUD: **Contact L-hand / R-hand / feet** go ● green while gripping
- [ ] Green marker dots appear on locked limbs
- [ ] Hands/feet stop jittering on holds

**Pass:** [ ] Yes  [ ] No  

**If fail, try:**

- [ ] Contact depth band → **0.18**
- [ ] Contact stillness → **0.05**
- [ ] Contact release → **0.35** *(if hands feel stuck)*

**Notes:**

```
Hands lock: [ ] good  [ ] never  [ ] too sticky
Feet lock:  [ ] good  [ ] never  [ ] too sticky
```

---

## Step 4 — Floor gate (vertical floating)

| Setting | Value |
|---------|-------|
| Max standing hip height | **1.3** |
| Floor fix | On |

**Pass criteria:**

- [ ] Mid-wall climb no longer **lifts whole character**
- [ ] Low start / standing at bottom still OK if applicable

**Pass:** [ ] Yes  [ ] No  

**If fail, try:**

- [ ] Max standing hip height → **1.5**
- [ ] Floor fix → **Off** *(wall-only test)*

**Notes:**

```
Still floating up at: ___________________
```

---

## Step 5 — Wall IK (GLB penetration polish)

| Setting | Value |
|---------|-------|
| Wall hand IK | **On** |
| Wall foot IK | **On** |
| Max IK weight | **1.0** |
| Penetration for full weight | **0.08** |

**Pass criteria:**

- [ ] Hands/feet that poke through wall get pushed onto surface
- [ ] No obvious popping / over-correction

**Pass:** [ ] Yes  [ ] No  

**If fail, try:**

- [ ] Max IK weight → **0.7**

**Notes:**

```
```

---

## Step 6 — Full fix preset (ship candidate)

Use as default **“everything on”**:

| Setting | Value |
|---------|-------|
| Enable wall projection | **On** |
| Depth slab clamp | **On** |
| Hold contact lock | **On** |
| Max body depth | **0.50** |
| Wall surface depth | **0.04** |
| Wall depth offset | *(calibrated Step 0)* |
| Contact depth band | **0.14** |
| Contact stillness | **0.08** |
| Contact release | **0.25** |
| Contact ease | **0.12** |
| Enable penetration fix | **On** |
| Floor fix | **On** |
| Max standing hip height | **1.3** |
| Wall hand IK | **On** |
| Wall foot IK | **On** |
| Debug overlay | On *(turn Off when done testing)* |

- [ ] Replay **same recording** as Step 1
- [ ] Compare at the **same 2–3 worst moments**

| Moment | Step 1 (baseline) | Step 6 (full fix) | Better? |
|--------|-------------------|-------------------|---------|
| 1 | | | [ ] |
| 2 | | | [ ] |
| 3 | | | [ ] |

**Overall:** [ ] Clearly better  [ ] Slightly better  [ ] No change  [ ] Worse  

**Notes:**

```
Would show this to someone as "what was climbed"?  [ ] Yes  [ ] Not yet
```

---

## Step 7 — Optional rebake (no re-record)

Only if **traverse / height** still feels wrong after Step 6:

| Setting | Value |
|---------|-------|
| Horizontal weight (X) | **0.8** |
| Vertical weight (Y) | **1.0** |
| Depth weight (Z) | **1.0** |
| Smoothing tau | **0.4** |

- [ ] Tap **Rebake from latest (no API)**
- [ ] Replay again

**Pass:** [ ] Yes  [ ] Skipped  

**Notes:**

```
Rebake status message:
```

---

## Step 8 — Optional re-record (source data polish)

Only after Step 6 **passes**. Inspector on **BodyTrackingSystem** → `BodyTrackingRecorder`:

| Setting | Value |
|---------|-------|
| Clamp recorded depth to wall slab | **On** |
| Record max body depth | **0.50** |

- [ ] Record **same climb** once more
- [ ] Fuse + replay
- [ ] Compare to Step 6 on same wall section

**Pass:** [ ] Better than Step 6  [ ] Same  [ ] Skipped  

**Notes:**

```
New recording filename:
```

---

## Reference — how the hand/foot IK works (read before judging Steps 3 & 5)

There are **two separate limb passes**, both run on the rig bones *after* the body pose is written, and both move **only one limb at a time** — never the torso/hips.

### A. Push-OUT pass (keep hands out of the wall) — Step 5

- For each hand/foot, we probe the wall at the **tip** (wrist/ankle). If the tip is **inside** the wall skin, we get a surface point + outward normal and the penetration depth.
- We pin the tip back onto the **surface point** with **two-bone IK** (shoulder→elbow→wrist or hip→knee→ankle).
- **It never changes the whole pose:** the IK is *analytic two-bone* — it only re-bends the **3 bones of that one limb** so the tip reaches the target. The torso, the other arm, the head — all untouched. A correct body with one bad hand becomes a correct body with a correct hand.
- **Elbow/knee never flips:** the solver **keeps the limb's current bend plane**, so the elbow stays bent the way it already was; the "hint" only kicks in if the arm is dead-straight.
- **Strength scales with depth:** weight = `penetration / penetrationForFullWeight`. A hand 1 mm in gets a tiny nudge; a hand 8 cm in gets full correction. So shallow clips don't pop.
- **Only while climbing:** this pass is skipped when the pose is classified *standing* (Step 4) and during jumps, so a grounded/airborne pose isn't disturbed.

### B. Pull-ON pass (grab the hold) — Step 3

- The contact detector watches each hand/foot and **latches** it to the wall only when **BOTH**:
  - it is **near the wall** — within **contact depth band** (default 0.14 m) of the surface, **and**
  - it is **barely moving** — smoothed speed below **stillness speed** (default 0.08 m/s).
- When latched, the tip is eased onto the locked surface point (two-bone IK again, same single-limb rule).
- It **releases** the moment the hand speeds up past **release speed** (0.25 m/s) or its depth grows past ~1.6× the band.

### How we DON'T pin a hand that's actually off the wall in reality

This is the key question. A reaching/swinging hand out in the air is **never pulled to the wall**, because:

1. **Depth gate:** if the hand's depth is bigger than the contact band, it fails the "near the wall" test → no latch. A hand reaching out at 0.4 m is far outside the 0.14 m band.
2. **Stillness gate:** a moving hand (reaching, matching, flagging) is above the stillness speed → no latch. Only a hand that is *both close AND still* (i.e. actually resting on a hold) latches.
3. **Hysteresis release:** once moving, it lets go immediately and won't re-grab until it's close + still again — so a hand that lifts off a hold isn't dragged back.
4. **The push-out pass only fires on penetration:** if a hand is out in the air (not inside the wall), there is nothing to push, so it's left exactly where the capture put it.

So the only thing that gets pulled onto the wall is a hand the climber is genuinely holding still near the surface. The slab clamp (Step 2) separately caps how far *any* joint can float out, but it does **not** pull hands onto the wall — it only stops them sinking behind it or ballooning unrealistically far out.

### What to watch for to confirm it looks good

- [ ] A hand **resting on a hold** is rock-steady on the surface (green dot in HUD), no jitter
- [ ] A hand **reaching to the next hold** travels freely through the air — **not** stuck to the wall
- [ ] The **elbow/knee bend direction** looks natural (doesn't snap inside-out) when a limb is corrected
- [ ] Correcting one hand does **not** twist the shoulders/hips or move the other limbs
- [ ] Releasing a hold happens **instantly** as the hand starts moving (no rubber-banding)

**If a reaching hand wrongly sticks to the wall:** lower **contact depth band** (e.g. 0.10 m) and/or **stillness speed** (e.g. 0.05 m/s) so only truly-planted hands latch.
**If a planted hand flickers off:** raise **contact depth band** slightly, or widen the gap between stillness and release speed.

---

## Symptom → fix cheat sheet

| Symptom | Fixed by step | If still broken |
|---------|---------------|-----------------|
| Inside wall | 2 + 0 | Wall depth offset, wall surface depth |
| Floating off wall | 2 | Lower max body depth |
| Hands swim on holds | 3 | Contact depth band / stillness |
| Reaching hand sticks to wall | 3 | Lower contact depth band + stillness speed |
| Planted hand flickers on/off | 3 | Raise depth band; widen stillness↔release gap |
| One fix twists torso/other limb | 5 | Shouldn't happen (single-limb IK) — report it |
| Whole body lifts mid-climb | 4 | Max standing hip height or floor fix off |
| GLB hands clip wall | 5 | Max IK weight |
| Wrong path along wall | 7 | Rebake weights |
| HUD Wall plane ○ red | Setup | Fix localization / RouteRoot |
| Blue plane wrong | 0 | Wall depth offset |

---

## 10-minute minimum test

If short on time:

1. [ ] Step 0 — blue plane on wall  
2. [ ] Step 1 — baseline (wall projection **Off**)  
3. [ ] Step 6 — full preset  
4. [ ] Same moments — clearly better? **[ ] Yes  [ ] No**

If **No** → check HUD (**Localized**, **Wall plane**) before tuning further.

---

## Final sign-off

- [ ] Step 6 accepted as default tuning values
- [ ] Debug overlay turned **Off** for normal use
- [ ] Scene saved with working settings
- [ ] Optional: document final slider values below

**Final wall depth offset:** _______ m  

**Final max body depth:** _______ m  

**Other tweaks kept:**

```
```

**Decision:** [ ] Good enough for gym  [ ] Needs more tuning  [ ] Re-record + rebake next session
