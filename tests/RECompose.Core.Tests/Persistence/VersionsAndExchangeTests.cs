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
}
