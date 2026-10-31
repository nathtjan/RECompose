// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Core.Services;

namespace RECompose.Core.Tests.Persistence;

// Version history, export and import (DM-90, REL-45).
public sealed class VersionsAndExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-versions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static Song MakeSong(string id, string title, string file, string? hash = null) => new()
    {
        Id = id,
        Title = title,
        Source = new SongSource { Path = "x" },
        OriginalKey = "C",
        Bpm = 100,
        DurationSec = 100,
        AudioHash = hash,
        Tracks = [new Track { Id = "t0", Name = "Keys", File = file }],
        Sections = [Section.Create("Verse 1", 0, SectionOrigin.Manual)],
    };

    [Fact]
    public void DM90_a_save_keeps_the_previous_file_at_most_every_five_minutes_and_twenty_in_all()
    {
        var store = new DataStore(_root);
        var now = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        store.Now = () => now;
        var service = new Service { Id = "s", Name = "Sunday" };

        store.SaveService(service);                          // new: nothing to keep
        Assert.Empty(store.ListVersions("s"));
        store.SaveService(service with { Name = "Sunday 2" });
        Assert.Single(store.ListVersions("s"));
        now = now.AddMinutes(2);
        store.SaveService(service with { Name = "Sunday 3" }); // within five minutes of the last version
        Assert.Single(store.ListVersions("s"));

        now = now.AddMinutes(4);
        store.SaveService(service with { Name = "Sunday 4" });
        var versions = store.ListVersions("s");
        Assert.Equal(2, versions.Count);
        Assert.Equal("Sunday 3", store.LoadServiceVersion(versions[0])!.Name); // newest first: the file as it was
        Assert.Equal("Sunday", store.LoadServiceVersion(versions[1])!.Name);

        for (var i = 0; i < 30; i++)
        {
            now = now.AddMinutes(6);
            store.SaveService(service with { Name = $"Sunday {i + 5}" });
        }
        Assert.Equal(DataStore.VersionsKept, store.ListVersions("s").Count);
    }

    [Fact]
    public void REL45_export_and_import_bring_the_entries_back_and_list_missing_stems()
    {
        var champion = MakeSong("a", "Champion", "C:/stems/champion/keys.wav", "hash-a");
        var goodness = MakeSong("b", "Goodness", "C:/stems/goodness/keys.wav");
        var service = new Service
        {
            Id = "s",
            Name = "Sunday 26 Oct",
            Entries = [ServiceEdit.NewEntry(champion) with { KeyOffset = 2 }, ServiceEdit.NewEntry(goodness)],
        };

        using var file = new MemoryStream();
        ServiceExchange.Export(service, [champion, goodness, MakeSong("c", "Other", "x.wav")], file);

        // Another computer: Champion is in its library under another id (found by its audio), Goodness is not.
        file.Position = 0;
        var local = MakeSong("local-a", "Champion", "D:/music/keys.wav", "hash-a");
        var result = ServiceExchange.Import(file, [local], f => f.StartsWith("D:", StringComparison.Ordinal), () => "new-id");

        Assert.Equal("new-id", result.Service.Id);
        Assert.Equal("Sunday 26 Oct", result.Service.Name);
        Assert.Equal(["local-a", "b"], result.Service.Entries.Select(e => e.SongId));
        Assert.Equal(2, result.Service.Entries[0].KeyOffset);
        Assert.Equal(["b"], result.NewSongs.Select(s => s.Id));
        Assert.Equal(["Goodness: keys.wav"], result.Missing);
    }

    [Fact]
    public void Importing_something_else_says_so()
    {
        using var junk = new MemoryStream("not a zip"u8.ToArray());
        Assert.ThrowsAny<InvalidDataException>(() => ServiceExchange.Import(junk, [], _ => true, () => "x"));
    }

    [Theory]
    [InlineData("../../x", "t0")]
    [InlineData("..", "t0")]
    [InlineData("a", "../t")]
    [InlineData("a", "")]
    public void REL45_ids_that_would_leave_the_data_folder_are_refused(string songId, string trackId)
    {
        var song = MakeSong(songId, "Champion", "C:/stems/keys.wav") with { Tracks = [new Track { Id = trackId, Name = "Keys", File = "C:/stems/keys.wav" }] };
        using var file = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(song)] }, [song], file);

        file.Position = 0;
        Assert.Throws<InvalidDataException>(() => ServiceExchange.Import(file, [], _ => true, () => "x"));
    }

    private string Stem(string name, byte fill)
    {
        var path = Path.Combine(_root, "stems", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 5000).ToArray());
        return path;
    }

    private static Stream? Open(string path) => File.Exists(path) ? File.OpenRead(path) : null;

    [Fact]
    public void With_audio_another_computer_gets_the_stems_and_the_exact_service()
    {
        var keys = Stem("keys.WAV", 1);
        var champion = MakeSong("a", "Champion", keys) with
        {
            Tracks = [new Track { Id = "t0", Name = "Keys", File = keys }, new Track { Id = "t1", Name = "Bass", File = Stem("bass.flac", 2), Hidden = true }],
        };
        var service = new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(champion) with { KeyOffset = 3 }] };
        using var file = new MemoryStream();
        Assert.Empty(ServiceExchange.Export(service, [champion], file, Open));

        // The other computer has nothing (this one's stems folder isn't there): the song comes in, its stems are copied out
        // of the file, hidden ones too.
        file.Position = 0;
        var media = Path.Combine(_root, "other", "media");
        var stems = Path.Combine(_root, "stems");
        var result = ServiceExchange.Import(file, [], f => File.Exists(f) && !f.StartsWith(stems, StringComparison.Ordinal), () => "new", media);

        Assert.True(result.HadAudio);
        Assert.Empty(result.Missing);
        var song = Assert.Single(result.NewSongs);
        Assert.Equal([Path.Combine(media, "a", "t0.wav"), Path.Combine(media, "a", "t1.flac")], song.Tracks.Select(t => t.File));
        Assert.Equal(File.ReadAllBytes(keys), File.ReadAllBytes(song.Tracks[0].File));
        Assert.Equal(3, result.Service.Entries[0].KeyOffset);
    }

    [Fact]
    public void With_audio_a_library_song_takes_only_the_stems_it_is_missing()
    {
        var keys = Stem("keys.wav", 1);
        var bass = Stem("bass.wav", 2);
        var exported = MakeSong("a", "Champion", keys, "hash-a") with
        {
            Tracks = [new Track { Id = "t0", Name = "Keys", File = keys }, new Track { Id = "t1", Name = "Bass", File = bass }],
        };
        using var file = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(exported)] }, [exported], file, Open);

        // The library has Champion under its own id (same audio): its Keys is on this computer, its Bass is not.
        var localKeys = Stem("mine/keys.wav", 9);
        var local = MakeSong("local", "Champion", localKeys, exported.AudioHash) with
        {
            Tracks = [new Track { Id = "k", Name = "Keys", File = localKeys }, new Track { Id = "b", Name = "Bass", File = "D:/gone/bass.wav" }],
        };
        file.Position = 0;
        var media = Path.Combine(_root, "media");
        var result = ServiceExchange.Import(file, [local], File.Exists, () => "new", media);

        Assert.Empty(result.NewSongs);
        var relinked = Assert.Single(result.UpdatedSongs);
        Assert.Equal([localKeys, Path.Combine(media, "local", "b.wav")], relinked.Tracks.Select(t => t.File));
        Assert.Equal(File.ReadAllBytes(bass), File.ReadAllBytes(relinked.Tracks[1].File));
        Assert.Equal(["local"], result.Service.Entries.Select(e => e.SongId));
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Without_audio_nothing_is_copied_and_a_stem_not_found_at_export_is_named()
    {
        var song = MakeSong("a", "Champion", Path.Combine(_root, "nowhere.wav"));
        var service = new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(song)] };
        using (var withAudio = new MemoryStream())
            Assert.Equal(["Champion: nowhere.wav"], ServiceExchange.Export(service, [song], withAudio, Open));

        using var file = new MemoryStream();
        ServiceExchange.Export(service, [song], file);
        file.Position = 0;
        var media = Path.Combine(_root, "media");
        var result = ServiceExchange.Import(file, [], File.Exists, () => "new", media);

        Assert.False(result.HadAudio);
        Assert.False(Directory.Exists(media));
        Assert.Equal(["Champion: nowhere.wav"], result.Missing);
    }

    // The same song (same audio) on both computers, edited on the other one: another variant and section names.
    private static (Song Exported, Song Local, MemoryStream File) EditedElsewhere()
    {
        var local = MakeSong("local", "Champion", "C:/stems/keys.wav", "hash-a");
        var exported = local with
        {
            Id = "theirs",
            Sections = [.. local.Sections.Select(s => s with { Name = "Verse" })],
            Arrangements = [new RECompose.Core.Arrangements.Arrangement { Id = "v2", Name = "Short" }],
        };
        var file = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(exported) with { Arrangement = ArrangementChoice.OfVariant("v2") }] }, [exported], file);
        file.Position = 0;
        return (exported, local, file);
    }

    [Fact]
    public void A_song_with_the_same_audio_but_other_contents_is_a_conflict_that_names_what_differs()
    {
        var (_, local, file) = EditedElsewhere();
        var conflict = Assert.Single(ServiceExchange.Conflicts(file, [local]));
        Assert.Equal(("theirs", "local"), (conflict.Incoming.Id, conflict.Library.Id));
        Assert.Equal(["sections", "variants"], conflict.Differences);

        // The same contents on both sides (where the stems are and when it was saved don't count) is no conflict.
        file.Position = 0;
        var same = local with { Id = "x", Tracks = [.. local.Tracks.Select(t => t with { File = "E:/elsewhere.wav" })], UpdatedAt = DateTimeOffset.UnixEpoch };
        using var sameFile = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(same)] }, [same], sameFile);
        sameFile.Position = 0;
        Assert.Empty(ServiceExchange.Conflicts(sameFile, [local]));
    }

    [Fact]
    public void A_conflict_keeps_the_library_song_replaces_it_or_adds_a_copy_as_chosen()
    {
        var (_, local, file) = EditedElsewhere();
        var keep = ServiceExchange.Import(file, [local], _ => true, () => "new");
        Assert.Equal(["local"], keep.Service.Entries.Select(e => e.SongId));
        Assert.Equal((0, 0), (keep.NewSongs.Count, keep.UpdatedSongs.Count));

        file.Position = 0;
        var replace = ServiceExchange.Import(file, [local], _ => true, () => "new", choices: new Dictionary<string, SongChoice> { ["theirs"] = SongChoice.UseFile });
        var replaced = Assert.Single(replace.UpdatedSongs);
        Assert.Equal(("local", "Verse", "v2"), (replaced.Id, replaced.Sections[0].Name, replaced.Arrangements.Single().Id)); // under the library's id
        Assert.Equal(["local"], replace.Service.Entries.Select(e => e.SongId));
        Assert.Empty(replace.NewSongs);

        file.Position = 0;
        var ids = new Queue<string>(["copy", "service"]);
        var both = ServiceExchange.Import(file, [local], _ => true, ids.Dequeue, choices: new Dictionary<string, SongChoice> { ["theirs"] = SongChoice.KeepBoth });
        var copy = Assert.Single(both.NewSongs);
        Assert.Equal(("copy", "Champion (imported)", "v2"), (copy.Id, copy.Title, copy.Arrangements.Single().Id));
        Assert.Equal(["copy"], both.Service.Entries.Select(e => e.SongId));
        Assert.Empty(both.UpdatedSongs);
    }

    [Theory]
    [InlineData("Sunday", new string[0], "Sunday")]
    [InlineData("Sunday", new[] { "Sunday" }, "Sunday (2)")]
    [InlineData("Sunday", new[] { "sunday", "Sunday (2)", "SUNDAY (3)" }, "Sunday (4)")]
    [InlineData("Sunday", new[] { "Sunday", "Sunday (3)" }, "Sunday (2)")] // the first free number
    [InlineData("Sunday (2)", new[] { "Sunday", "Sunday (2)" }, "Sunday (3)")] // never "Sunday (2) (2)"
    [InlineData("Sunday (2)", new[] { "Sunday" }, "Sunday (2)")]
    [InlineData("Psalm (23)", new[] { "Psalm (23)" }, "Psalm (23) (2)")] // a title, not a copy number
    [InlineData("Psalm (23)", new[] { "Psalm", "Psalm (23)" }, "Psalm (2)")]
    [InlineData("  Sunday ", new[] { "Sunday" }, "Sunday (2)")]
    public void An_imported_name_is_made_unique_however_many_copies_there_are(string wanted, string[] taken, string unique) =>
        Assert.Equal(unique, ServiceExchange.UniqueName(wanted, taken));

    [Fact]
    public void Keeping_both_again_and_again_gives_each_copy_its_own_title()
    {
        var (_, local, file) = EditedElsewhere();
        var keepBoth = new Dictionary<string, SongChoice> { ["theirs"] = SongChoice.KeepBoth };
        var library = new List<Song> { local };
        var titles = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            file.Position = 0;
            var n = i;
            var copy = Assert.Single(ServiceExchange.Import(file, library, _ => true, () => $"id{n}-{Guid.NewGuid():N}", choices: keepBoth).NewSongs);
            library.Add(copy);
            titles.Add(copy.Title);
        }

        Assert.Equal(["Champion (imported)", "Champion (imported) (2)", "Champion (imported) (3)"], titles);

        // A file whose song was itself an imported copy doesn't stack the word.
        var reimported = local with { Id = "r", Title = "Champion (imported) (2)", Notes = "other" };
        using var again = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(reimported)] }, [reimported], again);
        again.Position = 0;
        var title = Assert.Single(ServiceExchange.Import(again, library, _ => true, () => "x", choices: new Dictionary<string, SongChoice> { ["r"] = SongChoice.KeepBoth }).NewSongs).Title;
        Assert.Equal("Champion (imported) (4)", title);
    }

    [Fact]
    public void A_song_is_matched_by_its_audio_never_by_its_title()
    {
        var mine = MakeSong("mine", "Amazing Grace", "C:/a.wav", "hash-mine");
        var theirs = MakeSong("theirs", "Amazing Grace", "C:/b.wav", "hash-theirs");
        using var file = new MemoryStream();
        ServiceExchange.Export(new Service { Id = "s", Name = "Sunday", Entries = [ServiceEdit.NewEntry(theirs)] }, [theirs], file);
        file.Position = 0;

        var result = ServiceExchange.Import(file, [mine], _ => true, () => "new");
        Assert.Equal(["theirs"], result.NewSongs.Select(s => s.Id));
        Assert.Equal(["theirs"], result.Service.Entries.Select(e => e.SongId));
    }
}
