using F75Studio.Hotkeys;
using F75Studio.Hotkeys.Model;

namespace F75Studio.Tests;

public class AhkImporterTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "autokeyscript.ahk"));

    [Fact]
    public void Imports_every_hotkey_of_the_vim_layer_script()
    {
        var result = AhkImporter.Import(Fixture(), "autokeyscript");

        Assert.Equal(12, result.Hotkeys.Count);
        Assert.Empty(result.SkippedLines);

        var first = result.Hotkeys[0];
        Assert.Equal("CapsLock & h", first.Trigger);
        Assert.Equal("Basic Vim-style navigation", first.Group);
        Assert.Equal(new SendKeysAction("{Left}", Blind: true), first.Action);
    }

    [Fact]
    public void Imports_Return_as_disable_and_brace_block_as_custom_code()
    {
        var hotkeys = AhkImporter.Import(Fixture(), "autokeyscript").Hotkeys;

        var disable = Assert.Single(hotkeys, h => h.Trigger == "CapsLock");
        Assert.IsType<DisableKeyAction>(disable.Action);
        Assert.Equal("Prevent Caps Lock from toggling when pressed alone", disable.Group);

        var toggle = Assert.Single(hotkeys, h => h.Trigger == "CapsLock & Space");
        Assert.Equal(new CustomCodeAction("SetCapsLockState GetKeyState(\"CapsLock\", \"T\") ? \"Off\" : \"On\""), toggle.Action);
    }

    [Fact]
    public void Generate_then_import_round_trips()
    {
        var generator = new AhkGenerator(@"C:\Apps\F75 Studio\F75Studio.exe");
        var source = AhkImporter.Import(Fixture(), "autokeyscript").Hotkeys
            .Concat(HotkeyModelTests.AllActionKinds().Where(h => h.Enabled))
            .ToList();

        var reimported = AhkImporter.Import(generator.Generate(source).Text, "ignored").Hotkeys;

        Assert.Equal(Normalize(source), Normalize(reimported));
    }

    [Fact]
    public void HotIf_sets_and_clears_app_scope()
    {
        var hotkeys = AhkImporter.Import("#HotIf WinActive(\"ahk_exe chrome.exe\")\n^t::Send \"^t\"\n#HotIf\n^y::Return\n", "g").Hotkeys;

        Assert.Equal(2, hotkeys.Count);
        Assert.Equal("chrome.exe", hotkeys[0].AppScope);
        Assert.Null(hotkeys[1].AppScope);
    }

    [Fact]
    public void Bare_words_are_remaps_only_when_they_are_key_names()
    {
        var hotkeys = AhkImporter.Import("^q::ExitApp\nF5::Reload\nSpace::Enter\na::b\n", "g").Hotkeys;

        Assert.Equal(new CustomCodeAction("ExitApp"), hotkeys[0].Action);
        Assert.Equal(new CustomCodeAction("Reload"), hotkeys[1].Action);
        Assert.Equal(new RemapAction("Enter"), hotkeys[2].Action);
        Assert.Equal(new RemapAction("b"), hotkeys[3].Action);
    }

    [Fact]
    public void Unsupported_HotIf_skips_its_section_until_the_next_HotIf()
    {
        var result = AhkImporter.Import("#HotIf GetKeyState(\"Shift\")\n^k::Send \"x\"\n^m::\n{\n    MsgBox 1\n}\n#HotIf\n^j::Return\n", "g");

        var hotkey = Assert.Single(result.Hotkeys);
        Assert.Equal("^j", hotkey.Trigger);
        Assert.IsType<DisableKeyAction>(hotkey.Action);
        Assert.Null(hotkey.AppScope);
        Assert.Equal(new[] { "#HotIf GetKeyState(\"Shift\")", "^k::Send \"x\"", "^m::" }, result.SkippedLines);
    }

    [Fact]
    public void Unsupported_lines_are_reported_not_imported()
    {
        var result = AhkImporter.Import("::btw::by the way\nMsgBox \"a::b\"\n#Warn\n^j::Send \"{Down}\" ; trailing comment\n", "g");

        var hotkey = Assert.Single(result.Hotkeys);
        Assert.Equal(new SendKeysAction("{Down}"), hotkey.Action);
        Assert.Equal(new[] { "::btw::by the way", "MsgBox \"a::b\"", "#Warn" }, result.SkippedLines);
    }

    [Fact]
    public void One_liners_map_to_typed_actions()
    {
        var hotkeys = AhkImporter.Import(
            "a::b\nF13::Send \"{Media_Next}\"\n^r::Run \"notepad.exe C:\\notes.txt\"\n^x::MsgBox(\"hi\")\n", "g").Hotkeys;

        Assert.Equal(new RemapAction("b"), hotkeys[0].Action);
        Assert.Equal(new MediaAction(MediaCommand.Next), hotkeys[1].Action);
        Assert.Equal(new RunAction("notepad.exe", @"C:\notes.txt"), hotkeys[2].Action);
        Assert.Equal(new CustomCodeAction("MsgBox(\"hi\")"), hotkeys[3].Action);
        Assert.All(hotkeys, h => Assert.Equal("g", h.Group));
    }

    private static List<Hotkey> Normalize(IEnumerable<Hotkey> hotkeys) =>
        hotkeys.Select(h => h with { Id = Guid.Empty }).ToList();
}
