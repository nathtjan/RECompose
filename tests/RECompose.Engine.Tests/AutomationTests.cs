// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Automation;
using RECompose.Core.Planning;

namespace RECompose.Engine.Tests;

// Protocol 13 (AUTO-5, AUTO-21, AUTO-31, REL-40): the plan's MIDI comes back as midiOut when played, spoken calls sound on
// the speech track, and panic sends All Notes Off and All Sound Off on the channels used.
public class AutomationTests
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

    [Fact]
    public async Task Plan_midi_and_calls_play_on_the_audio_clock()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rec-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var silence = TestWav.Write(Path.Combine(folder, "silence.wav"), 48000, 1, 48000 * 4, (_, _) => 0);
        var clip = TestWav.Write(Path.Combine(folder, "call.wav"), 22050, 1, 2205, (i, _) => 0.1 * Math.Sin(2 * Math.PI * 440 * i / 22050.0));

        var endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
        var ct = TestContext.Current.CancellationToken;
        await using var client = new EngineClient(await EngineHost.ConnectOrStartAsync(new EngineHostOptions(Executable(), endpoint) { IdleExitSeconds = 3 }, ct));
        var sent = new List<MidiSent>();
        client.MidiSent += batch => { lock (sent) sent.AddRange(batch); };

        await client.LoadSongAsync("auto", [new EngineTrack("music", silence, 0, 1, 0, false, false, false), EngineTrack.Speech(1, 1)], 0, ct: ct);
        await client.SetPlanAsync([new PlanSegment(0, 4, 0, 0, 0, [])], 0, ct: ct,
            midi: [new TimedMidi(0.1, [0x91, 64, 127], 7), new TimedMidi(0.2, [0x81, 64, 0]), new TimedMidi(3.0, [0x91, 65, 127], 8)],
            calls: [new EngineCall(0.25, clip, Normalize: true)]);
        await client.PlayAsync(ct);

        var (peak, _, _) = await client.RenderOfflineAsync(24000, 2, ct: ct);
        Assert.InRange(peak[1], 0.49, 0.51); // the call, normalised to −6 dBFS, on the speech track's channel only
        Assert.Equal(0, peak[0], 3);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Count() < 2)
            await Task.Delay(20, ct);
        lock (sent)
        {
            Assert.Equal([7, 0], sent.Select(m => m.Id));
            Assert.Equal([0x91, 64, 127], sent[0].Bytes);
        }

        // REL-40: panic switches off every note on channel 2, the one used.
        await client.PanicAsync(ct);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Count() < 4)
            await Task.Delay(20, ct);
        lock (sent)
            Assert.Equal([[0xB1, 123, 0], [0xB1, 120, 0]], sent.Skip(2).Select(m => m.Bytes.ToArray()));

        int Count()
        {
            lock (sent)
                return sent.Count;
        }
    }
}
