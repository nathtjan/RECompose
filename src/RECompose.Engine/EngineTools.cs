// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RECompose.Core.Analysis;
using RECompose.Core.Sections;

namespace RECompose.Engine;

/// <summary>What <c>recompose-engine probe</c> reports about an audio file.</summary>
public sealed record AudioFileInfo(bool Ok, string? Problem, int Channels, double SampleRate, long Frames, double DurationSec, int BitsPerSample, string Format);

/// <summary>What <c>recompose-engine onsets</c> found in a click track (SEC-1).</summary>
public sealed record OnsetsResult(bool Ok, string? Problem, double DurationSec, IReadOnlyList<ClickOnset> Onsets);

/// <summary>What <c>recompose-engine transcribe</c> heard in a guide track (SEC-11).</summary>
public sealed record TranscriptResult(bool Ok, string? Problem, string Model, int Chunks, IReadOnlyList<GuideWord> Words);

/// <summary>The analysis jobs of milestone 2, behind an interface so view-model tests can fake them.</summary>
public interface IAnalysisTools
{
    Task<OnsetsResult> OnsetsAsync(string file, CancellationToken ct = default);

    Task<TranscriptResult> TranscribeAsync(string file, string model, string prompt, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Runs the engine's one-shot tool modes in short-lived processes at low priority, so
/// decoding for import never shares a process with playback (PB-92).
/// </summary>
public sealed class EngineTools(string executablePath) : IAnalysisTools
{
    public string ExecutablePath { get; } = executablePath;

    public static EngineTools Default() => new(EnginePaths.DefaultExecutablePath());

    public async Task<AudioFileInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunAsync(["probe", file], captureOutput: true, ct).ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(output) is JsonObject o)
            {
                return new AudioFileInfo(
                    (bool?)o["ok"] ?? false,
                    (string?)o["problem"],
                    (int?)o["channels"] ?? 0,
                    (double?)o["sampleRate"] ?? 0,
                    (long?)o["frames"] ?? 0,
                    (double?)o["durationSec"] ?? 0,
                    (int?)o["bitsPerSample"] ?? 0,
                    (string?)o["format"] ?? string.Empty);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new AudioFileInfo(false, exitCode == 0 ? "Can't read this file" : $"Can't read this file (exit code {exitCode})", 0, 0, 0, 0, 0, string.Empty);
    }

    /// <summary>Writes a peaks file (see engine/src/tools.h). Returns false if the file can't be read.</summary>
    public async Task<bool> PeaksAsync(string file, string output, int framesPerBucket, CancellationToken ct = default)
    {
        var (exitCode, _) = await RunAsync(["peaks", file, output, framesPerBucket.ToString(System.Globalization.CultureInfo.InvariantCulture)], captureOutput: false, ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>Onsets of a click track with level and brightness (SEC-1 step 1).</summary>
    public async Task<OnsetsResult> OnsetsAsync(string file, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunAsync(["onsets", file], captureOutput: true, ct).ConfigureAwait(false);
        var o = ParseObject(output);
        if (o is null || !((bool?)o["ok"] ?? false))
            return new OnsetsResult(false, Problem(o, exitCode), 0, []);

        var onsets = new List<ClickOnset>();
        if (o["onsets"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonArray>())
            {
                if (row.Count >= 3)
                    onsets.Add(new ClickOnset((double?)row[0] ?? 0, (double?)row[1] ?? -120, (double?)row[2] ?? 0));
            }
        }

        return new OnsetsResult(true, null, (double?)o["durationSec"] ?? 0, onsets);
    }

    /// <summary>
    /// Word timestamps of a guide track (SEC-11/12). <paramref name="progress"/> gets 0…1
    /// as the speech stretches are transcribed.
    /// </summary>
    public async Task<TranscriptResult> TranscribeAsync(string file, string model, string prompt, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture);
        var (exitCode, output) = await RunAsync(["transcribe", file, model, "--prompt", prompt, "--threads", threads], captureOutput: true, ct, line =>
        {
            if (line.StartsWith("progress ", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                progress?.Report(p);
        }).ConfigureAwait(false);

        var o = ParseObject(output);
        if (o is null || !((bool?)o["ok"] ?? false))
            return new TranscriptResult(false, Problem(o, exitCode), string.Empty, 0, []);

        var words = new List<GuideWord>();
        if (o["words"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonArray>())
            {
                if (row.Count >= 4 && (string?)row[0] is { } text)
                    words.Add(new GuideWord(text, (double?)row[1] ?? 0, (double?)row[2] ?? 0, (double?)row[3] ?? 0));
            }
        }

        return new TranscriptResult(true, null, (string?)o["model"] ?? string.Empty, (int?)o["chunks"] ?? 0, words);
    }

    private static JsonObject? ParseObject(string output)
    {
        // The JSON answer is the last line; anything before it is a library talking.
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
            return null;
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Problem(JsonObject? o, int exitCode) =>
        (string?)o?["problem"] ?? (exitCode == 0 ? "The analysis gave no answer" : $"The analysis failed (exit code {exitCode})");

    private async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, bool captureOutput, CancellationToken ct, Action<string>? stderrLine = null)
    {
        if (!File.Exists(ExecutablePath))
            throw new EngineUnavailableException($"The audio engine ({Path.GetFileName(ExecutablePath)}) was not found next to the app.");

        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new EngineUnavailableException("The audio engine could not be started.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Not allowed here; it still works at normal priority.
        }

        var stdout = captureOutput ? process.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                stderrLine?.Invoke(line);
        }, ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }

        await stderr.ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false));
    }
}
