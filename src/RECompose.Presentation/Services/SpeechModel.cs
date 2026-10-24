// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace RECompose.Presentation.Services;

/// <summary>
/// The offline speech model for guide transcription (SEC-12, PLAT-40): whisper.cpp's
/// English base model (MIT). It is looked up next to the app first (the CI build ships it
/// in <c>models/</c>), then in the data folder; otherwise it is downloaded once and checked.
/// </summary>
public sealed class SpeechModel(string dataRoot, bool allowDownload = true, HttpClient? http = null)
{
    public const string FileName = "ggml-base.en.bin";
    public const string Sha1 = "137c40403d78fd54d454da0f9bd998f78703390c";
    public const long ApproximateBytes = 147_964_211;
    public static readonly Uri Source = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName);

    public string DownloadFolder => Path.Combine(dataRoot, "models");

    /// <summary>Where a copy may live, in the order they are tried.</summary>
    public IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("RECOMPOSE_SPEECH_MODEL") is { Length: > 0 } overridden)
            yield return overridden;
        yield return Path.Combine(AppContext.BaseDirectory, "models", FileName);
        yield return Path.Combine(DownloadFolder, FileName);
    }

    /// <summary>The model file if one is present.</summary>
    public string? Find() => Candidates().FirstOrDefault(File.Exists);

    /// <summary>True when transcription can run without asking: the model is here or may be downloaded.</summary>
    public bool Available => allowDownload || Find() is not null;

    /// <summary>Finds the model or downloads it, reporting 0…1. Throws <see cref="SpeechModelException"/> with a readable reason.</summary>
    public async Task<string> EnsureAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Find() is { } found)
            return found;
        if (!allowDownload)
            throw new SpeechModelException($"The speech model isn't installed. Copy {FileName} into {DownloadFolder}.");

        Directory.CreateDirectory(DownloadFolder);
        var target = Path.Combine(DownloadFolder, FileName);
        var temp = target + ".download";
        var client = http ?? Shared.Value;
        try
        {
            using var response = await client.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? ApproximateBytes;

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(temp))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReport = -1.0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    var fraction = Math.Min(1, (double)done / total);
                    if (fraction - lastReport >= 0.01)
                    {
                        progress?.Report(fraction);
                        lastReport = fraction;
                    }
                }

                if (!Convert.ToHexStringLower(sha.GetHashAndReset()).Equals(Sha1, StringComparison.Ordinal))
                    throw new SpeechModelException("The downloaded speech model is damaged. Try again later.");
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechModelException($"The speech model couldn't be downloaded ({ex.Message}). Check the internet connection, or copy {FileName} into {DownloadFolder}.", ex);
        }
        catch (IOException ex)
        {
            throw new SpeechModelException($"The speech model couldn't be saved ({ex.Message}).", ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(20) });
}

public sealed class SpeechModelException(string message, Exception? inner = null) : Exception(message, inner);
