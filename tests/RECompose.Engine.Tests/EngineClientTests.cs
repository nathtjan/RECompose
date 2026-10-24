// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Audio;

namespace RECompose.Engine.Tests;

// Protocol 2 against the real engine process, rendering offline (no audio device needed).
public class EngineClientTests
{
    private static string Executable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            var problem = $"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.";
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail(problem);
            Assert.Skip(problem);
        }

        return executable;
    }

    private static async Task<EngineClient> StartAsync()
    {
        var endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
        var session = await EngineHost.ConnectOrStartAsync(new EngineHostOptions(Executable(), endpoint) { IdleExitSeconds = 3 }, TestContext.Current.CancellationToken);
        return new EngineClient(session);
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rec-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task Loads_a_multitrack_folder_and_plays_it_with_the_mix_and_routing()
    {
        var folder = Folder();
        var keys = TestWav.Write(Path.Combine(folder, "Keys.wav"), 48000, 2, 48000 * 3, (_, _) => 0.5);
        var click = TestWav.Write(Path.Combine(folder, "Click.wav"), 44100, 1, 44100 * 3, (_, _) => 0.25);
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        var song = await client.LoadSongAsync("song-1",
        [
            new EngineTrack("keys", keys, 0, 2, 0, false, false, false),
            new EngineTrack("click", click, 2, 1, 0, false, false, true),
            new EngineTrack("gone", Path.Combine(folder, "Missing.wav"), 0, 2, 0, false, false, false),
        ], 0, 0, ct);

        Assert.Equal("song-1", song.SongId);
        Assert.Equal(48000, song.SampleRate);
        Assert.Equal(48000 * 3, song.LengthFrames);
        Assert.True(song.Tracks[2].Missing);                // REL-50
        Assert.Equal("File missing", song.Tracks[2].Problem);
        Assert.Equal(44100, song.Tracks[1].SourceRate);

        await client.PlayAsync(ct);
        var (peak, rms, snapshot) = await client.RenderOfflineAsync(24000, 3, ct: ct);
        Assert.Equal(TransportState.Playing, snapshot.State);
        Assert.Equal(24000, snapshot.Frame);
        Assert.InRange(rms[0], 0.49, 0.51);                   // keys on 1–2
        Assert.InRange(rms[1], 0.49, 0.51);
        Assert.InRange(rms[2], 0.24, 0.26);                   // click alone on 3 (PB-85)

        await client.SetTrackAsync("keys", solo: true, ct: ct);
        await client.SetTrackAsync("keys", gainDb: -6, ct: ct);
        (peak, rms, _) = await client.RenderOfflineAsync(4800, 3, ct: ct);
        Assert.InRange(peak[2], 0.24, 0.26);                  // solo-safe click (PB-82)
        Assert.InRange(rms[0], 0.24, 0.26);                   // -6 dB

        await client.SetTrackAsync("keys", mute: true, solo: false, ct: ct);
        await client.RenderOfflineAsync(4800, 3, ct: ct);     // the 10 ms ramp
        (peak, _, _) = await client.RenderOfflineAsync(4800, 3, ct: ct);
        Assert.Equal(0, peak[0], 6);

        await client.SeekAsync(48000, ct);
        await client.PauseAsync(ct);
        (_, _, snapshot) = await client.RenderOfflineAsync(4800, 3, ct: ct);
        Assert.Equal(TransportState.Paused, snapshot.State);

        var (state, device, _) = await client.GetStateAsync(ct);
        Assert.Equal("song-1", state?.SongId);
        Assert.True(state!.Tracks[0].Mute);
        Assert.Null(device);                                  // nothing opened in tests

        await client.Session.ShutdownAsync(ct);
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task Probe_and_peaks_run_as_separate_jobs()
    {
        var folder = Folder();
        var file = TestWav.Write(Path.Combine(folder, "Bass.wav"), 44100, 2, 44100, (i, c) => c == 0 ? 0.5 : -0.25);
        var tools = new EngineTools(Executable());
        var ct = TestContext.Current.CancellationToken;

        var info = await tools.ProbeAsync(file, ct);
        Assert.True(info.Ok);
        Assert.Equal(2, info.Channels);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(44100, info.Frames);

        var bad = await tools.ProbeAsync(Path.Combine(folder, "nothing.wav"), ct);
        Assert.False(bad.Ok);
        Assert.Equal("File missing", bad.Problem);

        var peaksPath = Path.Combine(folder, "cache", "bass.peaks");
        Assert.True(await tools.PeaksAsync(file, peaksPath, 441, ct));
        var peaks = PeaksFile.Load(peaksPath)!;
        Assert.Equal(100, peaks.Count);
        Assert.Equal(-0.25f, peaks.Min[10], 3);
        Assert.Equal(0.5f, peaks.Max[10], 3);

        Directory.Delete(folder, true);
    }
}

/// <summary>Writes 16-bit PCM WAV files for tests.</summary>
public static class TestWav
{
    public static string Write(string path, int rate, int channels, int frames, Func<int, int, double> sample)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        var dataBytes = frames * channels * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++)
                w.Write((short)Math.Round(Math.Clamp(sample(i, c), -1, 1) * 32767));
        return path;
    }
}
