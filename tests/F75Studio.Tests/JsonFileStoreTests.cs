using F75Studio.Core;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class JsonFileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_returns_default_when_file_is_missing()
    {
        var store = new JsonFileStore<AppSettings>(_temp.Combine("settings.json"), () => new AppSettings());

        var settings = store.Load();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Null(settings.AhkPath);
        Assert.Empty(settings.StartupScripts);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = _temp.Combine("nested", "settings.json");
        var store = new JsonFileStore<AppSettings>(path, () => new AppSettings());

        store.Save(new AppSettings { AhkPath = @"C:\ahk.exe", Theme = AppTheme.Dark, StartupScripts = [@"C:\a.ahk"] });
        var loaded = store.Load();

        Assert.Equal(@"C:\ahk.exe", loaded.AhkPath);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(new[] { @"C:\a.ahk" }, loaded.StartupScripts);
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(path));
    }

    [Fact]
    public void Load_returns_default_and_keeps_a_copy_when_file_is_corrupt()
    {
        var path = _temp.WriteFile("settings.json", "{ not json");
        var store = new JsonFileStore<AppSettings>(path, () => new AppSettings());

        var settings = store.Load();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void AppPaths_places_generated_script_under_documents()
    {
        var paths = new AppPaths(@"C:\data", @"C:\docs");

        Assert.Equal(@"C:\docs\F75Studio\hotkeys.ahk", paths.GeneratedHotkeysScript);
        Assert.Equal(@"C:\data\hotkeys.json", paths.HotkeysFile);
        Assert.Equal(new[] { @"C:\docs", @"C:\docs\AutoHotkey", @"C:\docs\F75Studio" }, paths.ScriptSearchFolders);
    }
}
