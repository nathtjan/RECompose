// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;

namespace RECompose.Core.Tests.Library;

// SL-46, SL-47, AT-34, DM-60.
public class PadSetTests
{
    private const string Folder = "/pads/OB Pad";

    private static IEnumerable<string> Files(params string[] keys) => keys.Select(k => $"{Folder}/OB Pad {k}.wav");

    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    [Fact]
    public void AT34_a_folder_of_twelve_key_named_files_is_a_pad_set()
    {
        var set = PadSetDetector.Detect(Folder, Files(Flats));
        Assert.NotNull(set);
        Assert.Equal("OB Pad", set.Name);
        Assert.True(set.IsComplete);
        Assert.Equal($"{Folder}/OB Pad Db.wav", set.FileFor(1));
        Assert.Equal($"{Folder}/OB Pad B.wav", set.FileFor(-1)); // pitch classes wrap
    }

    [Fact]
    public void Sharps_and_flats_name_the_same_pitch()
    {
        var set = PadSetDetector.Detect(Folder, Files("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"));
        Assert.NotNull(set);
        Assert.Equal($"{Folder}/OB Pad F#.wav", set.FileFor(6));
        Assert.Equal($"{Folder}/OB Pad A#.wav", set.FileFor(10));
    }

    [Fact]
    public void A_song_folder_is_not_a_pad_set()
    {
        Assert.Null(PadSetDetector.Detect(Folder, Files(Flats[..11]))); // one key missing
        Assert.Null(PadSetDetector.Detect(Folder, [.. Files(Flats), $"{Folder}/Click Track.wav"])); // a click: it's a song
        Assert.Null(PadSetDetector.Detect(Folder, ["Drums.wav", "Bass.wav", "E Gtr.wav", "Keys 1.wav", "Guide.wav"]));
    }

    [Fact]
    public void Other_files_are_ignored_and_the_name_falls_back_to_the_folder()
    {
        var keys = Flats.Select(k => $"/pads/Warm/{k}.flac").Append("/pads/Warm/readme.txt");
        var set = PadSetDetector.Detect("/pads/Warm", keys);
        Assert.NotNull(set);
        Assert.Equal("Warm", set.Name);
        Assert.Equal(12, set.Samples.Count);

        Assert.Equal("Pad", PadSetDetector.CommonName(["Pad_C", "Pad_Db", "Pad_D"], "folder"));
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
