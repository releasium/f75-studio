using System.Text.Json.Serialization;

namespace F75Studio.Hotkeys.Model;

public enum MediaCommand { PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SendKeysAction), "send")]
[JsonDerivedType(typeof(RemapAction), "remap")]
[JsonDerivedType(typeof(TypeTextAction), "text")]
[JsonDerivedType(typeof(RunAction), "run")]
[JsonDerivedType(typeof(MediaAction), "media")]
[JsonDerivedType(typeof(SwitchLightingProfileAction), "lighting")]
[JsonDerivedType(typeof(CustomCodeAction), "code")]
[JsonDerivedType(typeof(DisableKeyAction), "disable")]
public abstract record HotkeyAction;

/// <summary>Keys in AutoHotkey Send syntax, e.g. "^{Left}".</summary>
public sealed record SendKeysAction(string Keys, bool Blind = false) : HotkeyAction;

public sealed record RemapAction(string TargetKey) : HotkeyAction;

public sealed record TypeTextAction(string Text) : HotkeyAction;

public sealed record RunAction(string Target, string? Arguments = null) : HotkeyAction;

public sealed record MediaAction(MediaCommand Command) : HotkeyAction;

public sealed record SwitchLightingProfileAction(string ProfileName) : HotkeyAction;

/// <summary>Raw AutoHotkey v2 code placed inside the hotkey's braces.</summary>
public sealed record CustomCodeAction(string Code) : HotkeyAction;

public sealed record DisableKeyAction() : HotkeyAction;
