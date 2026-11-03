using F75Studio.Core;
using F75Studio.Hotkeys;
using F75Studio.Hotkeys.Model;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class HotkeyModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static List<Hotkey> AllActionKinds() =>
    [
        new() { Trigger = "CapsLock & h", Group = "Nav", Action = new SendKeysAction("{Left}", Blind: true) },
        new() { Trigger = "RCtrl", Action = new RemapAction("AppsKey") },
        new() { Trigger = "^!m", Action = new TypeTextAction("me@example.com") },
        new() { Trigger = "^!t", Action = new RunAction(@"C:\Windows\notepad.exe", "C:\\notes.txt") },
        new() { Trigger = "F13", Action = new MediaAction(MediaCommand.PlayPause) },
        new() { Trigger = "^!1", Action = new SwitchLightingProfileAction("Gaming") },
        new() { Trigger = "^!c", AppScope = "chrome.exe", Action = new CustomCodeAction("MsgBox \"hi\"") },
        new() { Trigger = "Insert", Enabled = false, Action = new DisableKeyAction() },
    ];

    [Fact]
    public void Hotkeys_with_every_action_kind_round_trip_through_json()
    {
        var store = new JsonFileStore<List<Hotkey>>(_temp.Combine("hotkeys.json"), () => []);
        var original = AllActionKinds();

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original, loaded);
        Assert.Contains("\"type\": \"send\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void MediaKeys_maps_both_ways()
    {
        Assert.Equal("{Media_Play_Pause}", MediaKeys.ToSendKey(MediaCommand.PlayPause));
        Assert.True(MediaKeys.TryParse("{volume_up}", out var command));
        Assert.Equal(MediaCommand.VolumeUp, command);
        Assert.False(MediaKeys.TryParse("{Left}", out _));
    }
}
