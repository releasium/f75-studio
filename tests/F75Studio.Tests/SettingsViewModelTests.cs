using F75Studio.App.Services;
using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace F75Studio.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeAhkRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    private AppServices CreateServices() => new(new AppPaths(_temp.Combine("data"), _temp.Combine("docs")), _runner);

    [Fact]
    public void Changing_theme_persists_and_raises_SettingsChanged()
    {
        var services = CreateServices();
        AppSettings? raised = null;
        services.SettingsChanged += (_, s) => raised = s;
        var vm = new SettingsViewModel(services, new FakeStartupRegistration());

        vm.Theme = AppTheme.Dark;

        Assert.Equal(AppTheme.Dark, raised?.Theme);
        Assert.Equal(AppTheme.Dark, new JsonFileStore<AppSettings>(services.Paths.SettingsFile, () => new()).Load().Theme);
    }

    [Fact]
    public void Valid_ahk_path_updates_runner_and_settings()
    {
        var services = CreateServices();
        var exe = _temp.WriteFile("AutoHotkey64.exe", "");
        var vm = new SettingsViewModel(services, new FakeStartupRegistration());

        vm.AhkPath = exe;

        Assert.Equal(exe, _runner.AhkExePath);
        Assert.Equal(exe, services.Settings.AhkPath);
        Assert.Equal("AutoHotkey found.", vm.AhkStatus);
    }

    [Fact]
    public void Missing_ahk_path_is_reported_and_not_saved()
    {
        var services = CreateServices();
        var vm = new SettingsViewModel(services, new FakeStartupRegistration());

        vm.AhkPath = _temp.Combine("nope.exe");

        Assert.Equal("File not found.", vm.AhkStatus);
        Assert.Null(services.Settings.AhkPath);
    }

    [Fact]
    public void Start_with_windows_toggles_registration()
    {
        var startup = new FakeStartupRegistration();
        var vm = new SettingsViewModel(CreateServices(), startup);

        vm.StartWithWindows = true;

        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void Navigate_selects_the_item_for_a_page()
    {
        var a = new object();
        var b = new object();
        var main = new MainViewModel([new NavItem("A", SymbolRegular.Home24, a), new NavItem("B", SymbolRegular.Settings24, b)]);

        main.Navigate(b);

        Assert.Same(b, main.Selected.Page);
    }
}
