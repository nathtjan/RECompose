// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Automation;
using RECompose.Core.Export;
using RECompose.Core.Library;
using RECompose.Engine;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>Song › Render audio…: the loaded entry as it plays now, written to audio files by a second engine so the live one is left alone.</summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _renderCts;

    public bool IsRenderingAudio => _renderCts is not null;

    public async Task RenderAudioAsync()
    {
        if (_song is not { } song || _engine is null)
            return;
        if (IsRenderingAudio)
        {
            ShowToast("A render is already running.");
            return;
        }

        var model = song.Model;
        var sources = new List<RenderSourceChoice>
        {
            new("Mixes", new RenderSource(RenderSourceKind.Mix)),
            new("Outputs, as routed", new RenderSource(RenderSourceKind.Main)),
            new("Outputs, as routed", new RenderSource(RenderSourceKind.Cue)),
            new("Outputs, as routed", new RenderSource(RenderSourceKind.AllOutputs)),
        };
        var visible = model.Tracks.Where(t => !t.Hidden).ToList();
        foreach (var bus in visible.Select(t => AudioRender.BusOf(t.Group, t.Output)).Append(TrackGroups.Click).Append(TrackGroups.Guide).Distinct(StringComparer.OrdinalIgnoreCase))
            sources.Add(new("Buses", new RenderSource(RenderSourceKind.Bus, bus)));
        foreach (var track in visible)
            sources.Add(new("Tracks", new RenderSource(RenderSourceKind.Track, track.Name, track.Id)));

        var dialog = new RenderAudioViewModel(model.Title, sources);
        if (!await _dialogs.EditRenderAsync(dialog))
            return;
        var options = dialog.Options();
        if (options.Sources.Count == 0)
        {
            ShowToast("Nothing was chosen to render.");
            return;
        }

        if (await _dialogs.PickFolderAsync() is not { } folder)
            return;
        await RenderToFolderAsync(options, folder);
    }

    public void CancelRenderAudio() => _renderCts?.Cancel();

    /// <summary>Writes one file per chosen source into <paramref name="folder"/> and returns their names.</summary>
    internal async Task<IReadOnlyList<string>> RenderToFolderAsync(RenderOptions options, string folder)
    {
        if (_song is not { } song)
            return [];
        var model = song.Model;
        var plan = song.Plan;
        var mix = model.MixPresets.FirstOrDefault(p => p.Id == song.Entry?.MixPresetId) ?? model.DefaultMix;

        // The tracks as the engine gets them live, with the mixer as it stands now.
        var baseTracks = BuildEngineTracks(model, mix).Select(e =>
        {
            var live = song.Tracks.FirstOrDefault(t => t.Id == e.Id);
            return e with
            {
                GainDb = live?.EngineGainDb ?? e.GainDb,
                Mute = live?.Mute ?? e.Mute,
                Solo = live?.Solo ?? e.Solo,
            };
        }).ToList();
        var renderTracks = baseTracks.Select(e =>
        {
            var stem = model.Tracks.FirstOrDefault(t => t.Id == e.Id);
            var group = stem?.Group ?? (e.Id == EngineTrack.ClickTrackId ? TrackGroups.Click : TrackGroups.Guide);
            return new RenderTrack(e.Id, group, stem?.Output ?? "bus", e.Channel, e.Width, e.GainDb, e.Mute, e.Solo, e.SoloSafe);
        }).ToList();

        Directory.CreateDirectory(folder);
        var passes = AudioRender.Plan(model.Title, renderTracks, _settings.Audio, options, Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>());
        foreach (var skipped in passes.Where(p => p.Problem is not null))
            AppLog.Write("Render", $"Left out {skipped.FileName}: {skipped.Problem}");
        var todo = passes.Where(p => p.Problem is null).ToList();
        if (todo.Count == 0)
        {
            ShowToast(passes.Count > 0 ? $"Nothing was rendered. {passes[0].Problem}" : "Nothing was chosen to render.");
            return [];
        }

        var events = AutomationPlan.Build(plan, model, new AutomationOptions { ReplaceGuideCalls = _settings.ReplaceGuideCalls });
        var guideIds = GuideTrackIds(model);
        var segments = plan.Segments(events.GuideSilences, guideIds);
        if (!options.RespectArrangementMutes)
            segments = segments.Select(s => s with { Mute = s.Mute.Where(guideIds.Contains).ToList() }).ToList(); // the guide's own replacement mutes stay
        var calls = CallsFor(events, model, out _);
        var master = options.RespectMasterVolume ? song.Master.EngineGainDb : 0;

        var written = new List<string>();
        using var cts = _renderCts = new CancellationTokenSource();
        var ct = cts.Token;
        EngineSession? session = null;
        string? open = null;
        try
        {
            ShowToast("Starting the renderer…");
            var endpoint = Path.Combine(Path.GetDirectoryName(_engineOptions.Endpoint)!, $"render-{Guid.NewGuid():N}.sock");
            session = await EngineHost.ConnectOrStartAsync(_engineOptions with { Endpoint = endpoint, IdleExitSeconds = 5 }, ct);
            var engine = new EngineClient(session);

            for (var i = 0; i < todo.Count; i++)
            {
                var pass = todo[i];
                ShowToast($"Rendering {i + 1} of {todo.Count}: {pass.FileName}…");
                var tracks = baseTracks.Select(e =>
                {
                    var p = pass.Tracks.First(t => t.Id == e.Id);
                    return e with { Channel = p.Channel, Width = p.Width, GainDb = p.GainDb, Mute = p.Mute, Solo = p.Solo };
                }).ToList();

                var loaded = await engine.LoadSongAsync(model.Id, tracks, master, 0, ct);
                var planFrames = await engine.SetPlanAsync(segments, 0, plan.Grid, plan.TempoFactor, calls: calls, ct: ct);
                var total = (long)Math.Round(planFrames / plan.TempoFactor);
                await engine.PlayAsync(ct);

                var path = Path.Combine(folder, pass.FileName);
                open = path;
                var chunk = (int)(loaded.SampleRate * 10);
                for (long done = 0; done < total || done == 0; done += chunk)
                {
                    var frames = (int)Math.Min(chunk, total - done);
                    var last = done + frames >= total;
                    await engine.BounceAsync(frames, pass.Channel, pass.Width, done == 0 ? path : null, options.Format.IsFlac() ? "flac" : "wav", options.Format.Bits(), last, ct);
                    if (last)
                        break;
                }

                open = null;
                written.Add(pass.FileName);
            }

            AppLog.Write("Render", $"Rendered {written.Count} files of {model.Title} to {folder}");
            ShowToast($"Rendered {written.Count} file{(written.Count == 1 ? string.Empty : "s")} to {folder}." + (passes.Count > todo.Count ? $" {passes.Count - todo.Count} left out; the log says why." : string.Empty));
        }
        catch (OperationCanceledException)
        {
            ShowToast("Render cancelled.");
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or EngineUnavailableException or IOException)
        {
            AppLog.Error("Render", ex);
            Banner = $"The audio could not be rendered ({ex.Message}).";
        }
        finally
        {
            _renderCts = null;
            if (session is not null)
            {
                try
                {
                    await session.ShutdownAsync();
                }
                catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or IOException)
                {
                }
            }

            if (open is not null)
            {
                try
                {
                    File.Delete(open); // a half-written file
                }
                catch (IOException)
                {
                }
            }
        }

        return written;
    }
}
