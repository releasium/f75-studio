using F75Studio.Lighting;

namespace F75Studio.Tests;

public class PomodoroTimerTests
{
    private long _now = 1_000_000;
    private PomodoroTimer Create()
    {
        var t = new PomodoroTimer(() => _now);
        t.SetDurations(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5));
        return t;
    }
    private void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;

    [Fact]
    public void Starts_as_a_full_stopped_focus_period()
    {
        var s = Create().Snapshot();

        Assert.Equal(PomodoroPhase.Focus, s.Phase);
        Assert.False(s.Running);
        Assert.Equal(TimeSpan.FromMinutes(15), s.Remaining);
        Assert.Equal(1, s.RemainingFraction);
    }

    [Fact]
    public void Counts_down_only_while_running()
    {
        var t = Create();
        Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromMinutes(15), t.Snapshot().Remaining);

        t.Start();
        Advance(TimeSpan.FromMinutes(5));
        t.Pause();
        Advance(TimeSpan.FromMinutes(10));
        var s = t.Snapshot();

        Assert.False(s.Running);
        Assert.Equal(TimeSpan.FromMinutes(10), s.Remaining);
        Assert.Equal(2 / 3.0, s.RemainingFraction, 6);
    }

    [Fact]
    public void Focus_rolls_into_rest_and_back_without_losing_time()
    {
        var t = Create();
        var changes = new List<PomodoroPhase>();
        t.PhaseChanged += changes.Add;
        t.Start();

        Advance(TimeSpan.FromMinutes(16));
        var rest = t.Snapshot();
        Assert.Equal(PomodoroPhase.Rest, rest.Phase);
        Assert.Equal(TimeSpan.FromMinutes(4), rest.Remaining);
        Assert.Equal(1, rest.CompletedFocus);

        Advance(TimeSpan.FromMinutes(5));
        var focus = t.Snapshot();
        Assert.Equal(PomodoroPhase.Focus, focus.Phase);
        Assert.Equal(TimeSpan.FromMinutes(14), focus.Remaining);
        Assert.True(focus.Running);
        Assert.Equal([PomodoroPhase.Rest, PomodoroPhase.Focus], changes);
    }

    [Fact]
    public void Skip_moves_to_the_next_phase_and_keeps_running()
    {
        var t = Create();
        t.Start();
        Advance(TimeSpan.FromMinutes(2));
        t.Skip();
        var s = t.Snapshot();

        Assert.Equal(PomodoroPhase.Rest, s.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Remaining);
        Assert.True(s.Running);
    }

    [Fact]
    public void A_skipped_focus_is_not_counted_and_raises_no_phase_change()
    {
        var t = Create();
        var changes = 0;
        t.PhaseChanged += _ => changes++;
        t.Start();
        t.Skip();

        Assert.Equal(0, t.Snapshot().CompletedFocus);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Skip_while_paused_stays_paused()
    {
        var t = Create();
        t.Skip();
        var s = t.Snapshot();

        Assert.Equal(PomodoroPhase.Rest, s.Phase);
        Assert.False(s.Running);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Remaining);
    }

    [Fact]
    public void Reset_goes_back_to_a_stopped_full_focus()
    {
        var t = Create();
        t.Start();
        Advance(TimeSpan.FromMinutes(20));
        t.Reset();
        var s = t.Snapshot();

        Assert.Equal(PomodoroPhase.Focus, s.Phase);
        Assert.False(s.Running);
        Assert.Equal(TimeSpan.FromMinutes(15), s.Remaining);
        Assert.Equal(0, s.CompletedFocus);
    }

    [Fact]
    public void New_durations_apply_now_only_if_the_phase_has_not_started()
    {
        var t = Create();
        t.SetDurations(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(25), t.Snapshot().Remaining);

        t.Start();
        Advance(TimeSpan.FromMinutes(1));
        t.SetDurations(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(8));
        Assert.Equal(TimeSpan.FromMinutes(24), t.Snapshot().Remaining); // current phase unchanged

        Advance(TimeSpan.FromMinutes(24));
        Assert.Equal(TimeSpan.FromMinutes(8), t.Snapshot().Remaining); // next phase uses the new rest
    }

    [Fact]
    public void Durations_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create().SetDurations(TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    }
}

public class PomodoroMapperTests
{
    private static Rgb Key(IReadOnlyDictionary<int, Rgb> map, string name) => map[F75Layout.Keys.Single(k => k.Name == name).Led];

    private static PomodoroSnapshot Snap(PomodoroPhase phase, double fractionLeft, bool running = true) =>
        new(phase, TimeSpan.FromMinutes(12 * fractionLeft), TimeSpan.FromMinutes(12), running, 0);

    [Fact]
    public void Focus_is_red_and_a_full_bar_at_the_start()
    {
        var map = PomodoroMapper.Map(Snap(PomodoroPhase.Focus, 1));

        Assert.Equal(80, map.Count);
        Assert.All(map.Values, c => Assert.Equal(PomodoroMapper.FocusColor, c));
    }

    [Fact]
    public void Rest_is_blue()
    {
        var map = PomodoroMapper.Map(Snap(PomodoroPhase.Rest, 1));

        Assert.Equal(PomodoroMapper.RestColor, Key(map, "J"));
        Assert.Equal(PomodoroMapper.RestColor, Key(map, "F12"));
    }

    [Fact]
    public void Bar_drains_from_F12_towards_F1()
    {
        // 5.5 of 12 twelfths left: F1-F5 lit, F6 half, F7-F12 off; the rest of the board stays red.
        var map = PomodoroMapper.Map(Snap(PomodoroPhase.Focus, 5.5 / 12));

        foreach (var n in new[] { "F1", "F2", "F3", "F4", "F5" }) Assert.Equal(PomodoroMapper.FocusColor, Key(map, n));
        Assert.Equal(new Rgb(128, 0, 0), Key(map, "F6"));
        foreach (var n in new[] { "F7", "F8", "F9", "F10", "F11", "F12" }) Assert.Equal(new Rgb(0, 0, 0), Key(map, n));
        Assert.Equal(PomodoroMapper.FocusColor, Key(map, "Esc"));
        Assert.Equal(PomodoroMapper.FocusColor, Key(map, "Space"));
    }

    [Fact]
    public void Rest_bar_works_the_same_way()
    {
        var map = PomodoroMapper.Map(Snap(PomodoroPhase.Rest, 3.0 / 12));

        Assert.Equal(PomodoroMapper.RestColor, Key(map, "F3"));
        Assert.Equal(new Rgb(0, 0, 0), Key(map, "F4"));
    }

    [Fact]
    public void Paused_dims_everything()
    {
        var map = PomodoroMapper.Map(Snap(PomodoroPhase.Focus, 0.5, running: false));

        Assert.Equal(new Rgb(89, 0, 0), Key(map, "J"));
        Assert.Equal(new Rgb(89, 0, 0), Key(map, "F1"));
        Assert.Equal(new Rgb(0, 0, 0), Key(map, "F12"));
    }
}
