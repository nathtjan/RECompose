// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Timing;

// PB-32…34 on fixture A (72 BPM, 3/4, downbeat 0.02 s, 310.02 s = 124 bars), played as written.
public class ReadoutsTests
{
    private static readonly TempoGrid FixtureA = new(72, 3, 0.02);

    [Fact]
    public void A_bar_at_72_BPM_in_3_4_is_two_and_a_half_seconds()
    {
        Assert.Equal(2.5, FixtureA.BarLength, 9);
        Assert.Equal(60.0 / 72, FixtureA.BeatLength, 9);
    }

    [Theory]
    [InlineData(78.52, "32.2")]   // AT-2: chart bar 32.2
    [InlineData(0.0, "0.3")]      // before the first downbeat: the pickup beat
    [InlineData(0.02, "1.1")]
    [InlineData(0.019, "1.1")]    // within the 2 ms tolerance of the downbeat
    [InlineData(2.52, "2.1")]
    [InlineData(2.5, "1.3")]
    [InlineData(310.0, "124.3")]
    public void Song_bar_follows_PB_32(double seconds, string expected)
    {
        Assert.Equal(expected, Readouts.SongBar(FixtureA, seconds).ToString());
    }

    [Fact]
    public void Fixture_A_has_124_bars()
    {
        Assert.Equal(124, Readouts.TotalBars(FixtureA, 310.02));
    }

    [Fact]
    public void Times_read_elapsed_and_remaining_with_a_real_minus_sign()
    {
        var (elapsed, remaining) = Readouts.Times(78.52, 310.02);
        Assert.Equal("1:18", elapsed);
        Assert.Equal("−3:51", remaining);
    }

    [Theory]
    [InlineData(10.02, 1, 12.52)]   // on a bar line: next bar
    [InlineData(11.0, 1, 12.52)]    // inside a bar: the next bar line
    [InlineData(11.0, -1, 10.02)]   // inside a bar: back to its start
    [InlineData(10.02, -1, 7.52)]   // on a bar line: the previous bar
    [InlineData(0.5, -1, 0.02)]     // inside bar 1: its downbeat
    [InlineData(0.02, -1, 0.0)]     // clamped at the start
    public void Arrow_keys_step_one_bar(double from, int bars, double expected)
    {
        Assert.Equal(expected, Readouts.StepBars(FixtureA, from, bars, 310.02), 6);
    }

    [Theory]
    [InlineData(72.0, "72")]
    [InlineData(71.97, "72")]
    [InlineData(120.5, "120.5")]
    public void BPM_shows_as_an_integer_when_within_0_05(double bpm, string expected)
    {
        Assert.Equal(expected, Format.Bpm(bpm));
    }
}
