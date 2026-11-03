using F75Studio.Hotkeys;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class ScriptDiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Finds_ahk_files_in_each_folder_without_duplicates()
    {
        _temp.WriteFile("b.ahk", "");
        _temp.WriteFile("notes.txt", "");
        _temp.WriteFile(Path.Combine("AutoHotkey", "a.ahk"), "");
        _temp.WriteFile(Path.Combine("deep", "nested", "c.ahk"), "");

        var scripts = ScriptDiscovery.Discover([_temp.Path, _temp.Combine("AutoHotkey"), _temp.Path, _temp.Combine("missing")]);

        Assert.Equal(new[] { "a.ahk", "b.ahk" }, scripts.Select(s => s.Name));
        Assert.All(scripts, s => Assert.True(Path.IsPathFullyQualified(s.Path)));
    }

    [Fact]
    public void SaveWithBackup_keeps_previous_content()
    {
        var path = _temp.WriteFile("x.ahk", "old");

        ScriptFile.SaveWithBackup(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void Write_creates_folder_and_writes_utf8_without_bom()
    {
        var path = _temp.Combine("gen", "hotkeys.ahk");

        ScriptFile.Write(path, "é");

        Assert.Equal(new byte[] { 0xC3, 0xA9 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Backups_and_pending_files_are_not_listed()
    {
        _temp.WriteFile("a.ahk", "");
        _temp.WriteFile("a.ahk.bak", "");
        _temp.WriteFile("a.ahk.orig", "");
        _temp.WriteFile("hotkeys.pending.ahk", "");

        var scripts = ScriptDiscovery.Discover([_temp.Path]);

        Assert.Equal(new[] { "a.ahk" }, scripts.Select(s => s.Name));
    }

    [Fact]
    public void SaveWithBackup_writes_orig_once_and_rolls_bak()
    {
        var path = _temp.WriteFile("x.ahk", "v1");

        ScriptFile.SaveWithBackup(path, "v2");
        ScriptFile.SaveWithBackup(path, "v3");

        Assert.Equal("v3", File.ReadAllText(path));
        Assert.Equal("v1", File.ReadAllText(path + ".orig"));
        Assert.Equal("v2", File.ReadAllText(path + ".bak"));
    }
}
