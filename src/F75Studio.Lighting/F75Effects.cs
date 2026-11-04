namespace F75Studio.Lighting;

public static class EffectIds
{
    public const int Off = 0;
    public const int FixedOn = 1;
    public const int PerKey = 21;

    /// <summary>App-side live effect; never written to the keyboard's settings page.</summary>
    public const int ScreenSync = 100;

    /// <summary>App-side live effect (blurred image); never written to the keyboard.</summary>
    public const int ScreenSyncBlur = 101;

    /// <summary>App-side live effect (screen colours on the frame keys, a contrasting accent on the main keys); never written to the keyboard.</summary>
    public const int ScreenSyncAccent = 102;
}

/// <param name="Id">Value written to settings byte 0x0a (protocol doc §5).</param>
/// <param name="HasColor">Effect supports a single colour or "colourful" (random) mode.</param>
/// <param name="IsLive">Streamed with volatile live frames; not a keyboard effect.</param>
public sealed record EffectDefinition(int Id, string Name, bool HasSpeed, bool HasBrightness, bool HasColor, bool IsLive = false);

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
        new(EffectIds.ScreenSync, "Screen sync (edges)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true),
        new(EffectIds.ScreenSyncBlur, "Screen sync (blur)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true),
        new(EffectIds.ScreenSyncAccent, "Screen sync (accent)", HasSpeed: false, HasBrightness: true, HasColor: false, IsLive: true),
    ];

    public static bool IsSupported(int id) => All.Any(e => e.Id == id);

    public static EffectDefinition Get(int id) =>
        All.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported effect.");
}
