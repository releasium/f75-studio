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
