// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Services;

// The service check (REL-1, REL-2, REL-3, AT-40).
public class ServiceCheckTests
{
    private static Song MakeSong(string id, params string[] files) => new()
    {
        Id = id,
        Title = id.ToUpperInvariant(),
        Source = new SongSource { Path = "x" },
        OriginalKey = "C",
        Bpm = 100,
        DurationSec = 100,
        ClickAnalysis = new ClickAnalysis { TrackId = "click", Bpm = 100 },
        Tracks = [.. files.Select((f, i) => new Track { Id = $"t{i}", Name = $"Track {i}", File = f })],
        Sections = [Section.Create("Verse 1", 0, SectionOrigin.Manual), Section.Create("Chorus", 20, SectionOrigin.Manual)],
    };

    private static CheckContext Context(Service service, IReadOnlyList<Song> songs, Func<string, bool>? exists = null) => new()
    {
        Service = service,
        FindSong = id => songs.FirstOrDefault(s => s.Id == id),
        FileExists = exists ?? (_ => true),
        DeviceName = "Focusrite",
        OutputChannels = 4,
        MainChannel = 0,
        CueChannel = 2,
        FallbackDeviceName = "Speakers",
        FallbackPresent = true,
        FreeDiskBytes = 50L * 1024 * 1024 * 1024,
        OnBattery = false,
        Renders = (_, _) => new RenderNeed(0, 0),
    };

    private static Service ServiceOf(params ServiceEntry[] entries) => new() { Id = "s", Name = "Sunday", Entries = [.. entries] };

    private static ServiceEntry Entry(Song song) => ServiceEdit.NewEntry(song);

    private static CheckItem Item(CheckResult result, int number) => result.Items.Single(i => i.Number == number);

    [Fact]
    public void A_service_with_everything_in_place_is_ready()
    {
        var song = MakeSong("a", "a1.wav", "a2.wav");
        var result = ServiceCheck.Run(Context(ServiceOf(Entry(song)), [song]));

        Assert.Equal(CheckOverall.Ready, result.Overall);
        Assert.Equal("✓ Ready", result.Text);
        Assert.All(result.Items.Where(i => i.Number is 1 or 2 or 3 or 4 or 5 or 6 or 8 or 11 or 12), i => Assert.Equal(CheckStatus.Ok, i.Status));
        Assert.Equal(CheckStatus.Info, Item(result, 7).Status);
    }

    [Fact]
    public void AT40_a_missing_stem_is_an_error_with_relink_and_the_service_is_not_ready()
    {
        var song = MakeSong("a", "a1.wav", "a2.wav");
        var entry = Entry(song);
        var result = ServiceCheck.Run(Context(ServiceOf(entry), [song], file => file != "a2.wav"));

        var item = Item(result, 1);
        Assert.Equal(CheckStatus.Error, item.Status);
        Assert.Equal(CheckFix.Relink, item.Fix);
        Assert.Contains("a2.wav", item.Detail);
        Assert.Equal(entry.Id, item.EntryId);
        Assert.Equal("a", item.SongId);
        Assert.Equal(CheckOverall.NotReady, result.Overall);
        Assert.Equal("✗ Not ready", result.Text);
        Assert.Equal("notReady", result.Code);
    }

    [Fact]
    public void A_song_missing_from_the_library_is_an_error()
    {
        var song = MakeSong("a", "a1.wav");
        var result = ServiceCheck.Run(Context(ServiceOf(Entry(song)), []));
        Assert.Equal(CheckStatus.Error, Item(result, 1).Status);
    }

    [Fact]
    public void An_arrangement_with_a_removed_section_is_an_error_and_a_hold_at_the_end_a_warning()
    {
        var song = MakeSong("a", "a1.wav");
        var gone = new Arrangement { Id = "v", Name = "Broken", Entries = [ArrangementEntry.ForSection("no-such-section")] };
        var broken = Entry(song) with { Arrangement = ArrangementChoice.OfCustom(gone) };
        var result = ServiceCheck.Run(Context(ServiceOf(broken), [song]));
        Assert.Equal(CheckStatus.Error, Item(result, 2).Status);
        Assert.Equal(CheckFix.OpenEntry, Item(result, 2).Fix);

        var holdLast = new Arrangement { Id = "h", Name = "Hold", Entries = [ArrangementEntry.ForSection(song.Sections[0].Id), ArrangementEntry.NewHold()] };
        var warned = Entry(song) with { Arrangement = ArrangementChoice.OfCustom(holdLast) };
        result = ServiceCheck.Run(Context(ServiceOf(warned), [song]));
        Assert.Equal(CheckStatus.Warning, Item(result, 2).Status);
        Assert.Equal(CheckOverall.ReadyWithWarnings, result.Overall);
    }

    [Fact]
    public void Analysis_renders_and_the_fallback_device_warn_but_do_not_block()
    {
        var song = MakeSong("a", "a1.wav") with { ClickAnalysis = null };
        var entry = Entry(song) with { KeyOffset = 2 };
        var context = Context(ServiceOf(entry), [song]) with
        {
            Renders = (_, _) => new RenderNeed(4, 1),
            FallbackDeviceName = null,
        };
        var result = ServiceCheck.Run(context);

        Assert.Equal(CheckStatus.Warning, Item(result, 3).Status);
        Assert.Equal(CheckFix.AnalyseNow, Item(result, 3).Fix);
        Assert.Equal(CheckStatus.Warning, Item(result, 4).Status);
        Assert.Contains("1 of 4", Item(result, 4).Detail);
        Assert.Equal(CheckFix.RenderNow, Item(result, 4).Fix);
        Assert.Equal(CheckStatus.Warning, Item(result, 6).Status);
        Assert.Equal(CheckFix.ChooseFallback, Item(result, 6).Fix);
        Assert.Equal(CheckOverall.ReadyWithWarnings, result.Overall);

        // The renders finish: item 4 turns green.
        result = ServiceCheck.Run(context with { Renders = (_, _) => new RenderNeed(4, 4) });
        Assert.Equal(CheckStatus.Ok, Item(result, 4).Status);
    }

    [Fact]
    public void The_device_the_outputs_the_disk_the_battery_and_a_controller_are_checked()
    {
        var song = MakeSong("a", "a1.wav");
        var service = ServiceOf(Entry(song));

        Assert.Equal(CheckStatus.Error, Item(ServiceCheck.Run(Context(service, [song]) with { DeviceName = null }), 5).Status);
        Assert.Equal(CheckStatus.Warning, Item(ServiceCheck.Run(Context(service, [song]) with { OutputChannels = 2 }), 5).Status);        // Cue is on 3–4
        Assert.Equal(CheckStatus.Warning, Item(ServiceCheck.Run(Context(service, [song]) with { FreeDiskBytes = 500L * 1024 * 1024 }), 11).Status);
        var battery = Item(ServiceCheck.Run(Context(service, [song]) with { OnBattery = true, BatteryPercent = 15 }), 12);
        Assert.Equal(CheckStatus.Warning, battery.Status);
        Assert.Contains("15 %", battery.Detail);
        Assert.Equal(CheckStatus.Warning, Item(ServiceCheck.Run(Context(service, [song]) with { HasMidiBindings = true, MidiInputPresent = false }), 8).Status);
        Assert.Equal(CheckStatus.Ok, Item(ServiceCheck.Run(Context(service, [song]) with { HasMidiBindings = true, MidiInputPresent = true }), 8).Status);
    }

    [Fact]
    public void REL3_the_quick_check_in_Perform_runs_items_1_5_7_8_and_9_only()
    {
        var song = MakeSong("a", "a1.wav");
        var result = ServiceCheck.Run(Context(ServiceOf(Entry(song)), [song]), quick: true);
        Assert.Equal([1, 5, 7, 8, 9], result.Items.Select(i => i.Number));
    }

    [Fact]
    public void REL2_the_signature_changes_when_the_service_or_a_stem_changes()
    {
        var song = MakeSong("a", "a1.wav");
        var service = ServiceOf(Entry(song));
        Func<string, Song?> find = id => id == "a" ? song : null;
        var before = ServiceCheck.Signature(service, find);

        Assert.Equal(before, ServiceCheck.Signature(service, find));
        Assert.NotEqual(before, ServiceCheck.Signature(service with { Entries = [service.Entries[0] with { KeyOffset = 2 }] }, find));
        var moved = song with { Tracks = [song.Tracks[0] with { File = "other.wav" }] };
        Assert.NotEqual(before, ServiceCheck.Signature(service, id => id == "a" ? moved : null));
    }
}
