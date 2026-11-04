using System.ComponentModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using F75Studio.Hotkeys;
using F75Studio.Lighting;
using H.NotifyIcon;

namespace F75Studio.App.Services;

/// <summary>What the tray needs to show and drive the Pomodoro timer.</summary>
/// <param name="State">The timer's state while its window is open; null when it isn't.</param>
public sealed record PomodoroControls(Func<PomodoroSnapshot?> State, Action Show, Action StartPause, Action Skip, Action Reset, Action Stop);

public sealed class TrayService : IDisposable
{
    private readonly AppServices _services;
    private readonly HotkeyPauser _pauser;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Func<IReadOnlyList<string>> _lightingProfiles;
    private readonly Action<string> _applyLightingProfile;
    private readonly PomodoroControls _pomodoro;
    private readonly System.Drawing.Icon _defaultIcon;
    private string? _timerIconKey; // what the timer icon shows; null while the app icon is shown
    private readonly Func<int?> _currentEffect;
    private readonly Action<int> _selectEffect;
    private readonly TaskbarIcon _icon;

    /// <param name="currentEffect">The selected lighting effect id, or null while the Keyboard page isn't ready.</param>
    public TrayService(AppServices services, Action show, Action exit,
        Func<IReadOnlyList<string>> lightingProfiles, Action<string> applyLightingProfile,
        PomodoroControls pomodoro, Func<int?> currentEffect, Action<int> selectEffect)
    {
        _pomodoro = pomodoro;
        _currentEffect = currentEffect;
        _selectEffect = selectEffect;

        _lightingProfiles = lightingProfiles;
        _applyLightingProfile = applyLightingProfile;
        _services = services;
        _pauser = new HotkeyPauser(services.Runner);
        _show = show;
        _exit = exit;

        var menu = new ContextMenu();
        menu.Opened += (_, _) => BuildMenu(menu);
        BuildMenu(menu);

        _defaultIcon = System.Drawing.Icon.ExtractAssociatedIcon(AppExecutable.Path)!;
        _icon = new TaskbarIcon
        {
            ToolTipText = "F75 Studio",
            Icon = (System.Drawing.Icon)_defaultIcon.Clone(),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(show),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();

    /// <summary>
    /// While the Pomodoro timer is open (snapshot not null) the icon shows the minutes left on a red (focus), blue (rest)
    /// or green (paused / not started) tile and the tooltip the exact time; null puts the app icon back.
    /// </summary>
    public void UpdatePomodoro(PomodoroSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            if (_timerIconKey is null) return;
            // TaskbarIcon disposes the icon it replaces, so it always gets its own copy.
            _icon.Icon = (System.Drawing.Icon)_defaultIcon.Clone();
            _timerIconKey = null;
            _icon.ToolTipText = DefaultToolTip;
            return;
        }

        var text = TrayIconRenderer.MinutesText(snapshot.Remaining);
        var color = !snapshot.Running ? System.Drawing.Color.FromArgb(0x1E, 0x9E, 0x4A)
            : snapshot.Phase == PomodoroPhase.Focus ? System.Drawing.Color.FromArgb(0xE5, 0x38, 0x3B)
            : System.Drawing.Color.FromArgb(0x2F, 0x6B, 0xFF);
        var key = $"{text}|{color.ToArgb()}";
        if (key != _timerIconKey)
        {
            _icon.Icon = TrayIconRenderer.Render(text, color); // TaskbarIcon disposes the icon it replaces
            _timerIconKey = key;
        }
        _icon.ToolTipText = $"{PomodoroStatus(snapshot)} — F75 Studio";
    }

    private string DefaultToolTip => _pauser.IsPaused ? "F75 Studio (hotkeys paused)" : "F75 Studio";

    private static string PomodoroStatus(PomodoroSnapshot s)
    {
        var phase = s.Phase == PomodoroPhase.Focus ? "Focus" : "Rest";
        var state = s.Running ? "left" : s.Remaining == s.Duration ? "ready" : "paused";
        return $"{phase} · {(int)s.Remaining.TotalMinutes:00}:{s.Remaining.Seconds:00} {state}";
    }

    /// <summary>Rebuilt every time the menu opens so script states are current.</summary>
    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_pomodoro.State() is { } timer)
        {
            menu.Items.Add(new MenuItem { Header = PomodoroStatus(timer), IsEnabled = false });
            menu.Items.Add(Item(timer.Running ? "Pause" : timer.Remaining == timer.Duration ? "Start" : "Resume", _pomodoro.StartPause));
            menu.Items.Add(Item("Skip", _pomodoro.Skip));
            menu.Items.Add(Item("Reset", _pomodoro.Reset));
            menu.Items.Add(Item("Stop timer", _pomodoro.Stop));
            menu.Items.Add(Item("Show timer", _pomodoro.Show));
            menu.Items.Add(new Separator());
        }
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

        if (_pomodoro.State() is null) menu.Items.Add(Item("Pomodoro timer", _pomodoro.Show));

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
        if (_timerIconKey is null) _icon.ToolTipText = DefaultToolTip;
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
