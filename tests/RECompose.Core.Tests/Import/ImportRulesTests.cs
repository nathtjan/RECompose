// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Import;
using RECompose.Core.Library;

namespace RECompose.Core.Tests.Import;

// SL-52 role and group detection and lane order; SL-53 BPM and key parsing.
public class ImportRulesTests
{
    [Theory]
    [InlineData("Click Track.wav", TrackRole.Click, TrackGroups.Click)]
    [InlineData("Metronome.wav", TrackRole.Click, TrackGroups.Click)]
    [InlineData("Guide.wav", TrackRole.Guide, TrackGroups.Guide)]
    [InlineData("Vocal Cues.wav", TrackRole.Guide, TrackGroups.Guide)]
    [InlineData("Drums (Live).wav", TrackRole.Music, TrackGroups.Drums)]
    [InlineData("Kick.wav", TrackRole.Music, TrackGroups.Drums)]
    [InlineData("Perc.wav", TrackRole.Music, TrackGroups.Percussion)]
    [InlineData("Tambourine.wav", TrackRole.Music, TrackGroups.Percussion)]
    [InlineData("Bass.wav", TrackRole.Music, TrackGroups.Bass)]
    [InlineData("Synth Bass.wav", TrackRole.Music, TrackGroups.Bass)]
    [InlineData("Keys 3.wav", TrackRole.Music, TrackGroups.Keys)]
    [InlineData("Piano.flac", TrackRole.Music, TrackGroups.Keys)]
    [InlineData("EG 1.wav", TrackRole.Music, TrackGroups.ElectricGuitar)]
    [InlineData("Electric Guitar.wav", TrackRole.Music, TrackGroups.ElectricGuitar)]
    [InlineData("Acoustic Gtr.wav", TrackRole.Music, TrackGroups.AcousticGuitar)]
    [InlineData("AG.wav", TrackRole.Music, TrackGroups.AcousticGuitar)]
    [InlineData("BGV.wav", TrackRole.Music, TrackGroups.Vocals)]
    [InlineData("Choir.wav", TrackRole.Music, TrackGroups.Vocals)]
    [InlineData("Strings.wav", TrackRole.Music, TrackGroups.Strings)]
    [InlineData("Ambience.wav", TrackRole.Music, TrackGroups.Other)]
    public void Detects_role_and_group_from_vendor_file_names(string file, TrackRole role, string group)
    {
        var detected = ImportRules.Detect(file);
        Assert.Equal(role, detected.Role);
        Assert.Equal(group, detected.Group);
    }

    [Fact]
    public void Click_and_guide_default_to_the_cue_output()
    {
        Assert.Equal("cue", ImportRules.Detect("Click.wav").DefaultOutput);
        Assert.Equal("cue", ImportRules.Detect("Guide.wav").DefaultOutput);
        Assert.Equal("main", ImportRules.Detect("Bass.wav").DefaultOutput);
    }

    [Fact]
    public void Lanes_go_click_guide_then_groups_with_natural_sort()
    {
        Track T(string name)
        {
            var d = ImportRules.Detect(name + ".wav");
            return new Track { Id = name, Name = name, File = name + ".wav", Role = d.Role, Group = d.Group };
        }

        var ordered = ImportRules.InLaneOrder([T("Keys 10"), T("Bass"), T("Guide"), T("Keys 2"), T("Drums"), T("Click"), T("Ambience"), T("BGV")]);

        Assert.Equal(["Click", "Guide", "Drums", "Bass", "Keys 2", "Keys 10", "BGV", "Ambience"], ordered.Select(t => t.Name));
        Assert.Equal(Enumerable.Range(0, 8), ordered.Select(t => t.Order));
    }

    [Theory]
    [InlineData("Champion - Bb - 72bpm", 72.0)]
    [InlineData("Song 120 BPM", 120.0)]
    [InlineData("Song_96.5-bpm", 96.5)]
    [InlineData("Song", null)]
    public void Parses_BPM(string text, double? expected)
    {
        Assert.Equal(expected, ImportRules.ParseBpm(text));
    }

    [Theory]
    [InlineData("Champion - Bb - 72bpm", "Bb")]
    [InlineData("Come Ye Sinners (E)", "E")]
    [InlineData("Song_F#m_120bpm", "F#m")]
    [InlineData("Goodness - D# - 70", "Eb")]
    [InlineData("Amazing Grace", null)]
    public void Parses_the_key(string text, string? expected)
    {
        Assert.Equal(expected, ImportRules.ParseKey(text)?.ToString());
    }

    [Fact]
    public void Meta_comes_from_the_folder_name_first_then_the_files()
    {
        var (bpm, key) = ImportRules.ParseMeta("Champion", ["Click 72bpm.wav", "Guide - Bb.wav"]);
        Assert.Equal(72.0, bpm);
        Assert.Equal("Bb", key?.ToString());
    }

    [Theory]
    [InlineData("Song 3/4", 3, 4)]
    [InlineData("Song 6-8 72bpm", 6, 8)]
    public void Parses_the_meter(string text, int beats, int unit)
    {
        Assert.Equal((beats, unit), ImportRules.ParseMeter(text));
    }

    [Fact]
    public void Natural_comparer_orders_numbers_by_value()
    {
        string[] names = ["Keys 10", "keys 2", "Keys 1"];
        Assert.Equal(["Keys 1", "keys 2", "Keys 10"], names.Order(NaturalComparer.Instance));
    }
}
