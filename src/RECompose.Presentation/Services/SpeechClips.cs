// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RECompose.Presentation.Services;

/// <summary>
/// Spoken clips (PB-71, VC-50): each phrase rendered once with the platform's own voice (PLAT-40) into
/// <c>cache/tts/&lt;sha1&gt;.wav</c>: System.Speech on Windows, <c>say</c> on macOS, espeak-ng on Linux.
/// </summary>
public sealed class SpeechClips(string folder)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Folder => folder;

    public string PathFor(string text) =>
        Path.Combine(folder, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("default|" + text))) + ".wav");

    /// <summary>The clip's file, rendered now if it is not in the cache; null when this computer has no voice.</summary>
    public async Task<string?> GetAsync(string text, CancellationToken ct = default)
    {
        var path = PathFor(text);
        if (File.Exists(path))
            return path;

        await _gate.WaitAsync(ct).ConfigureAwait(false); // one voice process at a time
        try
        {
            if (File.Exists(path))
                return path;
            Directory.CreateDirectory(folder);
            var part = path[..^4] + ".part.wav";
            if (Command(text, part) is not { } start)
                return null;
            using var process = Process.Start(start);
            if (process is null)
                return null;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(part))
                return null;
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Voice", $"Speech clip not made: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ProcessStartInfo? Command(string text, string path)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            // Single-quoted PowerShell strings: a quote inside is doubled, nothing else is special.
            static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
            start = new ProcessStartInfo("powershell");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
                + $"$s.SetOutputToWaveFile({Quote(path)}); $s.Speak({Quote(text)}); $s.Dispose()");
        }
        else if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo("say");
            foreach (var a in new[] { "-o", path, "--file-format=WAVE", "--data-format=LEI16@22050", text })
                start.ArgumentList.Add(a);
        }
        else if (OperatingSystem.IsLinux())
        {
            start = new ProcessStartInfo("espeak-ng");
            foreach (var a in new[] { "-v", "en-us", "-w", path, text })
                start.ArgumentList.Add(a);
        }
        else
        {
            return null;
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return start;
    }
}
