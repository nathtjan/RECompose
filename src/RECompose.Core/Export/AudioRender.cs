// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Services;
using RECompose.Core.Settings;

namespace RECompose.Core.Export;

public enum RenderFormat
{
    Wav16,
    Wav24,
    Wav32Float,
    Flac16,
    Flac24,
}

public static class RenderFormats
{
    public static bool IsFlac(this RenderFormat format) => format is RenderFormat.Flac16 or RenderFormat.Flac24;

    public static string Extension(this RenderFormat format) => format.IsFlac() ? ".flac" : ".wav";

    public static int Bits(this RenderFormat format) => format switch
    {
        RenderFormat.Wav16 or RenderFormat.Flac16 => 16,
        RenderFormat.Wav32Float => 32,
        _ => 24,
    };

    public static string Label(this RenderFormat format) => format switch
    {
        RenderFormat.Wav16 => "WAV 16-bit",
        RenderFormat.Wav24 => "WAV 24-bit",
        RenderFormat.Wav32Float => "WAV 32-bit float",
        RenderFormat.Flac16 => "FLAC 16-bit",
        _ => "FLAC 24-bit",
    };
}

/// <summary>
/// What one rendered file holds: the band mix, a device output as routed, every output in one multichannel file,
/// the tracks of a bus, or a single track.
/// </summary>
public enum RenderSourceKind
{
    Mix,
    Main,
    Cue,
    AllOutputs,
    Bus,
    Track,
}

/// <param name="Kind">What the file holds.</param>
/// <param name="Name">The bus name, or the track's name.</param>
/// <param name="Id">The track's id.</param>
public sealed record RenderSource(RenderSourceKind Kind, string Name = "", string? Id = null)
{
    public string Label => Kind switch
    {
        RenderSourceKind.Mix => "Mix",
        RenderSourceKind.Main => "Main",
        RenderSourceKind.Cue => "Cue",
        RenderSourceKind.AllOutputs => "All outputs",
        RenderSourceKind.Bus => $"Bus {Name}",
        _ => $"Track {Name}",
    };
}

public sealed record RenderOptions
{
    public IReadOnlyList<RenderSource> Sources { get; init; } = [];

    /// <summary>Follow the mixer: a muted track stays silent, a soloed one silences the rest, volumes apply. Off plays every track at 0 dB.</summary>
    public bool RespectMute { get; init; } = true;

    public bool RespectSolo { get; init; } = true;
    public bool RespectTrackVolume { get; init; } = true;
    public bool RespectMasterVolume { get; init; } = true;

    /// <summary>The arrangement's own mutes ("Drums out" on a section).</summary>
    public bool RespectArrangementMutes { get; init; } = true;

    public RenderFormat Format { get; init; } = RenderFormat.Wav24;
}

/// <summary>A track as the engine would get it for the live song: where it plays and the mixer's state.</summary>
public sealed record RenderTrack(string Id, string Group, string Output, int Channel, int Width, double GainDb, bool Mute, bool Solo, bool SoloSafe);

public sealed record PassTrack(string Id, int Channel, int Width, double GainDb, bool Mute, bool Solo);

/// <summary>One file: the tracks as the engine gets them for it, and the output channels it writes.</summary>
public sealed record RenderPass(RenderSource Source, string FileName, int Channel, int Width, IReadOnlyList<PassTrack> Tracks, string? Problem);

/// <summary>Turns the options into one engine pass per file. Pure.</summary>
public static class AudioRender
{
    public const int FlacMaxChannels = 8;

    public static IReadOnlyList<RenderPass> Plan(string songTitle, IReadOnlyList<RenderTrack> tracks, AudioSettings audio, RenderOptions options,
        IEnumerable<string> existingFiles)
    {
        var taken = existingFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var anySolo = options.RespectSolo && tracks.Any(t => t.Solo);
        var passes = new List<RenderPass>();
        foreach (var source in options.Sources)
        {
            var routed = source.Kind is RenderSourceKind.Main or RenderSourceKind.Cue or RenderSourceKind.AllOutputs;
            var (channel, width) = source.Kind switch
            {
                RenderSourceKind.Main => (audio.MainChannel, audio.MainWidth),
                RenderSourceKind.Cue => (audio.CueChannel, audio.CueWidth),
                RenderSourceKind.AllOutputs => (0, Math.Max(2, tracks.Max(t => t.Channel + t.Width))),
                _ => (0, 2),
            };

            var audible = false;
            var passTracks = tracks.Select(t =>
            {
                var included = Includes(source, t);
                var mute = !included || (options.RespectMute && t.Mute);
                var solo = options.RespectSolo && t.Solo;
                audible |= !mute && (!anySolo || solo || t.SoloSafe);
                var (c, w) = routed ? (t.Channel, t.Width) : (0, 2);
                return new PassTrack(t.Id, c, w, options.RespectTrackVolume ? t.GainDb : 0, mute, solo);
            }).ToList();

            string? problem = null;
            if (!audible)
                problem = "Nothing in it would be heard with the mute and solo states respected.";
            else if (options.Format.IsFlac() && width > FlacMaxChannels)
                problem = $"FLAC holds up to {FlacMaxChannels} channels; this has {width}.";

            var name = Unique(songTitle, source, options.Format, taken);
            taken.Add(Path.GetFileNameWithoutExtension(name));
            passes.Add(new RenderPass(source, name, channel, width, passTracks, problem));
        }

        return passes;
    }

    /// <summary>The bus a track plays on: its named bus, else its group's.</summary>
    public static string BusOf(string group, string output) =>
        OutputRef.Parse(output) is { Kind: OutputKind.Bus, Bus: { } bus } ? bus : group;

    private static bool Includes(RenderSource source, RenderTrack t) => source.Kind switch
    {
        RenderSourceKind.Mix => t.Group is not (TrackGroups.Click or TrackGroups.Guide),
        RenderSourceKind.Bus => string.Equals(BusOf(t.Group, t.Output), source.Name, StringComparison.OrdinalIgnoreCase),
        RenderSourceKind.Track => t.Id == source.Id,
        _ => true,
    };

    private static string Unique(string songTitle, RenderSource source, RenderFormat format, IEnumerable<string> taken)
    {
        // The Windows set everywhere, so the folder can move between computers.
        var stem = string.Concat($"{songTitle} - {source.Label}".Where(c => !char.IsControl(c) && !"\\/:*?\"<>|".Contains(c))).Trim();
        return ServiceExchange.UniqueName(stem, taken) + format.Extension();
    }
}
