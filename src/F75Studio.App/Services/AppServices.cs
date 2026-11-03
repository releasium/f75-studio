using F75Studio.Core;
using F75Studio.Hotkeys;
using F75Studio.Hotkeys.Model;

namespace F75Studio.App.Services;

/// <summary>Shared app-wide services, created once in App.OnStartup (and per test with temp paths).</summary>
public sealed class AppServices
{
    private readonly JsonFileStore<AppSettings> _settingsStore;

    public AppServices(AppPaths paths, IAhkRunner? runner = null)
    {
        Paths = paths;
        _settingsStore = new JsonFileStore<AppSettings>(paths.SettingsFile, () => new AppSettings());
        Settings = _settingsStore.Load();
        HotkeyStore = new JsonFileStore<List<Hotkey>>(paths.HotkeysFile, () => []);
        Log = new FileLog(paths.LogsFolder);
        Runner = runner ?? new AhkRunner(AhkLocator.Find(Settings.AhkPath));
        Generator = new AhkGenerator(AppExecutable.Path);
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public JsonFileStore<List<Hotkey>> HotkeyStore { get; }
    public AhkGenerator Generator { get; }
    public IAhkRunner Runner { get; }
    public FileLog Log { get; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        _settingsStore.Save(Settings);
        SettingsChanged?.Invoke(this, Settings);
    }

    /// <summary>Starts the generated hotkeys script (if it was ever applied) and the user's "run at startup" scripts.</summary>
    public void StartAutoRunScripts()
    {
        if (Runner.AhkExePath is null) return;
        var scripts = Settings.StartupScripts.Prepend(Paths.GeneratedHotkeysScript).Where(File.Exists);
        foreach (var script in scripts)
        {
            try { Runner.Start(script); }
            catch (Exception ex) { Log.Write(ex, $"Starting {script}"); }
        }
    }
}
