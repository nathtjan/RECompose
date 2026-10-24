// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Analysis;
using RECompose.Core.Audio;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Engine;

namespace RECompose.Presentation.Services;

/// <summary>The outcome of a click analysis, applied to the song by the caller (SEC-1).</summary>
public sealed record ClickResult(ClickAnalysis Analysis, string LogLine);

/// <summary>
/// The background analysis of milestone 2 (SL-54): click analysis and guide transcription,
/// each in a short-lived engine process at low priority. Nothing here touches the UI; the
/// caller applies the results on the UI thread to the newest copy of the song.
/// </summary>
public sealed class AnalysisService(DataStore store, IAnalysisTools tools, SpeechModel model)
{
    public SpeechModel Model => model;

    /// <summary>A guide transcript is current if the file hasn't changed since (size and time).</summary>
    public static string FileStamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length.ToString(CultureInfo.InvariantCulture)}-{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    public static Track? ClickTrack(Song song) => song.Tracks.FirstOrDefault(t => t.Role == TrackRole.Click && !t.Hidden);

    public static Track? GuideTrack(Song song) => song.Tracks.FirstOrDefault(t => t.Role == TrackRole.Guide && !t.Hidden);

    public static bool TranscriptCurrent(Song song) =>
        GuideTrack(song) is { } guide && song.GuideAnalysis is { } g && g.TrackId == guide.Id && g.FileStamp == FileStamp(guide.File);

    /// <summary>SEC-1 steps 1–3 on the song's click track; null when there is no usable click.</summary>
    public async Task<(ClickResult? Result, string? Problem)> AnalyseClickAsync(Song song, CancellationToken ct = default)
    {
        if (ClickTrack(song) is not { } click)
            return (null, "There is no click track. Set the tempo in the SONG tab.");

        var onsets = await tools.OnsetsAsync(click.File, ct).ConfigureAwait(false);
        if (!onsets.Ok)
            return (null, $"The click track couldn't be analysed: {onsets.Problem}");

        if (ClickFit.Fit(onsets.Onsets) is not { } grid)
            return (null, "No steady click was found in the click track. Set the tempo in the SONG tab.");

        var analysis = new ClickAnalysis
        {
            TrackId = click.Id,
            Bpm = Math.Round(grid.Bpm, 4),
            BeatsPerBar = grid.BeatsPerBar,
            FirstDownbeatSec = Math.Round(grid.FirstDownbeatSec, 4),
            MeterAssumed = grid.MeterAssumed,
            Onsets = onsets.Onsets.Count,
            OnsetsUsed = grid.OnsetsUsed,
        };

        var line = $"Click: {Core.Timing.Format.Bpm(analysis.Bpm)} BPM, {analysis.BeatsPerBar}/{song.BeatUnit}" +
                   (analysis.MeterAssumed ? " (meter assumed)" : string.Empty) +
                   $", first downbeat {analysis.FirstDownbeatSec.ToString("0.000", CultureInfo.InvariantCulture)} s";
        if (song.TempoSource == TempoSource.FileName && !song.TempoAssumed && Math.Abs(song.Bpm - analysis.Bpm) > 1)
            line += $"; the file name said {Core.Timing.Format.Bpm(song.Bpm)} BPM, the click wins (SL-53)";
        return (new ClickResult(analysis, line), null);
    }

    /// <summary>The song with the click's grid applied, sections moved onto the new bar lines.</summary>
    public static Song ApplyClick(Song song, ClickAnalysis analysis)
    {
        var updated = song with
        {
            Bpm = analysis.Bpm,
            BeatsPerBar = analysis.BeatsPerBar,
            FirstDownbeatSec = analysis.FirstDownbeatSec,
            TempoSource = TempoSource.Click,
            TempoAssumed = false,
            MeterAssumed = analysis.MeterAssumed,
            ClickAnalysis = analysis,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return updated.Sections.Count == 0 ? updated : updated with { Sections = SectionList.Resnap(updated.Sections, updated.Grid) };
    }

    /// <summary>SEC-11/12: transcribes the guide (finding or downloading the speech model first).</summary>
    public async Task<(GuideAnalysis? Result, string? Problem)> TranscribeAsync(Song song, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken ct = default)
    {
        if (GuideTrack(song) is not { } guide)
            return (null, "There is no guide track.");
        if (!File.Exists(guide.File))
            return (null, $"The guide track's file is missing ({Path.GetFileName(guide.File)}).");

        string modelPath;
        try
        {
            modelPath = await model.EnsureAsync(new Progress<double>(f => progress?.Report(("Downloading the speech model", f))), ct).ConfigureAwait(false);
        }
        catch (SpeechModelException ex)
        {
            return (null, ex.Message);
        }

        progress?.Report(("Listening to the guide", 0));
        var transcript = await tools.TranscribeAsync(guide.File, modelPath, SectionDetector.VocabularyPrompt,
            new Progress<double>(f => progress?.Report(("Listening to the guide", f))), ct).ConfigureAwait(false);
        if (!transcript.Ok)
            return (null, $"The guide couldn't be transcribed: {transcript.Problem}");

        return (new GuideAnalysis
        {
            TrackId = guide.Id,
            FileStamp = FileStamp(guide.File),
            Model = transcript.Model,
            Words = [.. transcript.Words],
        }, null);
    }

    /// <summary>
    /// "Do the music tracks sound here?" for the first-bar rule (SEC-14): any music track's
    /// peaks above −40 dBFS between the two times. Without peaks the answer is no.
    /// </summary>
    public Func<double, double, bool> MusicSounds(Song song)
    {
        var peaks = song.Tracks
            .Where(t => t.Role == TrackRole.Music && !t.Hidden)
            .Select(t => PeaksFile.Load(store.PeaksPath(song.Id, t.Id)))
            .OfType<PeaksFile>()
            .ToList();
        return (from, to) => peaks.Any(p =>
        {
            var (lo, hi) = p.Range(from, to);
            return Math.Max(Math.Abs(lo), Math.Abs(hi)) > 0.01;
        });
    }

    /// <summary>SEC-14…17 on the song's transcript.</summary>
    public DetectionResult Detect(Song song) =>
        SectionDetector.Detect(song.GuideAnalysis?.Words ?? [], song.Grid, song.DurationSec, MusicSounds(song), song.Sections);
}
