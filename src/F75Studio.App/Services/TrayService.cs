using System.ComponentModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using F75Studio.Hotkeys;
using F75Studio.Lighting;
using H.NotifyIcon;

namespace F75Studio.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly AppServices _services;
    private readonly HotkeyPauser _pauser;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Func<IReadOnlyList<string>> _lightingProfiles;
    private readonly Action<string> _applyLightingProfile;
    private readonly Action _showPomodoro;
    private readonly Func<bool> _pomodoroOpen;
    private readonly Func<int?> _currentEffect;
    private readonly Action<int> _selectEffect;
    private readonly TaskbarIcon _icon;

    /// <param name="currentEffect">The selected lighting effect id, or null while the Keyboard page isn't ready.</param>
    public TrayService(AppServices services, Action show, Action exit,
        Func<IReadOnlyList<string>> lightingProfiles, Action<string> applyLightingProfile,
        Action showPomodoro, Func<bool> pomodoroOpen, Func<int?> currentEffect, Action<int> selectEffect)
    {
        _currentEffect = currentEffect;
        _selectEffect = selectEffect;
        _showPomodoro = showPomodoro;
        _pomodoroOpen = pomodoroOpen;
        _lightingProfiles = lightingProfiles;
        _applyLightingProfile = applyLightingProfile;
        _services = services;
        _pauser = new HotkeyPauser(services.Runner);
        _show = show;
        _exit = exit;

        var menu = new ContextMenu();
        menu.Opened += (_, _) => BuildMenu(menu);
        BuildMenu(menu);

        _icon = new TaskbarIcon
        {
            ToolTipText = "F75 Studio",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(AppExecutable.Path),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(show),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();

    /// <summary>Rebuilt every time the menu opens so script states are current.</summary>
    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("Open F75 Studio", _show));
        menu.Items.Add(new Separator());

        var lighting = new MenuItem { Header = "Lighting" };
        foreach (var name in _lightingProfiles())
        {
            var profileName = name;
            lighting.Items.Add(Item(profileName.Replace("_", "__"),() => _applyLightingProfile(profileName)));
        }
        if (lighting.Items.Count == 0) lighting.Items.Add(new MenuItem { Header = "No profiles", IsEnabled = false });
        menu.Items.Add(lighting);

        var effects = new MenuItem { Header = "Effect" };
        var current = _currentEffect();
        foreach (var effect in F75Effects.All)
        {
            var id = effect.Id;
            var item = Item(effect.Name, () => _selectEffect(id));
            item.IsChecked = current == id;
            effects.Items.Add(item);
        }
        effects.IsEnabled = current is not null;
        menu.Items.Add(effects);

        var pomodoro = Item("Pomodoro timer", _showPomodoro);
        pomodoro.IsChecked = _pomodoroOpen(); // a mark only: clicking always opens or brings forward the timer
        menu.Items.Add(pomodoro);

        var scripts = new MenuItem { Header = "Scripts" };
        foreach (var script in ScriptDiscovery.Discover(_services.Paths.ScriptSearchFolders))
        {
            var running = _services.Runner.IsRunning(script.Path);
            var item = new MenuItem { Header = script.Name, IsCheckable = true, IsChecked = running, IsEnabled = !_pauser.IsPaused };
            item.Click += (_, _) => Guard(() =>
            {
                if (_services.Runner.IsRunning(script.Path)) _services.Runner.Stop(script.Path);
                else _services.Runner.Start(script.Path);
            });
            scripts.Items.Add(item);
        }
        if (scripts.Items.Count == 0) scripts.Items.Add(new MenuItem { Header = "No scripts found", IsEnabled = false });
        if (_pauser.IsPaused) scripts.Header = "Scripts (paused - resume to change)";
        menu.Items.Add(scripts);

        menu.Items.Add(Item(_pauser.IsPaused ? "Resume hotkeys" : "Pause all hotkeys", TogglePause));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", _exit));
    }

    private void TogglePause() => Guard(() =>
    {
        if (_pauser.IsPaused) _pauser.Resume();
        else _pauser.Pause();
        _icon.ToolTipText = _pauser.IsPaused ? "F75 Studio (hotkeys paused)" : "F75 Studio";
    });

    private void Guard(Action action)
    {
        try { action(); }
        catch (AhkNotFoundException) { Notify("AutoHotkey not found", "Set the AutoHotkey v2 path in F75 Studio Settings."); }
        catch (Win32Exception ex) { Notify("Could not run AutoHotkey", ex.Message); }
        catch (InvalidOperationException ex) { Notify("Could not run AutoHotkey", ex.Message); }
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
