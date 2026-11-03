using System.Windows;
using System.Windows.Threading;
using F75Studio.App.Services;
using F75Studio.App.ViewModels;
using F75Studio.Core;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance(InstanceName);
        if (!_instance.IsFirstInstance)
        {
            var args = e.Args.Length > 0 ? e.Args : ["--show"];
            await SingleInstance.SendToFirstInstanceAsync(InstanceName, args, TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        _services = new AppServices(AppPaths.Default());
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => _services.Log.Write(args.Exception, "Unobserved task exception");

        ApplyTheme(_services.Settings.Theme);
        _services.SettingsChanged += (_, settings) => ApplyTheme(settings.Theme);

        _window = new MainWindow { DataContext = CreateMainViewModel(_services) };
        if (_services.Settings.Theme == AppTheme.System) SystemThemeWatcher.Watch(_window);

        _tray = new TrayService(_services, ShowMainWindow, ExitApp);
        _instance.ArgumentsReceived += (_, args) => Dispatcher.Invoke(() => HandleArguments(args));
        _services.StartAutoRunScripts();

        if (!e.Args.Contains("--tray")) ShowMainWindow();
    }

    /// <summary>Builds the left navigation pages, in order. Tasks 10 and 11 replace this method.</summary>
    private static MainViewModel CreateMainViewModel(AppServices services)
    {
        var settings = new SettingsViewModel(services, new RegistryStartupRegistration(Environment.ProcessPath!));
        return new MainViewModel([new NavItem("Settings", SymbolRegular.Settings24, settings)]);
    }

    /// <summary>Handles arguments forwarded by a second instance. Plan 2 adds "--profile".</summary>
    private void HandleArguments(string[] args)
    {
        if (args.Contains("--show")) ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _services?.Runner.StopAll();
        _tray?.Dispose();
        _instance?.Dispose();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    private static void ApplyTheme(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.Light: ApplicationThemeManager.Apply(ApplicationTheme.Light); break;
            case AppTheme.Dark: ApplicationThemeManager.Apply(ApplicationTheme.Dark); break;
            default: ApplicationThemeManager.ApplySystemTheme(); break;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = _services!.Log.Write(e.Exception, "Unhandled UI exception");
        _tray?.Notify("F75 Studio hit an error", $"Details were written to {path}");
        e.Handled = true;
    }
}
