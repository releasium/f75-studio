using F75Studio.Hotkeys;

namespace F75Studio.Tests;

public class TriggerRecorderTests
{
    [Theory]
    [InlineData("A", "a")]
    [InlineData("D5", "5")]
    [InlineData("F13", "F13")]
    [InlineData("Capital", "CapsLock")]
    [InlineData("LeftCtrl", "LCtrl")]
    [InlineData("Next", "PgDn")]
    [InlineData("OemComma", ",")]
    [InlineData("Oem1", "`;")]
    [InlineData("NumPad3", "Numpad3")]
    [InlineData("MediaPlayPause", "Media_Play_Pause")]
    public void Maps_wpf_key_names(string wpf, string ahk) => Assert.Equal(ahk, AhkKeyNames.FromWpfKeyName(wpf));

    [Fact]
    public void Unknown_keys_map_to_null() => Assert.Null(AhkKeyNames.FromWpfKeyName("ImeProcessed"));

    [Fact]
    public void Modifiers_then_key_produce_prefixed_trigger()
    {
        var r = Press(["LCtrl", "LAlt", "k"]);
        Assert.Equal("^!k", r.Trigger);
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void Win_shift_s_uses_hash_and_plus() => Assert.Equal("#+s", Press(["LWin", "LShift", "s"]).Trigger);

    [Fact]
    public void Holding_a_normal_key_then_another_makes_a_custom_combo() =>
        Assert.Equal("CapsLock & h", Press(["CapsLock", "h"]).Trigger);

    [Fact]
    public void A_lone_modifier_becomes_the_trigger() => Assert.Equal("RCtrl", Press(["RCtrl"]).Trigger);

    [Fact]
    public void Auto_repeat_and_input_after_completion_are_ignored()
    {
        var r = new TriggerRecorder();
        r.KeyDown("F13");
        r.KeyDown("F13");
        r.KeyUp("F13");
        r.KeyDown("x");
        Assert.Equal("F13", r.Trigger);
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void Not_complete_while_keys_are_held()
    {
        var r = new TriggerRecorder();
        r.KeyDown("LCtrl");
        r.KeyDown("k");
        r.KeyUp("k");
        Assert.Equal("^k", r.Trigger);
        Assert.False(r.IsComplete);
    }

    /// <summary>Presses keys in order, then releases them in reverse order.</summary>
    private static TriggerRecorder Press(string[] keys)
    {
        var r = new TriggerRecorder();
        foreach (var k in keys) r.KeyDown(k);
        foreach (var k in keys.Reverse()) r.KeyUp(k);
        return r;
    }
}
