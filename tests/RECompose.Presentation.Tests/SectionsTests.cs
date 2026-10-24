// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Analysis;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// Milestone 2 on the Prepare screen: analysis after import (SL-54), Detect from guide
// (SEC-10…17, AT-35/37), the review flow (SEC-30/31, AT-36), M marking (SEC-52, AT-38),
// NOW and NEXT (PB-30…35). The analysis tools are faked with fixture B's click and guide;
// the real engine plays.
public sealed class SectionsTests : IDisposable
{
    private const double Bar = 10.0 / 3;   // fixture B: 72 BPM 4/4
    private const double Downbeat = 0.0007;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-sec-" + Guid.NewGuid().ToString("N"));
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
    private readonly ScriptedDialogs _dialogs = new();
    private readonly FakeAnalysis _tools = new();

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

    private static double BarTime(int bar) => Downbeat + (bar - 1) * Bar;

    private MainViewModel NewViewModel()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail($"{EnginePaths.ExecutableFileName} not found");
            Assert.Skip($"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.");
        }

        var store = new DataStore(Path.Combine(_root, "data"));
        // A stand-in model file, so transcription doesn't try to download one.
        Directory.CreateDirectory(Path.Combine(store.Root, "models"));
        File.WriteAllText(Path.Combine(store.Root, "models", SpeechModel.FileName), "fake");
        return new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable),
            new LockedDispatcher(), _dialogs, new SpeechModel(store.Root, allowDownload: false), _tools);
    }

    /// <summary>A fixture-B-like folder: click, guide and keys, 82 bars (8 kHz mono, silent).</summary>
    private string StemsFolder()
    {
        var folder = Path.Combine(_root, "stems", "Come Ye Sinners");
        Directory.CreateDirectory(folder);
        foreach (var stem in new[] { "Click", "Guide", "Keys" })
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(folder, stem + ".wav")));
            const int rate = 8000;
            var frames = (int)(BarTime(83) * rate);
            var bytes = frames * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
            w.Write(new byte[bytes]);
        }
        return folder;
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    private async Task<MainViewModel> ImportedAsync()
    {
        var vm = NewViewModel();
        await vm.InitializeAsync();
        await vm.ImportFolderAsync(StemsFolder());
        var id = vm.Songs[0].Id;
        var store = new DataStore(Path.Combine(_root, "data"));

        // SL-54: the click and the guide are analysed in the background after import.
        await Eventually(() => store.LoadSong(id) is { TempoSource: TempoSource.Click, GuideAnalysis: not null } && !vm.Songs[0].HasActivity);
        await vm.LoadSongAsync(id);
        return vm;
    }

    [Fact]
    public async Task SL54_import_analyses_the_click_and_transcribes_the_guide()
    {
        await using var vm = await ImportedAsync();

        Assert.Equal(72, vm.Song!.Model.Bpm, 2);
        Assert.Equal(4, vm.Song.Model.BeatsPerBar);
        Assert.Equal(Downbeat, vm.Song.Model.FirstDownbeatSec, 3);
        Assert.Equal("Key ? · 72 BPM · 4/4 · 3 tracks", vm.Songs[0].MetaLine);
        Assert.Equal(1, _tools.Transcriptions);
        Assert.Contains("Chorus", _tools.LastPrompt); // the vocabulary prompt (SEC-12)

        // UI-80: no sections yet, so no NOW or NEXT.
        Assert.False(vm.HasSections);
        Assert.True(vm.Sections.IsEmpty);
        Assert.True(vm.Sections.CanDetect);
        Assert.Equal(string.Empty, vm.NowLabel);
    }

    [Fact]
    public async Task AT35_detect_from_guide_fills_the_table_the_result_box_and_the_readouts()
    {
        await using var vm = await ImportedAsync();

        await vm.DetectSectionsAsync();

        Assert.Equal(1, _tools.Transcriptions); // the cached transcript is used (SEC-11)
        Assert.Equal(
            "Come Ye Sinners: 12 sections: Verse 1, Chorus 1, Verse 2, Verse 3, Chorus 2, Verse 4, Verse 5, Chorus 3, Chorus 4, Tag, Outro, Ending. " +
            "Click: 72 BPM, 4 beats per bar. Guide: 12 sections from 19 spoken calls. 1 needs review.",
            vm.Sections.ResultText);
        Assert.Equal(12, vm.Sections.Rows.Count);
        var verse1 = vm.Sections.Rows[0];
        Assert.Equal(("Verse 1", "3.334", "2", "V1"), (verse1.Label, verse1.StartText, verse1.ChartBarText, verse1.Badge));
        Assert.Equal(("Outro", "253.334", "77"), (vm.Sections.Rows[10].Label, vm.Sections.Rows[10].StartText, vm.Sections.Rows[10].ChartBarText));
        Assert.True(vm.Sections.Rows[3].NeedsReview);
        Assert.Equal("Review (1)", vm.Sections.ReviewLabel);
        Assert.True(verse1.HasCall);

        // PB-30…35: at rest the lead-in counts down to Verse 1; song bars start there.
        Assert.Equal("LEAD-IN", vm.NowLabel);
        Assert.Equal("VERSE 1", vm.NextLabel);
        Assert.Equal("AS WRITTEN", vm.NextTag);
        Assert.Equal("in 1 bar", vm.CountdownText);
        Assert.Equal("bar 0.1 / 81", vm.SongBarText);

        await vm.SeekAsync(33.9);
        Assert.Equal("CHORUS 1", vm.NowLabel);
        Assert.Equal("bar 1 / 8", vm.NowDetail);
        Assert.Equal("VERSE 2", vm.NextLabel);
        Assert.Equal("in 8 bars", vm.CountdownText);
        Assert.Equal("bar 10.1 / 81", vm.SongBarText);
        Assert.Equal("chart bar 11.1", vm.ChartBarText);

        // Detect is one undo step and is saved at once.
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Sections.Rows);
        Assert.Equal("Undid: Detect from guide", vm.Toast);
        vm.RedoCommand.Execute(null);
        Assert.Equal(12, new DataStore(Path.Combine(_root, "data")).LoadSong(vm.Song!.Id)!.Sections.Count);
    }

    [Fact]
    public async Task AT36_review_steps_to_the_flagged_section_and_accept_clears_it()
    {
        await using var vm = await ImportedAsync();
        await vm.DetectSectionsAsync();

        vm.Sections.ReviewCommand.Execute(null);
        await Eventually(() => vm.Sections.IsReviewing);
        Assert.Equal("Review 1 of 1 · Verse 3", vm.Sections.ReviewTitle);
        Assert.Equal("heard 'verse three' at 1:24.2 · chart bar 27", vm.Sections.ReviewHeard);
        Assert.Equal("Verse 3", vm.Sections.Selected?.Label);

        vm.Sections.ReviewLater();      // → a bar later, plays again
        Assert.Equal("28", vm.Sections.Rows[3].ChartBarText);
        vm.Sections.ReviewEarlier();
        Assert.Equal("27", vm.Sections.Rows[3].ChartBarText);

        vm.Sections.ReviewAccept();
        await Eventually(() => !vm.Sections.IsReviewing);
        Assert.Equal(0, vm.Sections.ReviewCount);
        Assert.Equal("Review done. No sections need review.", vm.Toast);
    }

    [Fact]
    public async Task AT37_redetect_asks_and_keeps_edited_sections()
    {
        await using var vm = await ImportedAsync();
        await vm.DetectSectionsAsync();

        var tag = vm.Sections.Rows.Single(r => r.Label == "Tag");
        tag.Name = "Tag (big)";
        tag.CommitName();
        Assert.Equal("Tag (big)", vm.Sections.Rows[9].Label);

        await vm.DetectSectionsAsync();
        Assert.Equal(1, _dialogs.Confirmations);
        Assert.EndsWith("Kept 1 edited section.", vm.Sections.ResultText);
        Assert.Contains(vm.Sections.Rows, r => r.Label == "Tag (big)");
        Assert.Equal(12, vm.Sections.Rows.Count);
    }

    [Fact]
    public async Task AT38_M_marks_a_section_named_from_the_nearest_call()
    {
        await using var vm = await ImportedAsync();

        await vm.SeekAsync(33.9);
        var id = vm.MarkAtPlayhead();

        Assert.NotNull(id);
        var row = Assert.Single(vm.Sections.Rows);
        Assert.Equal(("Chorus", "33.334", "11"), (row.Label, row.StartText, row.ChartBarText));
        Assert.Equal("Marked Chorus at chart bar 11", vm.Toast);
        Assert.True(vm.ToastCanUndo);

        // Again on the same bar: nothing added, the row flashes.
        await vm.SeekAsync(34.5);
        Assert.Null(vm.MarkAtPlayhead());
        Assert.Single(vm.Sections.Rows);
        Assert.True(vm.Sections.Rows[0].Flash);
        Assert.Equal("Chorus already starts at chart bar 11", vm.Toast);

        // Lane edits: drag the boundary a bar later, change the type, split, merge, delete.
        vm.Sections.MoveStart(row.Id, BarTime(12) + 0.2, snapToBeat: false);
        Assert.Equal("12", vm.Sections.Rows[0].ChartBarText);
        vm.Sections.SetTypeOf(vm.Sections.Rows[0].Id, SectionType.Bridge);
        Assert.Equal("B", vm.Sections.Rows[0].Badge);
        await vm.SeekAsync(BarTime(20) + 1);
        vm.Sections.SplitAtPlayhead();
        Assert.Equal(["Chorus 1", "Chorus 2"], vm.Sections.Rows.Select(r => r.Label));
        vm.Sections.MergeWithNext(vm.Sections.Rows[0].Id);
        Assert.Single(vm.Sections.Rows);
        vm.Sections.DeleteById(vm.Sections.Rows[0].Id);
        Assert.Empty(vm.Sections.Rows);

        // Each edit is one undo step: five back to the marked chorus, one more to nothing.
        for (var i = 0; i < 5; i++)
            vm.UndoCommand.Execute(null);
        var back = Assert.Single(vm.Sections.Rows);
        Assert.Equal(("Chorus", "11", "C"), (back.Label, back.ChartBarText, back.Badge));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Sections.Rows);
        Assert.Equal("Undid: Marked Chorus at chart bar 11", vm.Toast);
    }

    /// <summary>Fixture B's click and guide, as the engine's analysis modes would report them.</summary>
    private sealed class FakeAnalysis : IAnalysisTools
    {
        public int Transcriptions { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<OnsetsResult> OnsetsAsync(string file, CancellationToken ct = default)
        {
            var onsets = Enumerable.Range(0, 328)
                .Select(k => new ClickOnset(Downbeat + k * 60.0 / 72, k % 4 == 0 ? -6 : -12, k % 4 == 0 ? 2400 : 1500))
                .ToList();
            return Task.FromResult(new OnsetsResult(true, null, BarTime(83), onsets));
        }

        public Task<TranscriptResult> TranscribeAsync(string file, string model, string prompt, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Transcriptions++;
            LastPrompt = prompt;
            progress?.Report(0.5);
            var words = new List<GuideWord>();
            var beat = Bar / 4;

            void Say(double start, double confidence, params string[] text)
            {
                foreach (var w in text)
                {
                    words.Add(new GuideWord(w, start, start + 0.35, confidence));
                    start += 0.42;
                }
            }

            int[] counted = [11, 35, 59, 80];
            foreach (var (bar, text) in new (int, string[])[]
            {
                (2, ["Verse", "one."]), (11, ["Chorus."]), (19, ["Verse", "2."]), (27, ["Verse", "three."]), (35, ["Chorus."]),
                (43, ["Verse", "four."]), (51, ["Verse", "five."]), (59, ["Chorus."]), (67, ["Chorus."]), (75, ["Tag."]),
                (77, ["Outro."]), (80, ["Ending."]),
            })
                Say(BarTime(bar - 1) + (counted.Contains(bar) ? 0.05 : beat), bar == 27 ? 0.5 : 0.96, text);
            foreach (var bar in counted)
                Say(BarTime(bar - 1) + 1.05, 0.9, "1,", "2,", "3,", "4.");
            Say(BarTime(56) + beat, 0.9, "All", "in.");
            Say(BarTime(64) + beat, 0.9, "Drums.");
            Say(BarTime(72) + beat, 0.9, "Last", "time.");

            return Task.FromResult(new TranscriptResult(true, null, "ggml-base.en", 19, words.OrderBy(w => w.Start).ToList()));
        }
    }
}
