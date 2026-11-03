namespace F75Studio.Hotkeys.Model;

public sealed record Hotkey
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Group { get; init; } = "General";
    public bool Enabled { get; init; } = true;

    /// <summary>AutoHotkey hotkey syntax, e.g. "^!k" or "CapsLock &amp; h".</summary>
    public required string Trigger { get; init; }

    /// <summary>Executable name the hotkey is limited to, e.g. "chrome.exe"; null means everywhere.</summary>
    public string? AppScope { get; init; }

    public required HotkeyAction Action { get; init; }
}
