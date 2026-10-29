// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace RECompose.Presentation.Services;

/// <summary>
/// A plain text log, one file per day (logs/app-YYYY-MM-DD.log in the data folder):
/// "HH:MM:SS  Category: message" (AUTO-50 style). Thread-safe; never throws.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _folder;

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

    public static void Write(string category, string message) => Line($"{category}: {message}");

    /// <summary>A line that names its own kind, as the voice lines do ("Voice ignored: …", VC-60).</summary>
    public static void Line(string text)
    {
        if (_folder is null)
            return;
        var now = DateTime.Now;
        var line = $"{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {text}{Environment.NewLine}";
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(_folder, $"app-{now:yyyy-MM-dd}.log"), line, Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Error(string context, Exception ex) => Write("Errors", $"{context}: {ex}");
}
