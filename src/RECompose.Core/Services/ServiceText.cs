// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Timing;

namespace RECompose.Core.Services;

/// <summary>What a service entry plays, resolved against its song (DM-41, PB-41 step 1).</summary>
/// <param name="Arrangement">The arrangement to build the plan from; null plays as written.</param>
/// <param name="Type">What actually plays: a variant, a custom arrangement or as written.</param>
/// <param name="Name">The variant name, "Custom for this service" or "As written" (UI-12, UI-13).</param>
/// <param name="Problem">Why the entry can't play what it asks for, or null.</param>
public sealed record ResolvedArrangement(Arrangement? Arrangement, ArrangementChoiceType Type, string Name, string? Problem = null);

public static class EntryArrangement
{
    public const string CustomName = "Custom for this service";
    public const string AsWrittenName = "As written";

    /// <summary>
    /// The arrangement an entry plays. Never fails: a variant that no longer exists, or a song missing from the
    /// library, plays as written and says why.
    /// </summary>
    public static ResolvedArrangement Resolve(ServiceEntry entry, Song? song)
    {
        if (song is null)
            return new ResolvedArrangement(null, ArrangementChoiceType.AsWritten, AsWrittenName, "Song missing from the library");

        var choice = entry.Arrangement;
        switch (choice.Type)
        {
            case ArrangementChoiceType.Variant:
                var variant = song.Arrangements.FirstOrDefault(a => a.Id == choice.ArrangementId);
                return variant is null
                    ? new ResolvedArrangement(null, ArrangementChoiceType.AsWritten, AsWrittenName, "Variant removed — plays as written")
                    : new ResolvedArrangement(variant, ArrangementChoiceType.Variant, variant.Name);

            case ArrangementChoiceType.Custom when choice.Arrangement is { } custom:
                return new ResolvedArrangement(custom, ArrangementChoiceType.Custom, CustomName);

            default:
                return new ResolvedArrangement(null, ArrangementChoiceType.AsWritten, AsWrittenName);
        }
    }

    /// <summary>A library song opened outside a service plays its default variant (M4 decision 1).</summary>
    public static ResolvedArrangement ForLibrarySong(Song song) =>
        song.ActiveArrangement is { } variant
            ? new ResolvedArrangement(variant, ArrangementChoiceType.Variant, variant.Name)
            : new ResolvedArrangement(null, ArrangementChoiceType.AsWritten, AsWrittenName);
}

/// <summary>Text for services, entries and transitions as the sidebar, the header and the popover show it.</summary>
public static class ServiceText
{
    /// <summary>"Sunday 26 Oct · 9:00" (UI-11): the name, then the time when the service has a date.</summary>
    public static string Title(Service service) =>
        service.Date is { } date ? $"{service.Name} · {date.ToString("H:mm", CultureInfo.InvariantCulture)}" : service.Name;

    /// <summary>
    /// SL-1: upcoming services first (soonest first), then past ones (most recent first), then those without a date.
    /// A service counts as upcoming until the end of its day.
    /// </summary>
    public static List<Service> Ordered(IEnumerable<Service> services, DateTime now)
    {
        var today = now.Date;
        var list = services.ToList();
        var upcoming = list.Where(s => s.Date is { } d && d.Date >= today).OrderBy(s => s.Date);
        var past = list.Where(s => s.Date is { } d && d.Date < today).OrderByDescending(s => s.Date);
        var undated = list.Where(s => s.Date is null).OrderByDescending(s => s.UpdatedAt);
        return [.. upcoming, .. past, .. undated];
    }

    /// <summary>The entry's key: the song's original key moved by the offset, or null when the song's key is unknown.</summary>
    public static Key? EntryKey(Song song, int keyOffset) =>
        Key.TryParse(song.OriginalKey, out var key) ? key.Transpose(keyOffset) : null;

    /// <summary>"A (−1)", "F (+1)", "E" at the original key, "Key ? (+2)" when the song's key is unknown (UI-13).</summary>
    public static string KeyText(Song song, int keyOffset)
    {
        var key = EntryKey(song, keyOffset)?.ToDisplayString() ?? "Key ?";
        return keyOffset == 0 ? key : $"{key} ({Signed(keyOffset)})";
    }

    /// <summary>"+2", "−1", "0" with a real minus sign.</summary>
    public static string Signed(int value) =>
        value > 0 ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
        : value < 0 ? $"−{(-value).ToString(CultureInfo.InvariantCulture)}"
        : "0";

    /// <summary>"72 BPM" at 100 %, otherwise the BPM heard at the entry tempo: "68 BPM (95%)".</summary>
    public static string TempoText(Song song, int tempoPercent) =>
        tempoPercent == 100
            ? $"{Format.Bpm(song.Bpm)} BPM"
            : $"{Format.Bpm(song.Bpm * tempoPercent / 100.0)} BPM ({tempoPercent.ToString(CultureInfo.InvariantCulture)}%)";

    /// <summary>UI-13: "A (−1) · 72 BPM · 3/4 · Short".</summary>
    public static string HeaderLine(Song song, ServiceEntry entry, ResolvedArrangement arrangement) =>
        $"{KeyText(song, entry.KeyOffset)} · {TempoText(song, entry.TempoPercent)} · {song.BeatsPerBar}/{song.BeatUnit} · {arrangement.Name}";

    /// <summary>The pad key of a Pad transition: its own, else the next song's key, else C.</summary>
    public static Key PadKey(Transition transition, Key? nextKey) =>
        Key.TryParse(transition.PadKey, out var key) ? key : nextKey ?? new Key(0);

    /// <summary>The click tempo of a Pad transition: its own, else the next song's BPM, else 120.</summary>
    public static double ClickBpm(Transition transition, double? nextBpm) => transition.ClickBpm ?? nextBpm ?? 120;

    /// <summary>The chip between two rows (SL-20): "Stop", "Segue", "Crossfade 6 s", "Pad in D♭ · click 72", "Carry pad over".</summary>
    public static string Chip(Transition transition, Key? nextKey, double? nextBpm) => transition.Type switch
    {
        TransitionType.Segue when transition.SegueGapBeats > 0 => $"Segue · {Beats(transition.SegueGapBeats)}",
        TransitionType.Segue => "Segue",
        TransitionType.Crossfade => $"Crossfade {Seconds(transition.CrossfadeSec)} s",
        TransitionType.Pad => transition.ClickDuring
            ? $"Pad in {PadKey(transition, nextKey).ToDisplayString()} · click {Format.Bpm(ClickBpm(transition, nextBpm))}"
            : $"Pad in {PadKey(transition, nextKey).ToDisplayString()}",
        TransitionType.CarryOver => "Carry pad over",
        _ => "Stop",
    };

    /// <summary>The explanation in the transition popover (SL-20, SL-21), with the song titles filled in.</summary>
    public static string Explanation(Transition transition, string thisTitle, string nextTitle, Key? nextKey)
    {
        switch (transition.Type)
        {
            case TransitionType.Segue:
                var when = transition.SegueGapBeats == 0 ? "right after" : $"{Beats(transition.SegueGapBeats)} after";
                return $"{nextTitle} starts by itself {when} {thisTitle} ends.";

            case TransitionType.Crossfade:
                return $"{nextTitle} starts by itself while {thisTitle} fades out over {Seconds(transition.CrossfadeSec)} seconds.";

            case TransitionType.Pad:
                var start = transition.AutoStartNext
                    ? $"The next song starts by itself after {Bars(transition.AutoStartAfterBars)}"
                    : "Press Play and the next song starts";
                var pad = transition.FadePadOutOnNextStart ? ", and the pad fades out" : " while the pad keeps playing";
                var click = transition.ClickDuring ? " with the click" : string.Empty;
                return $"At the end, the pad fades in{click}. {start}{pad}.";

            case TransitionType.CarryOver:
                var retune = transition.RetuneToNextKey && nextKey is { } k ? $", retuned to {k.ToDisplayString()}" : string.Empty;
                return $"The pad that's playing keeps going into {nextTitle}{retune}.";

            default:
                return $"At the end, playback stops. {nextTitle} is cued — press Play to start it.";
        }
    }

    private static string Beats(int n) => n == 1 ? "1 beat" : $"{n.ToString(CultureInfo.InvariantCulture)} beats";

    private static string Bars(int n) => n == 1 ? "1 bar" : $"{n.ToString(CultureInfo.InvariantCulture)} bars";

    private static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture);
}
