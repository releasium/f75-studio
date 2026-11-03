using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using F75Studio.Hotkeys.Model;

namespace F75Studio.App.ViewModels;

public enum ActionKind { SendKeys, Remap, TypeText, Run, Media, SwitchLightingProfile, CustomCode, Disable }

public sealed record ActionOption(ActionKind Kind, string Label);

/// <summary>One editable hotkey card. All action fields are kept so switching Kind does not lose input.</summary>
public sealed partial class HotkeyItemViewModel : ObservableObject
{
    public static IReadOnlyList<ActionOption> ActionOptions { get; } =
    [
        new(ActionKind.SendKeys, "Send keys"),
        new(ActionKind.Remap, "Remap to another key"),
        new(ActionKind.TypeText, "Type text"),
        new(ActionKind.Run, "Run program or URL"),
        new(ActionKind.Media, "Media / volume"),
        new(ActionKind.SwitchLightingProfile, "Switch lighting profile"),
        new(ActionKind.CustomCode, "Custom AHK code"),
        new(ActionKind.Disable, "Disable key"),
    ];

    public static IReadOnlyList<MediaCommand> MediaCommands { get; } = Enum.GetValues<MediaCommand>();

    [ObservableProperty] private string _trigger = "";
    [ObservableProperty] private string _group = "General";
    [ObservableProperty] private string _appScope = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private ActionKind _kind = ActionKind.SendKeys;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _keys = "";
    [ObservableProperty] private bool _blind;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _remapTarget = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _runTarget = "";
    [ObservableProperty] private string _runArguments = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private MediaCommand _media = MediaCommand.PlayPause;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _profileName = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string? _error;

    public HotkeyItemViewModel(Guid? id = null) => Id = id ?? Guid.NewGuid();

    public Guid Id { get; }

    public string Summary => Kind switch
    {
        ActionKind.SendKeys => $"Send {Keys}",
        ActionKind.Remap => $"Remap to {RemapTarget}",
        ActionKind.TypeText => $"Type \"{Shorten(Text)}\"",
        ActionKind.Run => $"Run {Path.GetFileName(RunTarget.TrimEnd('\\', '/'))}",
        ActionKind.Media => $"Media: {Media}",
        ActionKind.SwitchLightingProfile => $"Lighting: {ProfileName}",
        ActionKind.CustomCode => "Custom AHK code",
        _ => "Disable key",
    };

    public static HotkeyItemViewModel FromHotkey(Hotkey hotkey)
    {
        var vm = new HotkeyItemViewModel(hotkey.Id)
        {
            Trigger = hotkey.Trigger,
            Group = hotkey.Group,
            AppScope = hotkey.AppScope ?? "",
            Enabled = hotkey.Enabled,
        };
        switch (hotkey.Action)
        {
            case SendKeysAction a: vm.Kind = ActionKind.SendKeys; vm.Keys = a.Keys; vm.Blind = a.Blind; break;
            case RemapAction a: vm.Kind = ActionKind.Remap; vm.RemapTarget = a.TargetKey; break;
            case TypeTextAction a: vm.Kind = ActionKind.TypeText; vm.Text = a.Text; break;
            case RunAction a: vm.Kind = ActionKind.Run; vm.RunTarget = a.Target; vm.RunArguments = a.Arguments ?? ""; break;
            case MediaAction a: vm.Kind = ActionKind.Media; vm.Media = a.Command; break;
            case SwitchLightingProfileAction a: vm.Kind = ActionKind.SwitchLightingProfile; vm.ProfileName = a.ProfileName; break;
            case CustomCodeAction a: vm.Kind = ActionKind.CustomCode; vm.Code = a.Code; break;
            case DisableKeyAction: vm.Kind = ActionKind.Disable; break;
        }
        return vm;
    }

    public Hotkey ToHotkey() => new()
    {
        Id = Id,
        Trigger = Trigger.Trim(),
        Group = string.IsNullOrWhiteSpace(Group) ? "General" : Group.Trim(),
        AppScope = string.IsNullOrWhiteSpace(AppScope) ? null : AppScope.Trim(),
        Enabled = Enabled,
        Action = Kind switch
        {
            ActionKind.SendKeys => new SendKeysAction(Keys, Blind),
            ActionKind.Remap => new RemapAction(RemapTarget.Trim()),
            ActionKind.TypeText => new TypeTextAction(Text),
            ActionKind.Run => new RunAction(RunTarget.Trim(), string.IsNullOrWhiteSpace(RunArguments) ? null : RunArguments.Trim()),
            ActionKind.Media => new MediaAction(Media),
            ActionKind.SwitchLightingProfile => new SwitchLightingProfileAction(ProfileName.Trim()),
            ActionKind.CustomCode => new CustomCodeAction(Code),
            _ => new DisableKeyAction(),
        },
    };

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..24] + "…";
}
