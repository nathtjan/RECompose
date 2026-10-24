// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Sections;

namespace RECompose.Core.Arrangements;

/// <summary>
/// Edits of one arrangement (ARR-10, ARR-20) and of the list of variants (ARR-11). Every
/// operation is pure: it returns a new value and leaves the input alone, so each edit is one
/// undo step.
/// </summary>
public static class ArrangementEdit
{
    // ---- Entries ---------------------------------------------------------------------

    /// <summary>Adds an entry at the end (a palette click) or at <paramref name="index"/> (a drop).</summary>
    public static Arrangement Add(Arrangement arrangement, ArrangementEntry entry, int? index = null)
    {
        var entries = arrangement.Entries.ToList();
        var at = index is { } i ? Math.Clamp(i, 0, entries.Count) : entries.Count;
        entries.Insert(at, entry.Clamped());
        return arrangement with { Entries = entries };
    }

    /// <summary>Moves an entry so it ends up at <paramref name="newIndex"/> in the resulting list (drag to reorder).</summary>
    public static Arrangement Move(Arrangement arrangement, string entryId, int newIndex)
    {
        var entries = arrangement.Entries.ToList();
        var from = entries.FindIndex(e => e.Id == entryId);
        if (from < 0)
            return arrangement;
        var entry = entries[from];
        entries.RemoveAt(from);
        entries.Insert(Math.Clamp(newIndex, 0, entries.Count), entry);
        return arrangement with { Entries = entries };
    }

    /// <summary>A copy with a new id right after the original, or at <paramref name="index"/> (Ctrl+D, Alt-drag).</summary>
    public static (Arrangement Arrangement, ArrangementEntry? Added) Duplicate(Arrangement arrangement, string entryId, int? index = null)
    {
        var from = arrangement.Entries.FindIndex(e => e.Id == entryId);
        if (from < 0)
            return (arrangement, null);
        var copy = arrangement.Entries[from] with { Id = ArrangementEntry.NewId() };
        return (Add(arrangement, copy, index ?? from + 1), copy);
    }

    public static Arrangement Remove(Arrangement arrangement, string entryId) =>
        arrangement with { Entries = arrangement.Entries.Where(e => e.Id != entryId).ToList() };

    /// <summary>Changes one entry; the result is clamped to the allowed values (ARR-30).</summary>
    public static Arrangement Update(Arrangement arrangement, string entryId, Func<ArrangementEntry, ArrangementEntry> change) =>
        arrangement with { Entries = arrangement.Entries.Select(e => e.Id == entryId ? change(e).Clamped() : e).ToList() };

    /// <summary>Replaces all entries (Apply of the quick order, ARR-60).</summary>
    public static Arrangement Replace(Arrangement arrangement, IEnumerable<ArrangementEntry> entries) =>
        arrangement with { Entries = entries.Select(e => e.Clamped()).ToList() };

    public static Arrangement Rename(Arrangement arrangement, string name) =>
        string.IsNullOrWhiteSpace(name) ? arrangement : arrangement with { Name = name.Trim() };

    // ---- Presets for mutes (ARR-34) --------------------------------------------------

    /// <summary>"Drums out".</summary>
    public static MuteSpec DrumsOut(bool firstPassOnly = false) => new() { Groups = [Library.TrackGroups.Drums], FirstPassOnly = firstPassOnly };

    /// <summary>"Band down": only keys, click and guide play.</summary>
    public static MuteSpec BandDown(bool firstPassOnly = false) => new()
    {
        Groups = Library.TrackGroups.Music.Where(g => g != Library.TrackGroups.Keys).ToList(),
        FirstPassOnly = firstPassOnly,
    };

    /// <summary>"Click &amp; guide only": every music group is muted.</summary>
    public static MuteSpec ClickAndGuideOnly(bool firstPassOnly = false) => new() { Groups = [.. Library.TrackGroups.Music], FirstPassOnly = firstPassOnly };

    // ---- Variants (ARR-11) -----------------------------------------------------------

    /// <summary>Every section in order with no options: the song as written (ARR-11 "from as written").</summary>
    public static Arrangement FromAsWritten(IEnumerable<SectionInfo> sections, string name, IEnumerable<string> existingNames) => new()
    {
        Id = Arrangement.NewId(),
        Name = UniqueName(name, existingNames),
        Entries = sections.OrderBy(s => s.StartSec).Select(s => ArrangementEntry.ForSection(s.Id)).ToList(),
        Origin = ArrangementOrigin.Manual,
    };

    public static Arrangement Empty(string name, IEnumerable<string> existingNames) => new()
    {
        Id = Arrangement.NewId(),
        Name = UniqueName(name, existingNames),
        Origin = ArrangementOrigin.Manual,
    };

    /// <summary>A copy of a variant under a new name, with new entry ids.</summary>
    public static Arrangement Copy(Arrangement source, string name, IEnumerable<string> existingNames) => new()
    {
        Id = Arrangement.NewId(),
        Name = UniqueName(name, existingNames),
        Entries = source.Entries.Select(e => e with { Id = ArrangementEntry.NewId() }).ToList(),
        Origin = ArrangementOrigin.Copy,
    };

    /// <summary>"Short", then "Short 2", "Short 3" when the name is taken (case-insensitive).</summary>
    public static string UniqueName(string name, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var baseName = string.IsNullOrWhiteSpace(name) ? "Variant" : name.Trim();
        if (!taken.Contains(baseName))
            return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    public static List<Arrangement> ReplaceVariant(IEnumerable<Arrangement> variants, Arrangement changed) =>
        variants.Select(v => v.Id == changed.Id ? changed : v).ToList();

    // ---- Sections changed (SEC-17, SEC-54) --------------------------------------------

    /// <summary>
    /// Merging two sections removes one: entries that played it now play the merged section (SEC-54).
    /// </summary>
    public static List<Arrangement> RemapSection(IEnumerable<Arrangement> variants, string fromSectionId, string toSectionId) =>
        variants.Select(v => v with
        {
            Entries = v.Entries.Select(e => e.SectionId == fromSectionId ? e with { SectionId = toSectionId } : e).ToList(),
        }).ToList();

    /// <summary>
    /// After Detect replaced sections (SEC-17): entries that pointed at a section that no longer
    /// exists follow it to the new section with the same label, or else the same type and
    /// occurrence. Entries that can't be mapped keep their dead id, so validation flags them.
    /// </summary>
    public static List<Arrangement> RemapAfterRedetect(IEnumerable<Arrangement> variants, IReadOnlyList<SectionInfo> before, IReadOnlyList<SectionInfo> after)
    {
        var alive = new HashSet<string>(after.Select(s => s.Id));
        var map = new Dictionary<string, string>();
        foreach (var old in before.Where(s => !alive.Contains(s.Id)))
        {
            var byLabel = after.FirstOrDefault(s => string.Equals(s.Label, old.Label, StringComparison.OrdinalIgnoreCase));
            if (byLabel is not null)
            {
                map[old.Id] = byLabel.Id;
                continue;
            }

            var occurrence = before.Where(s => s.Type == old.Type).ToList().FindIndex(s => s.Id == old.Id);
            var sameType = after.Where(s => s.Type == old.Type).ToList();
            if (occurrence >= 0 && occurrence < sameType.Count)
                map[old.Id] = sameType[occurrence].Id;
        }

        if (map.Count == 0)
            return variants.ToList();
        return variants.Select(v => v with
        {
            Entries = v.Entries.Select(e => e.SectionId is { } id && map.TryGetValue(id, out var to) ? e with { SectionId = to } : e).ToList(),
        }).ToList();
    }
}
