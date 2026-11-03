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
