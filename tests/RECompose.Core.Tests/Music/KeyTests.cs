// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Music;

namespace RECompose.Core.Tests.Music;

// DM-11: spelling C, Db, D, Eb, E, F, F#, G, Ab, A, Bb, B (+ "m"); lenient parsing (D# → Eb).
public class KeyTests
{
    [Theory]
    [InlineData("C", 0, KeyMode.Major, "C")]
    [InlineData("Bb", 10, KeyMode.Major, "Bb")]
    [InlineData("B♭", 10, KeyMode.Major, "Bb")]
    [InlineData("bb", 10, KeyMode.Major, "Bb")]
    [InlineData("D#", 3, KeyMode.Major, "Eb")]
    [InlineData("a#", 10, KeyMode.Major, "Bb")]
    [InlineData("F♯", 6, KeyMode.Major, "F#")]
    [InlineData("Gb", 6, KeyMode.Major, "F#")]
    [InlineData("Cb", 11, KeyMode.Major, "B")]
    [InlineData("B#", 0, KeyMode.Major, "C")]
    [InlineData("Em", 4, KeyMode.Minor, "Em")]
    [InlineData("bm", 11, KeyMode.Minor, "Bm")]
    [InlineData("C#m", 1, KeyMode.Minor, "Dbm")]
    [InlineData("Ebmaj", 3, KeyMode.Major, "Eb")]
    [InlineData(" A minor ", 9, KeyMode.Minor, "Am")]
    public void Parses_leniently_and_spells_canonically(string text, int pitchClass, KeyMode mode, string spelled)
    {
        Assert.True(Key.TryParse(text, out var key));
        Assert.Equal(pitchClass, key.PitchClass);
        Assert.Equal(mode, key.Mode);
        Assert.Equal(spelled, key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("H")]
    [InlineData("Cx")]
    [InlineData("72bpm")]
    public void Rejects_text_that_is_not_a_key(string? text)
    {
        Assert.False(Key.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Key.Parse(text ?? string.Empty));
    }

    [Theory]
    [InlineData("Bb", -1, "A")]   // fixture A: original Bb, entry key A (−1)
    [InlineData("E", 1, "F")]     // AS-60: Come Ye Sinners from E up to F
    [InlineData("B", 1, "C")]
    [InlineData("C", -13, "B")]
    [InlineData("F#m", 12, "F#m")]
    public void Transposes_with_wraparound(string from, int semitones, string expected)
    {
        Assert.Equal(expected, Key.Parse(from).Transpose(semitones).ToString());
    }

    [Theory]
    [InlineData("Db", "D♭")]
    [InlineData("F#m", "F♯m")]
    [InlineData("Bbm", "B♭m")]
    [InlineData("A", "A")]
    public void Displays_real_accidentals(string text, string display)
    {
        Assert.Equal(display, Key.Parse(text).ToDisplayString());
    }
}
