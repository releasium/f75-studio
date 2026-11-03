# F75 Studio — Screen Sync (edge ambilight) Design Spec

Date: 2026-11-03
Status: Approved design, pending implementation plan
Builds on: Plan 2 (keyboard lighting), branch `feat/plan-2-lighting`. Protocol reference: `docs/protocol/aula-f75.md` §8 (live frames).

## Goal

A new lighting effect, **Screen sync (edges)**, that continuously shows the colors at the edges of the screen on the AULA F75, TV-ambilight style, while the keyboard is connected by USB cable.

Out of scope: NZXT CAM sync (parked), multi-monitor selection (the PC has one display; use the primary output), audio-reactive effects.

## Decisions

| Topic | Decision |
|---|---|
| Mapping | Screen edges: F-row = top edge, bottom row = bottom edge, left column = left edge, right column = right edge; interior keys = nearest edge's color at ~30% |
| Capture | DXGI Desktop Duplication via the Vortice.Direct3D11 / Vortice.DXGI libraries, GPU downscale to a small image |
| Transport | Volatile live frames, wired command `0x08` (protocol doc §8). Never writes flash. USB cable only |
| Activation | A selectable effect in the Effect list, savable in profiles (so tray/hotkeys can switch to it) |

## Behaviour

- **Selecting the effect** while the device is `Connected` starts streaming. On Bluetooth/dongle/not found the page shows "Screen sync needs the USB cable" and nothing is sent. When the device becomes `Connected` while the effect is selected, streaming starts automatically.
- **Edge mapping** (per key, using `F75Layout` positions; keyboard canvas 660×290 maps proportionally onto the screen width/height):
  - F-row keys (Esc, F1–F12) ← top edge strip, horizontal position = key center x.
  - Bottom-row keys (LCtrl, LWin, LAlt, Space, Fn, RCtrl, Left, Down, Right) ← bottom edge strip, by key center x.
  - Left column keys (`, Tab, CapsLock, LShift) ← left edge strip, vertical position = key center y.
  - Right column keys (Delete, PgUp, PgDn, End) ← right edge strip, by key center y.
  - Every other key ← the color of the nearest of those edge keys (Euclidean distance between key centers), scaled to 30%.
  - Each edge sample averages a region whose depth is 6% of the screen dimension perpendicular to the edge and whose width matches the key's span (at least one source pixel).
- **Rate and smoothing**: capture/send loop targets 25 frames per second (40 ms period). Exponential smoothing per LED, factor 0.35 toward the new color. The profile's Brightness (0–9) scales output (`0.25 + 0.75 × b/9`, same curve as the preview; brightness 0 → black).
- **Keep-alive**: if the smoothed frame equals the last sent frame, send nothing until 500 ms have passed since the last send, then resend (firmware reverts after ~3–5 s idle, doc §3.5).
- **Stopping**: switching to another effect, cable unplug, PC lock/sleep, app exit, or a send failure stops streaming. After stopping, the app force-applies the profile's stored lighting, so the keyboard shows the profile's saved effect again (the screen-sync profile's stored part is its "base" effect, see Data).
- **Preview**: while streaming, the Keyboard page's key preview shows the live colors (throttled to ≤10 updates/s on the UI thread).
- **Unknown to be settled on hardware**: whether `0x08` frames require per-key effect 21 to be selected first (doc §8, UNVERIFIED). Design: start without switching. If the hardware test shows frames are ignored, the engine's start sequence writes effect 21 once (normal stored apply) before streaming and the stop sequence restores the base effect. This is a single config flag (`RequiresPerKeyForLive`) with both paths implemented and tested.

## Data

- Screen sync is an **app-side effect id 100** (`EffectIds.ScreenSync`). It is never written to the keyboard's settings page; `F75Protocol.PatchSettings` and the stored-apply path must refuse it.
- `F75Effects.All` lists it as "Screen sync (edges)" with `HasBrightness = true`, `HasSpeed = false`, `HasColor = false`, plus a new flag `IsLive = true`.
- `LightingState` keeps its other fields. For the stored apply when screen sync stops (or while the cable is absent), the state's **base effect** is used: `LightingState.BaseEffectId` (new, default `EffectIds.FixedOn`) — the stored effect the keyboard shows when not streaming. Selecting Screen sync in the UI sets `BaseEffectId` to the previously selected stored effect.
- The redundancy skip and flash-write rules from Plan 2 apply unchanged to stored applies.

## Architecture

| Unit | Project | Responsibility |
|---|---|---|
| `F75Protocol.BuildLiveFrame(IReadOnlyDictionary<int,Rgb>)` + allowlist entry `(0x08, 0, 378)` | Lighting.Aula | 378 bytes **interleaved** R,G,B at `led*3`, real F75 LEDs only, no marker. `Frame` accepts `0x08` only with exactly 378 bytes |
| `F75Session.SendLiveFrameAsync(byte[] payload, ct)` | Lighting.Aula | One SET_FEATURE, minimum 7 ms since the previous live frame, IOException retry ×1 (no long retries for live data) |
| `ILiveLighting` (`StartLiveAsync`, `SendLiveAsync(colors)`, `StopLiveAsync`) | Lighting (interface), Aula (impl in `AulaF75Device`) | Start: cable + identity check, holds a live session (channel kept open for the session, released on stop). Frames are serialized with the device semaphore. The 500 ms inter-transaction gap applies before start and after stop, not between frames. Stored applies during live mode first stop live mode |
| `IScreenCapture` / `DxgiScreenCapture` | App | Desktop Duplication of the primary output; GPU downscale to 64×36 BGRA; returns a small `CapturedFrame(width, height, byte[] bgra)`. Recreates the duplication on `DXGI_ERROR_ACCESS_LOST`; returns "no new frame" on timeout |
| `EdgeMapper` | Lighting | Pure: `Map(CapturedFrame, F75Layout) → IReadOnlyDictionary<int,Rgb>` per the Behaviour rules |
| `ScreenSyncEngine` | App | Background loop (dedicated task, not the UI thread): capture → map → smooth → brightness → send-if-changed / keep-alive; exposes `IsRunning`, `LastError`, `FramePreview` event; start/stop driven by `KeyboardViewModel` |
| `KeyboardViewModel` changes | App | Effect list includes Screen sync; starts/stops the engine on effect/status changes; keeps stored applies for the base effect; status texts |

## Error handling

- Capture failures (protected content, GPU reset, lock screen): retry every 500 ms; keep sending the last good frame dimmed by 50% as keep-alive; status "Screen capture paused".
- Send failure or device leaving `Connected`: stop the engine, show the device status, no retries until the device is `Connected` again.
- CPU budget: the loop never queues frames; if a cycle overruns 40 ms the next capture starts immediately and the overdue frame is dropped.
- App exit / session end: engine stops before `FlushAsync` and device disposal; stop is bounded to 1 s.

## Testing

- Protocol: `BuildLiveFrame` byte layout (interleaved, LED index × 3), allowlist accepts `0x08`/378 and rejects other lengths; `PatchSettings` refuses effect 100.
- `EdgeMapper`: synthetic frames (top half red / bottom half blue → F-row red, bottom row blue; left/right stripes → left/right columns; interior keys = nearest edge × 0.3).
- `ScreenSyncEngine` with fake capture + fake live device: frame pacing, smoothing math, keep-alive after 500 ms of identical frames, stop on send failure, dropped frames on overrun, capture-failure dimming.
- `KeyboardViewModel`: selecting Screen sync on cable starts the engine; on Bluetooth shows the cable message and doesn't start; switching away stops it and force-applies the base effect; profile save/load round-trips `BaseEffectId`.
- `AulaF75Device` live path with the fake probe/channel: identity check at start, frames only while live, stored apply stops live first, never runs on Bluetooth.
- Manual hardware checks (supervised): smoothness/latency, exclusive-fullscreen game, whether effect 21 is required, keyboard returns to the base effect after stop, no flash writes while streaming (only `0x08` frames on the wire).

## Addendum (2026-11-03): Screen sync (blur)

Approved follow-up. A second live effect that shows a blurred, spatial copy of the **whole** screen.

- New app-side effect id **101** (`EffectIds.ScreenSyncBlur`), name "Screen sync (blur)", `IsLive = true`, `HasBrightness = true`, listed right after "Screen sync (edges)". Same rules as edges: USB cable only, volatile `0x08` frames only, base effect stored, never written to the settings page.
- **BlurMapper**: each key's rectangle, normalised to the keyboard extent (same `KbLeft/KbRight/KbTop/KbBottom` as `EdgeMapper`), is mapped onto the whole frame and grown on every side by **60 % of the key's own normalised width (x) and height (y)** (`BlurMapper.Margin = 0.6`), clamped to the frame. Key colour = average of that region (at least 1 pixel). Overlapping regions give the blur.
- The engine takes the mapper as a parameter of `StartAsync`; the view model passes a function that picks `EdgeMapper.Map` or `BlurMapper.Map` from the currently selected live effect (read via a volatile field). Switching between the two live effects while running changes the mapper on the next frame: no stop/restart, no stored write.
- Every place that tested `EffectId == EffectIds.ScreenSync` now tests "is a live effect" (`F75Effects.Get(id).IsLive`).
