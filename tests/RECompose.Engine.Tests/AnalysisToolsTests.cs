// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using RECompose.Core.Analysis;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Engine.Tests;

// The analysis tool modes through the real engine executable: click onsets (SEC-1) and,
// when CI provides a text-to-speech guide and the speech model, the whole Detect from guide
// path with real recognition (SEC-11…15).
public class AnalysisToolsTests
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
    public async Task SEC1_onsets_of_a_generated_click_fit_72_BPM_in_3_4()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rec-onsets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Fixture A's click: 72 BPM, 3/4, first downbeat 0.02 s; beat 1 at 2 kHz, the others 1 kHz.
            const int rate = 48000;
            var period = 60.0 / 72;
            var file = TestWav.Write(Path.Combine(folder, "Click Track.wav"), rate, 1, rate * 40, (i, _) =>
            {
                var t = i / (double)rate - 0.02;
                if (t < 0)
                    return 0;
                var k = (int)Math.Floor(t / period);
                var dt = t - k * period;
                if (dt > 0.025)
                    return 0;
                var accent = k % 3 == 0;
                return (accent ? 0.5 : 0.25) * Math.Sin(2 * Math.PI * (accent ? 2000 : 1000) * dt) * Math.Exp(-dt / 0.006);
            });

            var tools = new EngineTools(Executable());
            var result = await tools.OnsetsAsync(file, TestContext.Current.CancellationToken);
            Assert.True(result.Ok);
            Assert.Equal(48, result.Onsets.Count);

            var grid = ClickFit.Fit(result.Onsets)!;
            Assert.Equal("72", Format.Bpm(grid.Bpm));
            Assert.Equal(3, grid.BeatsPerBar);
            Assert.Equal(0.02, grid.FirstDownbeatSec, 3);

            var missing = await tools.OnsetsAsync(Path.Combine(folder, "none.wav"), TestContext.Current.CancellationToken);
            Assert.False(missing.Ok);
            Assert.Equal("File missing", missing.Problem);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Transcribe_reports_a_missing_model()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rec-asr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = TestWav.Write(Path.Combine(folder, "Guide.wav"), 16000, 1, 16000, (_, _) => 0);
            var result = await new EngineTools(Executable()).TranscribeAsync(file, Path.Combine(folder, "no-model.bin"), "Chorus.", ct: TestContext.Current.CancellationToken);
            Assert.False(result.Ok);
            Assert.Equal("The speech model is missing", result.Problem);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// End to end with real speech recognition. CI sets REC_E2E_GUIDE (a folder from
    /// tools/ci/make-guide-fixture.py) and RECOMPOSE_SPEECH_MODEL (ggml-base.en.bin).
    /// </summary>
    [Fact]
    public async Task SEC10_detects_sections_from_a_spoken_guide()
    {
        var fixture = Environment.GetEnvironmentVariable("REC_E2E_GUIDE");
        var model = Environment.GetEnvironmentVariable("RECOMPOSE_SPEECH_MODEL");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(model) || !File.Exists(model))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_E2E") == "1")
                Assert.Fail("REC_E2E_GUIDE and RECOMPOSE_SPEECH_MODEL must point at the guide fixture and the speech model.");
            Assert.Skip("No speech model or guide fixture (set REC_E2E_GUIDE and RECOMPOSE_SPEECH_MODEL).");
        }

        var ct = TestContext.Current.CancellationToken;
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "expected.json"), ct));
        var tools = new EngineTools(Executable());

        var onsets = await tools.OnsetsAsync(Path.Combine(fixture, "Click.wav"), ct);
        var fit = ClickFit.Fit(onsets.Onsets)!;
        Assert.Equal(expected.RootElement.GetProperty("bpm").GetDouble(), fit.Bpm, 1);
        Assert.Equal(expected.RootElement.GetProperty("beatsPerBar").GetInt32(), fit.BeatsPerBar);
        var grid = new TempoGrid(fit.Bpm, fit.BeatsPerBar, fit.FirstDownbeatSec);

        var started = DateTime.UtcNow;
        var transcript = await tools.TranscribeAsync(Path.Combine(fixture, "Guide.wav"), model, SectionDetector.VocabularyPrompt, ct: ct);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        Assert.True(transcript.Ok, transcript.Problem);
        var heard = $"{transcript.Chunks} stretches in {seconds:0.0} s: " +
            string.Join(" | ", transcript.Words.Select(w => $"{w.Text}@{w.Start:0.00}({w.Confidence:0.00})"));
        TestContext.Current.SendDiagnosticMessage(heard);

        var duration = onsets.DurationSec;
        var result = SectionDetector.Detect(transcript.Words, grid, duration, (_, _) => false);
        var infos = SectionList.Describe(result.Sections, grid, duration);
        var want = expected.RootElement.GetProperty("sections").EnumerateArray()
            .Select(s => (Label: s.GetProperty("label").GetString()!, Bar: s.GetProperty("chartBar").GetInt32()))
            .ToList();

        var got = infos.Select(i => $"{i.Label}@{i.ChartBar}").ToList();
        if (!got.SequenceEqual(want.Select(w => $"{w.Label}@{w.Bar}")))
            Assert.Fail($"Detected [{string.Join(", ", got)}], expected [{string.Join(", ", want.Select(w => $"{w.Label}@{w.Bar}"))}]. Heard {heard}");
        Assert.True(seconds < 60, $"Transcription took {seconds:0.0} s");
    }
}
