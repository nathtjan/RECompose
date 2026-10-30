// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;

namespace RECompose.Core.Tests.Library;

// Adding, removing and moving tracks, and the song length with track offsets.
public class TrackLayoutTests
{
    private static Track T(string id, string group = TrackGroups.Other, TrackRole role = TrackRole.Music, double offset = 0, long frames = 48000) =>
        new() { Id = id, Name = id, File = id + ".wav", Group = group, Role = role, SampleRate = 48000, Frames = frames, OffsetSec = offset };

    private static string Ids(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Id));

    [Fact]
    public void A_new_track_goes_after_the_last_track_of_its_group()
    {
        var tracks = new[] { T("click", TrackGroups.Click, TrackRole.Click), T("d", TrackGroups.Drums), T("b", TrackGroups.Bass), T("o", TrackGroups.Other) };

        var added = TrackLayout.Add(tracks, T("d2", TrackGroups.Drums));

        Assert.Equal("click,d,d2,b,o", Ids(added));
        Assert.Equal([0, 1, 2, 3, 4], added.Select(t => t.Order));
    }

    [Fact]
    public void A_new_track_in_an_empty_group_goes_before_the_later_groups()
    {
        var tracks = new[] { T("d", TrackGroups.Drums), T("o", TrackGroups.Other) };

        Assert.Equal("d,k,o", Ids(TrackLayout.Add(tracks, T("k", TrackGroups.Keys))));
        Assert.Equal("c,d,o", Ids(TrackLayout.Add(tracks, T("c", TrackGroups.Click, TrackRole.Click))));
    }

    [Fact]
    public void Move_shifts_a_track_and_stops_at_the_ends()
    {
        var tracks = new[] { T("a"), T("b"), T("c") };

        Assert.Equal("b,a,c", Ids(TrackLayout.Move(tracks, "a", 1)));
        Assert.Equal("a,c,b", Ids(TrackLayout.Move(tracks, "c", -1)));
        Assert.Equal("a,b,c", Ids(TrackLayout.Move(tracks, "a", -1)));
        Assert.Equal("a,b,c", Ids(TrackLayout.Move(tracks, "c", 1)));
        Assert.Equal("a,b,c", Ids(TrackLayout.Move(tracks, "nope", 1)));
    }

    [Fact]
    public void MoveTo_drops_a_track_at_a_place_and_clamps()
    {
        var tracks = new[] { T("a"), T("b"), T("c"), T("d") };

        Assert.Equal("b,c,a,d", Ids(TrackLayout.MoveTo(tracks, "a", 2)));
        Assert.Equal("d,a,b,c", Ids(TrackLayout.MoveTo(tracks, "d", 0)));
        Assert.Equal("b,c,d,a", Ids(TrackLayout.MoveTo(tracks, "a", 99)));
        Assert.Equal("a,b,c,d", Ids(TrackLayout.MoveTo(tracks, "x", 1)));
    }

    [Fact]
    public void InOrder_follows_the_ids_and_keeps_unnamed_tracks_after_them()
    {
        var tracks = new[] { T("a"), T("b"), T("c") };

        Assert.Equal("c,a,b", Ids(TrackLayout.InOrder(tracks, ["c", "a", "b"])));
        Assert.Equal("c,a,b", Ids(TrackLayout.InOrder(tracks, ["c", "gone"])));
        Assert.Equal([0, 1, 2], TrackLayout.InOrder(tracks, ["b"]).Select(t => t.Order));
    }

    [Fact]
    public void Remove_drops_the_track_and_renumbers()
    {
        var left = TrackLayout.Remove([T("a"), T("b"), T("c")], "b");

        Assert.Equal("a,c", Ids(left));
        Assert.Equal([0, 1], left.Select(t => t.Order));
    }

    [Fact]
    public void The_song_lasts_until_the_last_track_ends_counting_offsets()
    {
        // 1 s files: one starts 2 s late, one skips its first 0.25 s.
        var tracks = new[] { T("a"), T("late", offset: 2), T("early", offset: -0.25) };

        Assert.Equal(3, TrackLayout.DurationSec(tracks), 6);
        Assert.Equal(0.75, T("early", offset: -0.25).EndSec, 6);
        Assert.Equal(0, TrackLayout.DurationSec([]));
        Assert.Equal(0, T("gone", offset: -5).EndSec);
    }
}
