// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Commands;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Settings;

namespace RECompose.Core.Tests.Library;

public class LibraryTests
{
    [Theory]
    [InlineData("main", OutputKind.Main, 0, 2, "Main")]
    [InlineData("cue", OutputKind.Cue, 0, 2, "Cue")]
    [InlineData("3-4", OutputKind.Direct, 2, 2, "3–4")]
    [InlineData("5", OutputKind.Direct, 4, 1, "5")]
    [InlineData("nonsense", OutputKind.Main, 0, 2, "Main")]
    public void Outputs_parse_and_print(string text, OutputKind kind, int channel, int width, string label)
    {
        var o = OutputRef.Parse(text);
        Assert.Equal(kind, o.Kind);
        Assert.Equal(channel, o.Channel);
        Assert.Equal(width, o.Width);
        Assert.Equal(label, o.Label);
        if (kind != OutputKind.Main || text == "main")
            Assert.Equal(text, o.ToString());
    }

    [Fact]
    public void Logical_outputs_resolve_through_the_audio_settings()
    {
        var audio = new AudioSettings { MainChannel = 0, MainWidth = 2, CueChannel = 2, CueWidth = 2 };
        Assert.Equal((0, 2), audio.Resolve("main"));
        Assert.Equal((2, 2), audio.Resolve("cue"));   // demo step 6: Cue on outputs 3–4
        Assert.Equal((5, 1), audio.Resolve("6"));
    }

    [Fact]
    public void The_library_meta_line_uses_real_accidentals()
    {
        var song = new Song
        {
            Id = "a", Title = "Champion", Source = new SongSource { Path = "/x" },
            OriginalKey = "Bb", Bpm = 72, BeatsPerBar = 3, BeatUnit = 4,
            Tracks = [new Track { Id = "t", Name = "Click", File = "/x/Click.wav" }],
        };
        Assert.Equal("B♭ · 72 BPM · 3/4 · 1 tracks", song.MetaLine);
    }

    [Fact]
    public void Songs_settings_and_the_library_survive_a_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(root);
            var song = new Song
            {
                Id = "s1", Title = "Champion", Source = new SongSource { Path = "/stems" }, OriginalKey = "Bb", Bpm = 72, BeatsPerBar = 3,
                Tracks = [new Track { Id = "t1", Name = "Click", File = "/stems/Click.wav", Role = TrackRole.Click, Group = TrackGroups.Click, Output = "cue" }],
                MixPresets = [new MixPreset { Id = "m", Tracks = new() { ["t1"] = new TrackMix { GainDb = -3, Mute = true } } }],
                DefaultMixPresetId = "m",
            };

            store.SaveSong(song);
            store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });
            store.SaveSettings(new AppSettings { Audio = new AudioSettings { DeviceType = "ASIO", CueChannel = 2 } });

            var loaded = store.LoadSong("s1")!;
            Assert.Equal("Champion", loaded.Title);
            Assert.Equal(TrackRole.Click, loaded.Tracks[0].Role);
            Assert.True(loaded.DefaultMix.For("t1").Mute);
            Assert.Equal(-3, loaded.DefaultMix.For("t1").GainDb);
            Assert.Equal("s1", store.LoadLibrary().Songs.Single().Id);
            Assert.Equal("ASIO", store.LoadSettings().Audio.DeviceType);
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Contains("\"role\": \"Click\"", File.ReadAllText(Path.Combine(root, "songs", "s1.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_damaged_file_reads_as_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"), "{ not json");
            var store = new DataStore(root);
            Assert.Equal(256, store.LoadSettings().Audio.BufferSize);

            // The next save must not replace it: it is kept beside the new one (DM-90).
            store.SaveSettings(store.LoadSettings());
            var kept = Assert.Single(Directory.GetFiles(root, "settings.json.damaged-*"));
            Assert.Equal("{ not json", File.ReadAllText(kept));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_audio_hash_depends_on_content_not_on_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a.wav");
            var b = Path.Combine(root, "b.wav");
            File.WriteAllBytes(a, [1, 2, 3]);
            File.WriteAllBytes(b, [4, 5, 6]);
            var first = DataStore.AudioHash([a, b]);
            Assert.Equal(first, DataStore.AudioHash([b, a]));
            File.WriteAllBytes(b, [4, 5, 7]);
            Assert.NotEqual(first, DataStore.AudioHash([a, b]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Undo_merges_a_fader_drag_into_one_step()
    {
        var value = 0;
        var stack = new UndoStack();
        var t0 = DateTime.UtcNow;

        void Set(int v, DateTime at)
        {
            var before = value;
            value = v;
            stack.Push(new UndoableChange("Fader Drums", () => value = before, () => value = v) { MergeKey = "fader:drums" }, at);
        }

        Set(1, t0);
        Set(2, t0.AddMilliseconds(100));
        Set(3, t0.AddMilliseconds(200));
        Set(9, t0.AddSeconds(5)); // a separate move later

        stack.Undo();
        Assert.Equal(3, value);
        stack.Undo();
        Assert.Equal(0, value);
        Assert.False(stack.CanUndo);
        stack.Redo();
        Assert.Equal(3, value);
    }
}
