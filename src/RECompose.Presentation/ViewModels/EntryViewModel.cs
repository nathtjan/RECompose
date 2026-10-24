// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>One choice in the variant list: "As written" (no id) or a variant (ARR-1).</summary>
public sealed class VariantChoice(string? id, string name, bool isDefault)
{
    public string? Id { get; } = id;
    public string Name { get; } = name;
    public bool IsDefault { get; } = isDefault;

    /// <summary>"Short" or "Short · default".</summary>
    public string Display => IsDefault ? $"{Name} · default" : Name;

    public override string ToString() => Display;
}

/// <summary>A section the palette can add (ARR-20).</summary>
public sealed record PaletteItem(string SectionId, string Label, string? Badge, string ColorToken);

/// <summary>One line of the entry list (ARR-12).</summary>
public sealed class EntryRowViewModel : ObservableObject
{
    private bool _isSelected;

    public EntryRowViewModel(ArrangementEntry entry, int index, string text, string? issue, bool isSection, string? colorToken, string? badge)
    {
        Entry = entry;
        Id = entry.Id;
        Index = index;
        Text = text;
        Issue = issue;
        IsSection = isSection;
        ColorToken = colorToken;
        Badge = badge;
        Note = entry.Note;
    }

    public ArrangementEntry Entry { get; }
    public string Id { get; }

    /// <summary>1-based position in the list.</summary>
    public int Index { get; }

    public string Text { get; }
    public string? Note { get; }
    public string? Issue { get; }
    public bool HasIssue => Issue is not null;
    public bool IsSection { get; }
    public string? ColorToken { get; }
    public string? Badge { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>A word of the quick order with what it became (ARR-60).</summary>
public sealed record QuickTokenViewModel(string Text, string Label, bool IsOk, string? Message);

public sealed record MuteChoice(string Key, string Name)
{
    public static readonly IReadOnlyList<MuteChoice> All =
    [
        new("none", "Nothing"),
        new("drums", "Drums out"),
        new("band", "Band down"),
        new("click", "Click and guide only"),
    ];

    public override string ToString() => Name;
}

public sealed record EndingChoice(EndingMode Mode, string Name)
{
    public static readonly IReadOnlyList<EndingChoice> All =
    [
        new(EndingMode.Continue, "Continue"),
        new(EndingMode.RingOut, "Ring out"),
        new(EndingMode.Cut, "Cut"),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// The ENTRY tab: the variants of the loaded song and the order of the active one (ARR-1…13, ARR-20…22,
/// ARR-50, ARR-60). Every edit is one undo step and is saved at once. Editing the song as written makes a
/// variant first, as the spec's "New variant from as written" would.
/// </summary>
public sealed class EntryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;
    private EntryRowViewModel? _selected;
    private VariantChoice? _selectedVariant;
    private string _variantName = string.Empty;
    private string _quickOrder = string.Empty;

    // Options of the selected entry.
    private int _repeatCount = 1;
    private bool _untilCued;
    private string _fromText = string.Empty;
    private string _toText = string.Empty;
    private int _lift;
    private MuteChoice _mute = MuteChoice.All[0];
    private bool _firstPassOnly;
    private int _fadeIn;
    private int _fadeOut;
    private EndingChoice _ending = EndingChoice.All[0];
    private string _note = string.Empty;
    private int _lengthBars = 4;

    internal EntryViewModel(MainViewModel main)
    {
        _main = main;
        AddClickCommand = new RelayCommand(() => AddEntry(ArrangementEntry.ClickBars(4), "Added a click count-in"), () => HasSong);
        AddPadCommand = new RelayCommand(() => AddEntry(ArrangementEntry.PadBars(8), "Added a pad"), () => HasSong);
        AddHoldCommand = new RelayCommand(() => AddEntry(ArrangementEntry.NewHold(), "Added a hold"), () => HasSong);
        AddSectionCommand = new RelayCommand(o => { AddSection(o as string); return Task.CompletedTask; }, () => HasSong);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null && !IsAsWritten);
        DuplicateCommand = new RelayCommand(Duplicate, () => Selected is not null && !IsAsWritten);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => Selected is { Index: > 1 } && !IsAsWritten);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => Selected is not null && Selected.Index < Rows.Count && !IsAsWritten);
        NewFromWrittenCommand = new RelayCommand(() => NewVariant(VariantStart.AsWritten), () => HasSong);
        NewFromCurrentCommand = new RelayCommand(() => NewVariant(VariantStart.Current), () => HasSong && !IsAsWritten);
        NewEmptyCommand = new RelayCommand(() => NewVariant(VariantStart.Empty), () => HasSong);
        RenameVariantCommand = new RelayCommand(RenameVariant, () => !IsAsWritten);
        DeleteVariantCommand = new RelayCommand(DeleteVariant, () => !IsAsWritten);
        SetDefaultCommand = new RelayCommand(SetDefault, () => HasSong);
        ApplyQuickOrderCommand = new RelayCommand(ApplyQuickOrder, () => CanApplyQuickOrder);
    }

    // ---- State shown ----------------------------------------------------------------------

    private SongViewModel? Song => _main.Song;
    public bool HasSong => Song is not null;
    public bool IsAsWritten => Song?.IsAsWritten ?? true;
    public bool IsVariant => !IsAsWritten;

    /// <summary>"As written" and the variants (ARR-1).</summary>
    public ObservableCollection<VariantChoice> Variants { get; } = [];

    public VariantChoice? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (_loading || value is null || !Set(ref _selectedVariant, value))
                return;
            _main.SelectVariant(value.Id);
        }
    }

    /// <summary>The name typed for a new or renamed variant.</summary>
    public string VariantName { get => _variantName; set => Set(ref _variantName, value); }

    public ObservableCollection<EntryRowViewModel> Rows { get; } = [];
    public ObservableCollection<PaletteItem> Palette { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];
    public bool HasIssues => Issues.Count > 0;

    public EntryRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            foreach (var row in Rows)
                row.IsSelected = ReferenceEquals(row, value);
            LoadOptions();
            RefreshCommands();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedIsSection));
            OnPropertyChanged(nameof(SelectedIsGenerated));
        }
    }

    public IReadOnlyList<MuteChoice> MuteChoices => MuteChoice.All;
    public IReadOnlyList<EndingChoice> EndingChoices => EndingChoice.All;

    public bool HasSelection => Selected is not null;
    public bool SelectedIsSection => Selected?.Entry.Kind == EntryKind.Section;
    public bool SelectedIsGenerated => Selected?.Entry.Kind is EntryKind.Click or EntryKind.Pad;

    /// <summary>"1. Verse 1 · 2. Chorus 1 · …" (ARR-13).</summary>
    public string SummaryLabels { get; private set; } = string.Empty;

    /// <summary>"76 bars · 4:13".</summary>
    public string SummaryTotals { get; private set; } = string.Empty;

    /// <summary>Shown over the list while As written plays.</summary>
    public string AsWrittenNote => "This is the song as written. Add or change something and a variant is made for you.";

    // ---- Commands -------------------------------------------------------------------------

    public RelayCommand AddClickCommand { get; }
    public RelayCommand AddPadCommand { get; }
    public RelayCommand AddHoldCommand { get; }
    public RelayCommand AddSectionCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand NewFromWrittenCommand { get; }
    public RelayCommand NewFromCurrentCommand { get; }
    public RelayCommand NewEmptyCommand { get; }
    public RelayCommand RenameVariantCommand { get; }
    public RelayCommand DeleteVariantCommand { get; }
    public RelayCommand SetDefaultCommand { get; }
    public RelayCommand ApplyQuickOrderCommand { get; }

    private void RefreshCommands()
    {
        foreach (var c in new[]
                 {
                     AddClickCommand, AddPadCommand, AddHoldCommand, AddSectionCommand, RemoveCommand, DuplicateCommand, MoveUpCommand,
                     MoveDownCommand, NewFromWrittenCommand, NewFromCurrentCommand, NewEmptyCommand, RenameVariantCommand,
                     DeleteVariantCommand, SetDefaultCommand, ApplyQuickOrderCommand,
                 })
            c.Refresh();
    }

    // ---- Refresh from the song ------------------------------------------------------------

    /// <summary>Rebuilds everything from the song: variants, list, palette, summary and warnings.</summary>
    internal void Refresh()
    {
        _loading = true;
        try
        {
            var keep = Selected?.Id;
            Variants.Clear();
            Palette.Clear();
            Rows.Clear();
            Issues.Clear();

            if (Song is not { } song)
            {
                _selectedVariant = null;
                _selected = null;
                SummaryLabels = SummaryTotals = string.Empty;
            }
            else
            {
                var defaultId = song.Model.DefaultArrangementId;
                Variants.Add(new VariantChoice(null, "As written", defaultId is null));
                foreach (var v in song.Model.Arrangements)
                    Variants.Add(new VariantChoice(v.Id, v.Name, v.Id == defaultId));
                _selectedVariant = Variants.FirstOrDefault(v => v.Id == song.ActiveVariantId) ?? Variants[0];

                foreach (var s in song.Sections)
                    Palette.Add(new PaletteItem(s.Id, s.Label, s.Badge, s.ColorToken));

                var byId = song.Sections.ToDictionary(s => s.Id);
                var active = song.ActiveVariant;
                var issues = active is null ? [] : ArrangementValidator.Validate(active, song.Sections);
                if (active is null)
                {
                    // The written order, read-only.
                    var n = 0;
                    foreach (var s in song.Sections.OrderBy(x => x.StartSec))
                        Rows.Add(new EntryRowViewModel(ArrangementEntry.ForSection(s.Id) with { Id = "written:" + s.Id }, ++n, s.Label, null, true, s.ColorToken, s.Badge));
                    SummaryLabels = song.Sections.Count == 0 ? string.Empty : string.Join(" · ", song.Sections.OrderBy(x => x.StartSec).Select((s, i) => $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {s.Label}"));
                }
                else
                {
                    var n = 0;
                    foreach (var e in active.Entries)
                    {
                        n++;
                        SectionInfo? info = e.SectionId is not null && byId.TryGetValue(e.SectionId, out var found) ? found : null;
                        var issue = issues.FirstOrDefault(i => i.EntryId == e.Id && i.Severity != IssueSeverity.Info)?.Message;
                        Rows.Add(new EntryRowViewModel(e, n, ArrangementSummary.EntryText(e, byId), issue, e.Kind == EntryKind.Section, info?.ColorToken, info?.Badge));
                    }

                    SummaryLabels = ArrangementSummary.Labels(active, song.Sections);
                }

                SummaryTotals = song.Plan.IsEmpty ? string.Empty : ArrangementSummary.Totals(song.Plan);
                foreach (var issue in issues)
                    Issues.Add(issue.Message);

                _selected = Rows.FirstOrDefault(r => r.Id == keep);
                foreach (var row in Rows)
                    row.IsSelected = ReferenceEquals(row, _selected);
            }

            LoadOptions();
        }
        finally
        {
            _loading = false;
        }

        foreach (var name in new[]
                 {
                     nameof(HasSong), nameof(IsAsWritten), nameof(IsVariant), nameof(SelectedVariant), nameof(Selected), nameof(HasSelection),
                     nameof(SelectedIsSection), nameof(SelectedIsGenerated), nameof(SummaryLabels), nameof(SummaryTotals), nameof(HasIssues),
                 })
            OnPropertyChanged(name);
        UpdateQuickOrder();
        RefreshCommands();
    }

    // ---- Variants (ARR-1, ARR-2, ARR-3) -----------------------------------------------------

    private enum VariantStart { AsWritten, Current, Empty }

    private IEnumerable<string> ExistingNames => Song?.Model.Arrangements.Select(a => a.Name).Append("As written") ?? [];

    private void NewVariant(VariantStart start)
    {
        if (Song is not { } song)
            return;
        var name = string.IsNullOrWhiteSpace(_variantName) ? "Variant" : _variantName.Trim();
        var variant = start switch
        {
            VariantStart.AsWritten => ArrangementEdit.FromAsWritten(song.Sections, name, ExistingNames),
            VariantStart.Current when song.ActiveVariant is { } active => ArrangementEdit.Copy(active, name, ExistingNames),
            _ => ArrangementEdit.Empty(name, ExistingNames),
        };
        _main.CommitVariants($"New variant {variant.Name}", [.. song.Model.Arrangements, variant], variant.Id, keepDefault: true);
        VariantName = string.Empty;
    }

    private void RenameVariant()
    {
        if (Song is not { ActiveVariant: { } active } song || string.IsNullOrWhiteSpace(_variantName))
            return;
        var others = song.Model.Arrangements.Where(a => a.Id != active.Id).Select(a => a.Name).Append("As written");
        var renamed = ArrangementEdit.Rename(active, ArrangementEdit.UniqueName(_variantName, others));
        _main.CommitVariants($"Renamed {active.Name} to {renamed.Name}", ArrangementEdit.ReplaceVariant(song.Model.Arrangements, renamed), active.Id, keepDefault: true);
        VariantName = string.Empty;
    }

    private void DeleteVariant()
    {
        if (Song is not { ActiveVariant: { } active } song)
            return;
        var variants = song.Model.Arrangements.Where(a => a.Id != active.Id).ToList();
        var clearDefault = song.Model.DefaultArrangementId == active.Id;
        _main.CommitVariants($"Deleted {active.Name}", variants, null, keepDefault: !clearDefault, defaultId: null);
    }

    private void SetDefault()
    {
        if (Song is not { } song)
            return;
        var id = song.ActiveVariantId;
        if (song.Model.DefaultArrangementId == id)
            return;
        var name = song.ActiveVariant?.Name ?? "As written";
        _main.CommitVariants($"{name} is the default", song.Model.Arrangements, id, keepDefault: false, defaultId: id);
    }

    // ---- Entries (ARR-10, ARR-12, ARR-20…22) ------------------------------------------------

    /// <summary>
    /// Applies a change to the active variant as one undo step. From As written a variant is made first, named after the
    /// song's first free "Variant", and selected.
    /// </summary>
    private void Edit(string description, Func<Arrangement, Arrangement> change, string? selectEntryId = null)
    {
        if (Song is not { } song)
            return;
        var variants = song.Model.Arrangements;
        Arrangement basis;
        List<Arrangement> next;
        if (song.ActiveVariant is { } active)
        {
            basis = active;
            next = variants.ToList();
        }
        else
        {
            basis = ArrangementEdit.FromAsWritten(song.Sections, "Variant", ExistingNames);
            next = [.. variants, basis];
        }

        var changed = change(basis);
        next = ArrangementEdit.ReplaceVariant(next, changed);
        if (selectEntryId is not null)
            _pendingSelect = selectEntryId;
        _main.CommitVariants(description, next, changed.Id, keepDefault: true);
    }

    private string? _pendingSelect;

    internal void ApplyPendingSelection()
    {
        if (_pendingSelect is null)
            return;
        var id = _pendingSelect;
        _pendingSelect = null;
        Selected = Rows.FirstOrDefault(r => r.Id == id);
    }

    private void AddEntry(ArrangementEntry entry, string description)
    {
        var index = Selected is { } sel && !IsAsWritten ? sel.Index : (int?)null; // after the selected entry
        Edit(description, a => ArrangementEdit.Add(a, entry, index), entry.Id);
    }

    public void AddSection(string? sectionId)
    {
        if (sectionId is null || Song?.Sections.FirstOrDefault(s => s.Id == sectionId) is not { } info)
            return;
        AddEntry(ArrangementEntry.ForSection(sectionId), $"Added {info.Label}");
    }

    private void Remove()
    {
        if (Selected is not { } row)
            return;
        var next = Rows.FirstOrDefault(r => r.Index == row.Index + 1) ?? Rows.FirstOrDefault(r => r.Index == row.Index - 1);
        Edit($"Removed {row.Text}", a => ArrangementEdit.Remove(a, row.Id), next?.Id);
    }

    private void Duplicate()
    {
        if (Selected is not { } row)
            return;
        string? added = null;
        Edit($"Duplicated {row.Text}", a =>
        {
            var (arrangement, copy) = ArrangementEdit.Duplicate(a, row.Id);
            added = copy?.Id;
            return arrangement;
        });
        if (added is not null)
            _pendingSelect = added;
        ApplyPendingSelection();
    }

    private void MoveSelected(int delta)
    {
        if (Selected is { } row)
            MoveEntry(row.Id, row.Index - 1 + delta);
    }

    /// <summary>Moves an entry to a 0-based position (drag in the list or on the timeline, Alt+arrows).</summary>
    public void MoveEntry(string entryId, int newIndex)
    {
        if (Rows.FirstOrDefault(r => r.Id == entryId) is not { } row)
            return;
        newIndex = Math.Clamp(newIndex, 0, Math.Max(0, Rows.Count - 1));
        if (newIndex == row.Index - 1 && !IsAsWritten)
            return;
        Edit($"Moved {row.Text}", a => ArrangementEdit.Move(a, entryId, newIndex), entryId);
    }

    /// <summary>Changes one entry (timeline menu, drag-trim, options panel).</summary>
    public void UpdateEntry(string entryId, string description, Func<ArrangementEntry, ArrangementEntry> change) =>
        Edit(description, a => ArrangementEdit.Update(a, entryId, change), entryId);

    // ---- Options of the selected entry (ARR-21, ARR-22) -------------------------------------

    public int RepeatCount { get => _repeatCount; set { if (Set(ref _repeatCount, Math.Clamp(value, 1, RepeatSpec.MaxCount))) Change("Repeat", e => e with { Repeat = new RepeatSpec { Count = _repeatCount, UntilCued = false } }); } }

    public bool UntilCued { get => _untilCued; set { if (Set(ref _untilCued, value)) Change("Repeat", e => e with { Repeat = new RepeatSpec { Count = e.Repeat.Count, UntilCued = value } }); } }

    public string FromText { get => _fromText; set { if (Set(ref _fromText, value)) CommitRange(); } }

    public string ToText { get => _toText; set { if (Set(ref _toText, value)) CommitRange(); } }

    public int Lift { get => _lift; set { if (Set(ref _lift, Math.Clamp(value, -ArrangementEntry.MaxLift, ArrangementEntry.MaxLift))) Change("Key lift", e => e with { Transpose = _lift }); } }

    public MuteChoice Mute
    {
        get => _mute;
        set
        {
            if (value is null || !Set(ref _mute, value))
                return;
            Change("Mute", e => e with { Mutes = MuteFor(value.Key, _firstPassOnly) });
        }
    }

    public bool FirstPassOnly
    {
        get => _firstPassOnly;
        set
        {
            if (Set(ref _firstPassOnly, value))
                Change("Mute", e => e with { Mutes = MuteFor(_mute.Key, value) });
        }
    }

    public int FadeInBars { get => _fadeIn; set { if (Set(ref _fadeIn, Math.Clamp(value, 0, ArrangementEntry.MaxFadeBars))) Change("Fade in", e => e with { FadeInBars = _fadeIn }); } }

    public int FadeOutBars { get => _fadeOut; set { if (Set(ref _fadeOut, Math.Clamp(value, 0, ArrangementEntry.MaxFadeBars))) Change("Fade out", e => e with { FadeOutBars = _fadeOut }); } }

    public EndingChoice Ending { get => _ending; set { if (value is not null && Set(ref _ending, value)) Change("Ending", e => e with { Ending = value.Mode }); } }

    public string Note { get => _note; set { if (Set(ref _note, value)) Change("Note", e => e with { Note = string.IsNullOrWhiteSpace(value) ? null : value }); } }

    /// <summary>Bars of a click or pad entry.</summary>
    public int LengthBars { get => _lengthBars; set { if (Set(ref _lengthBars, Math.Clamp(value, 1, ArrangementEntry.MaxGeneratedBars))) Change("Length", e => e with { LengthBars = _lengthBars }); } }

    private static MuteSpec MuteFor(string key, bool firstPassOnly) => key switch
    {
        "drums" => ArrangementEdit.DrumsOut(firstPassOnly),
        "band" => ArrangementEdit.BandDown(firstPassOnly),
        "click" => ArrangementEdit.ClickAndGuideOnly(firstPassOnly),
        _ => new MuteSpec(),
    };

    private static string MuteKey(MuteSpec m)
    {
        if (m.IsEmpty)
            return "none";
        if (m.Groups.Count == 1 && m.Groups[0] == Core.Library.TrackGroups.Drums)
            return "drums";
        return m.Groups.Count == Core.Library.TrackGroups.Music.Count ? "click" : "band";
    }

    private void Change(string what, Func<ArrangementEntry, ArrangementEntry> change)
    {
        if (_loading || Selected is not { } row)
            return;
        if (IsAsWritten)
            return;
        UpdateEntry(row.Id, $"{what} on {row.Text}", change);
    }

    private void CommitRange()
    {
        if (_loading || Selected is null)
            return;
        var from = int.TryParse(_fromText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f >= 1 ? f : 1;
        int? to = int.TryParse(_toText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t >= 1 ? t : null;
        Change("Range", e => e with { Range = new BarRange { FromBar = from, ToBar = to } });
    }

    private void LoadOptions()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var e = Selected?.Entry ?? new ArrangementEntry { Id = string.Empty };
            Set(ref _repeatCount, Math.Max(1, e.Repeat.Count), nameof(RepeatCount));
            Set(ref _untilCued, e.Repeat.UntilCued, nameof(UntilCued));
            Set(ref _fromText, e.Range.FromBar > 1 || e.Range.ToBar is not null ? e.Range.FromBar.ToString(CultureInfo.InvariantCulture) : string.Empty, nameof(FromText));
            Set(ref _toText, e.Range.ToBar?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, nameof(ToText));
            Set(ref _lift, e.Transpose, nameof(Lift));
            Set(ref _mute, MuteChoice.All.First(m => m.Key == MuteKey(e.Mutes)), nameof(Mute));
            Set(ref _firstPassOnly, e.Mutes.FirstPassOnly, nameof(FirstPassOnly));
            Set(ref _fadeIn, e.FadeInBars, nameof(FadeInBars));
            Set(ref _fadeOut, e.FadeOutBars, nameof(FadeOutBars));
            Set(ref _ending, EndingChoice.All.First(c => c.Mode == e.Ending), nameof(Ending));
            Set(ref _note, e.Note ?? string.Empty, nameof(Note));
            Set(ref _lengthBars, e.LengthBars, nameof(LengthBars));
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    // ---- Quick order (ARR-60) -------------------------------------------------------------

    public string QuickOrderText
    {
        get => _quickOrder;
        set
        {
            if (Set(ref _quickOrder, value))
                UpdateQuickOrder();
        }
    }

    public ObservableCollection<QuickTokenViewModel> QuickTokens { get; } = [];
    private QuickOrderResult? _quickResult;

    public bool CanApplyQuickOrder => _quickResult is { Entries.Count: > 0, HasProblems: false };

    /// <summary>"6 entries", or the first thing wrong with the text.</summary>
    public string QuickOrderStatus { get; private set; } = string.Empty;

    private void UpdateQuickOrder()
    {
        QuickTokens.Clear();
        _quickResult = null;
        if (Song is null || string.IsNullOrWhiteSpace(_quickOrder))
        {
            QuickOrderStatus = string.Empty;
        }
        else
        {
            var result = QuickOrder.Parse(_quickOrder, Song.Sections);
            _quickResult = result;
            foreach (var t in result.Tokens)
                QuickTokens.Add(new QuickTokenViewModel(t.Text, t.Label, t.Status == TokenStatus.Ok, t.Message));
            QuickOrderStatus = result.Problems.FirstOrDefault() is { } p
                ? $"{p.Text}: {p.Message}"
                : result.Entries.Count == 1 ? "1 entry" : $"{result.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries";
        }

        OnPropertyChanged(nameof(QuickOrderStatus));
        OnPropertyChanged(nameof(CanApplyQuickOrder));
        ApplyQuickOrderCommand.Refresh();
    }

    /// <summary>Replaces the entries of the active variant with the typed order, or makes a variant of it (ARR-60).</summary>
    private void ApplyQuickOrder()
    {
        if (_quickResult is not { HasProblems: false, Entries.Count: > 0 } result || Song is not { } song)
            return;
        var entries = result.Entries.ToList();
        if (song.ActiveVariant is null)
        {
            var variant = ArrangementEdit.Empty("Quick order", ExistingNames) with { Entries = entries };
            _main.CommitVariants("Applied the quick order", [.. song.Model.Arrangements, variant], variant.Id, keepDefault: true);
        }
        else
        {
            Edit("Applied the quick order", a => ArrangementEdit.Replace(a, entries));
        }

        QuickOrderText = string.Empty;
    }
}
