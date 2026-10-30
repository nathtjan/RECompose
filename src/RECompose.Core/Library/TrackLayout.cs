// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Import;

namespace RECompose.Core.Library;

/// <summary>
/// Changes to a song's track list: add, remove, move. The list order is the lane and strip order, so a manual move
/// stays; the default order (<see cref="ImportRules.InLaneOrder"/>) only applies at import.
/// </summary>
public static class TrackLayout
{
    /// <summary>The track goes after the last one in its lane group, or before the first later group.</summary>
    public static List<Track> Add(IEnumerable<Track> tracks, Track added)
    {
        var list = tracks.ToList();
        var rank = ImportRules.LaneRank(added);
        var at = list.FindLastIndex(t => ImportRules.LaneRank(t) <= rank) + 1;
        list.Insert(at, added);
        return Renumbered(list);
    }

    public static List<Track> Remove(IEnumerable<Track> tracks, string id) =>
        Renumbered(tracks.Where(t => t.Id != id).ToList());

    /// <summary>Moves a track by <paramref name="delta"/> places (-1 up or left, +1 down or right), stopping at the ends.</summary>
    public static List<Track> Move(IEnumerable<Track> tracks, string id, int delta)
    {
        var list = tracks.ToList();
        var from = list.FindIndex(t => t.Id == id);
        return from < 0 ? Renumbered(list) : MoveTo(list, id, from + delta);
    }

    /// <summary>Puts a track at a 0-based place in the list (a drop), clamped to the ends.</summary>
    public static List<Track> MoveTo(IEnumerable<Track> tracks, string id, int index)
    {
        var list = tracks.ToList();
        var from = list.FindIndex(t => t.Id == id);
        if (from < 0)
            return Renumbered(list);

        var track = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(index, 0, list.Count), track);
        return Renumbered(list);
    }

    /// <summary>The tracks in the order of <paramref name="ids"/>; tracks not named keep their order after them, unknown ids are skipped.</summary>
    public static List<Track> InOrder(IEnumerable<Track> tracks, IReadOnlyList<string> ids)
    {
        var list = tracks.ToList();
        var named = ids.Distinct().Select(id => list.Find(t => t.Id == id)).OfType<Track>();
        return Renumbered(named.Concat(list.Where(t => !ids.Contains(t.Id))).ToList());
    }

    /// <summary>The song lasts until its last track ends, counting each track's offset.</summary>
    public static double DurationSec(IEnumerable<Track> tracks) => tracks.Select(t => t.EndSec).DefaultIfEmpty(0).Max();

    private static List<Track> Renumbered(List<Track> list) => list.Select((t, i) => t with { Order = i }).ToList();
}
