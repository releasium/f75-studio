using System.Diagnostics;
using F75Studio.Hotkeys;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class AhkRunnerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Locator_prefers_the_configured_path()
    {
        Assert.Equal(@"D:\ahk\AutoHotkey64.exe", AhkLocator.Find(@"D:\ahk\AutoHotkey64.exe", _ => true));
    }

    [Fact]
    public void Locator_falls_back_to_program_files_v2()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AutoHotkey", "v2", "AutoHotkey64.exe");
        Assert.Equal(expected, AhkLocator.Find(@"D:\missing.exe", p => p == expected));
    }

    [Fact]
    public void Locator_returns_null_when_nothing_exists() => Assert.Null(AhkLocator.Find(null, _ => false));

    [Fact]
    public void Parser_extracts_line_and_message()
    {
        const string output = "C:\\Docs\\F75Studio\\hotkeys.pending.ahk (7) : ==> Missing \")\"\r\n     Specifically: x := (1 + ^j::Send \"b\"\r\n";

        var result = AhkErrorParser.Parse(output);

        Assert.False(result.Success);
        Assert.Equal(7, result.Line);
        Assert.Equal("Missing \")\" (x := (1 + ^j::Send \"b\")", result.Message);
    }

    [Fact]
    public void Parser_keeps_raw_output_when_format_is_unknown()
    {
        var result = AhkErrorParser.Parse("something odd");
        Assert.Null(result.Line);
        Assert.Equal("something odd", result.Message);
    }

    [Fact]
    public async Task Runner_without_executable_throws_AhkNotFound()
    {
        var runner = new AhkRunner(null);
        Assert.Throws<AhkNotFoundException>(() => runner.Start(_temp.Combine("x.ahk")));
        await Assert.ThrowsAsync<AhkNotFoundException>(() => runner.ValidateAsync(_temp.Combine("x.ahk")));
    }

    // Integration tests: run only when AutoHotkey v2 is installed on this machine.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Validate_reports_the_line_of_a_real_syntax_error()
    {
        var exe = AhkLocator.Find(null);
        if (exe is null) return;
        var script = _temp.WriteFile("bad.ahk", "#Requires AutoHotkey v2.0\n^k::Send \"a\"\nx := (1 +\n^j::Send \"b\"\n");

        var result = await new AhkRunner(exe).ValidateAsync(script);

        Assert.False(result.Success);
        Assert.Equal(3, result.Line);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Validate_accepts_a_valid_script()
    {
        var exe = AhkLocator.Find(null);
        if (exe is null) return;
        var script = _temp.WriteFile("good.ahk", "#Requires AutoHotkey v2.0\n^k::Send \"a\"\n");

        Assert.True((await new AhkRunner(exe).ValidateAsync(script)).Success);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Start_and_Stop_track_a_real_process()
    {
        var exe = AhkLocator.Find(null);
        if (exe is null) return;
        var script = _temp.WriteFile("persist.ahk", "#Requires AutoHotkey v2.0\nPersistent\n");
        using var runner = new AhkRunner(exe);
        var changes = new List<string>();
        runner.RunningChanged += (_, path) => { lock (changes) changes.Add(path); };
        var full = Path.GetFullPath(script);
        var before = Process.GetProcessesByName("AutoHotkey64").Select(p => p.Id).ToHashSet();

        runner.Start(script);
        Assert.True(runner.IsRunning(script));
        Assert.Contains(full, runner.RunningScripts);
        lock (changes) Assert.Contains(full, changes);

        // Our process is the AutoHotkey64 instance that appeared since Start.
        Process[] started = [];
        for (var i = 0; i < 30 && started.Length == 0; i++)
        {
            started = Process.GetProcessesByName("AutoHotkey64").Where(p => !before.Contains(p.Id)).ToArray();
            if (started.Length == 0) Thread.Sleep(100);
        }
        Assert.NotEmpty(started);

        int changesBeforeStop;
        lock (changes) changesBeforeStop = changes.Count;
        runner.Stop(script);
        Assert.False(runner.IsRunning(script));
        lock (changes) Assert.True(changes.Count > changesBeforeStop && changes[^1] == full);

        foreach (var p in started)
        {
            Assert.True(p.WaitForExit(3000), "AutoHotkey process should have exited after Stop.");
        }
    }
}
