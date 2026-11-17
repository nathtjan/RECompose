// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using RECompose.Core.Logging;

namespace RECompose.Presentation.Services;

/// <summary>
/// The log (AUTO-50): every line goes to the LOG tab (<see cref="Added"/>) and to logs/service-YYYY-MM-DD.log as
/// "HH:MM:SS  Category: message"; logs/app-YYYY-MM-DD.log also keeps the details of errors. Thread-safe; never throws.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _folder;

    /// <summary>A new entry, raised on the thread that logged it.</summary>
    public static event Action<LogEntry>? Added;

    public static void Start(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, "logs");
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (IOException)
        {
            _folder = null;
        }
        catch (UnauthorizedAccessException)
        {
            _folder = null;
        }
    }

    public static string? Folder => _folder;

    public static void Write(string category, string message) => Entry(LogChips.Of(category), $"{category}: {message}");

    /// <summary>A line that names its own kind, as the voice lines do ("Voice ignored: …", VC-60).</summary>
    public static void Line(string text) => Entry(text.StartsWith("Voice", StringComparison.Ordinal) ? "Voice" : string.Empty, text);

    /// <summary>A line under a filter chip of the LOG tab ("Sections", "Cues", "Guide").</summary>
    public static void Entry(string chip, string text)
    {
        var now = DateTime.Now;
        var shown = text.Split('\n', 2)[0].TrimEnd('\r');
        Added?.Invoke(new LogEntry(now, chip, shown));
        if (_folder is null)
            return;
        var time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(_folder, $"app-{now:yyyy-MM-dd}.log"), $"{time}  {text}{Environment.NewLine}", Encoding.UTF8);
                File.AppendAllText(Path.Combine(_folder, $"service-{now:yyyy-MM-dd}.log"), $"{time}  {shown}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Error(string context, Exception ex) => Write("Errors", $"{context}: {ex}");

    private static string? CrashPath => _folder is null ? null : Path.Combine(_folder, "last-error.txt");

    /// <summary>
    /// Logs an error that ends the app and keeps its details for the next start, which shows them: no window can open
    /// while the process ends.
    /// </summary>
    public static void Crash(string details)
    {
        Write("Errors", $"Unhandled: {details}");
        if (CrashPath is not { } path)
            return;
        try
        {
            File.WriteAllText(path, details, Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The details kept by <see cref="Crash"/> at the last run, once: they are removed when read.</summary>
    public static string? TakeCrash()
    {
        if (CrashPath is not { } path || !File.Exists(path))
            return null;
        try
        {
            var details = File.ReadAllText(path, Encoding.UTF8);
            File.Delete(path);
            return details;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
