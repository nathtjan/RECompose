// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Audio;

namespace RECompose.Engine.Tests;

// Protocols 3 and 4 against the real engine process, rendering offline (no audio device needed).
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

    [Fact]
    public async Task Plays_a_plan_with_a_repeat_a_mute_and_ends_at_the_end_of_the_plan()
    {
        var folder = Folder();
        var a = TestWav.Write(Path.Combine(folder, "A.wav"), 48000, 1, 48000 * 4, (_, _) => 0.5);
        var b = TestWav.Write(Path.Combine(folder, "B.wav"), 48000, 1, 48000 * 4, (_, _) => 0.25);
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        await client.LoadSongAsync("song-2",
        [
            new EngineTrack("a", a, 0, 1, 0, false, false, false),
            new EngineTrack("b", b, 1, 1, 0, false, false, false),
        ], 0, 0, ct);

        // Plan seconds 0–1 from source 0, then 1–2 from source 3 (a jump) with track a muted: PB-40, AT-12.
        var frames = await client.SetPlanAsync(
        [
            new RECompose.Core.Planning.PlanSegment(0, 1, 0, 0, 0, []),
            new RECompose.Core.Planning.PlanSegment(1, 2, 3, 0, 0, ["a"]),
        ], 0, ct: ct);
        Assert.Equal(96000, frames);

        await client.PlayAsync(ct);
        var (peak, _, snapshot) = await client.RenderOfflineAsync(24000, 2, ct: ct);
        Assert.Equal(0.5, peak[0], 2);
        Assert.Equal(0.25, peak[1], 2);
        Assert.Equal(96000, snapshot.PlanFrames);
        Assert.Equal(48000 * 4, snapshot.LengthFrames);

        await client.RenderOfflineAsync(24000 + 4800, 2, ct: ct); // to 1.1 s: past the 10 ms mute ramp
        (peak, _, _) = await client.RenderOfflineAsync(24000, 2, ct: ct);
        Assert.Equal(0, peak[0], 3);
        Assert.Equal(0.25, peak[1], 2);

        // The plan ends at 2 s: stopped, back at the start.
        (_, _, snapshot) = await client.RenderOfflineAsync(48000, 2, ct: ct);
        Assert.Equal(TransportState.Stopped, snapshot.State);
        Assert.Equal(0, snapshot.Frame);

        // A plan whose segments leave a gap is refused.
        var error = await Assert.ThrowsAsync<EngineErrorException>(() => client.SetPlanAsync(
        [
            new RECompose.Core.Planning.PlanSegment(0, 1, 0, 0, 0, []),
            new RECompose.Core.Planning.PlanSegment(1.5, 2, 0, 0, 0, []),
        ], ct: ct));
        Assert.Equal("badMessage", error.Code);
    }

    [Fact]
    public async Task PB70_a_generated_count_in_clicks_on_the_cue_output_before_bar_1()
    {
        var folder = Folder();
        var music = TestWav.Write(Path.Combine(folder, "Keys.wav"), 48000, 1, 48000 * 6, (_, _) => 0);
        var wav = Path.Combine(folder, "render.wav");
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        await client.LoadSongAsync("song-click",
        [
            new EngineTrack("keys", music, 0, 1, 0, false, false, false),
            EngineTrack.Click(1, 1),
        ], 0, 0, ct);

        // 120 BPM, 4/4: one bar of count-in (plan 0–2 s, clicks on 0, 0.5, 1 and 1.5 s), then bar 1 at 2 s (SL-36).
        await client.SetPlanAsync(
        [
            new RECompose.Core.Planning.PlanSegment(0, 2, null, 0, 0, []) { Click = true },
            new RECompose.Core.Planning.PlanSegment(2, 4, 0, 0, 0, []),
        ], 0, new RECompose.Core.Planning.PlanGrid(2, 0.5, 4), ct: ct);

        await client.PlayAsync(ct);
        var (peak, _, _) = await client.RenderOfflineAsync(48000 * 4, 2, wav, ct);
        Assert.Equal(0, peak[0], 3);         // the music is silent and on output 1
        Assert.Equal(0.501, peak[1], 2);     // the click peaks at −6 dBFS on the Cue output

        // Beats 2–4 start exactly on their frames (beat 1 is under Play's 10 ms fade-in); bar 1 has no click.
        var right = ReadFloatWav(wav, channel: 1);
        foreach (var beat in new[] { 24000, 48000, 72000 })
        {
            Assert.Equal(0.501, right[beat], 3);
            Assert.Equal(0, right[beat - 1], 6);
        }

        Assert.All(right.Skip(96000).Take(96000), x => Assert.Equal(0, x, 6));
    }

    [Fact]
    public async Task PB60_tempo_and_key_keep_plan_frames_and_only_transposing_tracks_change_pitch()
    {
        var folder = Folder();
        var sine = TestWav.Write(Path.Combine(folder, "Keys.wav"), 48000, 1, 48000 * 6, (i, _) => 0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        var guide = TestWav.Write(Path.Combine(folder, "Guide.wav"), 48000, 1, 48000 * 6, (i, _) => 0.25 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        var wav = Path.Combine(folder, "render.wav");
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        await client.LoadSongAsync("song-key",
        [
            new EngineTrack("keys", sine, 0, 1, 0, false, false, false) { Transposes = true },
            new EngineTrack("guide", guide, 1, 1, 0, false, false, true),
        ], 0, 0, ct);

        // 4 plan seconds at 80 %, two semitones up: 5 seconds of audio; frames come back as plan frames.
        var frames = await client.SetPlanAsync(
            [new RECompose.Core.Planning.PlanSegment(0, 4, 0, 0, 0, []) { Key = 2 }], 0, tempo: 0.8, ct: ct);
        Assert.Equal(48000 * 4, frames);

        await client.PlayAsync(ct);
        try
        {
            var (_, _, snapshot) = await client.RenderOfflineAsync(48000 * 2, 2, wav, ct);
            Assert.Equal(76800, snapshot.Frame);               // 2 s of audio at 80 % is 1.6 plan seconds

            // One second of output: the keys are two semitones up (440 → 493.9 Hz); the guide is slower but keeps its
            // pitch, because it doesn't transpose (DM-12) and tempo never changes pitch (PB-60).
            int Crossings(float[] x)
            {
                var n = 0;
                for (var i = 48001; i < 96000; i++)
                    if (x[i - 1] < 0 && x[i] >= 0)
                        n++;
                return n;
            }

            Assert.InRange(Crossings(ReadFloatWav(wav, channel: 0)), 487, 501);
            Assert.InRange(Crossings(ReadFloatWav(wav, channel: 1)), 990, 1010);

            // A seek in plan frames lands on that plan frame (after Pause's 20 ms fade has played out).
            await client.PauseAsync(ct);
            await client.RenderOfflineAsync(4800, 2, ct: ct);
            await client.SeekAsync(48000 * 3, ct);
            (_, _, snapshot) = await client.RenderOfflineAsync(0, 2, ct: ct);
            Assert.Equal(TransportState.Paused, snapshot.State);
            Assert.Equal(48000 * 3, snapshot.Frame);
        }
        finally
        {
            await client.StopAsync(ct); // a playing engine never exits by itself (REL-34)
        }
    }

    [Fact]
    public async Task SL42_a_pad_plays_with_the_song_stopped_and_fades_out()
    {
        var folder = Folder();
        var files = Enumerable.Range(0, 12).ToDictionary(k => k, k =>
            TestWav.Write(Path.Combine(folder, $"Pad{k}.wav"), 48000, 2, 48000 * 4, (i, _) => 0.5 * Math.Sin(2 * Math.PI * (200 + 50 * k) * i / 48000.0)));
        var music = TestWav.Write(Path.Combine(folder, "Keys.wav"), 48000, 1, 48000 * 4, (_, _) => 0);
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        await client.LoadSongAsync("song-pad", [new EngineTrack("keys", music, 0, 1, 0, false, false, false)], 0, 0, ct);
        Assert.Empty(await client.LoadPadSetAsync("pads", files, ct));
        await client.SetPadAsync(gainDb: 0, fadeSec: 0.1, output: (0, 2), ct: ct);

        try
        {
            await client.PlayPadAsync(5, ct);
            await client.PlayAsync(ct); // the song plays silence; the pad is independent of it
            var (peak, _, snapshot) = await client.RenderOfflineAsync(48000, 2, ct: ct);
            Assert.Equal(0.5, peak[0], 1);
            Assert.Equal(5, snapshot.PadKey);

            await client.PauseAsync(ct);
            await client.PlayPadAsync(null, ct);
            await client.RenderOfflineAsync(12000, 2, ct: ct);
            (peak, _, snapshot) = await client.RenderOfflineAsync(4800, 2, ct: ct);
            Assert.Equal(0, peak[0], 3);
            Assert.Null(snapshot.PadKey);
        }
        finally
        {
            await client.StopAsync(ct);
            await client.PlayPadAsync(null, ct);
        }
    }

    [Fact]
    public async Task SL30_a_segue_starts_the_next_song_and_the_engine_promotes_it()
    {
        var folder = Folder();
        var a = TestWav.Write(Path.Combine(folder, "A.wav"), 48000, 1, 48000 * 4, (_, _) => 0.5);
        var b = TestWav.Write(Path.Combine(folder, "B.wav"), 48000, 1, 48000 * 4, (_, _) => 0.25);
        var ct = TestContext.Current.CancellationToken;

        await using var client = await StartAsync();
        var changed = new TaskCompletionSource<string>();
        client.SongChanged += id => changed.TrySetResult(id);

        await client.LoadSongAsync("song-a", [new EngineTrack("a", a, 0, 1, 0, false, false, false)], 0, 0, ct);
        var next = await client.LoadNextSongAsync("song-b", [new EngineTrack("b", b, 1, 1, 0, false, false, false)], 0, ct);
        Assert.Equal("song-b", next.SongId);
        await client.SetPlanAsync([new RECompose.Core.Planning.PlanSegment(0, 3, 0, 0, 0, [])], 0, ct: ct);
        await client.SetPlanAsync([new RECompose.Core.Planning.PlanSegment(0, 3, 0, 0, 0, [])], 0, next: true, ct: ct);

        // The tail begins at plan second 2: the next song starts there, and the engine reports it has the next song cued.
        await client.SetTransitionAsync(new EngineTransition("segue", 2), ct);
        try
        {
            await client.PlayAsync(ct);
            var (peak, _, snapshot) = await client.RenderOfflineAsync(48000, 2, ct: ct);
            Assert.Equal(0, peak[1], 3);
            Assert.Equal("song-b", snapshot.NextSongId);

            (peak, _, _) = await client.RenderOfflineAsync(48000 * 2, 2, ct: ct); // to second 3: the first song's plan ends
            Assert.Equal(0.25, peak[1], 2);                                          // the next song sounds

            Assert.Equal("song-b", await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
            (_, _, snapshot) = await client.RenderOfflineAsync(4800, 2, ct: ct);
            Assert.Equal("song-b", snapshot.SongId);
        }
        finally
        {
            await client.StopAsync(ct);
            await client.RenderOfflineAsync(4800, 2, ct: ct); // with no device the stop's fade only finishes when something renders
        }
    }

    /// <summary>One channel of a 32-bit float WAV written by renderOffline.</summary>
    private static float[] ReadFloatWav(string path, int channel)
    {
        var bytes = File.ReadAllBytes(path);
        int Chunk(string id)
        {
            var at = 12;
            while (at + 8 <= bytes.Length && System.Text.Encoding.ASCII.GetString(bytes, at, 4) != id)
                at += 8 + BitConverter.ToInt32(bytes, at + 4) + (BitConverter.ToInt32(bytes, at + 4) & 1);
            return at;
        }

        var channels = BitConverter.ToInt16(bytes, Chunk("fmt ") + 10);
        var data = Chunk("data");
        var start = data + 8;
        var frames = BitConverter.ToInt32(bytes, data + 4) / (4 * channels);
        var result = new float[frames];
        for (var i = 0; i < frames; i++)
            result[i] = BitConverter.ToSingle(bytes, start + ((i * channels) + channel) * 4);
        return result;
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
