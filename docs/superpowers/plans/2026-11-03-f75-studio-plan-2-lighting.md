# F75 Studio — Plan 2: Keyboard Lighting (editor + wired F75 driver)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Keyboard page where the user sets the AULA F75's lighting (built-in effects with brightness/speed/colour, and per-key custom colours) and saves it as profiles, driving the real keyboard over the USB cable.

**Architecture:** `F75Studio.Lighting` holds the device-independent model (colours, effect catalogue, key layout, state, `IKeyboardDevice`, `FakeKeyboardDevice`). `F75Studio.Lighting.Aula` holds the wire protocol as pure functions (`F75Protocol`), a paced transaction layer over an abstract feature-report channel (`F75Session`), and the HidSharp-backed `AulaF75Device`. The WPF app gets a `KeyboardViewModel`/`KeyboardView`, lighting profiles in `profiles.json`, a tray Lighting submenu and `--profile` handling.

**Tech Stack:** .NET 8, WPF + WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, HidSharp 2.6.4, xUnit.

**Protocol source of truth:** `docs/protocol/aula-f75.md` (committed). Every byte value in this plan comes from it; section numbers below refer to it.

**Scope decisions (from the protocol research):**
- Lighting is written **only over the USB cable** (`258A:010C`). Bluetooth: never send anything. 2.4 GHz dongle: detected and reported as "not supported yet" (dongle per-key format is unverified, §9.4).
- No firmware, keymap, macro or reset commands, ever. The protocol layer enforces an **allowlist** (§10) and refuses to write before a matching identity read (§3.1).
- Settings/colour table/per-key writes are stored in the keyboard's flash (§3.3), so the app **debounces** writes (500 ms after the last change) instead of streaming them. The volatile `0x08` live mode is out of scope.
- Hidden effects 9, 14, 18 are not offered (§5).
- The spec's "Save to keyboard" button is dropped: every write is already stored on the keyboard.
- Speed: raw 0–4; the UI labels it "0 = fastest" until the hardware check (Task 7) confirms the direction.

## Global Constraints

- Target frameworks: `net8.0` for `F75Studio.Lighting` and `F75Studio.Lighting.Aula`; App and Tests stay `net8.0-windows`.
- Package: `HidSharp` **2.6.4** (in `F75Studio.Lighting.Aula` only).
- Wired device: VID `0x258A`, PID `0x010C`, feature report `0x06`, frame length **520** bytes, header `06 cmd param 00 01 00 lenLo lenHi`.
- Allowed wired commands only: `0x82` (param 1, len 6), `0x84`/`0x04` (param 0, len 128), `0x8A`/`0x0A` (param 0, len 512), `0x86`/`0x06` (param 0, len 378). Anything else throws before reaching the device.
- Accepted identities (bytes 8–13 of the `0x82` reply): `03 00 00 00 00 CD`, `03 00 00 00 00 E1`, `03 00 00 00 01 91`. Any other → never write.
- Settings page and colour table are always read-modify-write and must carry `5A A5` (settings bytes 126–127, colour table bytes 506–507).
- Pacing (§3.4): 20 ms before every transfer; 30 ms before a per-key write; 120 ms before a colour-table write; 60 ms (100 ms after a colour-table/per-key write) before the settings write; settings/colour-table reads retried up to 5× 100 ms apart until the marker is present; SET_FEATURE retried up to 3× 70 ms apart on `IOException`.
- No code copied from any of the referenced repositories (licenses, §13 of the protocol doc). Implement from the documented facts only.
- Do not ship or read AULA's `KB.ini` at runtime; the key layout is embedded in `F75Layout`.
- All earlier Plan 1 constraints still hold (AppData paths, UTF-8 no BOM, commit trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` or the implementing model's real name).
- Repo root: `C:\Users\ckkos\Documents\Something\F75Studio`, branch `feat/plan-2-lighting`.

## File Structure

```
src/F75Studio.Core/AppPaths.cs                      + ProfilesFile
src/F75Studio.Lighting/
  Rgb.cs                     colour value, hex, HSV
  F75Effects.cs              EffectIds, EffectDefinition, catalogue
  F75Layout.cs               KeyDef + 80 keys (position, LED index) + groups
  LightingState.cs           effect/brightness/speed/colour/per-key state; LightingProfile, LightingProfileSet
  IKeyboardDevice.cs         DeviceStatus, IKeyboardDevice, KeyboardUnavailableException
  FakeKeyboardDevice.cs      in-memory device for UI work and tests
src/F75Studio.Lighting.Aula/
  F75Protocol.cs             frames, allowlist, identity, settings/colour/per-key encoders
  F75Session.cs              IFeatureChannel + paced read/write transactions
  HidSharpFeatureChannel.cs  IFeatureChannel over HidSharp HidStream
  AulaF75Device.cs           discovery, status, hot-plug, serialized apply
src/F75Studio.App/
  ViewModels/KeyItemViewModel.cs, KeyboardViewModel.cs
  Converters/RgbToBrushConverter.cs
  Views/KeyboardView.xaml(.cs)
  App.xaml.cs, Services/TrayService.cs, Views/ViewTemplates.xaml   (modified)
tests/F75Studio.Tests/
  LightingModelTests.cs, F75ProtocolTests.cs, F75SessionTests.cs, AulaF75DeviceTests.cs, KeyboardViewModelTests.cs
  TestSupport/FakeFeatureChannel.cs
```

---

### Task 1: Lighting model

**Files:**
- Create: `src/F75Studio.Lighting/{Rgb,F75Effects,F75Layout,LightingState,IKeyboardDevice,FakeKeyboardDevice}.cs`
- Modify: `src/F75Studio.Core/AppPaths.cs` (add `ProfilesFile`)
- Test: `tests/F75Studio.Tests/LightingModelTests.cs`

**Interfaces:**
- Produces: `readonly record struct Rgb(byte R, byte G, byte B)` with `Black`, `White`, `ToHex()`, `static bool TryParseHex(string?, out Rgb)`, `static Rgb FromHsv(double hue, double saturation, double value)`.
- Produces: `static class EffectIds { Off = 0, FixedOn = 1, PerKey = 21 }`, `record EffectDefinition(int Id, string Name, bool HasSpeed, bool HasBrightness, bool HasColor)`, `static class F75Effects { IReadOnlyList<EffectDefinition> All; EffectDefinition Get(int id); bool IsSupported(int id) }`.
- Produces: `record KeyDef(string Name, int Led, int X, int Y, int Width, int Height)`, `static class F75Layout { const int CanvasWidth = 660; const int CanvasHeight = 290; IReadOnlyList<KeyDef> Keys; IReadOnlyList<string> GroupNames; IReadOnlyList<int> Group(string name) }`.
- Produces: `record LightingState { const int LedCount = 126; int EffectId; int Brightness; int Speed; Rgb Color; bool Colorful; IReadOnlyDictionary<int, Rgb> KeyColors; LightingState Normalize() }`, `record LightingProfile(string Name, LightingState State)`, `record LightingProfileSet { IReadOnlyList<LightingProfile> Profiles; string? Active }`.
- Produces: `enum DeviceStatus { NotFound, Connected, Bluetooth, Dongle, Unsupported, Error }`, `interface IKeyboardDevice : IDisposable { DeviceStatus Status; string StatusText; event EventHandler? StatusChanged; Task ApplyAsync(LightingState, CancellationToken = default) }`, `class KeyboardUnavailableException(string message) : Exception`.
- Produces: `FakeKeyboardDevice : IKeyboardDevice` with `List<LightingState> Applied`, `Exception? FailWith`, `void SetStatus(DeviceStatus, string)`.
- Produces: `AppPaths.ProfilesFile` = `<DataRoot>\profiles.json`.

- [ ] **Step 1: Create the project**

```powershell
dotnet new classlib -n F75Studio.Lighting -o src/F75Studio.Lighting -f net8.0
Remove-Item src/F75Studio.Lighting/Class1.cs
dotnet sln add src/F75Studio.Lighting
dotnet add src/F75Studio.App reference src/F75Studio.Lighting
dotnet add tests/F75Studio.Tests reference src/F75Studio.Lighting
```

- [ ] **Step 2: Write the failing tests**

`tests/F75Studio.Tests/LightingModelTests.cs`:

```csharp
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class LightingModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("#FF8000", 255, 128, 0)]
    [InlineData("00ff7f", 0, 255, 127)]
    public void Hex_parses(string hex, int r, int g, int b)
    {
        Assert.True(Rgb.TryParseHex(hex, out var rgb));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), rgb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    public void Bad_hex_is_rejected(string hex) => Assert.False(Rgb.TryParseHex(hex, out _));

    [Fact]
    public void Hex_round_trips() => Assert.Equal("#0A0B0C", new Rgb(10, 11, 12).ToHex());

    [Fact]
    public void Hsv_primary_hues()
    {
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsv(0, 1, 1));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHsv(120, 1, 1));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHsv(240, 1, 1));
        Assert.Equal(new Rgb(255, 255, 255), Rgb.FromHsv(0, 0, 1));
    }

    [Fact]
    public void Catalogue_hides_masked_effects_and_includes_off_and_per_key()
    {
        var ids = F75Effects.All.Select(e => e.Id).ToList();
        Assert.Contains(EffectIds.Off, ids);
        Assert.Contains(EffectIds.PerKey, ids);
        Assert.DoesNotContain(9, ids);
        Assert.DoesNotContain(14, ids);
        Assert.DoesNotContain(18, ids);
        Assert.False(F75Effects.Get(3).HasColor);
        Assert.False(F75Effects.Get(EffectIds.FixedOn).HasSpeed);
    }

    [Fact]
    public void Layout_has_80_keys_with_unique_leds_inside_the_plane()
    {
        Assert.Equal(80, F75Layout.Keys.Count);
        Assert.Equal(80, F75Layout.Keys.Select(k => k.Led).Distinct().Count());
        Assert.All(F75Layout.Keys, k => Assert.InRange(k.Led, 0, LightingState.LedCount - 1));
        Assert.All(F75Layout.Keys, k => Assert.True(k.X + k.Width <= F75Layout.CanvasWidth && k.Y + k.Height <= F75Layout.CanvasHeight));
        Assert.Equal(0, F75Layout.Keys.Single(k => k.Name == "Esc").Led);
        Assert.Equal(9, F75Layout.Keys.Single(k => k.Name == "A").Led);
    }

    [Fact]
    public void Groups_resolve_to_led_indices()
    {
        Assert.Equal(new[] { 14, 9, 15, 21 }, F75Layout.Group("WASD"));
        Assert.Equal(80, F75Layout.Group("All").Count);
        Assert.Contains("Arrows", F75Layout.GroupNames);
    }

    [Fact]
    public void Normalize_clamps_ranges()
    {
        var s = new LightingState { Brightness = 20, Speed = -3 }.Normalize();
        Assert.Equal(9, s.Brightness);
        Assert.Equal(0, s.Speed);
    }

    [Fact]
    public void Profiles_round_trip_through_json()
    {
        var store = new JsonFileStore<LightingProfileSet>(_temp.Combine("profiles.json"), () => new LightingProfileSet());
        var state = new LightingState
        {
            EffectId = EffectIds.PerKey,
            KeyColors = new Dictionary<int, Rgb> { [14] = new(255, 0, 0) },
        };
        store.Save(new LightingProfileSet { Profiles = [new LightingProfile("Gaming", state)], Active = "Gaming" });

        var loaded = store.Load();

        Assert.Equal("Gaming", loaded.Active);
        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal(EffectIds.PerKey, profile.State.EffectId);
        Assert.Equal(new Rgb(255, 0, 0), profile.State.KeyColors[14]);
    }

    [Fact]
    public async Task Fake_device_records_applies_and_can_fail()
    {
        var device = new FakeKeyboardDevice();
        await device.ApplyAsync(new LightingState());
        Assert.Single(device.Applied);

        device.FailWith = new KeyboardUnavailableException("unplugged");
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));
    }

    [Fact]
    public void AppPaths_has_profiles_file() =>
        Assert.Equal(@"C:\data\profiles.json", new AppPaths(@"C:\data", @"C:\docs").ProfilesFile);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LightingModelTests`
Expected: build FAILS with `The type or namespace name 'Lighting' does not exist in the namespace 'F75Studio'`.

- [ ] **Step 4: Implement**

Add to `src/F75Studio.Core/AppPaths.cs`, next to `HotkeysFile`:

```csharp
    public string ProfilesFile => Path.Combine(DataRoot, "profiles.json");
```

`src/F75Studio.Lighting/Rgb.cs`:

```csharp
using System.Globalization;

namespace F75Studio.Lighting;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? text, out Rgb rgb)
    {
        rgb = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <param name="hue">0–360</param>
    /// <param name="saturation">0–1</param>
    /// <param name="value">0–1</param>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
```

`src/F75Studio.Lighting/F75Effects.cs`:

```csharp
namespace F75Studio.Lighting;

public static class EffectIds
{
    public const int Off = 0;
    public const int FixedOn = 1;
    public const int PerKey = 21;
}

/// <param name="Id">Value written to settings byte 0x0a (protocol doc §5).</param>
/// <param name="HasColor">Effect supports a single colour or "colourful" (random) mode.</param>
public sealed record EffectDefinition(int Id, string Name, bool HasSpeed, bool HasBrightness, bool HasColor);

public static class F75Effects
{
    // Effects 9, 14 and 18 are hidden by the official driver and may leave keys dark (protocol doc §5).
    public static IReadOnlyList<EffectDefinition> All { get; } =
    [
        new(EffectIds.Off, "Off", HasSpeed: false, HasBrightness: false, HasColor: false),
        new(EffectIds.FixedOn, "Static", HasSpeed: false, HasBrightness: true, HasColor: true),
        new(2, "Breathing", true, true, true),
        new(3, "Rainbow", true, true, false),
        new(4, "Flash away", true, true, true),
        new(5, "Raindrops", true, true, true),
        new(6, "Rainbow wheel", true, true, true),
        new(7, "Ripples", true, true, true),
        new(8, "Twinkling stars", true, true, true),
        new(10, "Retro snake", true, true, true),
        new(11, "Neon stream", true, true, true),
        new(12, "Reaction", true, true, true),
        new(13, "Sine wave", true, true, true),
        new(15, "Rotating windmill", true, true, false),
        new(16, "Colorful waterfall", true, true, false),
        new(17, "Blossoming", true, true, false),
        new(EffectIds.PerKey, "Custom per-key", HasSpeed: false, HasBrightness: true, HasColor: false),
    ];

    public static bool IsSupported(int id) => All.Any(e => e.Id == id);

    public static EffectDefinition Get(int id) =>
        All.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported effect.");
}
```

`src/F75Studio.Lighting/F75Layout.cs` (positions and LED indices are the AULA F75 ANSI layout; LED index = protocol doc §7.4):

```csharp
namespace F75Studio.Lighting;

/// <param name="Led">Index into the 126-entry per-key colour planes.</param>
public sealed record KeyDef(string Name, int Led, int X, int Y, int Width, int Height);

public static class F75Layout
{
    public const int CanvasWidth = 660;
    public const int CanvasHeight = 290;

    public static IReadOnlyList<KeyDef> Keys { get; } =
    [
        new("Esc", 0, 30, 30, 25, 27),
        new("F1", 12, 107, 30, 25, 27),
        new("F2", 18, 144, 30, 25, 27),
        new("F3", 24, 183, 30, 25, 27),
        new("F4", 30, 222, 30, 25, 27),
        new("F5", 36, 269, 30, 25, 27),
        new("F6", 42, 305, 30, 25, 27),
        new("F7", 48, 343, 30, 25, 27),
        new("F8", 54, 388, 30, 25, 27),
        new("F9", 60, 439, 30, 25, 27),
        new("F10", 66, 477, 30, 25, 27),
        new("F11", 72, 516, 30, 25, 27),
        new("F12", 78, 555, 30, 25, 27),
        new("`", 1, 31, 81, 25, 27),
        new("1", 7, 70, 81, 25, 27),
        new("2", 13, 108, 81, 25, 27),
        new("3", 19, 147, 81, 25, 27),
        new("4", 25, 184, 81, 25, 27),
        new("5", 31, 223, 81, 25, 27),
        new("6", 37, 262, 81, 25, 27),
        new("7", 43, 300, 80, 25, 27),
        new("8", 49, 338, 81, 25, 27),
        new("9", 55, 377, 81, 25, 27),
        new("0", 61, 416, 81, 25, 27),
        new("-", 67, 453, 81, 25, 27),
        new("=", 73, 491, 81, 25, 27),
        new("Backspace", 79, 531, 81, 60, 27),
        new("Delete", 85, 604, 81, 25, 27),
        new("Tab", 2, 34, 120, 44, 27),
        new("Q", 8, 89, 119, 25, 27),
        new("W", 14, 127, 119, 25, 27),
        new("E", 20, 165, 119, 25, 27),
        new("R", 26, 204, 119, 25, 27),
        new("T", 32, 242, 119, 25, 27),
        new("Y", 38, 280, 119, 25, 27),
        new("U", 44, 318, 119, 25, 27),
        new("I", 50, 356, 119, 25, 27),
        new("O", 56, 395, 119, 25, 27),
        new("P", 62, 433, 119, 25, 27),
        new("[", 68, 471, 120, 25, 27),
        new("]", 74, 509, 119, 25, 27),
        new("\\", 80, 549, 119, 44, 27),
        new("PgUp", 86, 605, 120, 25, 27),
        new("CapsLock", 3, 33, 158, 52, 27),
        new("A", 9, 98, 158, 25, 27),
        new("S", 15, 136, 158, 25, 27),
        new("D", 21, 174, 158, 25, 27),
        new("F", 27, 213, 158, 25, 27),
        new("G", 33, 252, 158, 25, 27),
        new("H", 39, 290, 158, 25, 27),
        new("J", 45, 327, 158, 25, 27),
        new("K", 51, 366, 158, 25, 27),
        new("L", 57, 405, 158, 25, 27),
        new(";", 63, 443, 158, 25, 27),
        new("'", 69, 481, 158, 25, 27),
        new("Enter", 81, 521, 158, 70, 27),
        new("PgDn", 87, 604, 159, 25, 27),
        new("LShift", 4, 33, 195, 72, 27),
        new("Z", 10, 116, 195, 25, 27),
        new("X", 16, 155, 195, 25, 27),
        new("C", 22, 193, 195, 25, 27),
        new("V", 28, 232, 195, 25, 27),
        new("B", 34, 270, 196, 25, 27),
        new("N", 40, 309, 195, 25, 27),
        new("M", 46, 347, 195, 25, 27),
        new(",", 52, 384, 195, 25, 27),
        new(".", 58, 424, 195, 25, 27),
        new("/", 64, 463, 195, 25, 27),
        new("RShift", 70, 502, 195, 56, 27),
        new("Up", 82, 567, 195, 25, 27),
        new("End", 88, 605, 195, 25, 27),
        new("LCtrl", 5, 34, 234, 35, 27),
        new("LWin", 11, 79, 234, 35, 27),
        new("LAlt", 17, 122, 235, 35, 27),
        new("Space", 35, 174, 235, 226, 27),
        new("Fn", 53, 414, 235, 35, 27),
        new("RCtrl", 59, 463, 236, 34, 27),
        new("Left", 77, 529, 234, 25, 27),
        new("Down", 83, 568, 234, 25, 27),
        new("Right", 89, 605, 234, 25, 27),
    ];

    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["All"] = [],
        ["WASD"] = ["W", "A", "S", "D"],
        ["Arrows"] = ["Up", "Down", "Left", "Right"],
        ["F-row"] = ["Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"],
        ["Numbers"] = ["`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="],
        ["Modifiers"] = ["Tab", "CapsLock", "LShift", "RShift", "LCtrl", "RCtrl", "LWin", "LAlt", "Fn"],
    };

    public static IReadOnlyList<string> GroupNames { get; } = Groups.Keys.ToList();

    public static IReadOnlyList<int> Group(string name) =>
        name == "All"
            ? Keys.Select(k => k.Led).ToList()
            : Groups[name].Select(n => Keys.Single(k => k.Name == n).Led).ToList();
}
```

`src/F75Studio.Lighting/LightingState.cs`:

```csharp
namespace F75Studio.Lighting;

public sealed record LightingState
{
    /// <summary>Size of each per-key colour plane (protocol doc §7.1).</summary>
    public const int LedCount = 126;

    public int EffectId { get; init; } = EffectIds.FixedOn;

    /// <summary>0–9.</summary>
    public int Brightness { get; init; } = 9;

    /// <summary>0–4, raw keyboard value (direction verified in Plan 2 Task 7).</summary>
    public int Speed { get; init; } = 2;

    public Rgb Color { get; init; } = new(0, 120, 255);

    /// <summary>Random/multi-colour instead of <see cref="Color"/>, for effects with HasColor.</summary>
    public bool Colorful { get; init; }

    /// <summary>LED index → colour, used by the per-key effect. Missing LEDs are off.</summary>
    public IReadOnlyDictionary<int, Rgb> KeyColors { get; init; } = new Dictionary<int, Rgb>();

    public LightingState Normalize() => this with
    {
        Brightness = Math.Clamp(Brightness, 0, 9),
        Speed = Math.Clamp(Speed, 0, 4),
    };
}

public sealed record LightingProfile(string Name, LightingState State);

public sealed record LightingProfileSet
{
    public IReadOnlyList<LightingProfile> Profiles { get; init; } = [];
    public string? Active { get; init; }
}
```

`src/F75Studio.Lighting/IKeyboardDevice.cs`:

```csharp
namespace F75Studio.Lighting;

public enum DeviceStatus { NotFound, Connected, Bluetooth, Dongle, Unsupported, Error }

public interface IKeyboardDevice : IDisposable
{
    DeviceStatus Status { get; }

    /// <summary>Human-readable explanation of <see cref="Status"/>.</summary>
    string StatusText { get; }

    /// <summary>May be raised on a background thread.</summary>
    event EventHandler? StatusChanged;

    /// <exception cref="KeyboardUnavailableException">The keyboard can't take lighting commands right now.</exception>
    Task ApplyAsync(LightingState state, CancellationToken ct = default);
}

public sealed class KeyboardUnavailableException(string message) : Exception(message);
```

`src/F75Studio.Lighting/FakeKeyboardDevice.cs`:

```csharp
namespace F75Studio.Lighting;

/// <summary>In-memory device: used by tests and when designing profiles without hardware.</summary>
public sealed class FakeKeyboardDevice : IKeyboardDevice
{
    public DeviceStatus Status { get; private set; } = DeviceStatus.Connected;
    public string StatusText { get; private set; } = "Simulated keyboard";
    public List<LightingState> Applied { get; } = [];
    public Exception? FailWith { get; set; }

    public event EventHandler? StatusChanged;

    public void SetStatus(DeviceStatus status, string text)
    {
        Status = status;
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ApplyAsync(LightingState state, CancellationToken ct = default)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        Applied.Add(state);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LightingModelTests`
Expected: 14 passed (2 + 3 hex theory rows, then 9 facts).

- [ ] **Step 6: Commit**

```powershell
git add src/F75Studio.Lighting src/F75Studio.Core/AppPaths.cs tests/F75Studio.Tests F75Studio.sln src/F75Studio.App/F75Studio.App.csproj
git commit -m "feat: lighting model, effect catalogue and F75 key layout" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: F75 protocol encoder (pure)

**Files:**
- Create: `src/F75Studio.Lighting.Aula/F75Protocol.cs`
- Test: `tests/F75Studio.Tests/F75ProtocolTests.cs`

**Interfaces:**
- Consumes: `LightingState`, `Rgb`, `EffectIds`, `F75Effects` (Task 1).
- Produces: `static class F75Protocol` with constants `ReportId = 0x06`, `FrameLength = 520`, `HeaderLength = 8`, `SettingsLength = 128`, `ColorTableLength = 512`, `PerKeyLength = 378`, `PlaneSize = 126`, `SettingsMarkerOffset = 126`, `ColorTableMarkerOffset = 506`, `CmdIdentity = 0x02`, `CmdSettings = 0x04`, `CmdColorTable = 0x0A`, `CmdPerKey = 0x06`, `ReadFlag = 0x80`; methods `byte[] Frame(byte cmd, byte param, int length, ReadOnlySpan<byte> payload = default)`, `byte[] ReadRequest(byte writeCmd, byte param, int length)`, `byte[] ReplyPayload(byte[] reply, byte expectedCmd, int length)`, `bool IsKnownIdentity(ReadOnlySpan<byte>)`, `bool HasMarker(ReadOnlySpan<byte> region, int offset)`, `int SlotFor(int effectId)`, `byte[] PatchSettings(ReadOnlySpan<byte> page, LightingState state)`, `byte[] PatchColorTable(ReadOnlySpan<byte> table, int effectId, Rgb color)`, `byte[] BuildPerKey(IReadOnlyDictionary<int, Rgb> colors)`.
- Produces: `sealed class F75ProtocolException(string message) : Exception`.

- [ ] **Step 1: Create the project**

```powershell
dotnet new classlib -n F75Studio.Lighting.Aula -o src/F75Studio.Lighting.Aula -f net8.0
Remove-Item src/F75Studio.Lighting.Aula/Class1.cs
dotnet sln add src/F75Studio.Lighting.Aula
dotnet add src/F75Studio.Lighting.Aula reference src/F75Studio.Lighting
dotnet add src/F75Studio.Lighting.Aula package HidSharp --version 2.6.4
dotnet add src/F75Studio.App reference src/F75Studio.Lighting.Aula
dotnet add tests/F75Studio.Tests reference src/F75Studio.Lighting.Aula
```

- [ ] **Step 2: Write the failing tests**

`tests/F75Studio.Tests/F75ProtocolTests.cs` (the settings page vector is the OEM "Fixed on" page from protocol doc §4):

```csharp
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests;

public class F75ProtocolTests
{
    public static byte[] FixedOnPage() => Convert.FromHexString(
        "00030301000004040700012001000000" +
        "00000200020100ff0a00000101000301" +
        "00000000000000000000000000000000" +
        "0000000000000000ffff094009470947" +
        "09470947094709470947094709470947" +
        "09470947094709470947094709470947" +
        "09370747074707440744074407440744" +
        "07440744040904040404040404045aa5");

    public static byte[] ColorTable()
    {
        var t = new byte[512];
        t[506] = 0x5A;
        t[507] = 0xA5;
        return t;
    }

    private static string Header(byte[] frame) => Convert.ToHexString(frame, 0, 8);

    [Fact]
    public void Test_vector_is_a_valid_page()
    {
        var page = FixedOnPage();
        Assert.Equal(128, page.Length);
        Assert.True(F75Protocol.HasMarker(page, F75Protocol.SettingsMarkerOffset));
        Assert.Equal(0x01, page[0x0A]);
    }

    [Fact]
    public void Frames_have_the_documented_headers_and_full_length()
    {
        Assert.Equal("0682010001000600", Header(F75Protocol.ReadRequest(F75Protocol.CmdIdentity, 1, 6)));
        Assert.Equal("0684000001008000", Header(F75Protocol.ReadRequest(F75Protocol.CmdSettings, 0, 128)));
        Assert.Equal("0604000001008000", Header(F75Protocol.Frame(F75Protocol.CmdSettings, 0, 128, new byte[128])));
        Assert.Equal("060A000001000002", Header(F75Protocol.Frame(F75Protocol.CmdColorTable, 0, 512, new byte[512])));
        Assert.Equal("0606000001007A01", Header(F75Protocol.Frame(F75Protocol.CmdPerKey, 0, 378, new byte[378])));
        Assert.Equal(520, F75Protocol.Frame(F75Protocol.CmdSettings, 0, 128, new byte[128]).Length);
    }

    [Theory]
    [InlineData(0x05, 0, 6)]      // macro / bootloader family
    [InlineData(0x11, 0, 1)]      // factory reset
    [InlineData(0x03, 0, 512)]    // key matrix
    [InlineData(0x88, 0, 378)]    // memory dump read
    [InlineData(0x04, 0, 512)]    // allowed command, wrong length
    [InlineData(0x04, 1, 128)]    // allowed command, wrong param
    public void Commands_outside_the_allowlist_are_blocked(byte cmd, byte param, int length) =>
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(cmd, param, length));

    [Fact]
    public void Identity_accepts_only_known_f75_models()
    {
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0xCD]));
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0xE1]));
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 1, 0x91]));
        Assert.False(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0x8F])); // F87
        Assert.False(F75Protocol.IsKnownIdentity([0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Reply_payload_requires_the_echoed_command()
    {
        var reply = new byte[520];
        reply[0] = 0x06;
        reply[1] = 0x84;
        reply[8] = 0xAB;
        Assert.Equal(0xAB, F75Protocol.ReplyPayload(reply, 0x84, 128)[0]);
        Assert.Throws<F75ProtocolException>(() => F75Protocol.ReplyPayload(reply, 0x8A, 512));
    }

    [Fact]
    public void Breathing_patches_only_its_bytes()
    {
        var original = FixedOnPage();
        var state = new LightingState { EffectId = 2, Brightness = 5, Speed = 1, Colorful = false };

        var page = F75Protocol.PatchSettings(original, state);

        Assert.Equal(2, page[0x0A]);
        Assert.Equal(0, page[0x09]);
        Assert.Equal(5, page[0x3C]);
        Assert.Equal(0x10, page[0x3D]);
        var changed = Enumerable.Range(0, 128).Where(i => page[i] != original[i]).ToArray();
        Assert.Equal(new[] { 0x0A, 0x3C, 0x3D }, changed);
    }

    [Fact]
    public void Colorful_sets_nibble_seven()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 4, Speed = 3, Colorful = true });
        Assert.Equal(0x37, page[0x3B + 2 * 3]);
    }

    [Fact]
    public void Effect_without_colour_keeps_the_stored_nibble()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 3, Speed = 2 });
        Assert.Equal(0x27, page[0x3F]); // slot 2 was 09 47 → speed 2, nibble 7 kept
    }

    [Fact]
    public void Static_keeps_the_stored_speed()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 1, Brightness = 3, Speed = 0 });
        Assert.Equal(3, page[0x3A]);
        Assert.Equal(0x40, page[0x3B]); // 09 40 → speed 4 kept, colour slot 0
    }

    [Fact]
    public void Per_key_sets_mode_flag_group_and_slot_18_brightness()
    {
        var original = FixedOnPage();
        var page = F75Protocol.PatchSettings(original, new LightingState { EffectId = EffectIds.PerKey, Brightness = 6 });

        Assert.Equal(0x15, page[0x0A]);
        Assert.Equal(1, page[0x09]);
        Assert.Equal(0x20, page[0x0B]);
        Assert.Equal(6, page[0x5E]);
        Assert.Equal(original[0x5F], page[0x5F]);
    }

    [Fact]
    public void Off_changes_only_the_effect_and_mode_bytes()
    {
        var original = FixedOnPage();
        var page = F75Protocol.PatchSettings(original, new LightingState { EffectId = EffectIds.Off });
        Assert.Equal(new[] { 0x0A }, Enumerable.Range(0, 128).Where(i => page[i] != original[i]).ToArray());
    }

    [Fact]
    public void Patching_refuses_pages_without_marker_and_unknown_effects()
    {
        var broken = FixedOnPage();
        broken[127] = 0;
        Assert.Throws<F75ProtocolException>(() => F75Protocol.PatchSettings(broken, new LightingState()));
        Assert.Throws<ArgumentOutOfRangeException>(() => F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 9 }));
    }

    [Fact]
    public void Colour_table_row_is_effect_id_slot_zero()
    {
        var table = F75Protocol.PatchColorTable(ColorTable(), 2, new Rgb(1, 2, 3));
        Assert.Equal(new byte[] { 1, 2, 3 }, table[42..45]);
        Assert.Equal(0x5A, table[506]);
        Assert.Throws<F75ProtocolException>(() => F75Protocol.PatchColorTable(new byte[512], 2, Rgb.White));
    }

    [Fact]
    public void Per_key_is_planar_by_led_index()
    {
        var data = F75Protocol.BuildPerKey(new Dictionary<int, Rgb>
        {
            [0] = new(255, 0, 0),
            [9] = new(0, 255, 0),
            [89] = new(0, 0, 255),
            [200] = Rgb.White, // out of range: ignored
        });

        Assert.Equal(378, data.Length);
        Assert.Equal(255, data[0]);
        Assert.Equal(255, data[126 + 9]);
        Assert.Equal(255, data[252 + 89]);
        Assert.Equal(3 * 255, data.Sum(b => b));
    }
}
```

`FixedOnPage()` is the protocol doc §4 hex dump; `Test_vector_is_a_valid_page` checks it.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~F75ProtocolTests`
Expected: build FAILS with `The name 'F75Protocol' does not exist in the current context`.

- [ ] **Step 4: Implement**

`src/F75Studio.Lighting.Aula/F75Protocol.cs`:

```csharp
namespace F75Studio.Lighting.Aula;

public sealed class F75ProtocolException(string message) : Exception(message);

/// <summary>Wired AULA F75 lighting protocol (docs/protocol/aula-f75.md). Pure functions only.</summary>
public static class F75Protocol
{
    public const byte ReportId = 0x06;
    public const int FrameLength = 520;
    public const int HeaderLength = 8;
    public const int SettingsLength = 128;
    public const int ColorTableLength = 512;
    public const int PerKeyLength = 378;
    public const int PlaneSize = 126;
    public const int SettingsMarkerOffset = 126;
    public const int ColorTableMarkerOffset = 506;

    public const byte CmdIdentity = 0x02;   // only ever sent as a read (0x82)
    public const byte CmdSettings = 0x04;
    public const byte CmdColorTable = 0x0A;
    public const byte CmdPerKey = 0x06;
    public const byte ReadFlag = 0x80;

    // (command, param, length) — everything else is refused (protocol doc §10).
    private static readonly HashSet<(byte Cmd, byte Param, int Length)> Allowed =
    [
        (0x82, 1, 6),
        (0x84, 0, SettingsLength), (0x04, 0, SettingsLength),
        (0x8A, 0, ColorTableLength), (0x0A, 0, ColorTableLength),
        (0x86, 0, PerKeyLength), (0x06, 0, PerKeyLength),
    ];

    private static readonly byte[][] KnownIdentities =
    [
        [0x03, 0x00, 0x00, 0x00, 0x00, 0xCD], // AULA F75 (tri-mode)
        [0x03, 0x00, 0x00, 0x00, 0x00, 0xE1], // AULA F75 Wired
        [0x03, 0x00, 0x00, 0x00, 0x01, 0x91], // AULA F75 KR
    ];

    public static byte[] Frame(byte cmd, byte param, int length, ReadOnlySpan<byte> payload = default)
    {
        if (!Allowed.Contains((cmd, param, length)))
            throw new InvalidOperationException($"Blocked F75 command 0x{cmd:X2} (param {param}, length {length}).");
        if (payload.Length > length) throw new ArgumentException("Payload is longer than the declared length.", nameof(payload));

        var frame = new byte[FrameLength];
        frame[0] = ReportId;
        frame[1] = cmd;
        frame[2] = param;
        frame[4] = 1; // one package
        frame[6] = (byte)(length & 0xFF);
        frame[7] = (byte)(length >> 8);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static byte[] ReadRequest(byte writeCmd, byte param, int length) => Frame((byte)(writeCmd | ReadFlag), param, length);

    public static byte[] ReplyPayload(byte[] reply, byte expectedCmd, int length)
    {
        if (reply.Length < HeaderLength + length || reply[0] != ReportId || reply[1] != expectedCmd)
            throw new F75ProtocolException($"Unexpected reply from the keyboard (expected command 0x{expectedCmd:X2}).");
        return reply.AsSpan(HeaderLength, length).ToArray();
    }

    public static bool IsKnownIdentity(ReadOnlySpan<byte> identity)
    {
        foreach (var known in KnownIdentities)
            if (identity.SequenceEqual(known)) return true;
        return false;
    }

    public static bool HasMarker(ReadOnlySpan<byte> region, int offset) =>
        region.Length >= offset + 2 && region[offset] == 0x5A && region[offset + 1] == 0xA5;

    /// <summary>Settings-page slot of an effect (protocol doc §4).</summary>
    public static int SlotFor(int effectId) => effectId switch
    {
        >= 1 and <= 18 => effectId - 1,
        EffectIds.PerKey => 18,
        _ => throw new ArgumentOutOfRangeException(nameof(effectId), effectId, "Effect has no settings slot."),
    };

    /// <summary>Read-modify-write of the 128-byte settings page; only the documented bytes change.</summary>
    public static byte[] PatchSettings(ReadOnlySpan<byte> page, LightingState state)
    {
        if (page.Length != SettingsLength || !HasMarker(page, SettingsMarkerOffset))
            throw new F75ProtocolException("The keyboard's settings page is missing its 5A A5 marker.");
        var definition = F75Effects.Get(state.EffectId);
        var s = state.Normalize();
        var p = page.ToArray();

        p[0x0A] = (byte)s.EffectId;
        p[0x09] = (byte)(s.EffectId == EffectIds.PerKey ? 1 : 0);
        if (s.EffectId == EffectIds.PerKey) p[0x0B] = 0x20;
        p[0x38] = 0xFF;
        p[0x39] = 0xFF;

        if (s.EffectId != EffectIds.Off)
        {
            var slot = SlotFor(s.EffectId);
            var stored = p[0x3B + 2 * slot];
            var speed = definition.HasSpeed ? s.Speed : stored >> 4;
            var colour = definition.HasColor ? (s.Colorful ? 7 : 0) : stored & 0x0F;
            p[0x3A + 2 * slot] = (byte)s.Brightness;
            p[0x3B + 2 * slot] = (byte)((speed << 4) | colour);
        }
        return p;
    }

    /// <summary>Puts <paramref name="color"/> into slot 0 of the effect's colour-table row (protocol doc §6).</summary>
    public static byte[] PatchColorTable(ReadOnlySpan<byte> table, int effectId, Rgb color)
    {
        if (table.Length != ColorTableLength || !HasMarker(table, ColorTableMarkerOffset))
            throw new F75ProtocolException("The keyboard's colour table is missing its 5A A5 marker.");
        if (effectId is < 1 or > 18) throw new ArgumentOutOfRangeException(nameof(effectId), effectId, "Effect has no colour row.");
        var t = table.ToArray();
        var offset = effectId * 21;
        t[offset] = color.R;
        t[offset + 1] = color.G;
        t[offset + 2] = color.B;
        return t;
    }

    /// <summary>378 bytes, planar: R[126], G[126], B[126] by LED index (protocol doc §7.1).</summary>
    public static byte[] BuildPerKey(IReadOnlyDictionary<int, Rgb> colors)
    {
        var data = new byte[PerKeyLength];
        foreach (var (led, color) in colors)
        {
            if (led is < 0 or >= PlaneSize) continue;
            data[led] = color.R;
            data[PlaneSize + led] = color.G;
            data[2 * PlaneSize + led] = color.B;
        }
        return data;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~F75ProtocolTests`
Expected: 19 passed.

- [ ] **Step 6: Commit**

```powershell
git add src/F75Studio.Lighting.Aula tests/F75Studio.Tests F75Studio.sln src/F75Studio.App/F75Studio.App.csproj
git commit -m "feat: AULA F75 lighting protocol encoder with command allowlist" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Paced session over a feature-report channel

**Files:**
- Create: `src/F75Studio.Lighting.Aula/F75Session.cs`
- Create: `tests/F75Studio.Tests/TestSupport/FakeFeatureChannel.cs`
- Test: `tests/F75Studio.Tests/F75SessionTests.cs`

**Interfaces:**
- Consumes: `F75Protocol`, `F75ProtocolException` (Task 2); `LightingState`, `F75Effects`, `EffectIds` (Task 1).
- Produces: `interface IFeatureChannel : IDisposable { void SetFeature(byte[] frame); void GetFeature(byte[] buffer); }`.
- Produces: `F75Session(IFeatureChannel channel, Func<int, CancellationToken, Task>? delay = null)` with `Task<byte[]> ReadIdentityAsync(CancellationToken)`, `Task ApplyAsync(LightingState, CancellationToken)`.
- Produces (tests): `FakeFeatureChannel : IFeatureChannel` with `byte[] Identity`, `byte[] SettingsPage`, `byte[] ColorTable`, `List<byte[]> Sent`, `IEnumerable<byte[]> Writes` (sent frames whose cmd has no read flag), `int FailNextSets` (throw IOException that many times).

- [ ] **Step 1: Write the fake channel and failing tests**

`tests/F75Studio.Tests/TestSupport/FakeFeatureChannel.cs`:

```csharp
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests.TestSupport;

/// <summary>Simulates the keyboard's feature-report endpoint: answers reads, records writes, applies them.</summary>
public sealed class FakeFeatureChannel : IFeatureChannel
{
    private byte[]? _pendingRead;

    public byte[] Identity { get; set; } = [3, 0, 0, 0, 0, 0xCD];
    public byte[] SettingsPage { get; set; } = F75ProtocolTests.FixedOnPage();
    public byte[] ColorTable { get; set; } = F75ProtocolTests.ColorTable();
    public byte[] PerKey { get; private set; } = new byte[378];
    public List<byte[]> Sent { get; } = [];
    public int FailNextSets { get; set; }
    public bool Disposed { get; private set; }

    public IEnumerable<byte[]> Writes => Sent.Where(f => (f[1] & 0x80) == 0);

    public void SetFeature(byte[] frame)
    {
        if (FailNextSets > 0)
        {
            FailNextSets--;
            throw new IOException("simulated transfer failure");
        }
        Sent.Add((byte[])frame.Clone());
        var length = frame[6] | (frame[7] << 8);
        var payload = frame.AsSpan(8, length).ToArray();
        switch (frame[1])
        {
            case 0x82: _pendingRead = Identity; break;
            case 0x84: _pendingRead = SettingsPage; break;
            case 0x8A: _pendingRead = ColorTable; break;
            case 0x04: SettingsPage = payload; break;
            case 0x0A: ColorTable = payload; break;
            case 0x06: PerKey = payload; break;
        }
        if ((frame[1] & 0x80) != 0) _lastReadHeader = frame[..8];
    }

    private byte[] _lastReadHeader = new byte[8];

    public void GetFeature(byte[] buffer)
    {
        Array.Clear(buffer);
        _lastReadHeader.CopyTo(buffer, 0);
        _pendingRead?.CopyTo(buffer, 8);
    }

    public void Dispose() => Disposed = true;
}
```

`tests/F75Studio.Tests/F75SessionTests.cs`:

```csharp
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class F75SessionTests
{
    private readonly FakeFeatureChannel _channel = new();
    private readonly List<int> _delays = [];

    private F75Session Session() => new(_channel, (ms, _) => { _delays.Add(ms); return Task.CompletedTask; });

    private static byte Cmd(byte[] frame) => frame[1];

    [Fact]
    public async Task Identity_is_read_with_the_documented_request()
    {
        var id = await Session().ReadIdentityAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0xCD }, id);
        Assert.Equal("0682010001000600", Convert.ToHexString(_channel.Sent[0], 0, 8));
    }

    [Fact]
    public async Task Static_colour_writes_colour_table_then_settings()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(255, 0, 0) }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x8A, 0x0A, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0 }, _channel.ColorTable[21..24]);
        Assert.Equal(1, _channel.SettingsPage[0x0A]);
        Assert.Contains(120, _delays);
    }

    [Fact]
    public async Task Colorful_and_colourless_effects_skip_the_colour_table()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 2, Colorful = true }, CancellationToken.None);
        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.DoesNotContain(_channel.Sent, f => Cmd(f) is 0x8A or 0x0A);
        Assert.Equal(3, _channel.SettingsPage[0x0A]);
    }

    [Fact]
    public async Task Per_key_writes_colours_first_then_selects_effect_21()
    {
        var state = new LightingState
        {
            EffectId = EffectIds.PerKey,
            KeyColors = new Dictionary<int, Rgb> { [14] = new(255, 0, 0) },
        };

        await Session().ApplyAsync(state, CancellationToken.None);

        Assert.Equal(new byte[] { 0x06, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(255, _channel.PerKey[14]);
        Assert.Equal(0x15, _channel.SettingsPage[0x0A]);
        Assert.Equal(1, _channel.SettingsPage[0x09]);
        Assert.Equal(30, _delays[0]);
    }

    [Fact]
    public async Task Settings_page_without_marker_is_retried_then_refused_without_writing()
    {
        var broken = F75ProtocolTests.FixedOnPage();
        broken[126] = 0;
        _channel.SettingsPage = broken;

        await Assert.ThrowsAsync<F75ProtocolException>(() => Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None));

        Assert.Equal(5, _channel.Sent.Count(f => Cmd(f) == 0x84));
        Assert.Empty(_channel.Writes);
    }

    [Fact]
    public async Task Transfer_failures_are_retried()
    {
        _channel.FailNextSets = 2;

        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.Equal(3, _channel.SettingsPage[0x0A]);
        Assert.Equal(2, _delays.Count(d => d == 70));
    }

    [Fact]
    public async Task Every_transfer_waits_20ms_first()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);
        Assert.True(_delays.Count(d => d == 20) >= _channel.Sent.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~F75SessionTests`
Expected: build FAILS with `The type or namespace name 'IFeatureChannel' could not be found`.

- [ ] **Step 3: Implement**

`src/F75Studio.Lighting.Aula/F75Session.cs`:

```csharp
namespace F75Studio.Lighting.Aula;

/// <summary>A HID feature-report endpoint (report 0x06, 520-byte buffers).</summary>
public interface IFeatureChannel : IDisposable
{
    void SetFeature(byte[] frame);

    /// <summary>Fills <paramref name="buffer"/> (buffer[0] = report id) with the device's reply.</summary>
    void GetFeature(byte[] buffer);
}

/// <summary>Paced read/write transactions with the driver's timings (protocol doc §2, §3.4).</summary>
public sealed class F75Session
{
    private const int MarkerReadAttempts = 5;
    private const int SetAttempts = 4; // first try + 3 retries

    private readonly IFeatureChannel _channel;
    private readonly Func<int, CancellationToken, Task> _delay;

    public F75Session(IFeatureChannel channel, Func<int, CancellationToken, Task>? delay = null)
    {
        _channel = channel;
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public Task<byte[]> ReadIdentityAsync(CancellationToken ct) => ReadAsync(F75Protocol.CmdIdentity, 1, 6, ct);

    public async Task ApplyAsync(LightingState state, CancellationToken ct)
    {
        var s = state.Normalize();
        var definition = F75Effects.Get(s.EffectId);
        var settingsWait = 60;

        if (s.EffectId == EffectIds.PerKey)
        {
            await _delay(30, ct);
            await WriteAsync(F75Protocol.CmdPerKey, F75Protocol.BuildPerKey(s.KeyColors), ct);
            settingsWait = 100;
        }
        else if (definition.HasColor && !s.Colorful)
        {
            var table = await ReadWithMarkerAsync(F75Protocol.CmdColorTable, F75Protocol.ColorTableLength, F75Protocol.ColorTableMarkerOffset, ct);
            await _delay(120, ct);
            await WriteAsync(F75Protocol.CmdColorTable, F75Protocol.PatchColorTable(table, s.EffectId, s.Color), ct);
            settingsWait = 100;
        }

        var page = await ReadWithMarkerAsync(F75Protocol.CmdSettings, F75Protocol.SettingsLength, F75Protocol.SettingsMarkerOffset, ct);
        await _delay(settingsWait, ct);
        await WriteAsync(F75Protocol.CmdSettings, F75Protocol.PatchSettings(page, s), ct);
    }

    private async Task<byte[]> ReadWithMarkerAsync(byte cmd, int length, int markerOffset, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var data = await ReadAsync(cmd, 0, length, ct);
            if (F75Protocol.HasMarker(data, markerOffset)) return data;
            if (attempt == MarkerReadAttempts)
                throw new F75ProtocolException("The keyboard returned incomplete lighting data. Nothing was changed.");
            await _delay(100, ct);
        }
    }

    private async Task<byte[]> ReadAsync(byte writeCmd, byte param, int length, CancellationToken ct)
    {
        var request = F75Protocol.ReadRequest(writeCmd, param, length);
        await SendAsync(request, ct);
        await _delay(20, ct);
        var reply = new byte[F75Protocol.FrameLength];
        reply[0] = F75Protocol.ReportId;
        _channel.GetFeature(reply);
        return F75Protocol.ReplyPayload(reply, request[1], length);
    }

    private Task WriteAsync(byte cmd, byte[] payload, CancellationToken ct) =>
        SendAsync(F75Protocol.Frame(cmd, 0, payload.Length, payload), ct);

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        await _delay(20, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _channel.SetFeature(frame);
                return;
            }
            catch (IOException) when (attempt < SetAttempts)
            {
                await _delay(70, ct);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~F75SessionTests`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/F75Studio.Lighting.Aula tests/F75Studio.Tests
git commit -m "feat: paced F75 lighting session over a feature-report channel" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: HidSharp device (discovery, status, hot-plug)

**Files:**
- Create: `src/F75Studio.Lighting.Aula/HidSharpFeatureChannel.cs`, `src/F75Studio.Lighting.Aula/AulaF75Device.cs`
- Test: `tests/F75Studio.Tests/AulaF75DeviceTests.cs`

**Interfaces:**
- Consumes: `IKeyboardDevice`, `DeviceStatus`, `KeyboardUnavailableException`, `LightingState` (Task 1); `F75Protocol`, `F75Session`, `IFeatureChannel` (Tasks 2–3).
- Produces: `AulaF75Device : IKeyboardDevice` with ctor `AulaF75Device()` (starts detection, subscribes to `DeviceList.Local.Changed`), `Task RefreshAsync()`, and `static (DeviceStatus Status, string Text) ClassifyWithoutCable(bool donglePresent, bool bluetoothPresent)`.
- Produces: `HidSharpFeatureChannel : IFeatureChannel` with `static HidSharpFeatureChannel Open(HidDevice device)`.

Behaviour:
1. Detection (serialized with apply by one `SemaphoreSlim`):
   - Wired config collection = `DeviceList.Local.GetHidDevices(0x258A, 0x010C)` filtered to `GetMaxFeatureReportLength() == 520` and whose report descriptor contains top-level usage `0xFF000001` (wrap descriptor parsing in try/catch; if parsing fails, accept on length alone).
   - If found: open, read identity. Known → `Connected` "Connected by USB cable". Unknown → `Unsupported` "This keyboard identified itself as {hex}; F75 Studio only controls the AULA F75 and won't send it lighting commands." I/O failure → `Error` "Found the F75 but couldn't talk to it ({message}). Close the AULA F75 app and try again."
   - Else `ClassifyWithoutCable(dongle: any 3554:FA09, bluetooth: any 3554:FA07 or 3554:FA08)`:
     - dongle → `Dongle` "Connected through the 2.4 GHz receiver. Lighting over the receiver isn't supported yet — connect the USB cable."
     - bluetooth → `Bluetooth` "Connected by Bluetooth. Lighting can only be changed over the USB cable."
     - neither → `NotFound` "Keyboard not found. Connect the F75 with its USB cable."
   - Dongle wins over Bluetooth when both are present.
   - Raise `StatusChanged` only when status or text changes.
2. `ApplyAsync`: under the semaphore; throw `KeyboardUnavailableException(StatusText)` unless `Connected`; re-find the device (else `KeyboardUnavailableException("The keyboard was disconnected.")`); open a channel; read identity again and throw `KeyboardUnavailableException` if it is not known; `F75Session.ApplyAsync`. Map `IOException`/`TimeoutException` to `KeyboardUnavailableException("Couldn't send lighting to the keyboard: …")`. Dispose the channel every time (no long-lived handle, so the AULA app can still connect).
3. `DeviceList.Local.Changed` → `_ = RefreshAsync()`. `Dispose` unsubscribes.

- [ ] **Step 1: Write the failing test**

`tests/F75Studio.Tests/AulaF75DeviceTests.cs`:

```csharp
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests;

public class AulaF75DeviceTests
{
    [Theory]
    [InlineData(true, true, DeviceStatus.Dongle)]
    [InlineData(true, false, DeviceStatus.Dongle)]
    [InlineData(false, true, DeviceStatus.Bluetooth)]
    [InlineData(false, false, DeviceStatus.NotFound)]
    public void Classifies_non_cable_connections(bool dongle, bool bluetooth, DeviceStatus expected)
    {
        var (status, text) = AulaF75Device.ClassifyWithoutCable(dongle, bluetooth);
        Assert.Equal(expected, status);
        Assert.Contains("USB cable", text);
    }

    // Hardware-dependent: runs against whatever is plugged in. It must never throw and never write.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Detection_produces_a_status_without_throwing()
    {
        using var device = new AulaF75Device();
        await device.RefreshAsync();
        Assert.False(string.IsNullOrWhiteSpace(device.StatusText));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AulaF75DeviceTests`
Expected: build FAILS with `The type or namespace name 'AulaF75Device' could not be found`.

- [ ] **Step 3: Implement**

`src/F75Studio.Lighting.Aula/HidSharpFeatureChannel.cs`:

```csharp
using HidSharp;

namespace F75Studio.Lighting.Aula;

public sealed class HidSharpFeatureChannel : IFeatureChannel
{
    private readonly HidStream _stream;

    private HidSharpFeatureChannel(HidStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = 2000;
        _stream.WriteTimeout = 2000;
    }

    public static HidSharpFeatureChannel Open(HidDevice device) =>
        device.TryOpen(out var stream)
            ? new HidSharpFeatureChannel(stream)
            : throw new IOException("Couldn't open the keyboard's lighting interface.");

    public void SetFeature(byte[] frame) => _stream.SetFeature(frame);

    public void GetFeature(byte[] buffer) => _stream.GetFeature(buffer);

    public void Dispose() => _stream.Dispose();
}
```

`src/F75Studio.Lighting.Aula/AulaF75Device.cs`:

```csharp
using HidSharp;

namespace F75Studio.Lighting.Aula;

public sealed class AulaF75Device : IKeyboardDevice
{
    public const int WiredVid = 0x258A;
    public const int WiredPid = 0x010C;
    public const int WirelessVid = 0x3554;
    public const int DonglePid = 0xFA09;
    private static readonly int[] BluetoothPids = [0xFA07, 0xFA08];
    private const uint ConfigUsage = 0xFF000001;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceStatus _status = DeviceStatus.NotFound;
    private string _statusText = "Looking for the keyboard…";

    public AulaF75Device()
    {
        DeviceList.Local.Changed += OnDevicesChanged;
        _ = RefreshAsync();
    }

    public DeviceStatus Status => _status;
    public string StatusText => _statusText;
    public event EventHandler? StatusChanged;

    public static (DeviceStatus Status, string Text) ClassifyWithoutCable(bool donglePresent, bool bluetoothPresent) =>
        donglePresent
            ? (DeviceStatus.Dongle, "Connected through the 2.4 GHz receiver. Lighting over the receiver isn't supported yet — connect the USB cable.")
            : bluetoothPresent
                ? (DeviceStatus.Bluetooth, "Connected by Bluetooth. Lighting can only be changed over the USB cable.")
                : (DeviceStatus.NotFound, "Keyboard not found. Connect the F75 with its USB cable.");

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var (status, text) = await DetectAsync();
            SetStatus(status, text);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(LightingState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_status != DeviceStatus.Connected) throw new KeyboardUnavailableException(_statusText);
            var hid = FindWiredConfigDevice() ?? throw new KeyboardUnavailableException("The keyboard was disconnected.");
            try
            {
                using var channel = HidSharpFeatureChannel.Open(hid);
                var session = new F75Session(channel);
                if (!F75Protocol.IsKnownIdentity(await session.ReadIdentityAsync(ct)))
                    throw new KeyboardUnavailableException("The keyboard no longer identifies as an AULA F75; nothing was sent.");
                await session.ApplyAsync(state, ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                throw new KeyboardUnavailableException($"Couldn't send lighting to the keyboard: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => DeviceList.Local.Changed -= OnDevicesChanged;

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => _ = RefreshAsync();

    private static async Task<(DeviceStatus, string)> DetectAsync()
    {
        var wired = FindWiredConfigDevice();
        if (wired is not null)
        {
            try
            {
                using var channel = HidSharpFeatureChannel.Open(wired);
                var identity = await new F75Session(channel).ReadIdentityAsync(CancellationToken.None);
                return F75Protocol.IsKnownIdentity(identity)
                    ? (DeviceStatus.Connected, "Connected by USB cable")
                    : (DeviceStatus.Unsupported,
                        $"This keyboard identified itself as {Convert.ToHexString(identity)}; F75 Studio only controls the AULA F75 and won't send it lighting commands.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or F75ProtocolException)
            {
                return (DeviceStatus.Error, $"Found the F75 but couldn't talk to it ({ex.Message}). Close the AULA F75 app and try again.");
            }
        }

        var list = DeviceList.Local;
        var dongle = list.GetHidDevices(WirelessVid, DonglePid).Any();
        var bluetooth = BluetoothPids.Any(pid => list.GetHidDevices(WirelessVid, pid).Any());
        return ClassifyWithoutCable(dongle, bluetooth);
    }

    private static HidDevice? FindWiredConfigDevice() =>
        DeviceList.Local.GetHidDevices(WiredVid, WiredPid).FirstOrDefault(IsConfigCollection);

    private static bool IsConfigCollection(HidDevice device)
    {
        try
        {
            if (device.GetMaxFeatureReportLength() != F75Protocol.FrameLength) return false;
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(ConfigUsage));
        }
        catch (Exception)
        {
            return true; // descriptor unavailable: the 520-byte feature report alone identifies the config collection
        }
    }

    private void SetStatus(DeviceStatus status, string text)
    {
        if (status == _status && text == _statusText) return;
        _status = status;
        _statusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AulaF75DeviceTests`
Expected: 5 passed. With the keyboard on Bluetooth (current setup) the integration test sees `Bluetooth`; it only asserts a non-empty status.
If any HidSharp 2.6.4 member name differs (e.g. `GetReportDescriptor`, `DeviceItems`, `Usages.GetAllValues`), check the package's XML docs in `%USERPROFILE%\.nuget\packages\hidsharp\2.6.4` and use the equivalent; document it in the report.

- [ ] **Step 5: Commit**

```powershell
git add src/F75Studio.Lighting.Aula tests/F75Studio.Tests
git commit -m "feat: AULA F75 HID device with detection, status and serialized apply" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Keyboard view model and profiles

**Files:**
- Create: `src/F75Studio.App/ViewModels/KeyItemViewModel.cs`, `src/F75Studio.App/ViewModels/KeyboardViewModel.cs`
- Test: `tests/F75Studio.Tests/KeyboardViewModelTests.cs`

**Interfaces:**
- Consumes: `IKeyboardDevice`, `FakeKeyboardDevice`, `LightingState`, `LightingProfile`, `LightingProfileSet`, `F75Effects`, `F75Layout`, `Rgb`, `KeyboardUnavailableException` (Task 1); `F75ProtocolException` (Task 2); `JsonFileStore<T>` (Core); `Ui.Run` (App).
- Produces: `KeyItemViewModel(KeyDef key)` with `Key`, `Name`, `Led`, `X`, `Y`, `Width`, `Height`, observable `Rgb Color`, `bool IsSelected`.
- Produces: `KeyboardViewModel(IKeyboardDevice device, JsonFileStore<LightingProfileSet> store, Func<CancellationToken, Task>? debounce = null)` with:
  - `IReadOnlyList<EffectDefinition> Effects`, `IReadOnlyList<KeyItemViewModel> Keys`, `IReadOnlyList<string> GroupNames`, `IReadOnlyList<Rgb> Swatches`
  - observable `EffectDefinition SelectedEffect`, `int Brightness`, `int Speed`, `Rgb Color`, `string ColorHex`, `double Hue`, `bool Colorful`, `string DeviceStatusText`, `DeviceStatus DeviceStatus`, `string? StatusMessage`, `bool IsStatusOpen`, `InfoBarSeverity StatusSeverity`
  - computed `bool ShowSpeed`, `bool ShowBrightness`, `bool ShowColor`, `bool ShowPerKeyTools`
  - `ObservableCollection<string> ProfileNames`, observable `string? SelectedProfile`, `string ProfileNameEdit`
  - commands `SelectGroupCommand(string)`, `ClearSelectionCommand`, `PaintSelectedCommand`, `PickSwatchCommand(Rgb)`, `NewProfileCommand`, `DuplicateProfileCommand`, `RenameProfileCommand`, `DeleteProfileCommand`
  - methods `void ClickKey(KeyItemViewModel key, bool additive)`, `void DragOverKey(KeyItemViewModel key)`, `bool ApplyProfileByName(string name)`, `LightingState CurrentState`, `Task PendingApply` (completes when the last scheduled apply finished; for tests)
  - `IReadOnlyList<string> ProfileNamesSnapshot()` for the tray.

Behaviour:
- First run (no profiles): create profile "Default" with `new LightingState()`, make it active, save.
- Loading/switching a profile sets all fields without scheduling an apply, then schedules exactly one apply.
- Any change to effect/brightness/speed/colour/colourful/key colours: update the active profile in the store (save) and schedule a debounced apply (default debounce `Task.Delay(500, ct)`; each change cancels the pending one).
- `Color` and `ColorHex` and `Hue` stay in sync (`ColorHex` invalid → ignored; `Hue` change → `Rgb.FromHsv(Hue, 1, 1)`).
- Per-key: `ClickKey` selects (additive with Ctrl); `DragOverKey` adds to the selection; `SelectGroupCommand("WASD")` selects those keys; `PaintSelectedCommand` sets the selected keys to `Color`, switches the effect to Custom per-key if needed, and schedules an apply. `ClearSelectionCommand` deselects.
- Key preview colours (`KeyItemViewModel.Color`): Off → `#202020`; per-key → its stored colour or `#101010` if none; colourful or colourless effects → rainbow by x position (`Rgb.FromHsv(360.0 * key.X / F75Layout.CanvasWidth, 1, 1)`); otherwise `Color`. Brightness dims the preview proportionally (`value = 0.25 + 0.75 * Brightness / 9`), except Off.
- Apply errors: `KeyboardUnavailableException` → Warning status with its message ("Saved in F75 Studio. " + message); `F75ProtocolException` → Error status; success → Success "Applied to keyboard." Never throws out of the debounce.
- Device `StatusChanged` → `Ui.Run` update of `DeviceStatus`/`DeviceStatusText`; when it becomes `Connected`, schedule an apply of the active profile.
- Profiles: New → "Profile N" (first unused) with the current state; Duplicate → "<name> copy"; Rename → from `ProfileNameEdit` (trimmed, non-empty, unique; else Error status); Delete → refuses to delete the last profile; selects the first remaining.
- `ApplyProfileByName(name)` → case-insensitive match; selects it (applying it); returns false if unknown.

- [ ] **Step 1: Write the failing tests**

`tests/F75Studio.Tests/KeyboardViewModelTests.cs`:

```csharp
using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace F75Studio.Tests;

public class KeyboardViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeKeyboardDevice _device = new();
    private JsonFileStore<LightingProfileSet> Store() => new(_temp.Combine("profiles.json"), () => new LightingProfileSet());

    public void Dispose() => _temp.Dispose();

    private KeyboardViewModel Create(JsonFileStore<LightingProfileSet>? store = null) =>
        new(_device, store ?? Store(), _ => Task.CompletedTask);

    [Fact]
    public async Task First_run_creates_a_default_profile_and_applies_it()
    {
        var vm = Create();
        await vm.PendingApply;

        Assert.Equal(new[] { "Default" }, vm.ProfileNames);
        Assert.Equal("Default", vm.SelectedProfile);
        Assert.Single(_device.Applied);
        Assert.Equal("Default", Store().Load().Active);
    }

    [Fact]
    public async Task Changing_brightness_saves_and_applies()
    {
        var vm = Create();
        await vm.PendingApply;

        vm.Brightness = 3;
        await vm.PendingApply;

        Assert.Equal(3, _device.Applied[^1].Brightness);
        Assert.Equal(3, Store().Load().Profiles.Single().State.Brightness);
        Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task Rapid_changes_collapse_into_one_apply()
    {
        var gate = new TaskCompletionSource();
        var vm = new KeyboardViewModel(_device, Store(), _ => gate.Task);
        vm.Brightness = 1;
        vm.Brightness = 2;
        vm.Brightness = 4;
        gate.SetResult();
        await vm.PendingApply;

        Assert.Single(_device.Applied);
        Assert.Equal(4, _device.Applied[0].Brightness);
    }

    [Fact]
    public async Task Hex_hue_and_colour_stay_in_sync()
    {
        var vm = Create();
        vm.ColorHex = "#00FF00";
        Assert.Equal(new Rgb(0, 255, 0), vm.Color);

        vm.Hue = 240;
        Assert.Equal("#0000FF", vm.ColorHex);

        vm.ColorHex = "nope";
        Assert.Equal(new Rgb(0, 0, 255), vm.Color);
        await vm.PendingApply;
    }

    [Fact]
    public async Task Painting_a_group_switches_to_per_key()
    {
        var vm = Create();
        vm.Color = new Rgb(255, 0, 0);
        vm.SelectGroupCommand.Execute("WASD");
        vm.PaintSelectedCommand.Execute(null);
        await vm.PendingApply;

        var applied = _device.Applied[^1];
        Assert.Equal(EffectIds.PerKey, applied.EffectId);
        Assert.Equal(new Rgb(255, 0, 0), applied.KeyColors[14]);
        Assert.Equal(4, applied.KeyColors.Count);
        Assert.Equal(new Rgb(255, 0, 0).WithBrightness(9), vm.Keys.Single(k => k.Name == "W").Color);
    }

    [Fact]
    public void Click_selects_and_ctrl_click_adds()
    {
        var vm = Create();
        var q = vm.Keys.Single(k => k.Name == "Q");
        var w = vm.Keys.Single(k => k.Name == "W");

        vm.ClickKey(q, additive: false);
        vm.ClickKey(w, additive: false);
        Assert.False(q.IsSelected);
        Assert.True(w.IsSelected);

        vm.ClickKey(q, additive: true);
        vm.DragOverKey(vm.Keys.Single(k => k.Name == "E"));
        Assert.Equal(3, vm.Keys.Count(k => k.IsSelected));
    }

    [Fact]
    public async Task Unavailable_keyboard_is_a_warning_not_a_crash()
    {
        _device.FailWith = new KeyboardUnavailableException("Connected by Bluetooth.");
        var vm = Create();
        await vm.PendingApply;

        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("Connected by Bluetooth.", vm.StatusMessage);
    }

    [Fact]
    public async Task Protocol_error_is_an_error_status()
    {
        _device.FailWith = new F75ProtocolException("bad page");
        var vm = Create();
        await vm.PendingApply;
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Profiles_new_duplicate_rename_delete()
    {
        var vm = Create();
        vm.NewProfileCommand.Execute(null);
        Assert.Equal("Profile 1", vm.SelectedProfile);

        vm.DuplicateProfileCommand.Execute(null);
        Assert.Equal("Profile 1 copy", vm.SelectedProfile);

        vm.ProfileNameEdit = "Gaming";
        vm.RenameProfileCommand.Execute(null);
        Assert.Contains("Gaming", vm.ProfileNames);

        vm.ProfileNameEdit = "Default";
        vm.RenameProfileCommand.Execute(null);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity); // duplicate name refused

        vm.DeleteProfileCommand.Execute(null);
        Assert.DoesNotContain("Gaming", vm.ProfileNames);
        await vm.PendingApply;
        Assert.Equal(vm.ProfileNames.Count, Store().Load().Profiles.Count);
    }

    [Fact]
    public void Last_profile_cannot_be_deleted()
    {
        var vm = Create();
        vm.DeleteProfileCommand.Execute(null);
        Assert.Single(vm.ProfileNames);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Apply_profile_by_name_is_case_insensitive()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Default", new LightingState()), new("Gaming", new LightingState { EffectId = 3 })],
            Active = "Default",
        });
        var vm = Create(store);
        await vm.PendingApply;

        Assert.True(vm.ApplyProfileByName("gaming"));
        await vm.PendingApply;
        Assert.Equal(3, _device.Applied[^1].EffectId);
        Assert.False(vm.ApplyProfileByName("nope"));
    }

    [Fact]
    public async Task Device_connecting_reapplies_the_active_profile()
    {
        _device.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");
        var vm = Create();
        await vm.PendingApply;
        var before = _device.Applied.Count;

        _device.SetStatus(DeviceStatus.Connected, "Connected by USB cable");
        await vm.PendingApply;

        Assert.Equal("Connected by USB cable", vm.DeviceStatusText);
        Assert.Equal(before + 1, _device.Applied.Count);
    }

    [Fact]
    public void Effect_flags_drive_visible_controls()
    {
        var vm = Create();
        vm.SelectedEffect = F75Effects.Get(3);
        Assert.True(vm.ShowSpeed);
        Assert.False(vm.ShowColor);
        vm.SelectedEffect = F75Effects.Get(EffectIds.PerKey);
        Assert.True(vm.ShowPerKeyTools);
    }
}
```

Add to `Rgb` (Task 1 file) a helper used by the preview and the test:

```csharp
    /// <summary>Scales the colour for an on-screen brightness preview (0–9).</summary>
    public Rgb WithBrightness(int brightness)
    {
        var f = 0.25 + 0.75 * Math.Clamp(brightness, 0, 9) / 9.0;
        return new Rgb((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~KeyboardViewModelTests`
Expected: build FAILS with `The type or namespace name 'KeyboardViewModel' could not be found`.

- [ ] **Step 3: Implement**

`src/F75Studio.App/ViewModels/KeyItemViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using F75Studio.Lighting;

namespace F75Studio.App.ViewModels;

public sealed partial class KeyItemViewModel(KeyDef key) : ObservableObject
{
    [ObservableProperty] private Rgb _color = Rgb.Black;
    [ObservableProperty] private bool _isSelected;

    public KeyDef Key { get; } = key;
    public string Name => Key.Name;
    public int Led => Key.Led;
    public int X => Key.X;
    public int Y => Key.Y;
    public int Width => Key.Width;
    public int Height => Key.Height;
}
```

`src/F75Studio.App/ViewModels/KeyboardViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F75Studio.App.Services;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using Wpf.Ui.Controls;

namespace F75Studio.App.ViewModels;

public sealed partial class KeyboardViewModel : ObservableObject
{
    private static readonly Rgb OffPreview = new(0x20, 0x20, 0x20);
    private static readonly Rgb UnsetKeyPreview = new(0x10, 0x10, 0x10);

    private readonly IKeyboardDevice _device;
    private readonly JsonFileStore<LightingProfileSet> _store;
    private readonly Func<CancellationToken, Task> _debounce;
    private readonly List<LightingProfile> _profiles;
    private Dictionary<int, Rgb> _keyColors = [];
    private CancellationTokenSource? _pendingCts;
    private bool _loading;
    private bool _syncingColor;

    [ObservableProperty] private EffectDefinition _selectedEffect = F75Effects.Get(EffectIds.FixedOn);
    [ObservableProperty] private int _brightness = 9;
    [ObservableProperty] private int _speed = 2;
    [ObservableProperty] private Rgb _color = new(0, 120, 255);
    [ObservableProperty] private string _colorHex = "#0078FF";
    [ObservableProperty] private double _hue;
    [ObservableProperty] private bool _colorful;
    [ObservableProperty] private string _deviceStatusText;
    [ObservableProperty] private DeviceStatus _deviceStatus;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isStatusOpen;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _profileNameEdit = "";

    public KeyboardViewModel(IKeyboardDevice device, JsonFileStore<LightingProfileSet> store, Func<CancellationToken, Task>? debounce = null)
    {
        _device = device;
        _store = store;
        _debounce = debounce ?? (ct => Task.Delay(500, ct));
        Keys = F75Layout.Keys.Select(k => new KeyItemViewModel(k)).ToList();
        _deviceStatus = device.Status;
        _deviceStatusText = device.StatusText;
        device.StatusChanged += (_, _) => Ui.Run(OnDeviceStatusChanged);

        var set = store.Load();
        _profiles = set.Profiles.ToList();
        if (_profiles.Count == 0)
        {
            _profiles.Add(new LightingProfile("Default", new LightingState()));
            set = new LightingProfileSet { Profiles = _profiles.ToList(), Active = "Default" };
            store.Save(set);
        }
        foreach (var p in _profiles) ProfileNames.Add(p.Name);
        SelectedProfile = _profiles.Any(p => p.Name == set.Active) ? set.Active : _profiles[0].Name;
    }

    public IReadOnlyList<EffectDefinition> Effects => F75Effects.All;
    public IReadOnlyList<KeyItemViewModel> Keys { get; }
    public IReadOnlyList<string> GroupNames => F75Layout.GroupNames;
    public ObservableCollection<string> ProfileNames { get; } = [];

    public IReadOnlyList<Rgb> Swatches { get; } =
    [
        new(255, 0, 0), new(255, 128, 0), new(255, 220, 0), new(0, 255, 0), new(0, 255, 200), new(0, 120, 255),
        new(0, 0, 255), new(140, 0, 255), new(255, 0, 200), new(255, 255, 255),
    ];

    public bool ShowSpeed => SelectedEffect.HasSpeed;
    public bool ShowBrightness => SelectedEffect.HasBrightness;
    public bool ShowColor => SelectedEffect.HasColor;
    public bool ShowPerKeyTools => SelectedEffect.Id == EffectIds.PerKey;

    /// <summary>Completes when the most recently scheduled apply has finished (tests await it).</summary>
    public Task PendingApply { get; private set; } = Task.CompletedTask;

    public LightingState CurrentState => new()
    {
        EffectId = SelectedEffect.Id,
        Brightness = Brightness,
        Speed = Speed,
        Color = Color,
        Colorful = Colorful,
        KeyColors = new Dictionary<int, Rgb>(_keyColors),
    };

    public IReadOnlyList<string> ProfileNamesSnapshot() => ProfileNames.ToList();

    public bool ApplyProfileByName(string name)
    {
        var match = ProfileNames.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        if (match == SelectedProfile) ScheduleApply();
        else SelectedProfile = match;
        return true;
    }

    public void ClickKey(KeyItemViewModel key, bool additive)
    {
        if (!additive)
            foreach (var k in Keys) k.IsSelected = false;
        key.IsSelected = !additive || !key.IsSelected;
    }

    public void DragOverKey(KeyItemViewModel key) => key.IsSelected = true;

    [RelayCommand]
    private void SelectGroup(string name)
    {
        var leds = F75Layout.Group(name).ToHashSet();
        foreach (var k in Keys) k.IsSelected = leds.Contains(k.Led);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var k in Keys) k.IsSelected = false;
    }

    [RelayCommand]
    private void PaintSelected()
    {
        var selected = Keys.Where(k => k.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Select keys first: click, Ctrl+click, drag, or pick a group.", InfoBarSeverity.Informational);
            return;
        }
        foreach (var k in selected) _keyColors[k.Led] = Color;
        if (SelectedEffect.Id != EffectIds.PerKey) SelectedEffect = F75Effects.Get(EffectIds.PerKey);
        else OnStateChanged();
    }

    [RelayCommand]
    private void PickSwatch(Rgb color) => Color = color;

    [RelayCommand]
    private void NewProfile()
    {
        var n = 1;
        while (ProfileNames.Contains($"Profile {n}")) n++;
        AddProfile($"Profile {n}");
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        var name = $"{SelectedProfile} copy";
        var n = 2;
        while (ProfileNames.Contains(name)) name = $"{SelectedProfile} copy {n++}";
        AddProfile(name);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var newName = ProfileNameEdit.Trim();
        if (newName.Length == 0 || SelectedProfile is null) return;
        if (ProfileNames.Any(n => n != SelectedProfile && string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A profile named \"{newName}\" already exists.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        _profiles[index] = _profiles[index] with { Name = newName };
        _loading = true;
        ProfileNames[index] = newName;
        SelectedProfile = newName;
        _loading = false;
        Save();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (_profiles.Count <= 1 || SelectedProfile is null)
        {
            SetStatus("You need at least one lighting profile.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        _profiles.RemoveAt(index);
        ProfileNames.RemoveAt(index);
        SelectedProfile = _profiles[0].Name;
        Save();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_loading || value is null) return;
        var profile = _profiles.FirstOrDefault(p => p.Name == value);
        if (profile is null) return;
        Load(profile.State);
        ProfileNameEdit = value;
        Save();
        ScheduleApply();
    }

    partial void OnSelectedEffectChanged(EffectDefinition value)
    {
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowPerKeyTools));
        OnStateChanged();
    }

    partial void OnBrightnessChanged(int value) => OnStateChanged();
    partial void OnSpeedChanged(int value) => OnStateChanged();
    partial void OnColorfulChanged(bool value) => OnStateChanged();

    partial void OnColorChanged(Rgb value)
    {
        if (!_syncingColor)
        {
            _syncingColor = true;
            ColorHex = value.ToHex();
            _syncingColor = false;
        }
        OnStateChanged();
    }

    partial void OnColorHexChanged(string value)
    {
        if (_syncingColor || !Rgb.TryParseHex(value, out var rgb)) return;
        _syncingColor = true;
        Color = rgb;
        _syncingColor = false;
    }

    partial void OnHueChanged(double value)
    {
        if (_loading) return;
        Color = Rgb.FromHsv(value, 1, 1);
    }

    private void AddProfile(string name)
    {
        _profiles.Add(new LightingProfile(name, CurrentState));
        ProfileNames.Add(name);
        _loading = true;
        SelectedProfile = name;
        ProfileNameEdit = name;
        _loading = false;
        Save();
    }

    private void Load(LightingState state)
    {
        _loading = true;
        var s = state.Normalize();
        SelectedEffect = F75Effects.IsSupported(s.EffectId) ? F75Effects.Get(s.EffectId) : F75Effects.Get(EffectIds.FixedOn);
        Brightness = s.Brightness;
        Speed = s.Speed;
        Color = s.Color;
        Colorful = s.Colorful;
        _keyColors = new Dictionary<int, Rgb>(s.KeyColors);
        _loading = false;
        RefreshPreview();
    }

    private void OnStateChanged()
    {
        if (_loading) return;
        RefreshPreview();
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        if (index >= 0) _profiles[index] = _profiles[index] with { State = CurrentState };
        Save();
        ScheduleApply();
    }

    private void Save() => _store.Save(new LightingProfileSet { Profiles = _profiles.ToList(), Active = SelectedProfile });

    private void ScheduleApply()
    {
        _pendingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        var state = CurrentState;
        PendingApply = ApplyAfterDebounceAsync(state, cts.Token);
    }

    private async Task ApplyAfterDebounceAsync(LightingState state, CancellationToken ct)
    {
        try
        {
            await _debounce(ct);
            if (ct.IsCancellationRequested) return;
            await _device.ApplyAsync(state, ct);
            SetStatus("Applied to keyboard.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change
        }
        catch (KeyboardUnavailableException ex)
        {
            SetStatus($"Saved in F75 Studio. {ex.Message}", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is F75ProtocolException or IOException or InvalidOperationException)
        {
            SetStatus($"The keyboard rejected the change: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnDeviceStatusChanged()
    {
        DeviceStatus = _device.Status;
        DeviceStatusText = _device.StatusText;
        if (DeviceStatus == DeviceStatus.Connected) ScheduleApply();
    }

    private void RefreshPreview()
    {
        var effect = SelectedEffect;
        foreach (var key in Keys)
        {
            key.Color = effect.Id switch
            {
                EffectIds.Off => OffPreview,
                EffectIds.PerKey => (_keyColors.TryGetValue(key.Led, out var c) ? c : UnsetKeyPreview).WithBrightness(Brightness),
                _ when !effect.HasColor || Colorful => Rgb.FromHsv(360.0 * key.X / F75Layout.CanvasWidth, 1, 1).WithBrightness(Brightness),
                _ => Color.WithBrightness(Brightness),
            };
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity) => Ui.Run(() =>
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    });
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~KeyboardViewModelTests`
Expected: 13 passed. Full suite: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/F75Studio.App/ViewModels src/F75Studio.Lighting/Rgb.cs tests/F75Studio.Tests
git commit -m "feat: keyboard lighting view model with profiles and debounced apply" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Keyboard page, tray Lighting menu, `--profile`

**Files:**
- Create: `src/F75Studio.App/Converters/RgbToBrushConverter.cs`, `src/F75Studio.App/Views/KeyboardView.xaml`, `src/F75Studio.App/Views/KeyboardView.xaml.cs`
- Modify: `src/F75Studio.App/App.xaml` (register converter), `src/F75Studio.App/Views/ViewTemplates.xaml`, `src/F75Studio.App/App.xaml.cs`, `src/F75Studio.App/Services/TrayService.cs`

**Interfaces:**
- Consumes: `KeyboardViewModel`, `KeyItemViewModel` (Task 5); `AulaF75Device` (Task 4); `AppPaths.ProfilesFile`, `JsonFileStore<LightingProfileSet>`.
- Produces: `TrayService(AppServices services, Action show, Action exit, Func<IReadOnlyList<string>> lightingProfiles, Action<string> applyLightingProfile)`.

- [ ] **Step 1: Converter and registration**

`src/F75Studio.App/Converters/RgbToBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F75Studio.Lighting;

namespace F75Studio.App.Converters;

public sealed class RgbToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rgb = value is Rgb c ? c : Rgb.Black;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

In `App.xaml`, next to the other converters: `<conv:RgbToBrushConverter x:Key="RgbBrush" />`.

In `Views/ViewTemplates.xaml`:

```xml
    <DataTemplate DataType="{x:Type vm:KeyboardViewModel}">
        <views:KeyboardView />
    </DataTemplate>
```

- [ ] **Step 2: The view**

`src/F75Studio.App/Views/KeyboardView.xaml`:

```xml
<UserControl x:Class="F75Studio.App.Views.KeyboardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:vm="clr-namespace:F75Studio.App.ViewModels"
             mc:Ignorable="d"
             d:DataContext="{d:DesignInstance vm:KeyboardViewModel}">
    <UserControl.Resources>
        <Style x:Key="FieldLabel" TargetType="TextBlock">
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Margin" Value="0,12,0,4" />
        </Style>
    </UserControl.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <Grid Margin="24,8,24,24">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <!-- Header: title, device status, profiles -->
            <DockPanel Grid.Row="0" Margin="0,0,0,12">
                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                    <ComboBox MinWidth="180" ItemsSource="{Binding ProfileNames}" SelectedItem="{Binding SelectedProfile}" Margin="0,0,8,0" />
                    <ui:TextBox Width="150" Text="{Binding ProfileNameEdit, UpdateSourceTrigger=PropertyChanged}" PlaceholderText="Profile name" Margin="0,0,4,0" />
                    <ui:Button Content="Rename" Command="{Binding RenameProfileCommand}" Margin="0,0,8,0" />
                    <ui:Button Icon="{ui:SymbolIcon Add24}" ToolTip="New profile" Command="{Binding NewProfileCommand}" Margin="0,0,4,0" />
                    <ui:Button Icon="{ui:SymbolIcon Copy24}" ToolTip="Duplicate profile" Command="{Binding DuplicateProfileCommand}" Margin="0,0,4,0" />
                    <ui:Button Icon="{ui:SymbolIcon Delete24}" ToolTip="Delete profile" Command="{Binding DeleteProfileCommand}" />
                </StackPanel>
                <StackPanel>
                    <TextBlock Text="Keyboard" FontSize="28" FontWeight="SemiBold" />
                    <StackPanel Orientation="Horizontal">
                        <Ellipse Width="10" Height="10" Margin="0,0,8,0" VerticalAlignment="Center">
                            <Ellipse.Style>
                                <Style TargetType="Ellipse">
                                    <Setter Property="Fill" Value="#FFE0A000" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding DeviceStatus}" Value="Connected">
                                            <Setter Property="Fill" Value="#FF2EB67D" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding DeviceStatus}" Value="Unsupported">
                                            <Setter Property="Fill" Value="#FFE5484D" />
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding DeviceStatus}" Value="Error">
                                            <Setter Property="Fill" Value="#FFE5484D" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Ellipse.Style>
                        </Ellipse>
                        <TextBlock Text="{Binding DeviceStatusText}" Opacity="0.8" TextWrapping="Wrap" MaxWidth="560" />
                    </StackPanel>
                </StackPanel>
            </DockPanel>

            <ui:InfoBar Grid.Row="1" Margin="0,0,0,12" Message="{Binding StatusMessage}"
                        IsOpen="{Binding IsStatusOpen, Mode=TwoWay}" Severity="{Binding StatusSeverity}" IsClosable="True" />

            <!-- Keyboard picture -->
            <Border Grid.Row="2" CornerRadius="12" Padding="12" Background="#FF1B1B1F" MaxWidth="980">
                <Viewbox Stretch="Uniform">
                    <ItemsControl x:Name="KeyboardCanvas" ItemsSource="{Binding Keys}" Width="660" Height="290">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <Canvas />
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemContainerStyle>
                            <Style TargetType="ContentPresenter">
                                <Setter Property="Canvas.Left" Value="{Binding X}" />
                                <Setter Property="Canvas.Top" Value="{Binding Y}" />
                            </Style>
                        </ItemsControl.ItemContainerStyle>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="{x:Type vm:KeyItemViewModel}">
                                <Border Width="{Binding Width}" Height="{Binding Height}" CornerRadius="4"
                                        Background="#FF2A2A30" BorderThickness="1.5"
                                        MouseLeftButtonDown="OnKeyMouseDown" MouseEnter="OnKeyMouseEnter" Cursor="Hand"
                                        ToolTip="{Binding Name}">
                                    <Border.Style>
                                        <Style TargetType="Border">
                                            <Setter Property="BorderBrush" Value="#33FFFFFF" />
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding IsSelected}" Value="True">
                                                    <Setter Property="BorderBrush" Value="White" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Border.Style>
                                    <Grid>
                                        <Border Margin="3" CornerRadius="3" Background="{Binding Color, Converter={StaticResource RgbBrush}}" Opacity="0.85" />
                                        <TextBlock Text="{Binding Name}" FontSize="7" Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center">
                                            <TextBlock.Effect>
                                                <DropShadowEffect BlurRadius="2" ShadowDepth="0" Opacity="0.9" />
                                            </TextBlock.Effect>
                                        </TextBlock>
                                    </Grid>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </Viewbox>
            </Border>

            <!-- Controls -->
            <Grid Grid.Row="3" Margin="0,16,0,0" MaxWidth="980" HorizontalAlignment="Left">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="24" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0">
                    <TextBlock Text="Effect" Style="{StaticResource FieldLabel}" />
                    <ComboBox ItemsSource="{Binding Effects}" SelectedItem="{Binding SelectedEffect}" DisplayMemberPath="Name" />

                    <StackPanel Visibility="{Binding ShowBrightness, Converter={StaticResource BoolVisible}}">
                        <TextBlock Style="{StaticResource FieldLabel}">
                            <Run Text="Brightness" /> <Run Text="{Binding Brightness, Mode=OneWay}" />
                        </TextBlock>
                        <Slider Minimum="0" Maximum="9" TickFrequency="1" IsSnapToTickEnabled="True" Value="{Binding Brightness}" />
                    </StackPanel>

                    <StackPanel Visibility="{Binding ShowSpeed, Converter={StaticResource BoolVisible}}">
                        <TextBlock Style="{StaticResource FieldLabel}">
                            <Run Text="Speed" /> <Run Text="{Binding Speed, Mode=OneWay}" /> <Run Text="(0 = fastest)" Foreground="Gray" />
                        </TextBlock>
                        <Slider Minimum="0" Maximum="4" TickFrequency="1" IsSnapToTickEnabled="True" Value="{Binding Speed}" />
                    </StackPanel>

                    <StackPanel Visibility="{Binding ShowPerKeyTools, Converter={StaticResource BoolVisible}}">
                        <TextBlock Text="Per-key: select keys, pick a colour, then paint" Style="{StaticResource FieldLabel}" />
                        <ItemsControl ItemsSource="{Binding GroupNames}">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <WrapPanel />
                                </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <ui:Button Content="{Binding}" Margin="0,0,6,6"
                                               Command="{Binding DataContext.SelectGroupCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                               CommandParameter="{Binding}" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <StackPanel Orientation="Horizontal">
                            <ui:Button Content="Paint selected" Appearance="Primary" Icon="{ui:SymbolIcon PaintBrush24}" Command="{Binding PaintSelectedCommand}" Margin="0,0,8,0" />
                            <ui:Button Content="Clear selection" Command="{Binding ClearSelectionCommand}" />
                        </StackPanel>
                    </StackPanel>
                </StackPanel>

                <StackPanel Grid.Column="2">
                    <StackPanel>
                        <StackPanel.Style>
                            <Style TargetType="StackPanel">
                                <Setter Property="Visibility" Value="Collapsed" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding ShowColor}" Value="True">
                                        <Setter Property="Visibility" Value="Visible" />
                                    </DataTrigger>
                                    <DataTrigger Binding="{Binding ShowPerKeyTools}" Value="True">
                                        <Setter Property="Visibility" Value="Visible" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </StackPanel.Style>
                        <TextBlock Text="Colour" Style="{StaticResource FieldLabel}" />
                        <ItemsControl ItemsSource="{Binding Swatches}">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <WrapPanel />
                                </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Button Width="28" Height="28" Margin="0,0,6,6" BorderThickness="0"
                                            Background="{Binding Converter={StaticResource RgbBrush}}"
                                            Command="{Binding DataContext.PickSwatchCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                                            CommandParameter="{Binding}" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <TextBlock Text="Hue" Style="{StaticResource FieldLabel}" />
                        <Slider Minimum="0" Maximum="359" Value="{Binding Hue}" />
                        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                            <Border Width="36" Height="36" CornerRadius="6" Margin="0,0,8,0" Background="{Binding Color, Converter={StaticResource RgbBrush}}" />
                            <ui:TextBox Width="120" Text="{Binding ColorHex, UpdateSourceTrigger=PropertyChanged}" FontFamily="Cascadia Mono, Consolas" />
                        </StackPanel>
                        <CheckBox Content="Colourful (random colours)" IsChecked="{Binding Colorful}" Margin="0,8,0,0"
                                  Visibility="{Binding ShowColor, Converter={StaticResource BoolVisible}}" />
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Grid>
    </ScrollViewer>
</UserControl>
```

`src/F75Studio.App/Views/KeyboardView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using F75Studio.App.ViewModels;

namespace F75Studio.App.Views;

public partial class KeyboardView : UserControl
{
    public KeyboardView() => InitializeComponent();

    private KeyboardViewModel? Vm => DataContext as KeyboardViewModel;

    private void OnKeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeyItemViewModel key || Vm is null) return;
        Vm.ClickKey(key, additive: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        e.Handled = true;
    }

    private void OnKeyMouseEnter(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.DataContext is KeyItemViewModel key) Vm?.DragOverKey(key);
    }
}
```

If `SymbolRegular.Copy24` or `PaintBrush24` don't exist in WPF-UI 4.3.0, use the closest names from the package XML docs and note them in the report.

- [ ] **Step 3: Wire into the app**

In `App.xaml.cs`:
- Add fields `private AulaF75Device? _keyboardDevice;` and `private KeyboardViewModel? _keyboard;` (usings `F75Studio.Lighting`, `F75Studio.Lighting.Aula`).
- In `CreateMainViewModel`, before building pages:

```csharp
        _keyboardDevice = new AulaF75Device();
        var lightingStore = new JsonFileStore<LightingProfileSet>(services.Paths.ProfilesFile, () => new LightingProfileSet());
        var keyboard = new KeyboardViewModel(_keyboardDevice, lightingStore);
        _keyboard = keyboard;
```

  and make the Keyboard page first:

```csharp
            new NavItem("Keyboard", SymbolRegular.Lightbulb24, keyboard),
```

- Construct the tray as `new TrayService(_services, ShowMainWindow, RequestExit, () => _keyboard?.ProfileNamesSnapshot() ?? [], ApplyLightingProfile);` with:

```csharp
    private void ApplyLightingProfile(string name)
    {
        if (_keyboard?.ApplyProfileByName(name) != true)
            _tray?.Notify("Lighting profile not found", $"There is no lighting profile named \"{name}\".");
    }
```

- Replace `HandleArguments` (update its doc comment; drop "Plan 2 adds"):

```csharp
    /// <summary>Handles startup arguments and ones forwarded by a second instance: --show, --profile "Name".</summary>
    private void HandleArguments(string[] args)
    {
        if (args.Contains("--show")) ShowMainWindow();
        var index = Array.IndexOf(args, "--profile");
        if (index >= 0 && index + 1 < args.Length) ApplyLightingProfile(args[index + 1]);
    }
```

  and call `HandleArguments(e.Args)` at the end of `StartUp` (so `F75Studio.exe --profile Gaming` also works when the app wasn't running).
- In `ExitApp`'s finally, add `try { _keyboardDevice?.Dispose(); } catch { /* shutting down anyway */ }`.

In `TrayService.cs`: extend the constructor with `Func<IReadOnlyList<string>> lightingProfiles, Action<string> applyLightingProfile` (store in fields) and in `BuildMenu`, right after "Open F75 Studio" and its separator, add:

```csharp
        var lighting = new MenuItem { Header = "Lighting" };
        foreach (var name in _lightingProfiles())
        {
            var profileName = name;
            lighting.Items.Add(Item(profileName, () => _applyLightingProfile(profileName)));
        }
        if (lighting.Items.Count == 0) lighting.Items.Add(new MenuItem { Header = "No profiles", IsEnabled = false });
        menu.Items.Add(lighting);
```

- [ ] **Step 4: Build, test, smoke-run**

Run: `dotnet build` (0 warnings) and `dotnet test` (all pass).
Launch `dotnet src/F75Studio.App/bin/Debug/net8.0-windows/F75Studio.dll` for ~8 s: no crash, no log. With the keyboard on Bluetooth the Keyboard page must show "Connected by Bluetooth…" and every change must give the yellow "Saved in F75 Studio…" warning — **no HID writes happen in this state**. Stop only your process. Do not modify `%AppData%\F75Studio\profiles.json` beyond what launching the app creates.

- [ ] **Step 5: Commit**

```powershell
git add src/F75Studio.App
git commit -m "feat: Keyboard lighting page, tray Lighting menu and --profile" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Hardware verification (controller + user, no subagent)

Needs the user: F75 connected **by USB cable** (mode switch on cable/wired), the official **AULA F75 app closed**.

- [ ] Launch the app. Keyboard page shows "Connected by USB cable" (green dot). If it shows "identified itself as …", stop: record the identity and do not continue.
- [ ] Static, red, brightness 9 → whole keyboard red. Brightness 3 → dimmer.
- [ ] Breathing with speed 0, then speed 4 → note which is faster. If 4 is faster, change the label to "(4 = fastest)" and record the result in `docs/protocol/aula-f75.md` §4.2.
- [ ] Colourful on → multi-colour.
- [ ] Rainbow wheel works (effect 6).
- [ ] Custom per-key: WASD red, arrows blue, rest dark → matches the picture. Change brightness → does per-key dim? Record in §7.2.
- [ ] Unplug the cable → status changes to Bluetooth/not found within a few seconds; replug → "Connected" and the active profile re-applies.
- [ ] Tray → Lighting → another profile → keyboard changes. `publish\F75Studio.exe --profile "<name>"` from a terminal → keyboard changes.
- [ ] Record results in the protocol doc's open-questions section and commit.
