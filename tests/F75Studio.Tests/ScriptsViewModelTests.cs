using F75Studio.App.Services;
using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class ScriptsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeAhkRunner _runner = new();
    private readonly AppServices _services;
    private readonly HotkeysViewModel _hotkeys;
    private object? _navigatedTo;

    public ScriptsViewModelTests()
    {
        _services = new AppServices(new AppPaths(_temp.Combine("data"), _temp.Combine("docs")), _runner);
        _hotkeys = new HotkeysViewModel(_services);
    }

    public void Dispose() => _temp.Dispose();

    private ScriptsViewModel Create() => new(_services, _hotkeys, page => _navigatedTo = page);

    [Fact]
    public void Lists_scripts_and_loads_the_first_into_the_editor()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "^j::Send \"{Down}\"");
        _temp.WriteFile(Path.Combine("docs", "AutoHotkey", "b.ahk"), "");
        _temp.WriteFile(Path.Combine("docs", "notes.txt"), "");

        var vm = Create();

        Assert.Equal(new[] { "a.ahk", "b.ahk" }, vm.Scripts.Select(s => s.Name));
        Assert.Equal("a.ahk", vm.SelectedScript?.Name);
        Assert.Equal("^j::Send \"{Down}\"", vm.Document.Text);
        Assert.False(vm.IsEditorDirty);
    }

    [Fact]
    public void Save_writes_with_backup()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "old");
        var vm = Create();

        vm.Document.Text = "new";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));
        Assert.False(vm.IsEditorDirty);
    }

    [Fact]
    public void Generated_script_cannot_be_saved()
    {
        _temp.WriteFile(Path.Combine("docs", "F75Studio", "hotkeys.ahk"), "; generated");
        var vm = Create();

        vm.Document.Text = "edited";

        Assert.True(vm.SelectedScript!.IsGenerated);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Start_runs_the_script_and_updates_running_state()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "");
        var vm = Create();

        vm.StartCommand.Execute(null);

        Assert.Contains(Path.GetFullPath(path), _runner.Started);
        Assert.True(vm.SelectedScript!.IsRunning);
    }

    [Fact]
    public void Run_at_startup_is_saved_in_settings()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "");
        var vm = Create();

        vm.SelectedScript!.RunAtStartup = true;
        Assert.Contains(Path.GetFullPath(path), _services.Settings.StartupScripts);

        vm.SelectedScript.RunAtStartup = false;
        Assert.Empty(_services.Settings.StartupScripts);
    }

    [Fact]
    public void Import_sends_the_editor_text_to_the_builder_and_navigates()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "^j::Send \"{Down}\"");
        var vm = Create();

        vm.ImportIntoBuilderCommand.Execute(null);

        var item = Assert.Single(_hotkeys.Items);
        Assert.Equal("a", item.Group);
        Assert.Same(_hotkeys, _navigatedTo);
    }

    [Fact]
    public void Save_failure_is_reported_instead_of_thrown()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "old");
        var vm = Create();
        vm.Document.Text = "new";

        using var locked = new FileStream(path + ".bak", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        vm.SaveCommand.Execute(null);

        Assert.True(vm.IsStatusOpen);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.True(vm.IsEditorDirty);
        Assert.Equal("old", File.ReadAllText(path));
    }
}
