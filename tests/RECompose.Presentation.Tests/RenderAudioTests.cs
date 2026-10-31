// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Export;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// Song > Render audio against the real engine: a second engine writes the files. Two 3-second mono stems at 0.2 and 0.4.
public sealed class RenderAudioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-bounce-" + Guid.NewGuid().ToString("N"));
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static void Wav(string path, double level)
    {
        using var w = new BinaryWriter(File.Create(path));
        const int rate = 48000;
        var bytes = rate * 3 * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        for (var i = 0; i < rate * 3; i++)
            w.Write((short)(level * 32767));
    }

    private async Task<MainViewModel> StartAsync()
    {
        Environment.SetEnvironmentVariable("RECOMPOSE_NO_DEVICE", "1");
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
            Assert.Skip($"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.");

        Directory.CreateDirectory(_root);
        var low = Path.Combine(_root, "low.wav");
        var high = Path.Combine(_root, "high.wav");
        Wav(low, 0.2);
        Wav(high, 0.4);
        Track Stem(string id, string file, string group) => new() { Id = id, Name = id, File = file, Group = group, Channels = 1, SampleRate = 48000, Frames = 48000L * 3 };
        var song = new Song
        {
            Id = "bounce",
            Title = "Bounce",
            Source = new SongSource { Path = _root },
            OriginalKey = "C",
            Bpm = 120,
            DurationSec = 3,
            Sections = [Section.Create("Verse 1", 0, SectionOrigin.Manual)],
            Tracks = [Stem("low", low, TrackGroups.Keys), Stem("high", high, TrackGroups.Bass)],
            MixPresets = [new MixPreset { Id = "default" }],
            DefaultMixPresetId = "default",
        };
        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveSong(song);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });
        var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable),
            new LockedDispatcher(), new ServiceDialogs(), new SpeechModel(store.Root, allowDownload: false));
        await vm.InitializeAsync();
        await vm.LoadSongAsync("bounce");
        return vm;
    }

    /// <summary>Channels, frames and peak of a 32-bit float WAV.</summary>
    private static (int Channels, int Frames, float Peak) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var channels = 0;
        var at = 12;
        while (System.Text.Encoding.ASCII.GetString(bytes, at, 4) != "data")
        {
            if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == "fmt ")
                channels = BitConverter.ToInt16(bytes, at + 10);
            at += 8 + BitConverter.ToInt32(bytes, at + 4);
        }

        var count = BitConverter.ToInt32(bytes, at + 4) / 4;
        var peak = 0f;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(bytes, at + 8 + i * 4)));
        return (channels, count / channels, peak);
    }

    private static RenderOptions Options(RenderSource[] sources) => new() { Sources = sources, Format = RenderFormat.Wav32Float };

    [Fact]
    public async Task The_mix_and_a_track_are_written_as_files_of_the_songs_length()
    {
        var vm = await StartAsync();
        var folder = Path.Combine(_root, "out");

        var files = await vm.RenderToFolderAsync(Options([new(RenderSourceKind.Mix), new(RenderSourceKind.Track, "high", "high")]), folder);

        Assert.Equal(["Bounce - Mix.wav", "Bounce - Track high.wav"], files);
        var mix = Read(Path.Combine(folder, files[0]));
        var high = Read(Path.Combine(folder, files[1]));
        Assert.Equal(2, mix.Channels);
        Assert.InRange(mix.Frames, 3 * 48000 - 1024, 3 * 48000 + 1024);
        Assert.True(mix.Peak > high.Peak * 1.3f, $"mix {mix.Peak} should hold both stems, the track {high.Peak}");
    }

    [Fact]
    public async Task A_muted_track_is_left_out_unless_the_mute_is_ignored()
    {
        var vm = await StartAsync();
        vm.Song!.Tracks.First(t => t.Id == "high").Mute = true;
        var track = new RenderSource(RenderSourceKind.Track, "high", "high");

        var respected = await vm.RenderToFolderAsync(Options([track]), Path.Combine(_root, "respected"));
        var ignored = await vm.RenderToFolderAsync(Options([track]) with { RespectMute = false }, Path.Combine(_root, "ignored"));

        Assert.Empty(respected);
        Assert.Equal(0.4f, Read(Path.Combine(_root, "ignored", Assert.Single(ignored))).Peak, 0.1f);
    }

    [Fact]
    public async Task A_flac_file_is_written_and_a_name_in_the_folder_is_not_overwritten()
    {
        var vm = await StartAsync();
        var folder = Path.Combine(_root, "flac");
        var options = Options([new(RenderSourceKind.Mix)]) with { Format = RenderFormat.Flac24 };

        var first = await vm.RenderToFolderAsync(options, folder);
        var second = await vm.RenderToFolderAsync(options, folder);

        Assert.Equal(["Bounce - Mix.flac"], first);
        Assert.Equal(["Bounce - Mix (2).flac"], second);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(folder, first[0])), 0, 4));
    }
}
