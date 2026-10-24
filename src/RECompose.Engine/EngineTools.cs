// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RECompose.Engine;

/// <summary>What <c>recompose-engine probe</c> reports about an audio file.</summary>
public sealed record AudioFileInfo(bool Ok, string? Problem, int Channels, double SampleRate, long Frames, double DurationSec, int BitsPerSample, string Format);

/// <summary>
/// Runs the engine's one-shot tool modes in short-lived processes at low priority, so
/// decoding for import never shares a process with playback (PB-92).
/// </summary>
public sealed class EngineTools(string executablePath)
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

    private async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, bool captureOutput, CancellationToken ct)
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
        var stderr = process.StandardError.ReadToEndAsync(ct);
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
