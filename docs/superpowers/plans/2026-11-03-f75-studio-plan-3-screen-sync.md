# F75 Studio — Plan 3: Screen Sync (edge ambilight)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Screen sync (edges)" lighting effect that streams the screen's edge colors to the AULA F75 over USB using volatile live frames.

**Architecture:** Pure pieces live in `F75Studio.Lighting` (effect metadata, `EdgeMapper`, `ScreenSyncEngine`, `IScreenCapture`/`ILiveLighting` contracts) and are fully unit-tested with fakes. The wire layer (`F75Protocol`, `F75Session`, `AulaF75Device`) gains the `0x08` live frame and a live session. The App gets a DXGI Desktop Duplication capture (`DxgiScreenCapture`, Vortice) and wires the engine into `KeyboardViewModel`.

**Tech Stack:** .NET 8, WPF, Vortice.Direct3D11 / Vortice.DXGI 3.8.3, HidSharp 2.6.4, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-f75-studio-screen-sync-design.md`. Protocol: `docs/protocol/aula-f75.md` §8.

## Global Constraints

- Branch `feat/plan-3-screen-sync`, repo `C:\Users\ckkos\Documents\Something\F75Studio`.
- Live frame: wired command `0x08`, param 0, **378-byte payload, interleaved** `R,G,B` at `led*3`, real F75 LEDs only, no marker, no checksum. Allowlist entry `(0x08, 0, 378)`; any other length refused.
- Screen sync is app-side effect id **100** (`EffectIds.ScreenSync`). It must never reach the settings page: `F75Protocol.PatchSettings` and `F75Session.ApplyAsync` refuse it.
- Live frames only while the device is `Connected` (USB cable) and the identity check passed at live start. Nothing is sent over Bluetooth or the dongle.
- Live pacing: at least 7 ms between frames; engine period 40 ms (25 fps); keep-alive every 500 ms of identical output; capture retry every 500 ms; smoothing factor 0.35; edge depth 6 %; interior keys = nearest edge × 0.30.
- Brightness factor for output: `b == 0 ? 0 : 0.25 + 0.75 * b / 9`.
- While screen sync is the selected effect, the stored apply sends the profile's **base effect** (`LightingState.BaseEffectId`), and only on forced applies (start/stop/connect) — never on brightness drags (flash wear).
- No code copied from referenced repositories. All earlier constraints (allowlist, identity before writes, 500 ms between stored transactions, UTF-8 no BOM, commit trailers) still hold.
- Vortice packages pinned to **3.8.3**.

## File Structure

```
src/F75Studio.Lighting/
  F75Effects.cs           + EffectIds.ScreenSync, EffectDefinition.IsLive
  LightingState.cs        + BaseEffectId, StoredState()
  ScreenSyncContracts.cs  CapturedFrame, IScreenCapture, ScreenCaptureException, ILiveLighting, IScreenSync
  EdgeMapper.cs           frame → LED colours
  ScreenSyncEngine.cs     capture/map/smooth/send loop
  FakeKeyboardDevice.cs   + ILiveLighting
src/F75Studio.Lighting.Aula/
  F75Protocol.cs          + 0x08 allowlist, BuildLiveFrame, refuse live effect
  F75Session.cs           + SendLiveFrameAsync, refuse live effect
  AulaF75Device.cs        + ILiveLighting (live session)
src/F75Studio.App/
  Services/DxgiScreenCapture.cs
  ViewModels/KeyboardViewModel.cs (modified), App.xaml.cs (modified)
tests/F75Studio.Tests/
  ScreenSyncModelTests.cs, EdgeMapperTests.cs, ScreenSyncEngineTests.cs, AulaF75LiveTests.cs, KeyboardScreenSyncTests.cs, DxgiScreenCaptureTests.cs
  TestSupport/FakeScreenCapture.cs, FakeScreenSync.cs
```

---

### Task 1: Live effect metadata and the 0x08 frame

**Files:**
- Modify: `src/F75Studio.Lighting/F75Effects.cs`, `src/F75Studio.Lighting/LightingState.cs`, `src/F75Studio.Lighting.Aula/F75Protocol.cs`, `src/F75Studio.Lighting.Aula/F75Session.cs`
- Test: `tests/F75Studio.Tests/ScreenSyncModelTests.cs`

**Interfaces:**
- Produces: `EffectIds.ScreenSync = 100`; `EffectDefinition(int Id, string Name, bool HasSpeed, bool HasBrightness, bool HasColor, bool IsLive = false)`; `F75Effects.All` gains `new(EffectIds.ScreenSync, "Screen sync (edges)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true)` as the **last** entry.
- Produces: `LightingState.BaseEffectId` (int, default `EffectIds.FixedOn`) and `LightingState StoredState()` → `this` when the effect is not live, else `this with { EffectId = BaseEffectId is a supported non-live id ? BaseEffectId : EffectIds.FixedOn }`.
- Produces: `F75Protocol.CmdLive = 0x08`, `F75Protocol.BuildLiveFrame(IReadOnlyDictionary<int, Rgb>) : byte[]` (378 bytes interleaved), allowlist `(0x08, 0, 378)`.
- Produces: `F75Session.SendLiveFrameAsync(byte[] payload, CancellationToken ct)`.
- Behaviour changes: `F75Protocol.PatchSettings` throws `ArgumentOutOfRangeException` for live effects; `F75Session.ApplyAsync` throws `ArgumentOutOfRangeException` for live effects before any I/O.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/ScreenSyncModelTests.cs`:

```csharp
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class ScreenSyncModelTests
{
    [Fact]
    public void Screen_sync_is_a_live_app_side_effect()
    {
        var def = F75Effects.Get(EffectIds.ScreenSync);
        Assert.True(def.IsLive);
        Assert.True(def.HasBrightness);
        Assert.False(def.HasSpeed);
        Assert.False(def.HasColor);
        Assert.Equal(EffectIds.ScreenSync, F75Effects.All[^1].Id);
        Assert.All(F75Effects.All.Where(e => e.Id != EffectIds.ScreenSync), e => Assert.False(e.IsLive));
    }

    [Fact]
    public void Stored_state_uses_the_base_effect_for_live_effects()
    {
        var live = new LightingState { EffectId = EffectIds.ScreenSync, BaseEffectId = 2, Brightness = 4 };
        Assert.Equal(2, live.StoredState().EffectId);
        Assert.Equal(4, live.StoredState().Brightness);

        var bogusBase = live with { BaseEffectId = EffectIds.ScreenSync };
        Assert.Equal(EffectIds.FixedOn, bogusBase.StoredState().EffectId);

        var normal = new LightingState { EffectId = 3 };
        Assert.Same(normal, normal.StoredState());
    }

    [Fact]
    public void Live_frame_is_interleaved_by_led_index_for_real_keys_only()
    {
        var frame = F75Protocol.BuildLiveFrame(new Dictionary<int, Rgb>
        {
            [0] = new(1, 2, 3),
            [89] = new(4, 5, 6),
            [84] = new(9, 9, 9),   // knob: no LED
            [200] = new(9, 9, 9),  // out of range
        });

        Assert.Equal(378, frame.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame[0..3]);
        Assert.Equal(new byte[] { 4, 5, 6 }, frame[267..270]);
        Assert.Equal(0, frame[252]);
        Assert.Equal(21, frame.Sum(b => b));
    }

    [Fact]
    public void Live_frame_header_and_allowlist()
    {
        var frame = F75Protocol.Frame(F75Protocol.CmdLive, 0, 378, new byte[378]);
        Assert.Equal("0608000001007A01", Convert.ToHexString(frame, 0, 8));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdLive, 0, 384, new byte[384]));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdLive, 0, 378, new byte[100]));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.ReadRequest(F75Protocol.CmdLive, 0, 378)); // 0x88 is never allowed
    }

    [Fact]
    public void Live_effect_never_reaches_the_settings_page()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            F75Protocol.PatchSettings(F75ProtocolTests.FixedOnPage(), new LightingState { EffectId = EffectIds.ScreenSync }));
    }

    [Fact]
    public async Task Session_refuses_live_effect_before_any_io()
    {
        var channel = new FakeFeatureChannel();
        var session = new F75Session(channel, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.ApplyAsync(new LightingState { EffectId = EffectIds.ScreenSync }, CancellationToken.None));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Session_sends_one_live_frame_per_call()
    {
        var channel = new FakeFeatureChannel();
        var delays = new List<int>();
        var session = new F75Session(channel, (ms, _) => { delays.Add(ms); return Task.CompletedTask; });
        var payload = F75Protocol.BuildLiveFrame(new Dictionary<int, Rgb> { [0] = new(255, 0, 0) });

        await session.SendLiveFrameAsync(payload, CancellationToken.None);
        await session.SendLiveFrameAsync(payload, CancellationToken.None);

        Assert.Equal(2, channel.Sent.Count);
        Assert.All(channel.Sent, f => Assert.Equal(0x08, f[1]));
        Assert.Equal(255, channel.Sent[0][8]);
        Assert.Contains(7, delays);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ScreenSyncModelTests`
Expected: build FAILS (`EffectIds` has no `ScreenSync`).

- [ ] **Step 3: Implement**

`F75Effects.cs`:
- Add `public const int ScreenSync = 100;` to `EffectIds` with comment `/// <summary>App-side live effect; never written to the keyboard's settings page.</summary>`.
- Change the record to `public sealed record EffectDefinition(int Id, string Name, bool HasSpeed, bool HasBrightness, bool HasColor, bool IsLive = false);` and document `IsLive` ("streamed with volatile live frames; not a keyboard effect").
- Append to `All` (after Custom per-key): `new(EffectIds.ScreenSync, "Screen sync (edges)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true),`

`LightingState.cs` — add:

```csharp
    /// <summary>Stored effect the keyboard shows while a live effect isn't streaming.</summary>
    public int BaseEffectId { get; init; } = EffectIds.FixedOn;

    /// <summary>The state to write to the keyboard's settings: live effects fall back to <see cref="BaseEffectId"/>.</summary>
    public LightingState StoredState()
    {
        if (!F75Effects.IsSupported(EffectId) || !F75Effects.Get(EffectId).IsLive) return this;
        var baseId = F75Effects.IsSupported(BaseEffectId) && !F75Effects.Get(BaseEffectId).IsLive ? BaseEffectId : EffectIds.FixedOn;
        return this with { EffectId = baseId };
    }
```

`F75Protocol.cs`:
- `public const byte CmdLive = 0x08;`
- Allowlist: add `(0x08, 0, PerKeyLength)`.
- In `Frame`, the existing "writes require payload.Length == length" rule already covers 0x08 once it's a write; make sure 0x08 needs no marker.
- In `PatchSettings`, right after `var definition = F75Effects.Get(state.EffectId);` add
  `if (definition.IsLive) throw new ArgumentOutOfRangeException(nameof(state), state.EffectId, "Live effects are not stored on the keyboard.");`
- Add:

```csharp
    /// <summary>378 bytes, interleaved R,G,B at led*3 (protocol doc §8). Volatile: nothing is stored.</summary>
    public static byte[] BuildLiveFrame(IReadOnlyDictionary<int, Rgb> colors)
    {
        var data = new byte[PerKeyLength];
        foreach (var (led, color) in colors)
        {
            if (!RealLeds.Contains(led)) continue;
            data[led * 3] = color.R;
            data[led * 3 + 1] = color.G;
            data[led * 3 + 2] = color.B;
        }
        return data;
    }
```

`F75Session.cs`:
- At the top of `ApplyAsync`, after computing `definition`: `if (definition.IsLive) throw new ArgumentOutOfRangeException(nameof(state), state.EffectId, "Live effects are streamed, not stored.");` (before any I/O).
- Add:

```csharp
    private long _lastLiveFrame;

    /// <summary>Sends one volatile live frame (protocol doc §8). At least 7 ms between frames; one retry on a transfer error.</summary>
    public async Task SendLiveFrameAsync(byte[] payload, CancellationToken ct)
    {
        var frame = F75Protocol.Frame(F75Protocol.CmdLive, 0, payload.Length, payload);
        var since = (int)Stopwatch.GetElapsedTime(_lastLiveFrame).TotalMilliseconds;
        await _delay(since >= 7 || _lastLiveFrame == 0 ? 7 : 7 - since, ct).ConfigureAwait(false);
        try
        {
            _channel.SetFeature(frame);
        }
        catch (IOException)
        {
            await _delay(7, ct).ConfigureAwait(false);
            _channel.SetFeature(frame);
        }
        _lastLiveFrame = Stopwatch.GetTimestamp();
    }
```

(add `using System.Diagnostics;`). The 7 ms delay is always passed to `_delay` on the first frame too, so tests can observe it.

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter FullyQualifiedName~ScreenSyncModelTests` → 7 passed. Then `dotnet test` → all pass (the effect list grew by one; if an existing test asserted the effect count or that the last effect is per-key, update it and note it in the report).

- [ ] **Step 5: Commit**

```powershell
git add src/F75Studio.Lighting src/F75Studio.Lighting.Aula tests/F75Studio.Tests
git commit -m "feat: screen sync effect metadata and 0x08 live frame" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Contracts and the edge mapper

**Files:**
- Create: `src/F75Studio.Lighting/ScreenSyncContracts.cs`, `src/F75Studio.Lighting/EdgeMapper.cs`
- Test: `tests/F75Studio.Tests/EdgeMapperTests.cs`

**Interfaces:**
- Produces (`ScreenSyncContracts.cs`):

```csharp
namespace F75Studio.Lighting;

/// <summary>A small, downscaled screen image. <see cref="Bgra"/> is Width*Height*4 bytes, row-major, B,G,R,A.</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra);

public sealed class ScreenCaptureException(string message, Exception? inner = null) : Exception(message, inner);

public interface IScreenCapture : IDisposable
{
    /// <summary>Returns the latest frame, or null if the screen didn't change within <paramref name="timeoutMs"/>.</summary>
    /// <exception cref="ScreenCaptureException">Capture is temporarily impossible (lock screen, GPU reset, unsupported format).</exception>
    CapturedFrame? TryCapture(int timeoutMs);
}

/// <summary>Volatile per-LED streaming (protocol doc §8). Implemented by AulaF75Device and FakeKeyboardDevice.</summary>
public interface ILiveLighting
{
    bool IsLive { get; }

    /// <exception cref="KeyboardUnavailableException">Not on the USB cable, or identity check failed.</exception>
    Task StartLiveAsync(CancellationToken ct = default);

    /// <exception cref="KeyboardUnavailableException">Live session ended (unplugged, send failed).</exception>
    Task SendLiveAsync(IReadOnlyDictionary<int, Rgb> colors, CancellationToken ct = default);

    Task StopLiveAsync();
}

/// <summary>What the view model needs from the screen-sync engine.</summary>
public interface IScreenSync
{
    bool IsRunning { get; }

    /// <summary>Raised on a background thread with each frame actually sent.</summary>
    event Action<IReadOnlyDictionary<int, Rgb>>? FrameSent;

    /// <summary>Raised on a background thread when streaming stops by itself (reason text).</summary>
    event Action<string>? Faulted;

    /// <summary>Raised on a background thread when capture pauses/resumes (null = resumed).</summary>
    event Action<string?>? CaptureStatusChanged;

    Task StartAsync(Func<int> brightness, CancellationToken ct = default);
    Task StopAsync();
}
```

- Produces (`EdgeMapper.cs`): `public static class EdgeMapper` with `const double EdgeDepth = 0.06`, `const double InteriorScale = 0.30`, `IReadOnlyList<string> TopKeys, BottomKeys, LeftKeys, RightKeys`, `IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame)` (entries for all 80 keys).

Mapping rules (spec "Behaviour"):
- Keyboard extent: `KbLeft = min X`, `KbRight = max(X+Width)`, `KbTop = min Y`, `KbBottom = max(Y+Height)` over `F75Layout.Keys` (30, 630, 30, 263).
- Horizontal edge key (top/bottom): screen x-range `[(X-KbLeft)/(KbRight-KbLeft)·W, (X+Width-KbLeft)/(KbRight-KbLeft)·W)`; y-range top `[0, depth)`, bottom `[H-depth, H)` with `depth = max(1, round(EdgeDepth·H))`.
- Vertical edge key (left/right): y-range `[(Y-KbTop)/(KbBottom-KbTop)·H, (Y+Height-KbTop)/(KbBottom-KbTop)·H)`; x-range left `[0, depth)`, right `[W-depth, W)` with `depth = max(1, round(EdgeDepth·W))`.
- Ranges are floored/ceiled to ints, clamped to the image, and at least 1 pixel wide/high.
- Colour = average of B,G,R over the region (rounded).
- Interior keys (not in any edge list) = nearest edge key by squared distance between key centres, each channel × `InteriorScale`, rounded.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/EdgeMapperTests.cs`:

```csharp
using F75Studio.Lighting;

namespace F75Studio.Tests;

public class EdgeMapperTests
{
    private static CapturedFrame Frame(int w, int h, Func<int, int, Rgb> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixel(x, y);
            var i = (y * w + x) * 4;
            data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = 255;
        }
        return new CapturedFrame(w, h, data);
    }

    private static Rgb Led(IReadOnlyDictionary<int, Rgb> map, string key) => map[F75Layout.Keys.Single(k => k.Name == key).Led];

    [Fact]
    public void Top_half_red_bottom_half_blue()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (_, y) => y < 18 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255)));

        Assert.All(EdgeMapper.TopKeys, k => Assert.Equal(new Rgb(255, 0, 0), Led(map, k)));
        Assert.All(EdgeMapper.BottomKeys, k => Assert.Equal(new Rgb(0, 0, 255), Led(map, k)));
        Assert.Equal(80, map.Count);
    }

    [Fact]
    public void Left_and_right_stripes_drive_the_side_columns()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (x, _) => x < 8 ? new Rgb(0, 255, 0) : x >= 56 ? new Rgb(255, 255, 0) : Rgb.Black));

        Assert.All(EdgeMapper.LeftKeys, k => Assert.Equal(new Rgb(0, 255, 0), Led(map, k)));
        Assert.All(EdgeMapper.RightKeys, k => Assert.Equal(new Rgb(255, 255, 0), Led(map, k)));
    }

    [Fact]
    public void Top_edge_follows_horizontal_position()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (x, _) => x < 32 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255)));

        Assert.Equal(new Rgb(255, 0, 0), Led(map, "Esc"));
        Assert.Equal(new Rgb(0, 0, 255), Led(map, "F12"));
    }

    [Fact]
    public void Interior_keys_take_the_nearest_edge_dimmed()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (_, _) => new Rgb(200, 100, 50)));

        Assert.Equal(new Rgb(60, 30, 15), Led(map, "G"));
        Assert.Equal(new Rgb(200, 100, 50), Led(map, "Esc"));
    }

    [Fact]
    public void Edge_lists_cover_the_documented_keys()
    {
        Assert.Equal(13, EdgeMapper.TopKeys.Count);
        Assert.Equal(new[] { "`", "Tab", "CapsLock", "LShift" }, EdgeMapper.LeftKeys);
        Assert.Equal(new[] { "Delete", "PgUp", "PgDn", "End" }, EdgeMapper.RightKeys);
        Assert.Contains("Space", EdgeMapper.BottomKeys);
    }

    [Fact]
    public void Tiny_frames_still_map_every_key()
    {
        var map = EdgeMapper.Map(Frame(2, 2, (_, _) => Rgb.White));
        Assert.Equal(80, map.Count);
    }
}
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~EdgeMapperTests` → build fails.

- [ ] **Step 3: Implement** `ScreenSyncContracts.cs` exactly as above, and `EdgeMapper.cs`:

```csharp
namespace F75Studio.Lighting;

/// <summary>Maps a downscaled screen image onto the F75's keys, TV-ambilight style (screen-sync spec).</summary>
public static class EdgeMapper
{
    public const double EdgeDepth = 0.06;
    public const double InteriorScale = 0.30;

    public static IReadOnlyList<string> TopKeys { get; } = ["Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];
    public static IReadOnlyList<string> BottomKeys { get; } = ["LCtrl", "LWin", "LAlt", "Space", "Fn", "RCtrl", "Left", "Down", "Right"];
    public static IReadOnlyList<string> LeftKeys { get; } = ["`", "Tab", "CapsLock", "LShift"];
    public static IReadOnlyList<string> RightKeys { get; } = ["Delete", "PgUp", "PgDn", "End"];

    private static readonly double KbLeft = F75Layout.Keys.Min(k => k.X);
    private static readonly double KbRight = F75Layout.Keys.Max(k => k.X + k.Width);
    private static readonly double KbTop = F75Layout.Keys.Min(k => k.Y);
    private static readonly double KbBottom = F75Layout.Keys.Max(k => k.Y + k.Height);

    private enum Edge { Top, Bottom, Left, Right }

    private static readonly IReadOnlyList<(KeyDef Key, Edge Edge)> EdgeKeys =
        TopKeys.Select(n => (Find(n), Edge.Top))
            .Concat(BottomKeys.Select(n => (Find(n), Edge.Bottom)))
            .Concat(LeftKeys.Select(n => (Find(n), Edge.Left)))
            .Concat(RightKeys.Select(n => (Find(n), Edge.Right)))
            .ToList();

    // For each interior key, the edge key it copies (fixed: depends only on the layout).
    private static readonly IReadOnlyList<(KeyDef Interior, KeyDef Source)> InteriorKeys =
        F75Layout.Keys
            .Where(k => EdgeKeys.All(e => e.Key.Led != k.Led))
            .Select(k => (k, EdgeKeys.MinBy(e => DistanceSquared(k, e.Key)).Key))
            .ToList();

    public static IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame)
    {
        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var (key, edge) in EdgeKeys) result[key.Led] = Average(frame, Region(frame, key, edge));
        foreach (var (interior, source) in InteriorKeys)
        {
            var c = result[source.Led];
            result[interior.Led] = new Rgb(Scale(c.R), Scale(c.G), Scale(c.B));
        }
        return result;
    }

    private static (int X0, int Y0, int X1, int Y1) Region(CapturedFrame f, KeyDef key, Edge edge)
    {
        int w = f.Width, h = f.Height;
        var depthY = Math.Max(1, (int)Math.Round(EdgeDepth * h));
        var depthX = Math.Max(1, (int)Math.Round(EdgeDepth * w));
        return edge switch
        {
            Edge.Top => (SpanX(key, w).Start, 0, SpanX(key, w).End, depthY),
            Edge.Bottom => (SpanX(key, w).Start, h - depthY, SpanX(key, w).End, h),
            Edge.Left => (0, SpanY(key, h).Start, depthX, SpanY(key, h).End),
            _ => (w - depthX, SpanY(key, h).Start, w, SpanY(key, h).End),
        };
    }

    private static (int Start, int End) SpanX(KeyDef k, int w) =>
        Span((k.X - KbLeft) / (KbRight - KbLeft), (k.X + k.Width - KbLeft) / (KbRight - KbLeft), w);

    private static (int Start, int End) SpanY(KeyDef k, int h) =>
        Span((k.Y - KbTop) / (KbBottom - KbTop), (k.Y + k.Height - KbTop) / (KbBottom - KbTop), h);

    private static (int Start, int End) Span(double from, double to, int size)
    {
        var start = Math.Clamp((int)Math.Floor(from * size), 0, size - 1);
        var end = Math.Clamp((int)Math.Ceiling(to * size), start + 1, size);
        return (start, end);
    }

    private static Rgb Average(CapturedFrame f, (int X0, int Y0, int X1, int Y1) r)
    {
        long b = 0, g = 0, red = 0, n = 0;
        for (var y = r.Y0; y < r.Y1; y++)
        for (var x = r.X0; x < r.X1; x++)
        {
            var i = (y * f.Width + x) * 4;
            b += f.Bgra[i];
            g += f.Bgra[i + 1];
            red += f.Bgra[i + 2];
            n++;
        }
        return n == 0 ? Rgb.Black : new Rgb((byte)Math.Round((double)red / n), (byte)Math.Round((double)g / n), (byte)Math.Round((double)b / n));
    }

    private static byte Scale(byte v) => (byte)Math.Round(v * InteriorScale);

    private static double DistanceSquared(KeyDef a, KeyDef b)
    {
        double dx = (a.X + a.Width / 2.0) - (b.X + b.Width / 2.0);
        double dy = (a.Y + a.Height / 2.0) - (b.Y + b.Height / 2.0);
        return dx * dx + dy * dy;
    }

    private static KeyDef Find(string name) => F75Layout.Keys.Single(k => k.Name == name);
}
```

- [ ] **Step 4: Run** focused → 6 passed; full suite → all pass.

- [ ] **Step 5: Commit** `feat: screen sync contracts and edge mapper`.

---

### Task 3: Live session on the device

**Files:**
- Modify: `src/F75Studio.Lighting.Aula/AulaF75Device.cs`, `src/F75Studio.Lighting/FakeKeyboardDevice.cs`
- Test: `tests/F75Studio.Tests/AulaF75LiveTests.cs`

**Interfaces:**
- Consumes: `ILiveLighting` (Task 2), `F75Session.SendLiveFrameAsync`, `F75Protocol.BuildLiveFrame` (Task 1), existing `IHidProbe`/`IWiredBoard`/fake probe used by `AulaF75DeviceTests`.
- Produces: `AulaF75Device : IKeyboardDevice, ILiveLighting`; `FakeKeyboardDevice : IKeyboardDevice, ILiveLighting` with `bool IsLive`, `List<IReadOnlyDictionary<int, Rgb>> LiveFrames`, `int LiveStarts`, `int LiveStops`, `Exception? LiveFailWith`.

`AulaF75Device` behaviour:
- `StartLiveAsync`: under `_gate`: `ThrowIfDisposed`; status must be `Connected` (else `KeyboardUnavailableException(StatusText)`); official app running → refresh + throw (same text as apply); wait the 500 ms transaction gap; find the first board whose identity is known (same scan as apply); keep its channel and an `F75Session` in fields (`_liveChannel`, `_liveSession`); `IsLive = true`. If already live, return.
- `SendLiveAsync(colors)`: under `_gate`: if not live → `KeyboardUnavailableException("Screen sync isn't running.")`; `await _liveSession.SendLiveFrameAsync(F75Protocol.BuildLiveFrame(colors), ct)`; on `IOException`/`TimeoutException`/`UnauthorizedAccessException` → end the live session (dispose channel, `IsLive = false`, stamp transaction end), `ScheduleRefresh()`, throw `KeyboardUnavailableException("Lost the keyboard while streaming: …")`. No 500 ms gap between frames.
- `StopLiveAsync`: under `_gate`: dispose channel if any, clear fields, `IsLive = false`, stamp `_lastTransactionEnd`. Idempotent; never throws.
- `ApplyAsync`: if live, end the live session first (same as stop) before the normal path.
- `Dispose`: ends any live session (best effort, inside the bounded gate wait).
- Status leaving `Connected` (refresh): if live, end the live session.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/AulaF75LiveTests.cs` — reuse the fake probe/board/channel helpers that `AulaF75DeviceTests` already defines (read that file first; if they're private to that class, move them into `TestSupport/FakeHidProbe.cs` as `internal` types and update `AulaF75DeviceTests` to use them — note the move in the report). Tests:

```csharp
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class AulaF75LiveTests
{
    // Helpers: CreateConnectedDevice(out FakeFeatureChannel channel) builds an AulaF75Device over a fake probe with one
    // wired board whose channel answers identity 03 00 00 00 00 CD, transaction gap 0 ms, and waits until the startup
    // refresh finished (device.IsRefreshing == false) and Status == Connected.

    [Fact]
    public async Task Live_session_checks_identity_then_streams_0x08_frames_only()
    {
        using var device = CreateConnectedDevice(out var channel);
        channel.Sent.Clear();

        await device.StartLiveAsync();
        await device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = new(255, 0, 0) });
        await device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = new(0, 255, 0) });
        await device.StopLiveAsync();

        Assert.Equal(new byte[] { 0x82, 0x08, 0x08 }, channel.Sent.Select(f => f[1]).ToArray());
        Assert.Equal(0, channel.Sent[1][9]);
        Assert.Equal(255, channel.Sent[2][9]);
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Live_start_refused_when_not_on_the_cable()
    {
        using var device = CreateBluetoothDevice(); // fake probe: no wired boards, Bluetooth present
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.StartLiveAsync());
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Live_start_refused_for_unknown_identity()
    {
        using var device = CreateConnectedDevice(out var channel);
        channel.Identity = [3, 0, 0, 0, 0, 0x8F];
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.StartLiveAsync());
        Assert.DoesNotContain(channel.Sent, f => f[1] == 0x08);
    }

    [Fact]
    public async Task Sending_without_a_session_is_refused()
    {
        using var device = CreateConnectedDevice(out _);
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.SendLiveAsync(new Dictionary<int, Rgb>()));
    }

    [Fact]
    public async Task Stored_apply_ends_the_live_session_first()
    {
        using var device = CreateConnectedDevice(out _);
        await device.StartLiveAsync();
        await device.ApplyAsync(new LightingState { EffectId = 3, Brightness = 5 });
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Transfer_failure_ends_the_session_with_a_clear_error()
    {
        using var device = CreateConnectedDevice(out var channel);
        await device.StartLiveAsync();
        channel.FailNextWriteSets = 2; // initial try + the single retry

        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = Rgb.White }));
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Fake_device_records_live_frames()
    {
        var fake = new FakeKeyboardDevice();
        await fake.StartLiveAsync();
        await fake.SendLiveAsync(new Dictionary<int, Rgb> { [0] = Rgb.White });
        await fake.StopLiveAsync();
        Assert.Equal(1, fake.LiveStarts);
        Assert.Single(fake.LiveFrames);
        Assert.Equal(1, fake.LiveStops);

        fake.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => fake.StartLiveAsync());
    }
}
```

`FakeKeyboardDevice` additions: `StartLiveAsync` throws `KeyboardUnavailableException(StatusText)` unless `Status == Connected` or `LiveFailWith` is set (throw it); otherwise `IsLive = true; LiveStarts++`. `SendLiveAsync` throws `LiveFailWith` if set, `KeyboardUnavailableException` if not live, else records a copy. `StopLiveAsync` → `IsLive = false; LiveStops++`. Use a lock around the lists (the engine calls from a background thread).

- [ ] **Step 2: Run** focused → fails to build.
- [ ] **Step 3: Implement** per the behaviour list. Keep every await `ConfigureAwait(false)`; never call device I/O on the UI thread.
- [ ] **Step 4: Run** focused → 7 passed; full suite → all pass.
- [ ] **Step 5: Commit** `feat: live lighting session on the F75 device`.

---

### Task 4: Screen sync engine

**Files:**
- Create: `src/F75Studio.Lighting/ScreenSyncEngine.cs`
- Create: `tests/F75Studio.Tests/TestSupport/FakeScreenCapture.cs`
- Test: `tests/F75Studio.Tests/ScreenSyncEngineTests.cs`

**Interfaces:**
- Consumes: `IScreenCapture`, `ILiveLighting`, `IScreenSync`, `CapturedFrame`, `ScreenCaptureException`, `EdgeMapper` (Task 2), `KeyboardUnavailableException`.
- Produces:

```csharp
public sealed record ScreenSyncTiming
{
    public int FramePeriodMs { get; init; } = 40;
    public int KeepAliveMs { get; init; } = 500;
    public int CaptureRetryMs { get; init; } = 500;
    public double Smoothing { get; init; } = 0.35;
    public int StopTimeoutMs { get; init; } = 1000;
}

public sealed class ScreenSyncEngine : IScreenSync
{
    public ScreenSyncEngine(Func<IScreenCapture> captureFactory, ILiveLighting live, ScreenSyncTiming? timing = null,
        Func<long>? nowMs = null, Func<int, CancellationToken, Task>? delay = null);
    // IScreenSync members
}
```

Loop (runs via `Task.Run`, never on the UI thread):
1. `StartAsync(brightness)`: if running, return. `await live.StartLiveAsync(ct)` (exceptions propagate to the caller; engine stays stopped). Create the capture via the factory (a `ScreenCaptureException` here → stop live, rethrow). Start the loop; `IsRunning = true`.
2. Each cycle (`start = now`):
   - `TryCapture(FramePeriodMs)`. On a frame: `target = EdgeMapper.Map(frame)`; if capture was paused, raise `CaptureStatusChanged(null)`. On `null`: keep the previous target. On `ScreenCaptureException`: raise `CaptureStatusChanged("Screen capture paused: " + message)` once per pause; dispose the capture, `delay(CaptureRetryMs)`, recreate it via the factory (exceptions from the factory keep the pause going), and use `target = lastGood × 0.5` for this cycle.
   - Smoothing per LED, per channel: `s += (t - s) × Smoothing` (doubles; start from the first target directly).
   - Output = `round(s × factor)` with `factor = b == 0 ? 0 : 0.25 + 0.75·b/9`, `b = brightness()` clamped 0–9.
   - Send when output differs from the last sent output **or** `now - lastSend ≥ KeepAliveMs`. After a send: raise `FrameSent(output)`.
   - `KeyboardUnavailableException` / `IOException` from sending → stop the loop, best-effort `StopLiveAsync`, dispose capture, `IsRunning = false`, raise `Faulted(message)`.
   - `remaining = FramePeriodMs - (now - start)`; if `> 0` → `delay(remaining)`; else continue immediately (overdue frame dropped, never queued).
3. `StopAsync`: cancel the loop, wait for it up to `StopTimeoutMs`, `StopLiveAsync`, dispose capture, `IsRunning = false`. Idempotent. Does **not** raise `Faulted`.

`FakeScreenCapture` (tests): queue of results (`CapturedFrame`, `null`, or an exception to throw); when the queue is empty it returns `null`; counts `Created`/`Disposed` via a shared factory helper.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/ScreenSyncEngineTests.cs` — use a fake clock (`long now`) whose `delay(ms)` advances `now` by `ms` and yields (`await Task.Yield()`), and a `FakeKeyboardDevice` as `ILiveLighting`. To make each test deterministic, wait for conditions with a helper `await WaitUntil(() => cond, timeoutMs: 2000)`. Tests:

1. `Streams_mapped_edge_colours`: capture returns one all-red 64×36 frame then nulls; brightness 9 → after the first send, `LiveFrames[0][Esc led] == (255,0,0)` and interior `G == (76,0,0)` (255×0.3 = 76.5 → `Math.Round` banker's rounding → 76).
2. `Smoothing_moves_35_percent_per_frame`: frames red then blue (each once, then nulls). Second sent Esc colour = `(166, 0, 89)` (255 + (0−255)·0.35 = 165.75 → 166; 0 + 255·0.35 = 89.25 → 89).
3. `Identical_output_is_only_resent_as_keep_alive`: one frame then nulls for 30 cycles (40 ms each = 1200 ms of fake time) → sends at t≈0, then keep-alives every 500 ms only: total 3 sends (assert `LiveFrames.Count == 3`).
4. `Brightness_scales_output_and_zero_is_black`: brightness func returns 0 → first frame all black.
5. `Send_failure_faults_and_stops`: `LiveFailWith = new KeyboardUnavailableException("unplugged")` after start → `Faulted` raised with "unplugged", `IsRunning == false`, `LiveStops == 1`, capture disposed.
6. `Capture_failure_pauses_dims_and_recovers`: queue: red frame, throw `ScreenCaptureException("locked")`, then red frame → `CaptureStatusChanged` gets "Screen capture paused: locked" then `null`; a frame sent during the pause has Esc ≤ 128 red; the capture factory was called twice.
7. `Overrun_drops_instead_of_queueing`: capture fake advances the clock by 100 ms per call (slow capture) → no `delay` calls with positive values happen between cycles (assert recorded delays contain no value > 0 except the 7 ms the device fake doesn't add — engine-level delays list must be empty) and sends == cycles.
8. `Start_failure_propagates_and_leaves_engine_stopped`: device status Bluetooth → `StartAsync` throws `KeyboardUnavailableException`; `IsRunning == false`; capture factory never called.
9. `Stop_is_idempotent_and_releases_everything`: start, stop, stop → `LiveStops == 1`, capture disposed once, no `Faulted`.

- [ ] **Step 2: Run** focused → fails to build.
- [ ] **Step 3: Implement** `ScreenSyncEngine` per the loop description. Use `Interlocked`/locks for `IsRunning`; event invocations wrapped so a throwing subscriber can't kill the loop.
- [ ] **Step 4: Run** focused → 9 passed (run 3 times to check for flakiness); full suite passes.
- [ ] **Step 5: Commit** `feat: screen sync engine (capture, map, smooth, stream)`.

---

### Task 5: DXGI Desktop Duplication capture

**Files:**
- Create: `src/F75Studio.App/Services/DxgiScreenCapture.cs`
- Modify: `src/F75Studio.App/F75Studio.App.csproj` (packages)
- Test: `tests/F75Studio.Tests/DxgiScreenCaptureTests.cs`

**Interfaces:**
- Consumes: `IScreenCapture`, `CapturedFrame`, `ScreenCaptureException` (Task 2).
- Produces: `public sealed class DxgiScreenCapture : IScreenCapture` with `public const int TargetWidth = 64;` and a public parameterless constructor (primary adapter, output 0).

```powershell
dotnet add src/F75Studio.App package Vortice.Direct3D11 --version 3.8.3
dotnet add src/F75Studio.App package Vortice.DXGI --version 3.8.3
```

Implementation outline (Vortice 3.x; check exact member names in `%USERPROFILE%\.nuget\packages\vortice.direct3d11\3.8.3` and `vortice.dxgi\3.8.3` XML docs and adapt, documenting any difference):
1. Constructor: `D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out _device, out _context)`; get the DXGI device → adapter → `EnumOutputs(0)` → `QueryInterface<IDXGIOutput1>()` → `DuplicateOutput(_device)`. Read the output description (desktop size). If the duplication's `ModeDesc.Format` is not `B8G8R8A8_UNorm` → `ScreenCaptureException("HDR or non-standard desktop format isn't supported yet.")`.
2. Choose mip level `L` = smallest level with `(width >> L) <= TargetWidth` (at least 0). Create one GPU texture: desktop size, `MipLevels = L + 1`, `BindFlags = RenderTarget | ShaderResource`, `MiscFlags = GenerateMips`, format BGRA; its shader resource view; and one staging texture of size `max(1, w >> L) × max(1, h >> L)` with `CpuAccessFlags.Read`, `Usage.Staging`.
3. `TryCapture(timeoutMs)`: `AcquireNextFrame(timeoutMs, out info, out resource)`:
   - wait-timeout result → return `null`;
   - access-lost result → release, recreate the duplication (steps 1–2) and return `null`; if recreation fails → `ScreenCaptureException`;
   - other failure → `ScreenCaptureException`.
   - Otherwise: `CopySubresourceRegion(mipTexture, 0, 0, 0, 0, desktopTexture, 0)`; `GenerateMips(srv)`; `CopySubresourceRegion(staging, 0, 0, 0, 0, mipTexture, L)`; `ReleaseFrame()`; `Map(staging, 0, MapMode.Read)` and copy row by row (respecting `RowPitch`) into a tight `byte[w*h*4]`; `Unmap`. Return `new CapturedFrame(w, h, bytes)`.
   - Always `ReleaseFrame` in a `finally` when a frame was acquired.
4. `Dispose` releases everything (idempotent).
5. COM exceptions (`SharpGenException` in Vortice) → wrap in `ScreenCaptureException`.

- [ ] **Step 1: Test (gated on real hardware)**

`tests/F75Studio.Tests/DxgiScreenCaptureTests.cs`:

```csharp
using F75Studio.App.Services;
using F75Studio.Lighting;

namespace F75Studio.Tests;

public class DxgiScreenCaptureTests
{
    // Real GPU/desktop: runs only when F75_CAPTURE_TESTS=1 (set it manually on a desktop session).
    [Fact]
    [Trait("Category", "Integration")]
    public void Captures_a_small_bgra_frame_of_the_primary_screen()
    {
        if (Environment.GetEnvironmentVariable("F75_CAPTURE_TESTS") != "1") return;

        using var capture = new DxgiScreenCapture();
        CapturedFrame? frame = null;
        for (var i = 0; i < 20 && frame is null; i++) frame = capture.TryCapture(100);

        Assert.NotNull(frame);
        Assert.InRange(frame!.Width, 1, DxgiScreenCapture.TargetWidth);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgra.Length);
    }
}
```

- [ ] **Step 2: Implement**, build (0 warnings), run the full suite.
- [ ] **Step 3: Run the gated test for real**: `$env:F75_CAPTURE_TESTS='1'; dotnet test --filter FullyQualifiedName~DxgiScreenCaptureTests; Remove-Item Env:F75_CAPTURE_TESTS`. Record the frame size you got (e.g. 64×36 on the 2048×1152 display) in the report. Do not save or print captured pixel data (the user's screen is private).
- [ ] **Step 4: Commit** `feat: DXGI desktop duplication screen capture`.

---

### Task 6: Wire screen sync into the Keyboard page

**Files:**
- Modify: `src/F75Studio.App/ViewModels/KeyboardViewModel.cs`, `src/F75Studio.App/App.xaml.cs`, `src/F75Studio.App/Views/KeyboardView.xaml` (status text only, if needed)
- Create: `tests/F75Studio.Tests/TestSupport/FakeScreenSync.cs`
- Test: `tests/F75Studio.Tests/KeyboardScreenSyncTests.cs`

**Interfaces:**
- Consumes: `IScreenSync`, `ScreenSyncEngine`, `DxgiScreenCapture`, `ILiveLighting`, `LightingState.BaseEffectId/StoredState()`, `EffectDefinition.IsLive`.
- Produces: `KeyboardViewModel` ctor gains optional `IScreenSync? screenSync = null` and `bool selectPerKeyBeforeLive = false`; new public members `bool IsScreenSyncRunning`, `Task StopScreenSyncAsync()`, `void SetScreenLocked(bool locked)`.
- Produces (tests): `FakeScreenSync : IScreenSync` with `Starts`, `Stops`, `StartFailWith`, `RaiseFrame(...)`, `RaiseFaulted(string)`.

Behaviour:
1. **Selecting Screen sync**: remember the previously selected non-live effect as `_baseEffectId` (saved into the profile as `BaseEffectId`). `CurrentState` keeps `EffectId = 100`; every device apply uses `CurrentState.StoredState()`.
2. **Stored applies while the live effect is selected** happen only when forced (profile switch, Connected transition, stop). Non-forced changes (brightness, etc.) only save the profile (debounced as today) — no device write.
3. **Engine control** (`UpdateScreenSync()`, called after effect changes, profile loads, device status changes, lock changes):
   - Should run = selected effect is live **and** device status is `Connected` **and** not locked **and** `screenSync != null`.
   - Start: if `selectPerKeyBeforeLive`, first `await device.ApplyAsync(stored state with EffectId = PerKey, KeyColors empty)`; then `await screenSync.StartAsync(() => Volatile.Read(ref _brightnessForLive))`. On failure: Warning status with the message.
   - Stop (should not run but is running): `await screenSync.StopAsync()`, then `ScheduleApply(force: true)` to restore the stored effect (unless the app is exiting).
   - Status texts: live selected but not on the cable → Informational "Screen sync needs the USB cable."; running → Success "Screen sync is running."; capture paused → Warning with the engine's text.
4. **Preview**: `FrameSent` → `Ui.Run`, throttled to one update per 100 ms, sets each key's preview colour from the frame. When the live effect is selected but not running, the preview shows the base effect's preview.
5. **Faulted** → Warning status with the reason; `IsScreenSyncRunning = false`; no automatic restart until the next `Connected` transition or effect selection.
6. `StopScreenSyncAsync()` stops the engine without restoring (used on exit). `SetScreenLocked(true)` stops (with restore); `false` re-evaluates.
7. `_brightnessForLive` mirrors `Brightness` (written on the UI thread, read by the engine via `Volatile.Read`).

App wiring (`App.xaml.cs`):
- Create `var screenSync = new ScreenSyncEngine(() => new DxgiScreenCapture(), _keyboardDevice!);` and pass it to `KeyboardViewModel`.
- `Microsoft.Win32.SystemEvents.SessionSwitch`: `SessionLock` → `Ui.Run(() => _keyboard?.SetScreenLocked(true))`; `SessionUnlock` → `false`. Unsubscribe on exit.
- `ExitApp` and the `SessionEnding` handler: call `_keyboard.StopScreenSyncAsync()` (wait up to 1 s) **before** `FlushAsync` and device disposal.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/KeyboardScreenSyncTests.cs` (FakeKeyboardDevice + FakeScreenSync + zero debounce):

1. `Selecting_screen_sync_on_cable_starts_the_engine_and_keeps_the_base_effect`: start on Static (id 1); select Screen sync → `Starts == 1`; profile saved with `EffectId 100`, `BaseEffectId 1`; no device apply has `EffectId 100` (`_device.Applied.All(s => s.EffectId != 100)`).
2. `On_bluetooth_it_waits_for_the_cable`: device status Bluetooth → selecting Screen sync → `Starts == 0`, status message "Screen sync needs the USB cable."; then `SetStatus(Connected)` → `Starts == 1`.
3. `Switching_away_stops_and_restores_the_stored_effect`: running → select Breathing → `Stops == 1`, last `Applied` has `EffectId 2`.
4. `Brightness_changes_while_live_do_not_write_flash`: running; record `Applied.Count`; set Brightness 3, 5, 7 (await PendingApply) → `Applied.Count` unchanged; profile saved brightness 7.
5. `Fault_shows_reason_and_stops`: running → `RaiseFaulted("unplugged")` → Warning status containing "unplugged", `IsScreenSyncRunning == false`.
6. `Lock_stops_and_unlock_restarts`: running → `SetScreenLocked(true)` → `Stops == 1`; `SetScreenLocked(false)` → `Starts == 2`.
7. `Per_key_first_flag_writes_effect_21_before_streaming`: ctor `selectPerKeyBeforeLive: true` → selecting Screen sync on cable → an `Applied` entry with `EffectId 21` precedes `Starts == 1`.
8. `Loading_a_screen_sync_profile_starts_it`: profiles.json with active profile EffectId 100, BaseEffectId 2 → ctor on cable → `Starts == 1`; the forced initial apply wrote `EffectId 2`.
9. `Frames_update_the_preview`: running → `RaiseFrame({Esc: red})` → Esc key preview red (throttle: first frame always shown).
10. `Stop_for_exit_does_not_restore`: running → `await StopScreenSyncAsync()` → `Stops == 1`, no new `Applied`.

- [ ] **Step 2: Run** focused → fails to build.
- [ ] **Step 3: Implement** VM changes and App wiring. Keep all existing KeyboardViewModel tests passing.
- [ ] **Step 4: Build, test, smoke**: `dotnet build` 0 warnings; `dotnet test` all pass. Smoke-launch `dotnet src/F75Studio.App/bin/Debug/net8.0-windows/F75Studio.dll` ~8 s with nothing clicked (keyboard is on Bluetooth): no crash, Effect list contains "Screen sync (edges)". Stop only your process; don't change the user's profiles.
- [ ] **Step 5: Commit** `feat: screen sync on the Keyboard page`.

---

### Task 7: Hardware verification (controller + user, no subagent)

Needs the F75 on the **USB cable**, official AULA app closed.

- [ ] Select Screen sync (edges). Status "Screen sync is running."; keys follow the screen edges (move a bright window to each edge).
- [ ] If the keys don't change: set `selectPerKeyBeforeLive: true` in App wiring, rebuild, retry; record the result in protocol doc §8 / §14 Q3.
- [ ] Smoothness/latency acceptable (≈25 fps); CPU usage of F75 Studio < ~3 % in Task Manager.
- [ ] Exclusive-fullscreen game: edges still update.
- [ ] Switch back to Static → keyboard shows the stored Static colour again; unplug during streaming → status changes, no crash.
- [ ] Lock the PC (Win+L) and unlock → streaming stops and resumes.
