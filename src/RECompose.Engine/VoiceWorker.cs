// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RECompose.Engine;

/// <summary>What the recogniser heard in one utterance (VC-10).</summary>
public sealed record Recognised(string Text, double Confidence);

/// <summary>
/// The voice worker (VC-10, REL-50): <c>recompose-engine listen &lt;model&gt;</c> kept running with the model loaded, so recognition
/// never runs in the playback process. One utterance at a time; a worker that dies is started again on the next one.
/// </summary>
public sealed class VoiceWorker(string executablePath, string model) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _id;

    /// <summary>Recognises 16 kHz 16-bit PCM, biased by <paramref name="prompt"/> (VC-11).</summary>
    /// <exception cref="EngineUnavailableException">The worker could not be started or the model is missing.</exception>
    public async Task<Recognised> RecogniseAsync(byte[] pcm16, string prompt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = await StartAsync(ct).ConfigureAwait(false);
            var request = new JsonObject { ["id"] = ++_id, ["prompt"] = prompt, ["samples"] = Convert.ToBase64String(pcm16) };
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (Parse(line) is { } o && (int?)o["id"] == _id)
                    return new Recognised((string?)o["text"] ?? string.Empty, (double?)o["confidence"] ?? 0);
            }

            Stop();
            throw new EngineUnavailableException("The voice recogniser stopped.");
        }
        catch (IOException)
        {
            Stop(); // it died between two utterances; the next one starts it again
            throw new EngineUnavailableException("The voice recogniser stopped.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Process> StartAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } running)
            return running;
        Stop();
        if (!File.Exists(executablePath))
            throw new EngineUnavailableException($"The audio engine ({Path.GetFileName(executablePath)}) was not found next to the app.");

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("listen");
        start.ArgumentList.Add(model);
        var process = Process.Start(start) ?? throw new EngineUnavailableException("The voice recogniser could not be started.");
        process.ErrorDataReceived += (_, _) => { }; // whisper's own talk; drained so it never blocks
        process.BeginErrorReadLine();

        // The first line says whether the model loaded.
        var ready = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
        if (Parse(ready) is not { } o || !((bool?)o["ok"] ?? false))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
            throw new EngineUnavailableException((string?)Parse(ready)?["problem"] ?? "The voice recogniser could not be started.");
        }

        _process = process;
        return process;
    }

    private static JsonObject? Parse(string? line)
    {
        if (line is null || !line.StartsWith('{'))
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

    private void Stop()
    {
        if (_process is null)
            return;
        try
        {
            _process.StandardInput.Close(); // the worker exits when its input closes
            if (!_process.WaitForExit(1000))
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _process = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
