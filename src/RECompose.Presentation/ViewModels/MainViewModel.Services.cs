// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Commands;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Services;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

// Services (SL-1…12, DM-40…42): the service shown in the sidebar, the entry selected for editing and the entry that
// is loaded (UI-16, DM-80). Every edit is one undo step and is saved at once.
public sealed partial class MainViewModel
{
    private readonly List<Service> _services = [];
    private readonly HashSet<string> _scopeAskedThisSession = [];
    private Service? _service;
    private string? _selectedEntryId;
    private string? _loadedServiceId;

    /// <summary>The sidebar's service block and the ENTRY tab's settings of the selected entry.</summary>
    public SetlistViewModel Setlist { get; }

    /// <summary>The service shown in the sidebar (UI-11 service picker).</summary>
    public Service? CurrentService => _service;

    public IReadOnlyList<Service> AllServices => _services;

    /// <summary>The entry selected for editing in the sidebar (UI-16); it need not be loaded.</summary>
    public ServiceEntry? SelectedEntry => _service?.Entry(_selectedEntryId);

    /// <summary>The service entry the loaded song plays, or null when a library song is loaded.</summary>
    public ServiceEntry? LoadedEntry => _song?.Entry;

    /// <summary>The service the loaded entry belongs to.</summary>
    public Service? LoadedService => _services.FirstOrDefault(s => s.Id == _loadedServiceId);

    private void LoadServices()
    {
        _services.Clear();
        _services.AddRange(_store.LoadServices());
        _service = _services.FirstOrDefault(s => s.Id == _settings.LastServiceId)
            ?? ServiceText.Ordered(_services, DateTime.Now).FirstOrDefault();
        _selectedEntryId = _service?.Entry(_settings.LastEntryId)?.Id; // loaded again once the engine is up
        Setlist.Refresh();
    }

    /// <summary>The song of an entry: the loaded copy when it is loaded, otherwise the one on disk.</summary>
    public Song? SongOf(string songId) => CurrentSong(songId);

    /// <summary>Runs <paramref name="action"/> on the UI thread once the current input event is done.</summary>
    internal void Later(Action action) => _ui.Queue(action);

    // ---- Choosing and loading (UI-11, SL-4, PB-12) ----------------------------------------------

    public void SelectService(string? id)
    {
        var service = _services.FirstOrDefault(s => s.Id == id);
        if (service is null || service.Id == _service?.Id)
            return;
        _service = service;
        _selectedEntryId = service.Entries.Any(e => e.Id == LoadedEntry?.Id) ? LoadedEntry!.Id : null;
        _settings = _settings with { LastServiceId = service.Id };
        _store.SaveSettings(_settings);
        Setlist.Refresh();
        Entry.Refresh();
    }

    /// <summary>A single click on a row: the ENTRY tab edits this entry (UI-16).</summary>
    public void SelectEntry(string? entryId)
    {
        if (entryId == _selectedEntryId)
            return;
        _selectedEntryId = _service?.Entry(entryId)?.Id;
        Setlist.Refresh();
        Entry.Refresh();
    }

    /// <summary>
    /// A double click, Enter or Load: the entry's song is loaded and plays the entry (PB-10). While another song plays,
    /// loading needs a confirmation (PB-12).
    /// </summary>
    public async Task LoadEntryAsync(string entryId)
    {
        if (_service?.Entry(entryId) is not { } entry)
            return;
        if (LoadedEntry?.Id == entryId && _loadedServiceId == _service.Id)
            return;
        if (IsPlaying && !await _dialogs.ConfirmAsync("Load another song", $"{_song?.Title} is playing. Stop it and load {SongOf(entry.SongId)?.Title ?? "the next song"}?", "Load"))
            return;
        if (SongOf(entry.SongId) is null)
        {
            ShowToast("This song is no longer in the library.");
            return;
        }

        if (IsPlaying && _engine is { } engine)
            await engine.StopAsync();
        _selectedEntryId = entryId;
        await LoadSongAsync(entry.SongId, force: true, entry: entry, serviceId: _service.Id);
    }

    // ---- Services (SL-1, SL-2) --------------------------------------------------------------------

    public async Task NewServiceAsync()
    {
        var sunday = NextSunday(DateTime.Now);
        var initial = new ServiceDetails($"Sunday {sunday.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}", sunday);
        if (await _dialogs.AskServiceDetailsAsync("New service", "Create", initial) is not { } details)
            return;
        var service = ServiceEdit.New(details.Name, details.Date);
        CommitServiceList($"New service {service.Name}", add: service, remove: null);
        ShowToast($"Created {service.Name}", canUndo: true);
    }

    public async Task DuplicateServiceAsync()
    {
        if (_service is not { } source)
            return;
        var date = source.Date?.AddDays(7);
        var name = date is { } d && source.Date is not null ? $"Sunday {d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}" : $"{source.Name} (copy)";
        if (await _dialogs.AskServiceDetailsAsync("Duplicate service", "Duplicate", new ServiceDetails(name, date)) is not { } details)
            return;
        var copy = ServiceEdit.Duplicate(source, details.Name, details.Date);
        CommitServiceList($"Duplicated {source.Name} as {copy.Name}", add: copy, remove: null);
        ShowToast($"{copy.Name}: {copy.Entries.Count} songs copied", canUndo: true);
    }

    public async Task RenameServiceAsync()
    {
        if (_service is not { } service)
            return;
        if (await _dialogs.AskServiceDetailsAsync("Rename service", "Save", new ServiceDetails(service.Name, service.Date)) is not { } details)
            return;
        CommitService($"Renamed {service.Name}", ServiceEdit.Rename(service, details.Name, details.Date));
    }

    public async Task DeleteServiceAsync()
    {
        if (_service is not { } service)
            return;
        if (_loadedServiceId == service.Id && IsPlaying)
        {
            ShowToast("Stop the song before deleting its service.");
            return;
        }

        var songs = service.Entries.Count == 1 ? "1 song" : $"{service.Entries.Count} songs";
        if (!await _dialogs.ConfirmAsync("Delete service", $"Delete {service.Name} with its {songs}? The songs stay in the library.", "Delete"))
            return;
        CommitServiceList($"Deleted {service.Name}", add: null, remove: service);
        ShowToast($"Deleted {service.Name}", canUndo: true);
    }

    private static DateTime NextSunday(DateTime now)
    {
        var days = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        return now.Date.AddDays(days == 0 && now.Hour >= 12 ? 7 : days).AddHours(9);
    }

    // ---- Entries (SL-4, SL-5, SL-6) -----------------------------------------------------------------

    /// <summary>Adds a library song to the current service (drag from the library, "Add to {service}", SL-5).</summary>
    public void AddSongToService(string songId, int? index = null)
    {
        if (_service is not { } service || SongOf(songId) is not { } song)
            return;
        var entry = ServiceEdit.NewEntry(song);
        CommitService($"Added {song.Title}", ServiceEdit.Add(service, entry, index));
        SelectEntry(entry.Id);
    }

    /// <summary>Removes an entry. The loaded entry can't be removed while it plays (SL-6).</summary>
    public void RemoveEntry(string entryId)
    {
        if (_service?.Entry(entryId) is not { } entry)
            return;
        if (IsPlaying && LoadedEntry?.Id == entryId)
        {
            ShowToast("This song is playing. Stop it before removing it.");
            return;
        }

        var title = SongOf(entry.SongId)?.Title ?? "the song";
        var index = _service.IndexOf(entryId);
        CommitService($"Removed {title}", ServiceEdit.Remove(_service, entryId));
        if (_selectedEntryId == entryId)
            SelectEntry(_service.Entries.ElementAtOrDefault(Math.Min(index, _service.Entries.Count - 1))?.Id);
        ShowToast($"Removed {title}", canUndo: true);
    }

    /// <summary>Moves an entry to a 0-based position (drag, Alt+arrows); its transition goes with it (SL-4).</summary>
    public void MoveServiceEntry(string entryId, int newIndex)
    {
        if (_service is not { } service || service.Entry(entryId) is not { } entry)
            return;
        newIndex = Math.Clamp(newIndex, 0, Math.Max(0, service.Entries.Count - 1));
        if (newIndex == service.IndexOf(entryId))
            return;
        CommitService($"Moved {SongOf(entry.SongId)?.Title ?? "the song"}", ServiceEdit.Move(service, entryId, newIndex));
    }

    /// <summary>Changes one entry of the current service as one undo step; quick repeats with the same key merge.</summary>
    internal void UpdateServiceEntry(string entryId, string description, Func<ServiceEntry, ServiceEntry> change, string? mergeKey = null)
    {
        var service = _services.FirstOrDefault(s => s.Entries.Any(e => e.Id == entryId));
        if (service is null)
            return;
        var after = ServiceEdit.Update(service, entryId, change);
        if (after.Entry(entryId) == service.Entry(entryId))
            return;
        CommitService(description, after, mergeKey);
    }

    // ---- Key, tempo, count-in, mix, notes, transition (SL-7, PB-60…62) ------------------------------------

    /// <summary>KEY −/+ (PB-60, Ctrl+↑/↓): the selected entry, or the loaded one.</summary>
    public void StepKey(int delta, string? entryId = null)
    {
        if (TargetEntry(entryId) is not { } entry || SongOf(entry.SongId) is not { } song)
            return;
        var offset = Math.Clamp(entry.KeyOffset + delta, -ServiceEntry.MaxKeyOffset, ServiceEntry.MaxKeyOffset);
        if (offset == entry.KeyOffset)
            return;
        var before = ServiceText.EntryKey(song, entry.KeyOffset)?.ToDisplayString() ?? ServiceText.Signed(entry.KeyOffset);
        var after = ServiceText.EntryKey(song, offset)?.ToDisplayString() ?? ServiceText.Signed(offset);
        UpdateServiceEntry(entry.Id, $"Key {before} → {after}", e => e with { KeyOffset = offset }, $"key:{entry.Id}");
        ShowToast($"Key {before} → {after}", canUndo: true);
    }

    /// <summary>TEMPO −/+ by 1 % (Shift: 5 %), 50–150 % (PB-60).</summary>
    public void StepTempo(int delta, string? entryId = null)
    {
        if (TargetEntry(entryId) is not { } entry)
            return;
        SetTempo(entry.Id, entry.TempoPercent + delta);
    }

    public void SetTempo(string entryId, int percent)
    {
        if (_services.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == entryId) is not { } entry)
            return;
        percent = Math.Clamp(percent, ServiceEntry.MinTempoPercent, ServiceEntry.MaxTempoPercent);
        if (percent == entry.TempoPercent)
            return;
        UpdateServiceEntry(entryId, $"Tempo {entry.TempoPercent}% → {percent}%", e => e with { TempoPercent = percent }, $"tempo:{entryId}");
    }

    public void SetKeyOffset(string entryId, int offset)
    {
        if (_services.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == entryId) is not { } entry)
            return;
        StepKey(Math.Clamp(offset, -ServiceEntry.MaxKeyOffset, ServiceEntry.MaxKeyOffset) - entry.KeyOffset, entryId);
    }

    /// <summary>A key typed into KEY (PB-60): the selected entry, or the loaded one.</summary>
    public void TypeKey(string text)
    {
        if (TargetEntry(null) is not { } entry || SongOf(entry.SongId) is not { } song)
            return;
        if (ServiceText.ParseKeyOffset(song, text) is { } offset)
            SetKeyOffset(entry.Id, offset);
        else
            ShowToast($"{text.Trim()} isn't a key. Type one such as B♭, or semitones such as +2.");
    }

    /// <summary>A tempo typed into TEMPO (PB-60): a percent or a BPM.</summary>
    public void TypeTempo(string text)
    {
        if (TargetEntry(null) is not { } entry || SongOf(entry.SongId) is not { } song)
            return;
        if (ServiceText.ParseTempoPercent(song, text) is { } percent)
            SetTempo(entry.Id, percent);
        else
            ShowToast($"{text.Trim()} isn't a tempo. Type a percent such as 95, or a BPM such as 68 BPM.");
    }

    public void SetCountIn(string entryId, CountIn countIn) =>
        UpdateServiceEntry(entryId, "Count-in", e => e with { CountIn = countIn });

    public void SetNotes(string entryId, string notes) =>
        UpdateServiceEntry(entryId, "Notes", e => e with { Notes = notes ?? string.Empty }, $"notes:{entryId}");

    public void SetTransition(string entryId, Transition transition) =>
        UpdateServiceEntry(entryId, $"Transition {ServiceText.Chip(transition, null, null)}", e => e with { TransitionAfter = transition });

    /// <summary>Opens the transition popover for the gap after an entry (SL-21).</summary>
    public async Task EditTransitionAsync(string entryId)
    {
        if (_services.FirstOrDefault(s => s.Entries.Any(e => e.Id == entryId)) is not { } service)
            return;
        var index = service.IndexOf(entryId);
        var entry = service.Entries[index];
        var next = service.Entries.ElementAtOrDefault(index + 1);
        var song = SongOf(entry.SongId);
        var nextSong = next is null ? null : SongOf(next.SongId);
        var vm = new TransitionViewModel(entry.TransitionAfter, song?.Title ?? "this song", nextSong?.Title ?? "the next song",
            nextSong is null ? null : ServiceText.EntryKey(nextSong, next!.KeyOffset), nextSong?.Bpm * (next?.TempoFactor ?? 1));
        if (await _dialogs.EditTransitionAsync(vm))
            SetTransition(entryId, vm.Result);
    }

    /// <summary>The arrangement an entry plays (ENTRY tab variant select, sidebar menu "Variant ▸").</summary>
    public void SetEntryArrangement(string entryId, ArrangementChoice choice, string? name = null)
    {
        UpdateServiceEntry(entryId, $"Plays {name ?? choice.Type.ToString()}", e => e with { Arrangement = choice });
        Entry.Refresh();
    }

    /// <summary>"Edit for this service only" (SL-4 menu, ARR-12): the entry gets a custom copy of its variant.</summary>
    public void MakeEntryCustom(string entryId)
    {
        if (_services.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == entryId) is not { } entry || SongOf(entry.SongId) is not { } song)
            return;
        var custom = ServiceEdit.MakeCustom(entry, song);
        if (custom.Arrangement.Type != ArrangementChoiceType.Custom)
        {
            ShowToast(entry.Arrangement.Type == ArrangementChoiceType.Custom ? "This entry already has its own arrangement." : "Make a variant first, or edit the order in the ENTRY tab.");
            return;
        }

        UpdateServiceEntry(entryId, "Custom for this service", _ => custom);
        ShowToast($"{song.Title}: custom for this service", canUndo: true);
    }

    /// <summary>"Revert to 'Short'": the entry plays its variant again (ARR-10).</summary>
    public void RevertEntry(string entryId)
    {
        if (_services.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == entryId) is not { } entry || SongOf(entry.SongId) is not { } song
            || ServiceEdit.RevertTarget(entry, song) is not { } variant)
            return;
        UpdateServiceEntry(entryId, $"Reverted to {variant.Name}", e => ServiceEdit.Revert(e, song));
        ShowToast($"Reverted to {variant.Name}", canUndo: true);
    }

    /// <summary>"Save as variant…": the custom arrangement becomes a variant of the song and the entry plays it (ARR-10).</summary>
    public async Task SaveEntryAsVariantAsync(string entryId)
    {
        var service = _services.FirstOrDefault(s => s.Entries.Any(e => e.Id == entryId));
        if (service?.Entry(entryId) is not { Arrangement.Type: ArrangementChoiceType.Custom } entry || SongOf(entry.SongId) is not { } song)
            return;
        var suggested = ServiceEdit.RevertTarget(entry, song)?.Name ?? "Variant";
        if (await _dialogs.AskNameAsync("Save as variant", "Name", suggested, "Save") is not { } name)
            return;
        var (songAfter, entryAfter, variant) = ServiceEdit.SaveAsVariant(entry, song, name);
        if (variant is null)
            return;
        Commit($"Saved {variant.Name} as a variant", songAfter, [ServiceEdit.Update(service, entryId, _ => entryAfter)]);
        ShowToast($"Saved {variant.Name}", canUndo: true);
    }

    /// <summary>The mix preset an entry uses (PB-81, DM-41).</summary>
    public void SetEntryMixPreset(string entryId, string presetId) =>
        UpdateServiceEntry(entryId, "Mix preset", e => e with { MixPresetId = presetId });

    private ServiceEntry? TargetEntry(string? entryId) =>
        entryId is not null ? _services.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == entryId) : SelectedEntry ?? LoadedEntry;

    // ---- Arrangements edited from a service (ARR-12) ------------------------------------------------------

    /// <summary>
    /// Where an edit of <paramref name="variant"/> made from the loaded entry goes: asked once per variant (ARR-12),
    /// unless the answer was remembered. Null = the user cancelled.
    /// </summary>
    internal async Task<VariantScope?> VariantScopeAsync(Arrangement variant)
    {
        if (_settings.VariantScopes?.TryGetValue(variant.Id, out var remembered) == true)
            return remembered == "all" ? VariantScope.AllServices : VariantScope.ThisService;
        if (_scopeAskedThisSession.Contains(variant.Id))
            return VariantScope.AllServices; // "This service only" makes a custom copy, so only "All" can be asked twice

        var others = _services.SelectMany(s => s.Entries)
            .Count(e => e.Id != LoadedEntry?.Id && e.Arrangement.Type == ArrangementChoiceType.Variant && e.Arrangement.ArrangementId == variant.Id);
        if (await _dialogs.AskVariantScopeAsync(variant.Name, others) is not { } answer)
            return null;
        if (answer.Scope == VariantScope.AllServices)
            _scopeAskedThisSession.Add(variant.Id);
        if (answer.Remember)
        {
            var scopes = new Dictionary<string, string>(_settings.VariantScopes ?? []) { [variant.Id] = answer.Scope == VariantScope.AllServices ? "all" : "service" };
            _settings = _settings with { VariantScopes = scopes };
            _store.SaveSettings(_settings);
        }

        return answer.Scope;
    }

    /// <summary>Stores a new custom arrangement on the loaded entry (ARR-12) as one undo step.</summary>
    internal void CommitCustom(string description, Arrangement custom)
    {
        if (LoadedEntry is not { } entry)
            return;
        UpdateServiceEntry(entry.Id, description, e => e with { Arrangement = ArrangementChoice.OfCustom(custom with { Origin = ArrangementOrigin.Copy }) });
        AppLog.Write("Arrangements", description);
        Entry.ApplyPendingSelection();
    }

    /// <summary>"This service only": the loaded entry's variant is copied, then changed (ARR-12, AT-15).</summary>
    internal void CommitCustomFromVariant(string description, Func<Arrangement, Arrangement> change)
    {
        if (LoadedEntry is not { } entry || _song is null)
            return;
        var copy = ServiceEdit.MakeCustom(entry, _song.Model);
        if (copy.Arrangement.Arrangement is not { } custom)
            return;
        CommitCustom(description, change(custom));
        ShowToast($"{_song.Title}: custom for this service", canUndo: true);
    }

    /// <summary>
    /// ARR-11: deleting a variant that service entries play asks what they play instead; with none, it just goes.
    /// The song's default falls back to as written when it was the default.
    /// </summary>
    internal async Task DeleteVariantAsync(Arrangement variant)
    {
        if (_song is not { } song)
            return;
        var users = _services.Sum(s => ServiceEdit.UsingVariant(s, song.Id, variant.Id).Count());
        var replacement = ArrangementChoice.AsWritten;
        if (users > 0)
        {
            var others = song.Model.Arrangements.Where(a => a.Id != variant.Id).ToList();
            var names = others.Select(a => a.Name).Prepend(EntryArrangement.AsWrittenName).ToList();
            var entries = users == 1 ? "1 service entry plays" : $"{users} service entries play";
            if (await _dialogs.ChooseAsync("Delete variant", $"{entries} {variant.Name}. What should they play instead?", names, "Delete") is not { } choice)
                return;
            if (choice > 0)
                replacement = ArrangementChoice.OfVariant(others[choice - 1].Id);
        }

        var variants = song.Model.Arrangements.Where(a => a.Id != variant.Id).ToList();
        var defaultId = song.Model.DefaultArrangementId == variant.Id ? null : song.Model.DefaultArrangementId;
        var songAfter = song.Model with { Arrangements = variants, DefaultArrangementId = defaultId, UpdatedAt = DateTimeOffset.UtcNow };
        var servicesAfter = _services.Select(s => ServiceEdit.ReplaceVariant(s, song.Id, variant.Id, replacement)).ToList();
        Commit($"Deleted {variant.Name}", songAfter, servicesAfter, (null, true));
        ShowToast($"Deleted {variant.Name}", canUndo: true);
    }

    /// <summary>Sections changed (SEC-17, SEC-54): custom arrangements of the song's entries follow, like its variants.</summary>
    private List<Service> ServicesWithCustomsMapped(string songId, Func<List<Arrangement>, List<Arrangement>> map) =>
        _services.Select(s => ServiceEdit.MapCustomArrangements(s, songId, map)).Where(s => !ReferenceEquals(s, _services.First(x => x.Id == s.Id))).ToList();

    // ---- Committing (undo, autosave, live plan) ------------------------------------------------------------

    /// <summary>Replaces one service as one undo step.</summary>
    private void CommitService(string description, Service after, string? mergeKey = null) =>
        Commit(description, null, [after], mergeKey: mergeKey);

    /// <summary>
    /// Applies a song and any number of services as one undo step: a variant saved from an entry changes both, a
    /// deleted variant changes every service that played it.
    /// </summary>
    private void Commit(string description, Song? songAfter, IReadOnlyList<Service> servicesAfter, (string? Id, bool Set) active = default, string? mergeKey = null)
    {
        var songBefore = songAfter is null ? null : CurrentSong(songAfter.Id);
        var activeBefore = (_song?.ActiveVariantId, active.Set);
        var servicesBefore = servicesAfter.Select(s => _services.FirstOrDefault(x => x.Id == s.Id)).OfType<Service>().ToList();

        void Apply(Song? song, IReadOnlyList<Service> services, (string?, bool) act)
        {
            if (song is not null)
                UpdateSong(song.Id, _ => song, act);
            foreach (var service in services)
                ApplyService(service);
        }

        Apply(songAfter, servicesAfter, active);
        _undo.Push(new UndoableChange(description, () => Apply(songBefore, servicesBefore, activeBefore), () => Apply(songAfter, servicesAfter, active))
        {
            MergeKey = mergeKey,
        });
        AppLog.Write("Services", description);
    }

    /// <summary>Adds or removes a whole service as one undo step (New, Duplicate, Delete).</summary>
    private void CommitServiceList(string description, Service? add, Service? remove)
    {
        var currentBefore = _service?.Id;

        void Apply(Service? adding, Service? removing, string? current)
        {
            if (removing is not null)
            {
                _services.RemoveAll(s => s.Id == removing.Id);
                _store.DeleteService(removing.Id);
            }

            if (adding is not null)
            {
                _services.RemoveAll(s => s.Id == adding.Id);
                _services.Add(adding);
                _store.SaveService(adding);
            }

            _service = _services.FirstOrDefault(s => s.Id == current) ?? ServiceText.Ordered(_services, DateTime.Now).FirstOrDefault();
            _selectedEntryId = _service?.Entry(_selectedEntryId)?.Id;
            _settings = _settings with { LastServiceId = _service?.Id };
            _store.SaveSettings(_settings);
            Setlist.Refresh();
            Entry.Refresh();
        }

        var currentAfter = add?.Id ?? (remove?.Id == currentBefore ? null : currentBefore);
        Apply(add, remove, currentAfter);
        _undo.Push(new UndoableChange(description, () => Apply(remove, add, currentBefore), () => Apply(add, remove, currentAfter)));
        AppLog.Write("Services", description);
    }

    /// <summary>Saves a service, shows it, and plays the change when it touches the loaded entry (ARR-90).</summary>
    private void ApplyService(Service service)
    {
        service = service with { UpdatedAt = DateTimeOffset.UtcNow };
        var index = _services.FindIndex(s => s.Id == service.Id);
        if (index < 0)
            _services.Add(service);
        else
            _services[index] = service;
        _store.SaveService(service);
        if (_service?.Id == service.Id)
            _service = service;
        ScheduleRenders(service); // a new key or tempo starts its renders (REL-11)

        if (_song is { Entry: { } loaded } song && _loadedServiceId == service.Id)
        {
            var now = service.Entry(loaded.Id);
            if (now is null)
            {
                song.SetEntry(null); // removed while stopped: the song stays loaded as a library song
                _loadedServiceId = null;
            }
            else if (now != loaded)
            {
                var mixChanged = now.MixPresetId != loaded.MixPresetId;
                song.SetEntry(now);
                if (mixChanged)
                    ApplyMixPreset();
                UpdateReadouts();
            }
        }

        Setlist.Refresh();
        if (_loadedServiceId == service.Id)
            _ = RefreshNextAsync(); // the next entry, or the transition out of this one, may have changed
    }

    /// <summary>The strips, the master and the engine take the gains of the mix preset in use (PB-81).</summary>
    private void ApplyMixPreset()
    {
        if (_song is null)
            return;
        var mix = _song.Mix;
        _applyingUndo = true;
        try
        {
            foreach (var track in _song.Tracks)
            {
                var m = mix.For(track.Id);
                track.Apply(m.GainDb, m.Mute, m.Solo);
                track.ApplyTransposes(mix.Transposes(track.Track));
                _committed[track.Id] = (track.GainDb, track.Mute, track.Solo);
                SendTrack(track, "all");
            }

            _song.Master.Apply(gainDb: mix.MasterGainDb);
            _committed["master"] = (_song.Master.GainDb, false, false);
            SendTrack(_song.Master, "gain");
        }
        finally
        {
            _applyingUndo = false;
        }
    }

    // ---- Plans of the service's entries (ARR-80) ------------------------------------------------------------

    /// <summary>Each entry of a service with its song and plan, for durations and the rows.</summary>
    internal List<PlannedEntry> Planned(Service service)
    {
        var songs = new Dictionary<string, Song?>();
        return service.Entries.Select(e =>
        {
            if (!songs.TryGetValue(e.SongId, out var song))
                songs[e.SongId] = song = SongOf(e.SongId);
            var plan = song is null ? null : _song?.Entry?.Id == e.Id && _song.Id == song.Id ? _song.Plan : EntryPlan.Build(e, song);
            return new PlannedEntry(e, song, plan);
        }).ToList();
    }
}
