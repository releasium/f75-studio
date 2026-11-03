using System.Text;

namespace F75Studio.Hotkeys;

/// <summary>Turns a stream of key presses into an AHK trigger. Complete once every key is released.</summary>
public sealed class TriggerRecorder
{
    private readonly List<string> _held = [];

    public string? Trigger { get; private set; }
    public bool IsComplete { get; private set; }

    public void KeyDown(string ahkKey)
    {
        if (IsComplete || _held.Contains(ahkKey)) return;
        _held.Add(ahkKey);
        if (AhkKeyNames.IsModifier(ahkKey)) return;

        var heldNormalKey = _held.FirstOrDefault(k => k != ahkKey && !AhkKeyNames.IsModifier(k));
        Trigger = heldNormalKey is not null ? $"{heldNormalKey} & {ahkKey}" : ModifierPrefix() + ahkKey;
    }

    public void KeyUp(string ahkKey)
    {
        if (IsComplete) return;
        if (Trigger is null && _held is [var only] && only == ahkKey && AhkKeyNames.IsModifier(ahkKey)) Trigger = ahkKey;
        _held.Remove(ahkKey);
        if (_held.Count == 0 && Trigger is not null) IsComplete = true;
    }

    private string ModifierPrefix()
    {
        var sb = new StringBuilder();
        if (_held.Any(k => k is "LWin" or "RWin")) sb.Append('#');
        if (_held.Any(k => k is "LCtrl" or "RCtrl")) sb.Append('^');
        if (_held.Any(k => k is "LAlt" or "RAlt")) sb.Append('!');
        if (_held.Any(k => k is "LShift" or "RShift")) sb.Append('+');
        return sb.ToString();
    }
}
