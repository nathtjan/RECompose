// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Services;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

// Running a service (SL-12, SL-20, SL-30, PB-11, PB-23…25): while an entry plays, the entry after it is loaded into the
// engine's second slot with its plan, and the transition out of the current entry is described to the engine, which
// starts the next song on the exact sample. The app follows what the engine reports.
public sealed partial class MainViewModel
{
    private ServiceEntry? _nextEntry;     // the entry the engine's next slot holds
    private Song? _nextSong;
    private LoadedSong? _nextLoaded;
    private DateTimeOffset _nextStamp;
    private int _nextVersion;
    private string _transition = "none";  // the engine's transition phase: none, armed, segue, waiting, handing
    private bool _awaitingNext;           // the current song ended and the next waits for Play (Carry over)

    /// <summary>The song after the loaded one in its service, for ⏭ and NEXT SONG (PB-24, LIVE-20).</summary>
    public ServiceEntry? NextServiceEntry =>
        _song?.Entry is { } entry && LoadedService is { } service ? service.Entries.ElementAtOrDefault(service.IndexOf(entry.Id) + 1) : null;

    public ServiceEntry? PreviousServiceEntry =>
        _song?.Entry is { } entry && LoadedService is { } service && service.IndexOf(entry.Id) is > 0 and var i ? service.Entries[i - 1] : null;

    /// <summary>The title of the song preloaded in the engine's next slot, or null.</summary>
    public string? NextSongTitle => _nextSong?.Title;

    /// <summary>The engine connection, for tests that drive an offline render.</summary>
    internal EngineClient? EngineForTests => _engine;

    /// <summary>Between two songs: a pad transition waits for Play, or a Carry over ended (status TRANSITION, NEXT SONG).</summary>
    public bool TransitionPending => _transition == "waiting" || _awaitingNext;

    public RelayCommand NextSongCommand => _nextSongCommand ??= new RelayCommand(NextSongAsync, () => NextServiceEntry is not null);

    private RelayCommand? _nextSongCommand;

    /// <summary>⏭ (PB-24): loads the next entry, with a confirmation while playing (PB-12).</summary>
    private Task NextSongAsync() => NextServiceEntry is { } next ? LoadEntryAsync(next.Id) : Task.CompletedTask;

    /// <summary>⏮ (PB-23): at the start of a stopped entry it selects the previous one; otherwise back to the plan start.</summary>
    private async Task ToStartAsync()
    {
        if (!IsPlaying && _planPosition < 0.05 && PreviousServiceEntry is { } previous)
        {
            await LoadEntryAsync(previous.Id);
            return;
        }

        _awaitingNext = false;
        await SeekAsync(0);
    }

    /// <summary>The engine's tracks for a song with a mix preset: its stems, and the engine's own click on the Cue output (PB-70, PB-85).</summary>
    private List<EngineTrack> BuildEngineTracks(Song model, MixPreset mix)
    {
        var tracks = model.Tracks.Where(t => !t.Hidden).Select(t =>
        {
            var (channel, width) = _settings.Audio.Resolve(t.Output);
            var m = mix.For(t.Id);
            return new EngineTrack(t.Id, t.File, channel, width, m.GainDb <= TrackViewModel.MinimumDb ? -100 : m.GainDb, m.Mute, m.Solo, t.IsClickOrGuide)
            {
                Transposes = mix.Transposes(t),
                Renders = RendersFor(t), // REL-10: rendered keys and tempos instead of live processing
            };
        }).ToList();
        var (cueChannel, cueWidth) = _settings.Audio.Resolve("cue");
        tracks.Add(EngineTrack.Click(cueChannel, cueWidth));
        return tracks;
    }

    // ---- Preloading the next entry (PB-11) ------------------------------------------------------------------

    /// <summary>
    /// Makes the engine's next slot hold the entry after the loaded one: nothing when it is already there, another song
    /// when the service or the song changed, an empty slot when there is no next entry.
    /// </summary>
    private async Task RefreshNextAsync()
    {
        var version = ++_nextVersion;
        if (_engine is not { } engine)
            return;

        try
        {
            var next = NextServiceEntry;
            var song = next is null ? null : SongOf(next.SongId);
            if (next is null || song is null)
            {
                if (_nextEntry is not null)
                {
                    ClearNext();
                    await engine.UnloadNextSongAsync();
                }
            }
            else if (!ReferenceEquals(_nextEntry, next) || _nextStamp != song.UpdatedAt || _nextLoaded is null)
            {
                var mix = song.MixPresets.FirstOrDefault(p => p.Id == next.MixPresetId) ?? song.DefaultMix;
                var loaded = await engine.LoadNextSongAsync(song.Id, BuildEngineTracks(song, mix), mix.MasterGainDb);
                if (version != _nextVersion)
                    return; // a newer refresh took over while this one was loading
                var plan = EntryPlan.Build(next, song);
                await engine.SetPlanAsync(plan.Segments(), 0, plan.Grid, plan.TempoFactor, next: true);
                _nextEntry = next;
                _nextSong = song;
                _nextLoaded = loaded;
                _nextStamp = song.UpdatedAt;
                AppLog.Write("Transport", $"Next: {song.Title} ({ServiceText.KeyText(song, next.KeyOffset)})");
            }

            if (version == _nextVersion)
                await SendTransitionAsync();
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Transport", ex); // nothing optional may stop the music (REL-50): the song just plays on its own
        }

        _ui.Post(() =>
        {
            NextSongCommand.Refresh();
            UpdateReadouts();
        });
    }

    private void ClearNext()
    {
        _nextEntry = null;
        _nextSong = null;
        _nextLoaded = null;
    }

    /// <summary>Describes the transition out of the loaded entry to the engine (SL-20, SL-30); resent whenever the plan, the entry or the next song changes.</summary>
    private async Task SendTransitionAsync(SongPlan? planOverride = null)
    {
        if (_engine is not { } engine || _song is not { Entry: { } entry } song)
            return;

        var transition = entry.TransitionAfter;
        var plan = planOverride ?? song.Plan;
        var tf = plan.TempoFactor;
        var result = EngineTransition.None;
        if (_nextEntry is { } nextEntry && _nextSong is { } next && !plan.IsEmpty)
        {
            switch (transition.Type)
            {
                case TransitionType.Segue:
                    result = new EngineTransition("segue", plan.TransitionPointSec, GapSec: transition.SegueGapBeats * plan.ChartGrid.BeatLength / tf);
                    break;

                case TransitionType.Crossfade:
                    // The outgoing song fades over the last CrossfadeSec of what is heard while the next starts at full level.
                    result = new EngineTransition("crossfade", Math.Max(0, plan.DurationSec - transition.CrossfadeSec * tf), CrossfadeSec: transition.CrossfadeSec);
                    break;

                case TransitionType.Pad:
                {
                    var nextKey = ServiceText.EntryKey(next, nextEntry.KeyOffset);
                    var bpm = ServiceText.ClickBpm(transition, next.Bpm * nextEntry.TempoFactor);
                    var (channel, width) = _settings.Audio.Resolve("cue");
                    var pad = new PadTransition(ServiceText.PadKey(transition, nextKey).PitchClass, transition.FadePadOutOnNextStart,
                        transition.ClickDuring ? bpm : null, next.BeatsPerBar, channel, width);
                    double? auto = transition.AutoStartNext ? transition.AutoStartAfterBars * next.BeatsPerBar * 60.0 / bpm : null;
                    result = new EngineTransition("pad", plan.TransitionPointSec, AutoStartSec: auto, Pad: pad);
                    break;
                }
            }
        }

        await engine.SetTransitionAsync(result);
    }

    /// <summary>Carry over (SL-20): the pitch class the playing pad moves to when the next song starts, or null.</summary>
    private int? CarryRetuneKey() =>
        _song?.Entry is { TransitionAfter: { Type: TransitionType.CarryOver, RetuneToNextKey: true } } && _nextEntry is { } nextEntry && _nextSong is { } next
            ? ServiceText.EntryKey(next, nextEntry.KeyOffset)?.PitchClass
            : null;

    // ---- Following the engine (SL-12, SL-30) ---------------------------------------------------------------------

    /// <summary>The next song took over (a segue, a crossfade, or Auto-advance): the header follows to its entry and the one after it is preloaded.</summary>
    private async Task OnSongChangedAsync(string songId)
    {
        if (_nextEntry is not { } entry || _nextSong is not { } model || _nextLoaded is not { } loaded || entry.SongId != songId)
        {
            AppLog.Write("Transport", $"The engine moved on to {songId}, which was not the song expected next");
            return;
        }

        var previousTitle = _song?.Title ?? string.Empty;
        var serviceId = _loadedServiceId;
        _awaitingNext = false;
        ClearNext();
        BindSong(model, loaded, entry, serviceId);
        _settings = _settings with { LastSongId = model.Id, LastEntryId = entry.Id };
        _store.SaveSettings(_settings);
        AppLog.Write("Transport", $"Transition: {previousTitle} → {model.Title}");
        await RefreshNextAsync();
    }

    /// <summary>The loaded song played to its end with nothing handing over (SL-12, SL-30): Auto-advance cues the next one, Carry over waits for Play.</summary>
    private async Task OnSongEndedAsync(string songId)
    {
        if (_song is not { Entry: { } entry } song || song.Id != songId || _nextEntry is null || LoadedService is not { } service)
            return;

        switch (entry.TransitionAfter.Type)
        {
            case TransitionType.Stop when service.AutoAdvance:
                await SendToEngineAsync(e => e.PromoteNextAsync()); // the engine reports songChanged and the header follows
                break;

            case TransitionType.CarryOver:
                _awaitingNext = true;
                UpdateTransitionState();
                break;
        }
    }

    private void UpdateTransitionState()
    {
        Status = _awaitingNext ? "TRANSITION" : Status;
        UpdateReadouts();
    }

    // ---- Auto-advance (SL-12) -------------------------------------------------------------------------------------

    public void ToggleAutoAdvance()
    {
        if (_service is not { } service)
            return;
        CommitService(service.AutoAdvance ? "Auto-advance off" : "Auto-advance on", service with { AutoAdvance = !service.AutoAdvance });
    }
}
