// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Sections;

namespace RECompose.Core.Services;

public enum CheckStatus
{
    Ok,
    Warning,
    Error,

    /// <summary>Nothing to decide yet (the feature arrives later, or only information).</summary>
    Info,
}

/// <summary>What the Fix button of a check item does (REL-1).</summary>
public enum CheckFix
{
    None,
    Relink,
    OpenEntry,
    AnalyseNow,
    RenderNow,
    OpenAudioSettings,
    ChooseFallback,
    CleanCache,
    IncreaseBuffer,
}

public sealed record CheckItem(int Number, string Title, CheckStatus Status, string Detail, CheckFix Fix = CheckFix.None, string? EntryId = null, string? SongId = null)
{
    public string Mark => Status switch { CheckStatus.Ok => "✓", CheckStatus.Warning => "⚠", CheckStatus.Error => "✗", _ => "·" };
}

/// <summary>How many renders an entry needs and has (REL-10).</summary>
public readonly record struct RenderNeed(int Needed, int Done, int Failed = 0);

/// <summary>What the check looks at; the app fills it in (the check itself touches nothing).</summary>
public sealed record CheckContext
{
    public required Service Service { get; init; }
    public required Func<string, Song?> FindSong { get; init; }
    public required Func<string, bool> FileExists { get; init; }

    /// <summary>The renders an entry needs and has; null = no render cache.</summary>
    public Func<ServiceEntry, Song, RenderNeed>? Renders { get; init; }

    /// <summary>The open audio device: its name and output channel count, or null when none is open.</summary>
    public string? DeviceName { get; init; }
    public int OutputChannels { get; init; }
    public int MainChannel { get; init; }
    public int MainWidth { get; init; } = 2;
    public int CueChannel { get; init; }
    public int CueWidth { get; init; } = 2;

    public string? FallbackDeviceName { get; init; }
    public bool FallbackPresent { get; init; }

    /// <summary>Bindings of MIDI controllers exist, and one is connected.</summary>
    public bool HasMidiBindings { get; init; }
    public bool MidiInputPresent { get; init; }

    public long? FreeDiskBytes { get; init; }

    /// <summary>On battery power (laptops); null = unknown or a desktop.</summary>
    public bool? OnBattery { get; init; }
    public int? BatteryPercent { get; init; }

    public bool AssistantEnabled { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

public enum CheckOverall
{
    Ready,
    ReadyWithWarnings,
    NotReady,
}

public sealed record CheckResult(IReadOnlyList<CheckItem> Items, DateTimeOffset At)
{
    public CheckOverall Overall =>
        Items.Any(i => i.Status == CheckStatus.Error) ? CheckOverall.NotReady
        : Items.Any(i => i.Status == CheckStatus.Warning) ? CheckOverall.ReadyWithWarnings
        : CheckOverall.Ready;

    /// <summary>"✓ Ready", "⚠ Ready with warnings", "✗ Not ready" (REL-2).</summary>
    public string Text => Overall switch
    {
        CheckOverall.Ready => "✓ Ready",
        CheckOverall.ReadyWithWarnings => "⚠ Ready with warnings",
        _ => "✗ Not ready",
    };

    public string Code => Overall switch { CheckOverall.Ready => "ready", CheckOverall.ReadyWithWarnings => "warnings", _ => "notReady" };
}

/// <summary>REL-1: the pre-flight checklist of one service. Pure.</summary>
public static class ServiceCheck
{
    public const long MinFreeDiskBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The full check (REL-1), or in Perform the quick one: items 1, 5, 7, 8 and 9 (REL-3).</summary>
    public static CheckResult Run(CheckContext c, bool quick = false)
    {
        var items = new List<CheckItem>
        {
            Files(c),
        };
        if (!quick)
        {
            items.Add(Arrangements(c));
            items.Add(Analysis(c));
            items.Add(RenderCache(c));
        }

        items.Add(Device(c));
        if (!quick)
            items.Add(Fallback(c));
        items.Add(new CheckItem(7, "MIDI out", CheckStatus.Info, "No song uses section notes or MIDI cues yet."));
        items.Add(Controller(c));
        items.Add(new CheckItem(9, "Voice", CheckStatus.Info, "Voice control comes with a later version."));
        if (!quick)
        {
            items.Add(new CheckItem(10, "Performance", CheckStatus.Info, "Not measured yet. Watch CPU in the status row while you rehearse."));
            items.Add(Disk(c));
            items.Add(Power(c));
            items.Add(new CheckItem(13, "Assistant", CheckStatus.Info, c.AssistantEnabled ? "The assistant is on; its key is checked when you use it." : "The assistant is off."));
        }

        return new CheckResult(items, c.Now);
    }

    // 1: every entry's song exists and every track file is there.
    private static CheckItem Files(CheckContext c)
    {
        var missing = new List<(string Text, string SongId, string EntryId)>();
        foreach (var entry in c.Service.Entries)
        {
            if (c.FindSong(entry.SongId) is not { } song)
            {
                missing.Add(("a song is missing from the library", entry.SongId, entry.Id));
                continue;
            }

            foreach (var track in song.Tracks.Where(t => !t.Hidden && !c.FileExists(t.File)))
                missing.Add(($"{song.Title}: {track.Name} ({Path.GetFileName(track.File)}) is missing", song.Id, entry.Id));
        }

        if (missing.Count == 0)
            return new CheckItem(1, "Songs and stem files", CheckStatus.Ok, $"{c.Service.Entries.Count} entries, every stem file is readable.");
        var first = missing[0];
        return new CheckItem(1, "Songs and stem files", CheckStatus.Error,
            string.Join("; ", missing.Select(m => m.Text).Distinct().Take(3)) + (missing.Count > 3 ? $" and {missing.Count - 3} more" : string.Empty),
            CheckFix.Relink, first.EntryId, first.SongId);
    }

    // 2: every entry has a playable arrangement.
    private static CheckItem Arrangements(CheckContext c)
    {
        var errors = 0;
        var warnings = 0;
        string? where = null, firstEntry = null, text = null;
        foreach (var entry in c.Service.Entries)
        {
            if (c.FindSong(entry.SongId) is not { } song)
                continue;
            var resolved = EntryArrangement.Resolve(entry, song);
            if (resolved.Arrangement is not { } arrangement)
                continue; // as written always plays
            var sections = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
            foreach (var issue in ArrangementValidator.Validate(arrangement, sections).Where(i => i.Severity != IssueSeverity.Info))
            {
                if (issue.Severity == IssueSeverity.Error)
                    errors++;
                else
                    warnings++;
                where ??= song.Title;
                firstEntry ??= entry.Id;
                text ??= issue.Message;
            }
        }

        if (errors > 0)
            return new CheckItem(2, "Arrangements", CheckStatus.Error, $"{errors} problem{(errors == 1 ? string.Empty : "s")}: {where} — {text}", CheckFix.OpenEntry, firstEntry);
        if (warnings > 0)
            return new CheckItem(2, "Arrangements", CheckStatus.Warning, $"{warnings} warning{(warnings == 1 ? string.Empty : "s")}: {where} — {text}", CheckFix.OpenEntry, firstEntry);
        return new CheckItem(2, "Arrangements", CheckStatus.Ok, "Every entry plays.");
    }

    // 3: the click grid is analysed.
    private static CheckItem Analysis(CheckContext c)
    {
        var pending = c.Service.Entries
            .Select(e => c.FindSong(e.SongId))
            .OfType<Song>()
            .DistinctBy(s => s.Id)
            .Where(s => s.ClickAnalysis is null && s.TempoSource != TempoSource.Manual)
            .ToList();
        return pending.Count == 0
            ? new CheckItem(3, "Analysis", CheckStatus.Ok, "Every song has its tempo grid.")
            : new CheckItem(3, "Analysis", CheckStatus.Warning, $"{string.Join(", ", pending.Take(3).Select(s => s.Title))} {(pending.Count == 1 ? "has" : "have")} no tempo analysis.", CheckFix.AnalyseNow, SongId: pending[0].Id);
    }

    // 4: the render cache.
    private static CheckItem RenderCache(CheckContext c)
    {
        if (c.Renders is null)
            return new CheckItem(4, "Rendered keys and tempos", CheckStatus.Info, "The render cache is not available.");

        var needed = 0;
        var done = 0;
        var failed = 0;
        string? entryId = null;
        foreach (var entry in c.Service.Entries)
        {
            if (c.FindSong(entry.SongId) is not { } song)
                continue;
            var need = c.Renders(entry, song);
            needed += need.Needed;
            done += need.Done;
            failed += need.Failed;
            if (need.Done < need.Needed)
                entryId ??= entry.Id;
        }

        if (needed == 0)
            return new CheckItem(4, "Rendered keys and tempos", CheckStatus.Ok, "Nothing needs rendering.");
        if (done >= needed)
            return new CheckItem(4, "Rendered keys and tempos", CheckStatus.Ok, $"All {needed} renders are in the cache.");
        var detail = $"{done} of {needed} renders are ready; live processing is used for the rest" + (failed > 0 ? $" ({failed} failed)." : ".");
        return new CheckItem(4, "Rendered keys and tempos", CheckStatus.Warning, detail, CheckFix.RenderNow, entryId);
    }

    // 5: the audio device and the logical outputs.
    private static CheckItem Device(CheckContext c)
    {
        if (c.DeviceName is null)
            return new CheckItem(5, "Audio device", CheckStatus.Error, "No audio device is open.", CheckFix.OpenAudioSettings);

        var needed = Math.Max(c.MainChannel + c.MainWidth, c.CueChannel + c.CueWidth);
        if (c.OutputChannels > 0 && needed > c.OutputChannels)
            return new CheckItem(5, "Audio device", CheckStatus.Warning, $"{c.DeviceName} has {c.OutputChannels} outputs, and Main and Cue use up to channel {needed}.", CheckFix.OpenAudioSettings);
        return new CheckItem(5, "Audio device", CheckStatus.Ok, $"{c.DeviceName}, {c.OutputChannels} outputs.");
    }

    // 6: a fallback device (REL-20).
    private static CheckItem Fallback(CheckContext c) =>
        c.FallbackDeviceName is null
            ? new CheckItem(6, "Fallback device", CheckStatus.Warning, "No fallback device is chosen. If the main device is lost the music clock runs silently.", CheckFix.ChooseFallback)
            : c.FallbackPresent
                ? new CheckItem(6, "Fallback device", CheckStatus.Ok, $"{c.FallbackDeviceName} is present.")
                : new CheckItem(6, "Fallback device", CheckStatus.Warning, $"{c.FallbackDeviceName} is not connected.", CheckFix.ChooseFallback);

    // 8: a controller, when bindings need one.
    private static CheckItem Controller(CheckContext c) =>
        !c.HasMidiBindings
            ? new CheckItem(8, "Foot controller", CheckStatus.Ok, "No MIDI bindings are set.")
            : c.MidiInputPresent
                ? new CheckItem(8, "Foot controller", CheckStatus.Ok, "A MIDI input is connected.")
                : new CheckItem(8, "Foot controller", CheckStatus.Warning, "Bindings use a MIDI controller, and none is connected.");

    // 11: disk space.
    private static CheckItem Disk(CheckContext c) =>
        c.FreeDiskBytes is not { } free
            ? new CheckItem(11, "Disk space", CheckStatus.Info, "Free space could not be read.")
            : free >= MinFreeDiskBytes
                ? new CheckItem(11, "Disk space", CheckStatus.Ok, $"{(free / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture)} GB free.")
                : new CheckItem(11, "Disk space", CheckStatus.Warning, $"Only {(free / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture)} GB free for the cache and the logs.", CheckFix.CleanCache);

    // 12: power.
    private static CheckItem Power(CheckContext c) =>
        c.OnBattery is true
            ? new CheckItem(12, "Power", CheckStatus.Warning, $"Running on battery{(c.BatteryPercent is { } p ? $" ({p.ToString(CultureInfo.InvariantCulture)} %)" : string.Empty)}. Plug in before the service; Perform keeps the computer awake. Turn on Do Not Disturb.")
            : new CheckItem(12, "Power", CheckStatus.Ok, "On AC power (or no battery). Perform keeps the computer awake; turn on Do Not Disturb or Focus.");

    /// <summary>
    /// What the result was about: the entries and their songs' stems. A different signature later means the check is out of date
    /// ("Changed since check", REL-2).
    /// </summary>
    public static string Signature(Service service, Func<string, Song?> findSong)
    {
        var text = new StringBuilder();
        foreach (var entry in service.Entries)
        {
            text.Append(entry.SongId).Append('|').Append(entry.KeyOffset).Append('|').Append(entry.TempoPercent).Append('|')
                .Append(entry.MixPresetId).Append('|').Append(entry.CountIn.Mode).Append(entry.CountIn.Bars).Append('|')
                .Append(entry.Arrangement.Type).Append(entry.Arrangement.ArrangementId).Append('|');
            if (entry.Arrangement.Arrangement is { } custom)
                foreach (var e in custom.Entries)
                    text.Append(e.SectionId).Append(e.Kind).Append(e.Transpose).Append(e.Repeat.Count).Append(e.Repeat.UntilCued).Append(',');
            if (findSong(entry.SongId) is { } song)
            {
                text.Append('|').Append(song.UpdatedAt.UtcTicks);
                foreach (var t in song.Tracks)
                    text.Append(t.File).Append(t.Hidden);
                if (song.Arrangements.FirstOrDefault(a => a.Id == entry.Arrangement.ArrangementId) is { } variant)
                    foreach (var e in variant.Entries)
                        text.Append(e.SectionId).Append(e.Kind).Append(e.Transpose).Append(',');
            }

            text.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
