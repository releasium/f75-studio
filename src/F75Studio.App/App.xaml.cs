using System.Windows;
using System.Windows.Threading;
using F75Studio.App.Services;
using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace F75Studio.App;

public partial class App : Application
{
    private const string InstanceName = "F75Studio";

    private SingleInstance? _instance;
    private AppServices? _services;
    private TrayService? _tray;
    private MainWindow? _window;
    private HotkeysViewModel? _hotkeys;
    private ScriptsViewModel? _scripts;
    private AulaF75Device? _keyboardDevice;
    private KeyboardViewModel? _keyboard;
    private bool _started;
    private bool _exiting;
    private bool _watchingSystemTheme;
    private AppTheme? _appliedTheme;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) =>
        {
            // Logoff/shutdown: no prompt. Keep un-applied cards so they load again next time.
            _hotkeys?.SaveDrafts();
            try { _keyboard?.FlushAsync().GetAwaiter().GetResult(); } catch { /* shutting down anyway */ }
            ExitApp();
        };

        _instance = new SingleInstance(InstanceName);
        if (!_instance.IsFirstInstance)
        {
            var args = e.Args.Length > 0 ? e.Args : ["--show"];
            await SingleInstance.SendToFirstInstanceAsync(InstanceName, args, TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        try
        {
            StartUp(e);
            _started = true;
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    private void StartUp(StartupEventArgs e)
    {
        _services = new AppServices(AppPaths.Default());
        TaskScheduler.UnobservedTaskException += (_, args) => _services.Log.Write(args.Exception, "Unobserved task exception");

        _window = new MainWindow { DataContext = CreateMainViewModel(_services) };
        ApplyTheme(_services.Settings.Theme);
        _services.SettingsChanged += (_, settings) => ApplyTheme(settings.Theme);

        _tray = new TrayService(_services, ShowMainWindow, RequestExit, () => _keyboard?.ProfileNamesSnapshot() ?? [], ApplyLightingProfile);
        _instance!.ArgumentsReceived += (_, args) => Ui.Run(() => HandleArguments(args));
        _services.StartAutoRunScripts();

        if (!e.Args.Contains("--tray")) ShowMainWindow();
        HandleArguments(e.Args);
    }

    private static bool ConfirmOverwrite(string message) => ConfirmDiscard(message);

    private static bool ConfirmDiscard(string message) =>
        System.Windows.MessageBox.Show(message, "F75 Studio", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Builds the navigation pages, in order.</summary>
    private MainViewModel CreateMainViewModel(AppServices services)
    {
        MainViewModel? main = null;
        var hotkeys = new HotkeysViewModel(services);
        var scripts = new ScriptsViewModel(services, hotkeys, page => main?.Navigate(page), ConfirmDiscard, ConfirmOverwrite);
        _hotkeys = hotkeys;
        _scripts = scripts;
        _keyboardDevice = new AulaF75Device();
        var lightingStore = new JsonFileStore<LightingProfileSet>(services.Paths.ProfilesFile, () => new LightingProfileSet());
        var keyboard = new KeyboardViewModel(_keyboardDevice, lightingStore);
        _keyboard = keyboard;
        var settings = new SettingsViewModel(services, new RegistryStartupRegistration(AppExecutable.Path));
        NavItem[] pages =
        [
            new NavItem("Keyboard", SymbolRegular.Lightbulb24, keyboard),
            new NavItem("Hotkeys", SymbolRegular.Keyboard24, hotkeys),
            new NavItem("Scripts", SymbolRegular.Code24, scripts),
            new NavItem("Settings", SymbolRegular.Settings24, settings),
        ];
        return main = new MainViewModel(pages);
    }

    /// <summary>Handles startup arguments and ones forwarded by a second instance: --show, --profile "Name".</summary>
    private void HandleArguments(string[] args)
    {
        if (args.Contains("--show")) ShowMainWindow();
        var index = Array.IndexOf(args, "--profile");
        if (index >= 0 && index + 1 < args.Length) ApplyLightingProfile(args[index + 1]);
    }

    private void ApplyLightingProfile(string name)
    {
        if (_keyboard?.ApplyProfileByName(name) != true)
            _tray?.Notify("Lighting profile not found", $"There is no lighting profile named \"{name}\".");
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>User-initiated exit (tray menu): asks first if there is un-applied or unsaved work.</summary>
    private void RequestExit()
    {
        if (_exiting) return;
        if (!ExitGuard.ShouldExit(_hotkeys?.IsDirty ?? false, _scripts?.IsEditorDirty ?? false, ConfirmDiscard)) return;
        ExitApp();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        // Cancel any recording first so it cannot restart the generated script after StopAll.
        try { _hotkeys?.CancelRecording(resume: false); } catch { /* shutting down anyway */ }
        try { _keyboard?.FlushAsync().GetAwaiter().GetResult(); } catch { /* shutting down anyway */ }
        try { _services?.Runner.StopAll(); }
        catch (Exception ex) { _services?.Log.Write(ex, "Stopping scripts on exit"); }
        finally
        {
            try { _keyboardDevice?.Dispose(); } catch { /* shutting down anyway */ }
            try { _tray?.Dispose(); } catch { /* shutting down anyway */ }
            try { _instance?.Dispose(); } catch { /* shutting down anyway */ }
            if (_window is not null)
            {
                _window.AllowClose = true;
                try { _window.Close(); } catch { /* shutting down anyway */ }
            }
            Shutdown();
        }
    }

    /// <summary>Applies the theme; owns the system-theme watcher (attached only while the theme is System).</summary>
    private void ApplyTheme(AppTheme theme)
    {
        if (_appliedTheme == theme) return;
        _appliedTheme = theme;

        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme();
            if (!_watchingSystemTheme && _window is not null)
            {
                SystemThemeWatcher.Watch(_window);
                _watchingSystemTheme = true;
            }
            return;
        }

        if (_watchingSystemTheme && _window is not null)
        {
            SystemThemeWatcher.UnWatch(_window);
            _watchingSystemTheme = false;
        }
        ApplicationThemeManager.Apply(theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }

    private FileLog GetLog() => _services?.Log ?? new FileLog(AppPaths.Default().LogsFolder);

    private void FailStartup(Exception ex)
    {
        string path;
        try { path = GetLog().Write(ex, "Startup failure"); }
        catch { path = "(could not write log)"; }
        System.Windows.MessageBox.Show($"F75 Studio could not start.\n\n{ex.Message}\n\nDetails: {path}", "F75 Studio", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _instance?.Dispose(); } catch { /* ignore */ }
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (!_started)
        {
            FailStartup(e.Exception);
            return;
        }
        string path;
        try { path = GetLog().Write(e.Exception, "Unhandled UI exception"); }
        catch { path = "(could not write log)"; }
        try { _tray?.Notify("F75 Studio hit an error", $"Details were written to {path}"); }
        catch { /* the handler itself must not throw */ }
    }
}
