// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Rendering;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Rendering;

// The render cache (REL-10, REL-11, REL-15, REL-16).
public class RenderCacheTests
{
    private static Song MakeSong() => new()
    {
        Id = "a",
        Title = "Champion",
        Source = new SongSource { Path = "x" },
        OriginalKey = "C",
        Bpm = 100,
        DurationSec = 100,
        ClickAnalysis = new ClickAnalysis { TrackId = "t2", Bpm = 100 },
        Tracks =
        [
            new Track { Id = "t0", Name = "Keys", File = "keys.wav" },
            new Track { Id = "t1", Name = "Bass", File = "bass.wav" },
            new Track { Id = "t2", Name = "Click", File = "click.wav", Role = TrackRole.Click },
        ],
        Sections = [Section.Create("Verse 1", 0, SectionOrigin.Manual), Section.Create("Chorus", 20, SectionOrigin.Manual)],
    };

    private static List<RenderJob> Jobs(ServiceEntry entry, Song song, Func<string, string?>? print = null) =>
        RenderPlanner.Jobs(song, EntryPlan.Build(entry, song), t => !t.IsClickOrGuide, print ?? (f => "fp-" + f));

    [Fact]
    public void An_entry_at_the_written_key_and_tempo_needs_no_renders()
    {
        var song = MakeSong();
        Assert.Empty(Jobs(ServiceEdit.NewEntry(song), song));
    }

    [Fact]
    public void REL11_a_key_change_renders_the_stems_that_transpose_and_a_tempo_change_renders_every_stem()
    {
        var song = MakeSong();
        var keyed = Jobs(ServiceEdit.NewEntry(song) with { KeyOffset = 2 }, song);
        Assert.Equal(["t0", "t1"], keyed.Select(j => j.TrackId));
        Assert.All(keyed, j => Assert.Equal((2, 1.0), (j.Semitones, j.Tempo)));

        var slower = Jobs(ServiceEdit.NewEntry(song) with { TempoPercent = 90 }, song);
        Assert.Equal(["t0", "t1", "t2"], slower.Select(j => j.TrackId)); // the click and guide follow the tempo too
        Assert.All(slower, j => Assert.Equal((0, 0.9), (j.Semitones, j.Tempo)));
    }

    [Fact]
    public void A_missing_stem_is_not_rendered()
    {
        var song = MakeSong();
        var jobs = Jobs(ServiceEdit.NewEntry(song) with { KeyOffset = -1 }, song, f => f == "bass.wav" ? null : "fp");
        Assert.Equal(["t0"], jobs.Select(j => j.TrackId));
    }

    [Fact]
    public void REL16_a_changed_source_is_a_miss()
    {
        var before = RenderKeys.Fingerprint("keys.wav", 1000, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var touched = RenderKeys.Fingerprint("keys.wav", 1000, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        var resized = RenderKeys.Fingerprint("keys.wav", 1001, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(before, touched);
        Assert.NotEqual(before, resized);

        var index = new RenderIndex().With(new RenderRecord { Fingerprint = before, Semitones = 2, Tempo = 1, File = "x.flac", Bytes = 10 });
        Assert.NotNull(index.Find(before, 2, 1));
        Assert.Null(index.Find(touched, 2, 1));
        Assert.Null(index.Find(before, 3, 1));
        Assert.Null(index.Find(before, 2, 0.95));
    }

    [Fact]
    public void File_names_carry_the_key_and_the_tempo()
    {
        Assert.Equal("abc_+2_1000.flac", RenderKeys.FileName("abc", 2, 1));
        Assert.Equal("abc_-3_0950.flac", RenderKeys.FileName("abc", -3, 0.95));
        Assert.Equal("abc_0_0800.flac", RenderKeys.FileName("abc", 0, 0.8));
    }

    [Fact]
    public void The_check_counts_renders_done_and_failed()
    {
        var song = MakeSong();
        var jobs = Jobs(ServiceEdit.NewEntry(song) with { KeyOffset = 2 }, song);
        var index = new RenderIndex()
            .With(new RenderRecord { Fingerprint = jobs[0].Fingerprint, Semitones = 2, Tempo = 1, File = jobs[0].FileName })
            .WithFailure(new RenderFailure(jobs[1].Fingerprint, 2, 1, "Can't read this file"));
        Assert.Equal(new RenderNeed(2, 1, 1), RenderPlanner.Need(jobs, index));

        // A success clears the failure.
        index = index.With(new RenderRecord { Fingerprint = jobs[1].Fingerprint, Semitones = 2, Tempo = 1, File = jobs[1].FileName });
        Assert.Equal(new RenderNeed(2, 2, 0), RenderPlanner.Need(jobs, index));
    }

    [Fact]
    public void REL15_eviction_drops_the_least_recently_used_and_keeps_the_next_seven_days()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        RenderRecord R(string file, int day) => new() { Fingerprint = file, File = file, Bytes = 100, LastUsedAt = t0.AddDays(day) };
        var index = new RenderIndex { Renders = [R("old.flac", 0), R("sunday.flac", 1), R("mid.flac", 2), R("new.flac", 3)] };

        var (kept, evicted) = index.Evict(250, new HashSet<string> { "sunday.flac" });
        Assert.Equal(["old.flac", "mid.flac"], evicted.Select(r => r.File));
        Assert.Equal(["sunday.flac", "new.flac"], kept.Renders.Select(r => r.File));

        // Over budget with everything protected: nothing goes.
        Assert.Empty(index.Evict(0, index.Renders.Select(r => r.File).ToHashSet()).Evicted);

        var now = new DateTime(2026, 10, 26, 8, 0, 0);
        Assert.True(RenderPlanner.IsProtected(new Service { Id = "s", Name = "Sunday", Date = now.AddDays(7) }, now));
        Assert.False(RenderPlanner.IsProtected(new Service { Id = "s", Name = "Sunday", Date = now.AddDays(8) }, now));
        Assert.False(RenderPlanner.IsProtected(new Service { Id = "s", Name = "Sunday", Date = now.AddDays(-1) }, now));
    }
}
