// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;

namespace RECompose.Core.Services;

/// <summary>
/// Edits of a service and its entries (SL-1…7, ARR-11, ARR-12). Every operation is pure: it returns
/// a new value and leaves the input alone, so each edit is one undo step.
/// </summary>
public static class ServiceEdit
{
    // ---- Services (SL-1, SL-2) --------------------------------------------------------

    public static Service New(string name, DateTime? date) => new()
    {
        Id = Service.NewId(),
        Name = string.IsNullOrWhiteSpace(name) ? "New service" : name.Trim(),
        Date = date,
    };

    /// <summary>
    /// SL-2: a copy with the same entries (variants or custom arrangements, keys, tempos, mix presets,
    /// count-ins, transitions, notes) under a new name and date. Entries get new ids; the last check is not copied.
    /// </summary>
    public static Service Duplicate(Service source, string name, DateTime? date) => source with
    {
        Id = Service.NewId(),
        Name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim(),
        Date = date,
        Entries = source.Entries.Select(e => e with { Id = ServiceEntry.NewId() }).ToList(),
        LastCheck = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public static Service Rename(Service service, string name, DateTime? date) =>
        service with { Name = string.IsNullOrWhiteSpace(name) ? service.Name : name.Trim(), Date = date };

    // ---- Entries (SL-4, SL-5) ---------------------------------------------------------

    /// <summary>
    /// A new entry for <paramref name="song"/>: its default variant (or as written), the original key,
    /// 100 %, the default mix, the track count-in and a Stop transition (DM-10, DM-41).
    /// </summary>
    public static ServiceEntry NewEntry(Song song) => new()
    {
        Id = ServiceEntry.NewId(),
        SongId = song.Id,
        Arrangement = song.ActiveArrangement is { } variant ? ArrangementChoice.OfVariant(variant.Id) : ArrangementChoice.AsWritten,
        MixPresetId = song.DefaultMix.Id,
    };

    /// <summary>Adds an entry at the end, or at <paramref name="index"/> (a drop between rows). The same song may appear twice.</summary>
    public static Service Add(Service service, ServiceEntry entry, int? index = null)
    {
        var entries = service.Entries.ToList();
        entries.Insert(index is { } i ? Math.Clamp(i, 0, entries.Count) : entries.Count, entry.Clamped());
        return service with { Entries = entries };
    }

    /// <summary>
    /// Moves an entry so it ends up at <paramref name="newIndex"/>. Its transition goes with it: a transition
    /// belongs to the entry before the gap (SL-4).
    /// </summary>
    public static Service Move(Service service, string entryId, int newIndex)
    {
        var entries = service.Entries.ToList();
        var from = entries.FindIndex(e => e.Id == entryId);
        if (from < 0)
            return service;
        var entry = entries[from];
        entries.RemoveAt(from);
        entries.Insert(Math.Clamp(newIndex, 0, entries.Count), entry);
        return service with { Entries = entries };
    }

    public static Service Remove(Service service, string entryId) =>
        service with { Entries = service.Entries.Where(e => e.Id != entryId).ToList() };

    /// <summary>Changes one entry; the result is clamped to the allowed values.</summary>
    public static Service Update(Service service, string entryId, Func<ServiceEntry, ServiceEntry> change) =>
        service with { Entries = service.Entries.Select(e => e.Id == entryId ? change(e).Clamped() : e).ToList() };

    // ---- The arrangement of an entry (ARR-1, ARR-12) ------------------------------------

    /// <summary>
    /// "This service only" (ARR-12): the entry's variant becomes a custom copy that remembers where it came
    /// from. Entry ids are kept, so the playhead stays on the same entry when this happens while playing
    /// (ARR-90). Returns the entry unchanged when it doesn't play a variant of <paramref name="song"/>.
    /// </summary>
    public static ServiceEntry MakeCustom(ServiceEntry entry, Song song)
    {
        if (entry.Arrangement.Type != ArrangementChoiceType.Variant)
            return entry;
        var variant = song.Arrangements.FirstOrDefault(a => a.Id == entry.Arrangement.ArrangementId);
        if (variant is null)
            return entry;
        var copy = variant with
        {
            Id = Arrangement.NewId(),
            Entries = variant.Entries.ToList(),
            Origin = ArrangementOrigin.Copy,
            CopiedFromId = variant.Id,
        };
        return entry with { Arrangement = ArrangementChoice.OfCustom(copy) };
    }

    /// <summary>A custom arrangement that starts empty or as written, for an entry that had none to copy.</summary>
    public static ServiceEntry WithCustom(ServiceEntry entry, Arrangement arrangement) =>
        entry with { Arrangement = ArrangementChoice.OfCustom(arrangement with { Origin = ArrangementOrigin.Copy }) };

    /// <summary>The variant a custom arrangement was copied from, if it still exists.</summary>
    public static Arrangement? RevertTarget(ServiceEntry entry, Song song) =>
        entry.Arrangement is { Type: ArrangementChoiceType.Custom, Arrangement.CopiedFromId: { } from }
            ? song.Arrangements.FirstOrDefault(a => a.Id == from)
            : null;

    /// <summary>"Revert to 'Short'": the entry plays the variant again. Unchanged when there is nothing to revert to.</summary>
    public static ServiceEntry Revert(ServiceEntry entry, Song song) =>
        RevertTarget(entry, song) is { } variant ? entry with { Arrangement = ArrangementChoice.OfVariant(variant.Id) } : entry;

    /// <summary>
    /// "Save as variant…" (ARR-10): the custom arrangement becomes a new variant of the song, and the entry
    /// plays that variant. Returns the inputs unchanged when the entry has no custom arrangement.
    /// </summary>
    public static (Song Song, ServiceEntry Entry, Arrangement? Variant) SaveAsVariant(ServiceEntry entry, Song song, string name)
    {
        if (entry.Arrangement is not { Type: ArrangementChoiceType.Custom, Arrangement: { } custom })
            return (song, entry, null);
        var variant = custom with
        {
            Id = Arrangement.NewId(),
            Name = ArrangementEdit.UniqueName(string.IsNullOrWhiteSpace(name) ? custom.Name : name, song.Arrangements.Select(a => a.Name)),
            Entries = custom.Entries.ToList(),
            Origin = ArrangementOrigin.Copy,
            CopiedFromId = null,
        };
        var updated = song with { Arrangements = [.. song.Arrangements, variant], UpdatedAt = DateTimeOffset.UtcNow };
        return (updated, entry with { Arrangement = ArrangementChoice.OfVariant(variant.Id) }, variant);
    }

    // ---- Songs and variants used by services (ARR-11, SL-56) ----------------------------

    /// <summary>The entries of <paramref name="service"/> that play variant <paramref name="arrangementId"/> of <paramref name="songId"/>.</summary>
    public static IEnumerable<ServiceEntry> UsingVariant(Service service, string songId, string arrangementId) =>
        service.Entries.Where(e => e.SongId == songId && e.Arrangement.Type == ArrangementChoiceType.Variant && e.Arrangement.ArrangementId == arrangementId);

    /// <summary>
    /// Deleting a variant that services use (ARR-11): its entries play <paramref name="replacement"/> instead. A custom
    /// arrangement copied from it keeps its entries but forgets where it came from.
    /// </summary>
    public static Service ReplaceVariant(Service service, string songId, string arrangementId, ArrangementChoice replacement) =>
        service with
        {
            Entries = service.Entries.Select(e =>
            {
                if (e.SongId != songId)
                    return e;
                if (e.Arrangement.Type == ArrangementChoiceType.Variant && e.Arrangement.ArrangementId == arrangementId)
                    return e with { Arrangement = replacement };
                if (e.Arrangement is { Type: ArrangementChoiceType.Custom, Arrangement: { } custom } && custom.CopiedFromId == arrangementId)
                    return e with { Arrangement = ArrangementChoice.OfCustom(custom with { CopiedFromId = null }) };
                return e;
            }).ToList(),
        };

    /// <summary>True when any entry of the service plays the song (warning before "Delete from library", SL-56).</summary>
    public static bool UsesSong(Service service, string songId) => service.Entries.Any(e => e.SongId == songId);

    /// <summary>Sections changed (SEC-17, SEC-54): custom arrangements of the song's entries follow, like its variants.</summary>
    public static Service MapCustomArrangements(Service service, string songId, Func<List<Arrangement>, List<Arrangement>> map)
    {
        if (!service.Entries.Any(e => e.SongId == songId && e.Arrangement.Type == ArrangementChoiceType.Custom))
            return service;
        return service with
        {
            Entries = service.Entries.Select(e =>
                e.SongId == songId && e.Arrangement is { Type: ArrangementChoiceType.Custom, Arrangement: { } custom }
                    ? e with { Arrangement = ArrangementChoice.OfCustom(map([custom])[0]) }
                    : e).ToList(),
        };
    }
}
