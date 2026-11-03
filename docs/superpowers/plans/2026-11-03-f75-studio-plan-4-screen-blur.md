# F75 Studio — Plan 4: Screen sync (blur)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add the "Screen sync (blur)" live effect: each key shows the blurred colour of its matching spot on the screen.

**Architecture:** A pure `BlurMapper` beside `EdgeMapper`; the engine gets the mapper per start; the view model chooses the mapper from the selected live effect and generalises its screen-sync checks to "any live effect".

**Spec:** `docs/superpowers/specs/2026-11-03-f75-studio-screen-sync-design.md`, section "Addendum: Screen sync (blur)".

## Global Constraints

- Branch `feat/plan-3-screen-sync` (continue on it), repo `C:\Users\ckkos\Documents\Something\F75Studio`.
- Effect id **101** `EffectIds.ScreenSyncBlur`, name "Screen sync (blur)", `IsLive: true`, `HasBrightness: true`, `HasSpeed/HasColor: false`, listed directly after "Screen sync (edges)" (so it is the last entry).
- `BlurMapper.Margin = 0.6`. Keyboard extent from `F75Layout.Keys` (30, 630, 30, 263).
- All Plan 3 rules hold: live effects never reach the settings page, cable only, no stored writes while streaming except forced ones routed through the screen-sync gate.

---

### Task 1: Blur effect metadata, BlurMapper, engine mapper parameter

**Files:**
- Modify: `src/F75Studio.Lighting/F75Effects.cs`, `src/F75Studio.Lighting/ScreenSyncContracts.cs`, `src/F75Studio.Lighting/ScreenSyncEngine.cs`, `src/F75Studio.Lighting/EdgeMapper.cs` (shared helpers only), `tests/F75Studio.Tests/TestSupport/FakeScreenSync.cs`
- Create: `src/F75Studio.Lighting/BlurMapper.cs`
- Test: `tests/F75Studio.Tests/BlurMapperTests.cs` (+ extend `ScreenSyncEngineTests.cs`, `ScreenSyncModelTests.cs`)

**Interfaces:**
- Produces: `EffectIds.ScreenSyncBlur = 101`; `F75Effects.All` appends `new(EffectIds.ScreenSyncBlur, "Screen sync (blur)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true)`.
- Produces: `public static class BlurMapper { public const double Margin = 0.6; public static IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame); }` with the same frame validation as `EdgeMapper.Map` (ArgumentException for non-positive dims or a short buffer) and entries for all 80 keys.
- Changes: `IScreenSync.StartAsync(Func<int> brightness, Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>>? mapper = null, CancellationToken ct = default)`; `null` means `EdgeMapper.Map`. The engine calls the mapper **every frame** (the VM's function may switch mappers between frames). Exceptions from the mapper are handled like EdgeMapper's today. `FakeScreenSync` records the last mapper (`LastMapper`).

Mapping per key: `nx0 = (X-KbLeft)/(KbRight-KbLeft)`, `nx1 = (X+Width-KbLeft)/(KbRight-KbLeft)`, `ny0 = (Y-KbTop)/(KbBottom-KbTop)`, `ny1 = (Y+Height-KbTop)/(KbBottom-KbTop)`; `mx = Margin*(nx1-nx0)`, `my = Margin*(ny1-ny0)`; region = `[clamp01(nx0-mx), clamp01(nx1+mx)) x [clamp01(ny0-my), clamp01(ny1+my))`, converted to pixels with floor/ceil and at least 1 pixel each way (same Span rule as EdgeMapper). Colour = rounded average of R,G,B over the region.

- [ ] **Step 1: Failing tests.** `BlurMapperTests`:
  1. Theory over sizes (1,1),(2,2),(3,5),(40,22),(64,36),(1920,1080): uniform (10,20,30) gives no throw, 80 entries, every key (10,20,30).
  2. 64x36, left half red / right half blue: `Esc` and `CapsLock` red; `PgUp` and `Right` blue; `J` has R > 0 and B > 0 (blurred across the split).
  3. 64x36 black with a white 8x8 block at x 28-35, y 14-21: the keys whose blur region (computed in the test with the same formula) overlaps the block are non-black; `Esc`, `PgUp`, `LCtrl`, `Right` are black.
  4. 64x36, top half red / bottom half blue: `F5` red, `Space` blue.
  5. Guards: (0,1) and a short buffer throw ArgumentException.
  - `ScreenSyncModelTests`: the blur effect is live and last in `All`; `StoredState()` of a blur state uses the base effect; `PatchSettings` refuses 101.
  - `ScreenSyncEngineTests`: a custom mapper passed to `StartAsync` is used (a mapper returning all green gives green frames); a mapper function that switches its result between frames switches the output without restarting (`LiveStarts` stays 1).
- [ ] **Step 2: Run** the focused tests: they fail to build.
- [ ] **Step 3: Implement.** Factor the shared extent/Span/Average/validation helpers so EdgeMapper and BlurMapper don't duplicate logic (e.g. an internal `FrameSampler` static class in the same project). EdgeMapper behaviour must not change: its tests pass unchanged.
- [ ] **Step 4: Run** focused + full suite (all pass, 0 warnings).
- [ ] **Step 5: Commit** `feat: screen sync blur mapper and effect`.

---

### Task 2: Wire the blur effect into the Keyboard page

**Files:**
- Modify: `src/F75Studio.App/ViewModels/KeyboardViewModel.cs`
- Test: `tests/F75Studio.Tests/KeyboardScreenSyncTests.cs`

**Behaviour:**
1. Replace every `EffectId == EffectIds.ScreenSync` check (the non-forced skip and the same-stored-part skip in the stored-apply path) with "is a live effect" (`F75Effects.IsSupported(id) && F75Effects.Get(id).IsLive`). Audit the file for any other ScreenSync-specific literal.
2. Keep a `volatile int _liveEffectId`, updated on the UI thread whenever the selected effect changes and on profile load. Pass `mapper: frame => Volatile.Read(ref _liveEffectId) == EffectIds.ScreenSyncBlur ? BlurMapper.Map(frame) : EdgeMapper.Map(frame)` to `StartAsync`.
3. Switching between the two live effects while streaming: no engine stop/start, no stored write (stored part unchanged); the profile saves the new effect id. Switching from a live effect to a stored one behaves as today.
4. Base-effect handling (`_baseEffectId`) is unchanged: selecting either live effect keeps the last non-live effect as the base.

**Tests (add to `KeyboardScreenSyncTests`):**
1. Selecting "Screen sync (blur)" on the cable starts the engine; the mapper passed maps a synthetic frame exactly like `BlurMapper.Map`.
2. Edges to blur while running: `Starts` stays 1, `Stops` 0, no new `Applied`, the mapper now produces `BlurMapper` output; profile saved with EffectId 101 and unchanged BaseEffectId.
3. Blur to Static: stops and restores the stored effect (as edges does).
4. Brightness drag while blur is running writes nothing.
5. Loading a profile with EffectId 101 on the cable starts the engine with the blur mapper.

- [ ] Implement, run focused + full suite, smoke-launch about 8 s (nothing clicked; keyboard on Bluetooth; stop only your process). The user's own F75 Studio instance is running; if your launch exits at once because of the single-instance mutex, do not close the user's instance, just report it. Commit `feat: screen sync blur on the Keyboard page`.
