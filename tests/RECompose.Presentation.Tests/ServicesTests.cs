// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// Services in the app (SL-1…12, ARR-11, ARR-12, UI-11…16, PB-60, PB-81). These run without the engine process: the
// view model keeps working with no audio, and what reaches the engine is covered by the engine-backed tests.
public sealed class ServicesTests : IDisposable
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-svc-vm-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceDialogs _dialogs = new();

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

    private DataStore Store => new(Path.Combine(_root, "data"));

    /// <summary>A view model whose engine can't start: everything but audio works.</summary>
    private async Task<MainViewModel> NewViewModelAsync()
    {
        var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
        var vm = new MainViewModel(Store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing), new LockedDispatcher(), _dialogs,
            new SpeechModel(Store.Root, allowDownload: false));
        await vm.InitializeAsync();
        return vm;
    }

    /// <summary>Fixture A ("Champion") in B♭ with its Demo variant, and a 4/4 song in D♭ without variants.</summary>
    private (Song Champion, Song Goodness) SaveSongs()
    {
        var sections = new (string Name, int Bar)[]
        {
            ("Intro", 1), ("Verse 1", 11), ("Chorus", 27), ("Vamp", 41), ("Verse 2", 49), ("Chorus", 65),
            ("Interlude", 79), ("Bridge", 87), ("Chorus", 103), ("Ending", 117),
        }.Select(s => Section.Create(s.Name, A.BarToTime(s.Bar), SectionOrigin.Guide)).ToList();
        var infos = SectionList.Describe(sections, A, 310.02);
        var demo = new Arrangement
        {
            Id = "demo",
            Name = "Demo",
            Entries =
            [
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Intro").Id),
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Chorus 1").Id) with { Repeat = new RepeatSpec { Count = 2 } },
            ],
        };
        Track T(string id, TrackRole role, string group) => new() { Id = id, Name = id, File = Path.Combine(_root, id + ".wav"), Role = role, Group = group };
        var champion = new Song
        {
            Id = "champion",
            Title = "Champion",
            Source = new SongSource { Path = _root },
            OriginalKey = "Bb",
            Bpm = 72,
            BeatsPerBar = 3,
            FirstDownbeatSec = 0.02,
            DurationSec = 310.02,
            Sections = sections,
            Arrangements = [demo],
            DefaultArrangementId = "demo",
            Tracks = [T("click", TrackRole.Click, TrackGroups.Click), T("drums", TrackRole.Music, TrackGroups.Drums), T("keys", TrackRole.Music, TrackGroups.Keys)],
            MixPresets = [new MixPreset { Id = "default", Name = "Default" }],
            DefaultMixPresetId = "default",
        };
        var g = new TempoGrid(68, 4, 0);
        var goodness = new Song
        {
            Id = "goodness",
            Title = "Goodness",
            Source = new SongSource { Path = _root },
            OriginalKey = "Db",
            Bpm = 68,
            BeatsPerBar = 4,
            DurationSec = 200,
            Sections = [Section.Create("Verse 1", 0, SectionOrigin.Guide), Section.Create("Chorus", g.BarToTime(17), SectionOrigin.Guide)],
            Tracks = [T("click", TrackRole.Click, TrackGroups.Click), T("bass", TrackRole.Music, TrackGroups.Bass)],
        };

        var store = Store;
        store.SaveSong(champion);
        store.SaveSong(goodness);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(champion), SongSummary.From(goodness)] });
        return (champion, goodness);
    }

    private async Task<MainViewModel> SundayAsync()
    {
        SaveSongs();
        var vm = await NewViewModelAsync();
        _dialogs.Details = new ServiceDetails("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0));
        await vm.NewServiceAsync();
        vm.AddSongToService("champion");
        vm.AddSongToService("goodness");
        return vm;
    }

    // ---- The service block (SL-1, SL-3, UI-11, UI-12, UI-80) ----------------------------------------------

    [Fact]
    public async Task UI80_without_a_service_the_sidebar_offers_to_create_one()
    {
        SaveSongs();
        var vm = await NewViewModelAsync();
        Assert.True(vm.Setlist.HasNoService);

        _dialogs.Details = new ServiceDetails("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0));
        await vm.NewServiceAsync();
        Assert.True(vm.Setlist.HasService);
        Assert.True(vm.Setlist.IsEmpty); // "Add songs from the library (drag them here)"
        Assert.Equal("Sunday 26 Oct · 9:00", vm.Setlist.Title);
        Assert.Equal("No songs", vm.Setlist.Summary);
        Assert.Single(vm.Setlist.Services);
        Assert.Single(Store.LoadServices());
    }

    [Fact]
    public async Task SL3_rows_show_title_key_variant_and_the_transition_chip()
    {
        var vm = await SundayAsync();
        vm.AddSongToService("champion"); // the same song twice: independent entries (SL-4)

        Assert.Equal(["Champion", "Goodness", "Champion"], vm.Setlist.Rows.Select(r => r.Title));
        Assert.Equal(["B♭", "D♭", "B♭"], vm.Setlist.Rows.Select(r => r.KeyText));
        Assert.Equal(["Demo", "As written", "Demo"], vm.Setlist.Rows.Select(r => r.VariantText));
        Assert.Equal([true, true, false], vm.Setlist.Rows.Select(r => r.HasChip));
        Assert.Equal("Stop", vm.Setlist.Rows[0].ChipText);
        Assert.Equal("38 bars · 1:40", vm.Setlist.Rows[0].LengthText);
        Assert.StartsWith("3 songs · ", vm.Setlist.Summary);
        Assert.NotEqual(vm.Setlist.Rows[0].Id, vm.Setlist.Rows[2].Id);
    }

    [Fact]
    public async Task UI16_a_selected_entry_is_edited_and_loading_it_plays_it()
    {
        var vm = await SundayAsync();
        var first = vm.Setlist.Rows[0];
        vm.Setlist.Selected = first;

        Assert.Equal("Editing: Champion · service entry 1", vm.Setlist.EditingText);
        Assert.True(vm.Setlist.IsSelectionNotLoaded);
        Assert.Null(vm.Song);

        await vm.LoadEntryAsync(first.Id);
        Assert.NotNull(vm.Song);
        Assert.Equal(first.Id, vm.LoadedEntry!.Id);
        Assert.True(vm.Setlist.IsSelectionLoaded);
        Assert.True(vm.Setlist.Rows[0].IsLoaded);
        Assert.DoesNotContain(vm.Songs, s => s.IsLoaded); // the library marks only a song loaded from the library
        Assert.Equal("B♭ · 72 BPM · 3/4 · Demo", vm.Song!.HeaderLine);
        Assert.Equal("Editing: Champion · service entry 1", vm.Entry.EditingText);

        // Selecting another entry leaves the loaded one playing.
        vm.Setlist.Selected = vm.Setlist.Rows[1];
        Assert.True(vm.Setlist.IsSelectionNotLoaded);
        Assert.Equal(first.Id, vm.LoadedEntry!.Id);
    }

    // ---- Key and tempo (PB-60…62, UI-13) ---------------------------------------------------------------------

    [Fact]
    public async Task PB60_key_and_tempo_change_the_entry_the_header_and_the_plan()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[0].Id;
        await vm.LoadEntryAsync(entryId);

        vm.KeyDownCommand.Execute(null);
        Assert.Equal(-1, vm.LoadedEntry!.KeyOffset);
        Assert.Equal("A (−1)", vm.EntryKeyText);
        Assert.Equal("A (−1) · 72 BPM · 3/4 · Demo", vm.Song!.HeaderLine);
        Assert.Equal("A", vm.Setlist.Rows[0].KeyText);
        Assert.All(vm.Song.Plan.Segments(), s => Assert.Equal(-1, s.Key));
        Assert.Equal(-1, Store.LoadService(vm.CurrentService!.Id)!.Entry(entryId)!.KeyOffset); // saved at once
        Assert.NotNull(vm.LiveKeyBadge);

        vm.SetTempo(entryId, 95);
        Assert.Equal("95%", vm.EntryTempoText);
        Assert.Equal("0:00 / −1:45", vm.TimeText); // 100 s of plan at 95 %
        Assert.Equal("bar 1.1 / 38", vm.SongBarText);

        vm.UndoCommand.Execute(null);
        Assert.Equal(100, vm.LoadedEntry!.TempoPercent);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.LoadedEntry!.KeyOffset);
        Assert.Null(vm.LiveKeyBadge);

        // PB-62: beyond ±5 the key shows the warning.
        vm.SetKeyOffset(entryId, 6);
        Assert.True(vm.EntryKeyWarning);
        vm.SetKeyOffset(entryId, 20);
        Assert.Equal(12, vm.LoadedEntry!.KeyOffset);
    }

    [Fact]
    public async Task SL36_a_generated_count_in_starts_the_plan_with_click_bars()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[1].Id;
        await vm.LoadEntryAsync(entryId);
        vm.Setlist.Selected = vm.Setlist.Rows[1];

        vm.Setlist.CountIn = CountInChoice.All.Single(c => c.Mode == CountInMode.Generated && c.Bars == 2);
        Assert.Equal(PlanItemKind.CountIn, vm.Song!.Plan.Items[0].Kind);
        Assert.Equal("COUNT-IN", vm.NowLabel);
        Assert.Equal("beat −8", vm.NowDetail);
        Assert.Equal(CountInMode.Generated, Store.LoadService(vm.CurrentService!.Id)!.Entry(entryId)!.CountIn.Mode);
    }

    // ---- The arrangement of an entry (ARR-12, AT-15, ARR-11) ---------------------------------------------------

    [Fact]
    public async Task AT15_editing_the_variant_for_this_service_only_makes_a_custom_arrangement()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[0].Id;
        await vm.LoadEntryAsync(entryId);
        vm.Entry.SelectById(vm.Entry.Rows[1].Id); // Chorus 1 ×2

        _dialogs.Scope = new VariantScopeAnswer(VariantScope.ThisService, Remember: false);
        vm.Entry.RepeatCount = 3;
        await Eventually(() => vm.Song!.IsCustom);

        Assert.Equal(1, _dialogs.ScopeQuestions);
        Assert.Equal("Custom for this service", vm.Setlist.Rows[0].VariantText);
        Assert.Equal(3, vm.Song!.Custom!.Entries[1].Repeat.Count);
        Assert.Equal(2, Store.LoadSong("champion")!.Arrangements[0].Entries[1].Repeat.Count); // the song's variant is unchanged
        Assert.Contains(vm.Entry.Variants, v => v.IsCustom);
        Assert.True(vm.Entry.SelectedVariant!.IsCustom);

        // Further edits stay in the custom arrangement and don't ask again.
        vm.Entry.SelectById(vm.Entry.Rows[0].Id);
        vm.Entry.FadeInBars = 1;
        Assert.Equal(1, _dialogs.ScopeQuestions);
        Assert.Equal(1, vm.Song.Custom!.Entries[0].FadeInBars);

        // Revert to "Demo" brings the variant back.
        vm.Setlist.Selected = vm.Setlist.Rows[0];
        Assert.Equal("Revert to “Demo”", vm.Setlist.RevertText);
        vm.Setlist.RevertCommand.Execute(null);
        Assert.False(vm.Song.IsCustom);
        Assert.Equal("Demo", vm.Setlist.Rows[0].VariantText);
    }

    [Fact]
    public async Task ARR12_editing_for_all_services_changes_the_variant_and_can_be_remembered()
    {
        var vm = await SundayAsync();
        vm.AddSongToService("champion");
        await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);
        vm.Entry.SelectById(vm.Entry.Rows[1].Id);

        _dialogs.Scope = new VariantScopeAnswer(VariantScope.AllServices, Remember: true);
        vm.Entry.RepeatCount = 4;
        await Eventually(() => Store.LoadSong("champion")!.Arrangements[0].Entries[1].Repeat.Count == 4);

        Assert.Equal(1, _dialogs.OtherEntriesAsked); // the other Champion entry plays Demo too
        Assert.False(vm.Song!.IsCustom);
        Assert.Equal("Demo", vm.Setlist.Rows[2].VariantText);

        // Remembered: no question in a new session either.
        var again = await NewViewModelAsync();
        await again.LoadEntryAsync(again.Setlist.Rows[0].Id);
        again.Entry.SelectById(again.Entry.Rows[1].Id);
        again.Entry.RepeatCount = 2;
        await Eventually(() => Store.LoadSong("champion")!.Arrangements[0].Entries[1].Repeat.Count == 2);
        Assert.Equal(1, _dialogs.ScopeQuestions);
    }

    [Fact]
    public async Task Editing_an_entry_that_plays_as_written_makes_a_custom_arrangement_not_a_variant()
    {
        var vm = await SundayAsync();
        await vm.LoadEntryAsync(vm.Setlist.Rows[1].Id); // Goodness, as written
        vm.Entry.AddClickCommand.Execute(null);

        await Eventually(() => vm.Song!.IsCustom);
        Assert.Empty(Store.LoadSong("goodness")!.Arrangements);
        Assert.Equal(EntryKind.Click, vm.Song!.Custom!.Entries[^1].Kind);
        Assert.Equal(0, _dialogs.ScopeQuestions);

        // Save as variant: the song gets it and the entry plays it.
        vm.Setlist.Selected = vm.Setlist.Rows[1];
        _dialogs.Name = "With click";
        await vm.SaveEntryAsVariantAsync(vm.Setlist.Rows[1].Id);
        Assert.Equal(["With click"], Store.LoadSong("goodness")!.Arrangements.Select(a => a.Name));
        Assert.Equal("With click", vm.Setlist.Rows[1].VariantText);
        Assert.False(vm.Song.IsCustom);
    }

    [Fact]
    public async Task ARR11_deleting_a_variant_that_entries_play_asks_for_a_replacement()
    {
        var vm = await SundayAsync();
        await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);

        _dialogs.Choice = 0; // As written
        vm.Entry.DeleteVariantCommand.Execute(null);
        await Eventually(() => Store.LoadSong("champion")!.Arrangements.Count == 0);

        Assert.Equal("1 service entry plays Demo. What should they play instead?", _dialogs.ChooseMessage);
        Assert.Equal("As written", vm.Setlist.Rows[0].VariantText);
        Assert.Equal(ArrangementChoiceType.AsWritten, Store.LoadServices()[0].Entries[0].Arrangement.Type);

        vm.UndoCommand.Execute(null);
        Assert.Equal("Demo", vm.Setlist.Rows[0].VariantText);
        Assert.Single(Store.LoadSong("champion")!.Arrangements);
    }

    // ---- Services (SL-2, SL-4, SL-6, SL-21) ------------------------------------------------------------------

    [Fact]
    public async Task AT33_duplicate_service_copies_the_entries()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[0].Id;
        vm.SetKeyOffset(entryId, -1);
        vm.SetTransition(entryId, new Transition { Type = TransitionType.Pad, PadKey = "Db", ClickBpm = 72 });

        // Both dates are past, so the order (SL-1: past services, most recent first) does not change with the day the test runs.
        _dialogs.Details = new ServiceDetails("Sunday 19 Oct", new DateTime(2026, 10, 19, 9, 0, 0));
        await vm.DuplicateServiceAsync();

        Assert.Equal(2, vm.AllServices.Count);
        Assert.Equal("Sunday 19 Oct · 9:00", vm.Setlist.Title);
        Assert.Equal(["A", "D♭"], vm.Setlist.Rows.Select(r => r.KeyText));
        Assert.Equal("Pad in D♭ · click 72", vm.Setlist.Rows[0].ChipText);
        Assert.NotEqual(entryId, vm.Setlist.Rows[0].Id);
        Assert.Equal(["Sunday 26 Oct · 9:00", "Sunday 19 Oct · 9:00"], vm.Setlist.Services.Select(s => s.Title));

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.AllServices);
        Assert.Single(Store.LoadServices());
    }

    [Fact]
    public async Task REL45_restore_earlier_version_brings_the_entries_back()
    {
        var vm = await SundayAsync();
        var before = vm.Setlist.Rows.Select(r => r.Title).ToList();
        Directory.Delete(Path.Combine(_root, "data", "versions"), true); // the setup's own edits were versions too (one per five minutes)
        vm.RemoveEntry(vm.Setlist.Rows[0].Id); // the first change of the session keeps the service as it was
        Assert.Single(vm.Setlist.Rows);

        _dialogs.Choice = 0;
        await vm.RestoreServiceVersionAsync();
        Assert.Equal(before, vm.Setlist.Rows.Select(r => r.Title));
        Assert.Contains("version", _dialogs.ChooseMessage);

        vm.UndoCommand.Execute(null); // one undo step
        Assert.Single(vm.Setlist.Rows);
    }

    [Fact]
    public async Task REL3_run_check_in_perform_shows_the_quick_check_and_does_not_store_it()
    {
        var vm = await NewViewModelAsync();
        vm.QuickCheckCommand.Execute(null);
        Assert.Equal("Load a service to check it.", vm.Toast);

        vm = await SundayAsync();
        vm.QuickCheckCommand.Execute(null);
        Assert.Matches("^(✓ Ready|⚠ Ready with warnings|✗ Not ready)", vm.Toast);
        Assert.Equal("Not checked", vm.CheckBadgeText); // only the full check is kept on the service (REL-2)
    }

    [Fact]
    public async Task AUTO50_the_log_tab_keeps_the_newest_lines_up_to_its_limit()
    {
        var vm = await NewViewModelAsync();
        for (var i = 0; i < 5100; i++)
            AppLog.Write("Services", $"Log limit test {i}");

        await Eventually(() => vm.ShownLog.Any(e => e.Text == "Services: Log limit test 5099"));
        Assert.Equal(5000, vm.ShownLog.Count);
        Assert.DoesNotContain(vm.ShownLog, e => e.Text == "Services: Log limit test 0");
    }

    [Fact]
    public async Task REL45_export_and_import_bring_the_service_back_as_a_new_one()
    {
        var vm = await SundayAsync();
        vm.SetKeyOffset(vm.Setlist.Rows[0].Id, 2);
        var file = Path.Combine(_root, "sunday" + ServiceExchange.Extension);
        _dialogs.SavePath = file;
        await vm.ExportServiceAsync();
        Assert.True(File.Exists(file));

        var originalId = Assert.Single(vm.AllServices).Id;
        _dialogs.OpenPath = file;
        await vm.ImportServiceAsync();
        Assert.Equal(2, vm.AllServices.Count);
        var imported = vm.AllServices.Single(s => s.Id != originalId);
        Assert.Equal(2, imported.Entries[0].KeyOffset);
        Assert.Equal(["champion", "goodness"], imported.Entries.Select(e => e.SongId));
    }

    [Fact]
    public async Task SL4_reorder_keeps_transitions_and_SL6_the_playing_entry_stays()
    {
        var vm = await SundayAsync();
        var champion = vm.Setlist.Rows[0].Id;
        vm.SetTransition(champion, new Transition { Type = TransitionType.Segue });
        vm.MoveServiceEntry(champion, 1);

        Assert.Equal(["Goodness", "Champion"], vm.Setlist.Rows.Select(r => r.Title));
        Assert.Equal("Segue", vm.CurrentService!.Entry(champion)!.TransitionAfter.Type.ToString());
        Assert.Equal("Stop", vm.Setlist.Rows[0].ChipText);

        await vm.LoadEntryAsync(champion);
        vm.RemoveEntry(champion); // stopped: allowed, the song stays loaded as a library song
        Assert.Single(vm.Setlist.Rows);
        Assert.NotNull(vm.Song);
        Assert.Null(vm.LoadedEntry);
        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Setlist.Rows.Count);
    }

    [Fact]
    public async Task SL21_the_transition_popover_saves_its_choice()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[0].Id;
        _dialogs.EditTransition = t =>
        {
            Assert.Equal("After Champion", t.AfterText);
            Assert.Equal("Into Goodness", t.IntoText);
            Assert.Equal("Next song's key (D♭)", t.PadKey.Name);
            t.Type = TransitionTypeChoice.All.Single(c => c.Type == TransitionType.Pad);
            Assert.Equal("At the end, the pad fades in with the click. Press Play and the next song starts, and the pad fades out.", t.Explanation);
            return true;
        };

        await vm.EditTransitionAsync(entryId);
        Assert.Equal("Pad in D♭ · click 68", vm.Setlist.Rows[0].ChipText); // AT-30's defaults: the next song's key and BPM
        Assert.EndsWith("+", vm.Setlist.Summary); // a pad waits for Play
    }

    [Fact]
    public async Task The_service_and_the_loaded_entry_come_back_after_a_restart()
    {
        var vm = await SundayAsync();
        var entryId = vm.Setlist.Rows[1].Id;
        await vm.LoadEntryAsync(entryId);
        vm.SetKeyOffset(entryId, 2);
        await vm.DisposeAsync();

        // Without an engine the song isn't loaded at start-up (as in M1); the service and the entry are selected again,
        // and the entry-backed reload is covered by the engine tests.
        var again = await NewViewModelAsync();
        Assert.Equal("Sunday 26 Oct · 9:00", again.Setlist.Title);
        Assert.Equal(entryId, again.Setlist.Selected?.Id);
        Assert.Equal("E♭ (+2)", again.Setlist.KeyText);
        Assert.Equal(entryId, Store.LoadSettings().LastEntryId);
    }

    // ---- Mix presets (PB-81, DM-13) -------------------------------------------------------------------------

    [Fact]
    public async Task PB81_each_entry_uses_its_own_mix_preset()
    {
        var vm = await SundayAsync();
        vm.AddSongToService("champion");
        await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);

        _dialogs.Name = "Rehearsal – band live";
        vm.SaveMixAsNewCommand.Execute(null);
        await Eventually(() => vm.CurrentMixPreset?.Name == "Rehearsal – band live");
        vm.Song!.Tracks.Single(t => t.Id == "keys").Mute = true;
        vm.Flush();

        var song = Store.LoadSong("champion")!;
        var rehearsal = song.MixPresets.Single(p => p.Name == "Rehearsal – band live");
        Assert.True(rehearsal.For("keys").Mute);
        Assert.False(song.MixPresets.Single(p => p.Id == "default").For("keys").Mute);

        // The other Champion entry keeps the default.
        await vm.LoadEntryAsync(vm.Setlist.Rows[2].Id);
        Assert.Equal("Default", vm.CurrentMixPreset!.Name);
        Assert.False(vm.Song!.Tracks.Single(t => t.Id == "keys").Mute);

        // Click and guide don't follow the key; music does (DM-12).
        Assert.False(vm.Song.Tracks.Single(t => t.Id == "click").Transposes);
        Assert.True(vm.Song.Tracks.Single(t => t.Id == "keys").Transposes);
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition was not met in time.");
            await Task.Delay(20);
        }
    }
}

/// <summary>Dialogs with scripted answers for the services tests.</summary>
internal sealed class ServiceDialogs : IDialogs
{
    public ServiceDetails? Details { get; set; }
    public VariantScopeAnswer? Scope { get; set; }
    public int ScopeQuestions { get; private set; }
    public int OtherEntriesAsked { get; private set; }
    public string? Name { get; set; }
    public int? Choice { get; set; }
    public string? ChooseMessage { get; private set; }
    public Func<TransitionViewModel, bool>? EditTransition { get; set; }

    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult(SavePath);
    public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult(OpenPath);
    public Task<bool> ReviewImportAsync(ImportReviewViewModel review) => Task.FromResult(false);
    public Task<DuplicateChoice> AskDuplicateAsync(string existingTitle) => Task.FromResult(DuplicateChoice.Cancel);
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(true);
    public Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings) => Task.FromResult(false);

    public Task EditControlsAsync(ControlsViewModel controls) => Task.CompletedTask;

    public Task ShowServiceCheckAsync(ServiceCheckViewModel check) => Task.CompletedTask;

    public ResumeChoice ResumeAnswer { get; set; } = ResumeChoice.DontResume;
    public ResumeQuestion? ResumeAsked { get; private set; }

    public Task<ResumeChoice> AskResumeAsync(ResumeQuestion question)
    {
        ResumeAsked = question;
        return Task.FromResult(ResumeAnswer);
    }
    public Task<ServiceDetails?> AskServiceDetailsAsync(string title, string confirmLabel, ServiceDetails initial) => Task.FromResult(Details);

    public Task<VariantScopeAnswer?> AskVariantScopeAsync(string variantName, int otherEntries)
    {
        ScopeQuestions++;
        OtherEntriesAsked = otherEntries;
        return Task.FromResult(Scope);
    }

    public Task<string?> AskNameAsync(string title, string label, string initial, string confirmLabel) => Task.FromResult(Name);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> choices, string confirmLabel)
    {
        ChooseMessage = message;
        return Task.FromResult(Choice);
    }

    public Task<bool> EditTransitionAsync(TransitionViewModel transition) => Task.FromResult(EditTransition?.Invoke(transition) ?? false);
}

// SL-47, AT-34 in the app: a pad folder imports as a pad set under Library › Pads, not as a song.
public sealed class PadImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-pads-vm-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task AT34_a_folder_of_twelve_key_named_pads_is_a_pad_set_not_a_song()
    {
        var folder = Path.Combine(_root, "OB Pads");
        Directory.CreateDirectory(folder);
        foreach (var key in new[] { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" })
            File.WriteAllBytes(Path.Combine(folder, $"OB Pad {key}.wav"), [0]);

        var store = new DataStore(Path.Combine(_root, "data"));
        var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
        await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing), new LockedDispatcher(),
            new ServiceDialogs(), new SpeechModel(store.Root, allowDownload: false));
        await vm.InitializeAsync();
        Assert.True(vm.PadsEmpty);

        await vm.ImportFolderAsync(folder);
        Assert.Empty(vm.Songs);
        Assert.Equal(["OB Pad"], vm.PadSets.Select(p => p.Name));
        Assert.Equal("12 keys", vm.PadSets[0].MetaLine);
        Assert.True(vm.ShowPads);
        Assert.Equal("Imported the pad set OB Pad · 12 keys", vm.Toast);
        Assert.Single(store.LoadPads().PadSets);

        // Importing it again doesn't add a second one.
        await vm.ImportFolderAsync(folder);
        Assert.Single(vm.PadSets);

        // The switch: pressing the selected key changes nothing.
        vm.SongsKey = true;
        Assert.True(vm.ShowSongs);
        vm.SongsKey = false;
        Assert.True(vm.ShowSongs);
    }
}
