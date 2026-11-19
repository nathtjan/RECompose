// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;

namespace RECompose.Core.Tests.Library;

// SL-46, SL-47, AT-34, DM-60.
public class PadSetTests
{
    private const string Folder = "/pads/OB Pad";

    private static IEnumerable<string> Files(params string[] keys) => keys.Select(k => $"{Folder}/OB Pad {k}.wav");

    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    private static PadScan Scan(IEnumerable<string> files) => PadImport.Classify(Folder, files);

    [Fact]
    public void AT34_a_folder_of_twelve_key_named_files_is_a_pad_set()
    {
        var scan = Scan(Files(Flats));
        Assert.Equal(PadGuess.PadSet, scan.Guess);
        var set = Assert.Single(scan.Sets).ToPadSet();
        Assert.Equal("OB Pad", set.Name);
        Assert.True(set.IsComplete);
        Assert.Equal("12 keys", set.MetaLine);
        Assert.Equal($"{Folder}/OB Pad Db.wav", set.FileFor(1));
        Assert.Equal($"{Folder}/OB Pad B.wav", set.FileFor(-1)); // pitch classes wrap
    }

    [Fact]
    public void Sharps_and_flats_name_the_same_pitch_and_two_spellings_are_a_conflict()
    {
        var set = Assert.Single(Scan(Files("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")).Sets).ToPadSet();
        Assert.Equal($"{Folder}/OB Pad F#.wav", set.FileFor(6));
        Assert.Equal($"{Folder}/OB Pad A#.wav", set.FileFor(10));

        var both = Assert.Single(Scan([.. Files(Flats), $"{Folder}/OB Pad C#.wav"]).Sets);
        Assert.Equal([1], both.Conflicts);
        Assert.Equal(2, both.Keys[1].Count);
    }

    [Theory]
    [InlineData("OB Pad C#", 1)]
    [InlineData("pad_eb", 3)]
    [InlineData("Warm - F sharp", 6)]
    [InlineData("Warm_B-flat", 10)]
    [InlineData("Gb Major", 6)]
    [InlineData("Ebm", 3)]
    [InlineData("Shimmer A♭", 8)]
    [InlineData("Csharp", 1)]
    [InlineData("01 - D", 2)]
    [InlineData("Cb", 11)]
    [InlineData("Ambient Bed", null)]
    [InlineData("Pad 2", null)]
    public void Pad_file_names_give_a_pitch_class(string name, int? pitchClass) => Assert.Equal(pitchClass, PadImport.ParseKey(name));

    [Fact]
    public void A_folder_with_a_few_keys_missing_is_still_a_pad_set_and_says_which()
    {
        var eleven = Scan(Files(Flats[..11])); // B missing, "Pad" in the names
        Assert.Equal(PadGuess.PadSet, eleven.Guess);
        Assert.Equal("11 keys · B missing", eleven.Sets[0].ToPadSet().MetaLine);

        var seven = PadImport.Classify("/x/Warm", Flats[..7].Select(k => $"/x/Warm/{k}.wav")); // no pad word, seven keys
        Assert.Equal(PadGuess.Unsure, seven.Guess);
        Assert.Equal("7 keys · 5 missing", seven.Sets[0].ToPadSet().MetaLine);
    }

    [Fact]
    public void A_single_pad_file_is_unsure_not_a_song()
    {
        var scan = PadImport.Classify("/x/Pads", ["/x/Pads/Warm Pad.wav"]);
        Assert.Equal(PadGuess.Unsure, scan.Guess);
        Assert.Empty(scan.Sets[0].Keys);
    }

    [Fact]
    public void A_song_folder_is_not_a_pad_set()
    {
        Assert.Equal(PadGuess.Song, Scan([.. Files(Flats), $"{Folder}/Click Track.wav"]).Guess); // a click: it's a song
        Assert.Equal(PadGuess.Song, PadImport.Classify("/s/Champion", ["Drums.wav", "Bass.wav", "E Gtr.wav", "Keys 1.wav", "Guide.wav"]).Guess);
        // A song with one pad stem in the song's key.
        Assert.Equal(PadGuess.Song, PadImport.Classify("/s/Champion - 72bpm", ["/s/Drums.wav", "/s/Bass.wav", "/s/Pad Bb.wav", "/s/Vox.wav"]).Guess);
    }

    [Fact]
    public void A_folder_of_pad_set_folders_is_one_set_per_folder()
    {
        var files = Flats.Select(k => $"/p/Warm/Warm {k}.wav").Concat(Flats.Select(k => $"/p/Shimmer/Shimmer {k}.wav"));
        var scan = PadImport.Classify("/p", files);
        Assert.Equal(PadGuess.PadSet, scan.Guess);
        Assert.Equal(["Shimmer", "Warm"], scan.Sets.Select(s => s.Name).Order());
        Assert.All(scan.Sets, s => Assert.Equal(12, s.Keys.Count));
    }

    [Fact]
    public void Other_files_are_ignored_and_the_name_falls_back_to_the_folder()
    {
        var keys = Flats.Select(k => $"/pads/Warm/{k}.flac").Append("/pads/Warm/readme.txt");
        var set = Assert.Single(PadImport.Classify("/pads/Warm", keys).Sets).ToPadSet();
        Assert.Equal("Warm", set.Name);
        Assert.Equal(12, set.Samples.Count);

        Assert.Equal("Pad", PadSetDetector.CommonName(["Pad_C", "Pad_Db", "Pad_D"], "folder"));
    }

    [Fact]
    public void A_missing_key_is_made_from_the_nearest_key()
    {
        Assert.Equal((2, -1), PadImport.NearestSource([0, 2, 4], 1)); // C# from D down a semitone (a tie: down)
        Assert.Equal((4, 1), PadImport.NearestSource([0, 4], 5));
        Assert.Equal((0, -1), PadImport.NearestSource([0], 11)); // B from C, across the octave
        Assert.Equal((0, 6), PadImport.NearestSource([0], 6)); // a tritone up
        Assert.Null(PadImport.NearestSource([], 3));
    }

    [Fact]
    public void SL46_without_loop_points_the_pad_loops_from_a_quarter_to_nine_tenths()
    {
        var loop = PadLoop.Default(100);
        Assert.Equal(25, loop.StartSec, 6);
        Assert.Equal(90, loop.EndSec, 6);
        Assert.Equal(2, loop.CrossfadeSec, 6);
        Assert.True(PadLoop.Default(4).CrossfadeSec < 2); // a very short file still loops
    }

    [Fact]
    public void Button_keys_use_real_accidentals()
    {
        Assert.Equal("C D♭ D E♭ E F F♯ G A♭ A B♭ B", string.Join(' ', PadSetDetector.ButtonKeys.Select(k => k.ToDisplayString())));
    }
}
