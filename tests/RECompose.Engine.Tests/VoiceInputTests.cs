// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Sections;
using RECompose.Core.Voice;

namespace RECompose.Engine.Tests;

// Protocol 12 (VC-10): speech into the engine's voice detector comes back as an utterance, and the listen worker hears it.
public class VoiceInputTests
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

    private static byte[] Pcm16(IEnumerable<double> samples)
    {
        var list = samples.ToList();
        var bytes = new byte[list.Count * 2];
        for (var i = 0; i < list.Count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(2 * i), (short)Math.Round(Math.Clamp(list[i], -1, 1) * 32767));
        return bytes;
    }

    private static async Task<Utterance> HearAsync(EngineClient client, byte[] pcm)
    {
        var heard = new TaskCompletionSource<Utterance>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UtteranceReceived += u => heard.TrySetResult(u);
        await client.InjectVoiceAsync(pcm, TestContext.Current.CancellationToken);
        return await heard.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VC10_injected_speech_comes_back_as_one_utterance()
    {
        await using var client = await StartAsync();
        var random = new Random(3);
        var tone = Enumerable.Range(0, 16000 * 3).Select(i =>
            random.NextDouble() * 0.002 - 0.001 + (i is >= 8000 and < 24000 ? 0.14 * Math.Sin(2 * Math.PI * 300 * i / 16000.0) : 0));

        var utterance = await HearAsync(client, Pcm16(tone));

        Assert.InRange(utterance.DurationSec, 1.6, 1.9); // 300 ms before, the second of speech, 500 ms after
        Assert.Equal(utterance.DurationSec * 32000, utterance.Pcm16.Length, 0);
        Assert.Equal(0.5, utterance.TrailingSec, 2);
    }

    /// <summary>CI's guide fixture has Voice.wav ("Let us go to the bridge", text to speech); with the speech model the worker hears it.</summary>
    [Fact]
    public async Task VC10_the_listen_worker_hears_the_md_through_the_engine()
    {
        var fixture = Environment.GetEnvironmentVariable("REC_E2E_GUIDE");
        var model = Environment.GetEnvironmentVariable("RECOMPOSE_SPEECH_MODEL");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(model) || !File.Exists(model) || !File.Exists(Path.Combine(fixture, "Voice.wav")))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_E2E") == "1")
                Assert.Fail("REC_E2E_GUIDE and RECOMPOSE_SPEECH_MODEL must point at the guide fixture and the speech model.");
            Assert.Skip("No speech model or voice fixture (set REC_E2E_GUIDE and RECOMPOSE_SPEECH_MODEL).");
        }

        var (rate, voice) = ReadMonoWav(Path.Combine(fixture, "Voice.wav"));
        var at16k = Enumerable.Range(0, (int)(voice.Length * 16000.0 / rate)).Select(i =>
        {
            var x = i * (double)rate / 16000;
            var j = Math.Min((int)x, voice.Length - 2);
            return voice[j] + (x - j) * (voice[j + 1] - voice[j]);
        });

        await using var client = await StartAsync();
        var utterance = await HearAsync(client, Pcm16(at16k));
        await using var worker = new VoiceWorker(Executable(), model);
        var heard = await worker.RecogniseAsync(utterance.Pcm16, string.Join(", ", VoiceText.CommandWords.Append("bridge")), TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"heard \"{heard.Text}\" ({heard.Confidence:0.00})");

        var command = Assert.Single(VoiceGrammar.Parse(heard.Text));
        Assert.Equal(VoiceIntent.Jump, command.Intent);
        Assert.Equal(SectionType.Bridge, command.Target!.Type);
        Assert.True(heard.Confidence >= 0.6, $"confidence {heard.Confidence:0.00}");

        // The model stays loaded: a second utterance needs no new process.
        Assert.Contains("bridge", (await worker.RecogniseAsync(utterance.Pcm16, string.Empty, TestContext.Current.CancellationToken)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VC50_a_clip_plays_once_on_the_md_output_only()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rec-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var silence = TestWav.Write(Path.Combine(folder, "silence.wav"), 48000, 1, 48000 * 4, (_, _) => 0);
        var clip = TestWav.Write(Path.Combine(folder, "clip.wav"), 22050, 1, 11025, (i, _) => 0.5 * Math.Sin(2 * Math.PI * 440 * i / 22050.0));

        await using var client = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await client.LoadSongAsync("clip", [new EngineTrack("music", silence, 0, 2, 0, false, false, false)], 0, ct: ct);
        var seconds = await client.PlayClipAsync(clip, 3, 1, -6, ct);
        Assert.Equal(0.5, seconds, 2);

        var (peak, _, _) = await client.RenderOfflineAsync(48000 / 4, 4, ct: ct);
        Assert.InRange(peak[3], 0.24, 0.26); // 0.5 at −6 dB
        Assert.Equal(0, peak[0], 3);
        Assert.Equal(0, peak[2], 3);

        await client.RenderOfflineAsync(48000 / 2, 4, ct: ct); // past its end
        var (after, _, _) = await client.RenderOfflineAsync(4800, 4, ct: ct);
        Assert.Equal(0, after[3], 3);

        var missing = await Assert.ThrowsAsync<EngineErrorException>(() => client.PlayClipAsync(Path.Combine(folder, "none.wav"), 3, 1, ct: ct));
        Assert.Contains("missing", missing.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Rate, double[] Samples) ReadMonoWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var channels = BitConverter.ToInt16(bytes, 22);
        var rate = BitConverter.ToInt32(bytes, 24);
        var at = 12;
        while (at + 8 <= bytes.Length && System.Text.Encoding.ASCII.GetString(bytes, at, 4) != "data")
            at += 8 + BitConverter.ToInt32(bytes, at + 4);
        var size = Math.Min(BitConverter.ToInt32(bytes, at + 4), bytes.Length - at - 8);
        var frames = size / 2 / channels;
        var samples = new double[frames];
        for (var i = 0; i < frames; i++)
            samples[i] = BitConverter.ToInt16(bytes, at + 8 + 2 * channels * i) / 32768.0;
        return (rate, samples);
    }
}
