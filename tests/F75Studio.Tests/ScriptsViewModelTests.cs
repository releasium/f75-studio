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

    private ScriptsViewModel Create(Func<string, bool> confirm) => new(_services, _hotkeys, page => _navigatedTo = page, confirm);

    [Fact]
    public void Switching_with_unsaved_edits_and_confirm_declined_keeps_everything()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "A");
        _temp.WriteFile(Path.Combine("docs", "b.ahk"), "B");
        var asked = new List<string>();
        var vm = Create(m => { asked.Add(m); return false; });
        var a = vm.SelectedScript;
        vm.Document.Text = "A edited";

        vm.SelectedScript = vm.Scripts[1];

        Assert.Single(asked);
        Assert.Same(a, vm.SelectedScript);
        Assert.Equal("A edited", vm.Document.Text);
        Assert.True(vm.IsEditorDirty);
    }

    [Fact]
    public void Switching_with_unsaved_edits_and_confirm_accepted_loads_the_new_script()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "A");
        _temp.WriteFile(Path.Combine("docs", "b.ahk"), "B");
        var vm = Create(_ => true);
        vm.Document.Text = "A edited";

        vm.SelectedScript = vm.Scripts[1];

        Assert.Equal("b.ahk", vm.SelectedScript!.Name);
        Assert.Equal("B", vm.Document.Text);
        Assert.False(vm.IsEditorDirty);
    }

    [Fact]
    public void Refresh_keeps_unsaved_edits()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "A");
        var vm = Create(_ => false);
        vm.Document.Text = "A edited";

        vm.RefreshCommand.Execute(null);

        Assert.Equal("A edited", vm.Document.Text);
        Assert.True(vm.IsEditorDirty);
        Assert.Equal(Path.GetFullPath(path), vm.SelectedScript!.Path);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Deleted_script_gives_an_error_empty_editor_and_no_save()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "A");
        var b = _temp.WriteFile(Path.Combine("docs", "b.ahk"), "B");
        var vm = Create(_ => true);
        File.Delete(b);

        vm.SelectedScript = vm.Scripts[1];
        vm.Document.Text = "typed anyway";

        Assert.True(vm.IsStatusOpen);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.True(vm.IsEditorReadOnly);
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_temp.Combine("docs"), "a.ahk")));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void Start_failure_is_reported_instead_of_thrown()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "");
        var vm = Create();
        _runner.StartException = new System.ComponentModel.Win32Exception("boom");

        vm.StartCommand.Execute(null);

        Assert.True(vm.IsStatusOpen);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.False(vm.SelectedScript!.IsRunning);
    }

    private static void Touch(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void Save_refuses_when_file_changed_on_disk_and_overwrite_declined()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "old");
        var vm = Create();
        vm.Document.Text = "mine";
        Touch(path, "external");

        vm.SaveCommand.Execute(null);

        Assert.Equal("external", File.ReadAllText(path));
        Assert.True(vm.IsEditorDirty);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Equal("Not saved: file changed on disk. Refresh to reload it.", vm.StatusMessage);
    }

    [Fact]
    public void Save_overwrites_changed_file_when_confirmed()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "old");
        string? asked = null;
        var vm = new ScriptsViewModel(_services, _hotkeys, _ => { }, null, m => { asked = m; return true; });
        vm.Document.Text = "mine";
        Touch(path, "external");

        vm.SaveCommand.Execute(null);

        Assert.Equal("mine", File.ReadAllText(path));
        Assert.Equal("external", File.ReadAllText(path + ".bak"));
        Assert.Equal("a.ahk was changed outside F75 Studio. Overwrite those changes?", asked);
        Assert.False(vm.IsEditorDirty);
    }

    [Fact]
    public void Second_save_does_not_prompt_about_its_own_write()
    {
        _temp.WriteFile(Path.Combine("docs", "a.ahk"), "old");
        var vm = Create();
        vm.Document.Text = "one";
        vm.SaveCommand.Execute(null);
        vm.Document.Text = "two";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Saved a.ahk. The previous version is kept as a.ahk.bak.", vm.StatusMessage);
    }

    [Fact]
    public void First_save_mentions_orig_and_keeps_it_afterwards()
    {
        var path = _temp.WriteFile(Path.Combine("docs", "a.ahk"), "original");
        var vm = Create();
        vm.Document.Text = "v2";
        vm.SaveCommand.Execute(null);
        Assert.Contains(".orig", vm.StatusMessage);
        vm.Document.Text = "v3";
        vm.SaveCommand.Execute(null);

        Assert.Equal("original", File.ReadAllText(path + ".orig"));
        Assert.Equal("v2", File.ReadAllText(path + ".bak"));
        Assert.DoesNotContain(".orig", vm.StatusMessage);
    }

    [Fact]
    public void Import_is_disabled_for_the_generated_script()
    {
        _temp.WriteFile(Path.Combine("docs", "F75Studio", "hotkeys.ahk"), "; generated");
        var vm = Create();

        Assert.True(vm.SelectedScript!.IsGenerated);
        Assert.False(vm.ImportIntoBuilderCommand.CanExecute(null));
    }
}
