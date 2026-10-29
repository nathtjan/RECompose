// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RECompose.Engine;

/// <summary>The audio device the engine has open.</summary>
public sealed record DeviceInfo(
    string Type,
    string Name,
    double SampleRate,
    int BufferSize,
    IReadOnlyList<string> OutputChannels,
    IReadOnlyList<double> SampleRates,
    IReadOnlyList<int> BufferSizes,
    int OutputLatency,
    IReadOnlyList<string>? InputChannels = null)
{
    /// <summary>"Focusrite USB ASIO · 4 out · 48 kHz" (PLAT-10).</summary>
    public string StatusLabel
    {
        get
        {
            var khz = (SampleRate / 1000).ToString(SampleRate % 1000 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
            var driver = Type.Contains("ASIO", StringComparison.OrdinalIgnoreCase) || Name.Contains(Type, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" ({Type})";
            return $"{Name}{driver} · {OutputChannels.Count} out · {khz} kHz";
        }
    }

    internal static DeviceInfo? From(JsonNode? node) => node is JsonObject o
        ? new DeviceInfo(
            (string?)o["type"] ?? string.Empty,
            (string?)o["name"] ?? string.Empty,
            (double?)o["sampleRate"] ?? 0,
            (int?)o["bufferSize"] ?? 0,
            Strings(o["outputChannels"]),
            (o["sampleRates"] as JsonArray)?.Select(n => (double?)n ?? 0).ToList() ?? [],
            (o["bufferSizes"] as JsonArray)?.Select(n => (int?)n ?? 0).ToList() ?? [],
            (int?)o["outputLatency"] ?? 0,
            Strings(o["inputChannels"]))
        : null;

    internal static List<string> Strings(JsonNode? node) =>
        (node as JsonArray)?.Select(n => (string?)n ?? string.Empty).ToList() ?? [];
}

/// <summary>A driver type and its output devices.</summary>
public sealed record DeviceTypeInfo(string Name, IReadOnlyList<string> Devices, IReadOnlyList<string>? Inputs = null);

/// <summary>A track to load: file, device channels and mixer state.</summary>
public sealed record EngineTrack(string Id, string File, int Channel, int Width, double GainDb, bool Mute, bool Solo, bool SoloSafe)
{
    /// <summary>"click": no file, the engine synthesizes the click from the plan's grid (protocol 4, PB-70).</summary>
    public string? Generated { get; init; }

    /// <summary>The track follows each segment's key (protocol 5, DM-12): music yes, click and guide no.</summary>
    public bool Transposes { get; init; }

    /// <summary>Renders of this stem at other keys and tempos (protocol 11, REL-10); the engine plays the matching one.</summary>
    public IReadOnlyList<EngineRender> Renders { get; init; } = [];

    /// <summary>The engine's own click, on the given device channels (the Cue output, PB-85).</summary>
    public static EngineTrack Click(int channel, int width) =>
        new(ClickTrackId, string.Empty, channel, width, 0, false, false, SoloSafe: true) { Generated = "click" };

    public const string ClickTrackId = "generated-click";
}

/// <summary>A track as the engine opened it.</summary>
public sealed record LoadedTrack(string Id, int Channels, double SourceRate, long SourceFrames, bool Missing, string Problem, double GainDb, bool Mute, bool Solo, int Channel, int Width, int Renders = 0);

/// <summary>The song the engine has loaded. Frames are at <see cref="SampleRate"/>, the engine rate.</summary>
public sealed record LoadedSong(string SongId, double SampleRate, long LengthFrames, double MasterGainDb, IReadOnlyList<LoadedTrack> Tracks)
{
    public double DurationSec => SampleRate > 0 ? LengthFrames / SampleRate : 0;

    internal static LoadedSong? From(JsonNode? node)
    {
        if (node is not JsonObject o || (string?)o["songId"] is not { } id)
            return null;
        var tracks = (o["tracks"] as JsonArray ?? []).OfType<JsonObject>().Select(t => new LoadedTrack(
            (string?)t["id"] ?? string.Empty,
            (int?)t["channels"] ?? 0,
            (double?)t["sourceRate"] ?? 0,
            (long?)t["sourceFrames"] ?? 0,
            (bool?)t["missing"] ?? false,
            (string?)t["problem"] ?? string.Empty,
            (double?)t["gainDb"] ?? 0,
            (bool?)t["mute"] ?? false,
            (bool?)t["solo"] ?? false,
            (int?)t["output"]?["channel"] ?? 0,
            (int?)t["output"]?["width"] ?? 2,
            (int?)t["renders"] ?? 0)).ToList();
        return new LoadedSong(id, (double?)o["sampleRate"] ?? 48000, (long?)o["lengthFrames"] ?? 0, (double?)o["masterGainDb"] ?? 0, tracks);
    }
}

public enum TransportState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>How the current song hands over to the next one (protocol 7, SL-20, SL-30).</summary>
/// <param name="Type">"none" (stop, carry over), "segue", "crossfade" or "pad".</param>
/// <param name="AtSec">Plan seconds of the current plan where the transition starts: the tail, or the start of the fade.</param>
/// <param name="GapSec">Segue: seconds of silence between the songs, as heard.</param>
/// <param name="CrossfadeSec">Crossfade: seconds the current song fades out over.</param>
/// <param name="AutoStartSec">Pad: seconds after the start when the next song starts by itself, or null to wait for Play.</param>
public sealed record EngineTransition(string Type, double AtSec, double GapSec = 0, double CrossfadeSec = 0, double? AutoStartSec = null, PadTransition? Pad = null)
{
    public static EngineTransition None { get; } = new("none", 0);
}

/// <summary>The pad of a pad transition and its click (SL-20).</summary>
/// <param name="Key">The pitch class that fades in, or null.</param>
/// <param name="FadeOut">Fade the pad out when the next song starts.</param>
/// <param name="ClickBpm">The click tempo, or null for no click.</param>
public sealed record PadTransition(int? Key, bool FadeOut, double? ClickBpm, int BeatsPerBar, int ClickChannel, int ClickWidth);

/// <summary>The engine's state, sent at display rate while playing and on change otherwise.</summary>
public sealed record EngineSnapshot(
    TransportState State,
    long Frame,
    long TimeNs,
    int Latency,
    double SampleRate,
    string? SongId,
    long LengthFrames,
    bool Buffered,
    bool Waiting,
    long Starved,
    int Xruns,
    double Cpu,
    bool DeviceOpen,
    IReadOnlyDictionary<string, float> Meters,
    float MasterLeft,
    float MasterRight,
    long PlanFrames = 0,
    int? PadKey = null,
    float PadLevel = 0,
    string? NextSongId = null,
    bool NextBuffered = false,
    string Transition = "none",
    bool Holding = false,
    double HoldSec = 0,
    string Route = "none",
    double LostSec = 0,
    bool MainBack = false,
    VoiceState? Voice = null)
{
    /// <summary>The frame being heard: the render position minus the output latency while playing (PB-4).</summary>
    public long HeardFrame => State == TransportState.Playing ? Math.Max(0, Frame - Latency) : Frame;

    internal static EngineSnapshot From(JsonObject o)
    {
        var meters = new Dictionary<string, float>();
        foreach (var m in (o["meters"] as JsonArray ?? []).OfType<JsonObject>())
            if ((string?)m["id"] is { } id)
                meters[id] = (float?)m["peak"] ?? 0;
        var master = o["master"] as JsonArray;
        return new EngineSnapshot(
            ((string?)o["state"]) switch { "playing" => TransportState.Playing, "paused" => TransportState.Paused, _ => TransportState.Stopped },
            (long?)o["frame"] ?? 0,
            (long?)o["t"] ?? 0,
            (int?)o["latency"] ?? 0,
            (double?)o["sampleRate"] ?? 48000,
            (string?)o["songId"],
            (long?)o["lengthFrames"] ?? 0,
            (bool?)o["buffered"] ?? false,
            (bool?)o["waiting"] ?? false,
            (long?)o["starved"] ?? 0,
            (int?)o["xruns"] ?? 0,
            (double?)o["cpu"] ?? 0,
            (bool?)o["deviceOpen"] ?? false,
            meters,
            (float?)master?[0] ?? 0,
            (float?)master?[1] ?? 0,
            (long?)o["planFrames"] ?? 0,
            (int?)o["pad"]?["key"],
            (float?)o["pad"]?["level"] ?? 0,
            (string?)o["next"]?["songId"],
            (bool?)o["next"]?["buffered"] ?? false,
            (string?)o["transition"] ?? "none",
            (bool?)o["holding"] ?? false,
            (double?)o["holdSec"] ?? 0,
            (string?)o["route"] ?? "none",
            (double?)o["lostSec"] ?? 0,
            (bool?)o["mainBack"] ?? false,
            o["voice"] is JsonObject v ? new VoiceState((bool?)v["on"] ?? false, (float?)v["level"] ?? -100, (bool?)v["hearing"] ?? false) : null);
    }
}

/// <summary>
/// The engine lost its device (protocol 10, REL-20, REL-21). <paramref name="Route"/> is "fallback" (playing on
/// <paramref name="Fallback"/>) or "silent" (the clock keeps time with no sound).
/// </summary>
public sealed record DeviceLoss(string Name, string Route, string? Fallback, DeviceInfo? Device);

/// <summary>The talkback mic (protocol 12, VC-5): open, its peak in dBFS, and whether speech is going on.</summary>
public sealed record VoiceState(bool On, float LevelDb, bool Hearing);

/// <summary>One utterance from the voice input (protocol 12): 16 kHz 16-bit PCM; the last <paramref name="TrailingSec"/> are silence.</summary>
public sealed record Utterance(byte[] Pcm16, double DurationSec, double TrailingSec);

/// <summary>
/// Typed access to the engine protocol (docs/engine-protocol.md). Events are raised on a background
/// thread; the UI marshals them itself.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    public EngineClient(EngineSession session)
    {
        Session = session;
        session.Connection.EventReceived += OnEvent;
        _ = session.Connection.Completion.ContinueWith(_ => Disconnected?.Invoke(), TaskScheduler.Default);
    }

    public EngineSession Session { get; }

    public event Action<EngineSnapshot>? SnapshotReceived;
    public event Action<DeviceInfo?>? DeviceChanged;
    public event Action<string>? DeviceError;
    public event Action<string, string>? FileMissing; // track id, problem
    public event Action<LoadedSong>? SongReloaded;

    /// <summary>The next song took over as the current one (a segue, a crossfade, or promoteNext); its id.</summary>
    public event Action<string>? SongChanged;

    /// <summary>The current song played to its end with nothing handing over (a Stop transition); its id.</summary>
    public event Action<string>? SongEnded;
    public event Action? Disconnected;

    /// <summary>The device stopped; the engine moved to the fallback device or the silent clock (protocol 10).</summary>
    public event Action<DeviceLoss>? DeviceLost;

    /// <summary>The main device plays again (from the silent clock by itself, or after <see cref="SwitchToMainAsync"/>).</summary>
    public event Action<string, DeviceInfo?>? DeviceRestored;

    /// <summary>On the fallback device, the main one is connected again (REL-23).</summary>
    public event Action<string>? MainDeviceBack;

    /// <summary>The silent clock ran for its limit with no device; the song paused (REL-21). The limit in seconds.</summary>
    public event Action<double>? SilentClockStopped;

    /// <summary>An xrun on the device: the total count, and the plan frame it happened near (REL-24).</summary>
    public event Action<int, long>? Xrun;

    /// <summary>A MIDI input was unplugged or came back (REL-50): the inputs open now and the ones missing.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? MidiInputsChanged;

    /// <summary>A MIDI message from a controller or foot pedal (protocol 9, LIVE-52). Raised on a background thread.</summary>
    public event Action<RECompose.Core.Controls.MidiMessage>? MidiReceived;

    /// <summary>The voice input heard an utterance (protocol 12, VC-10).</summary>
    public event Action<Utterance>? UtteranceReceived;

    /// <summary>The voice input could not be opened; the outputs play on without it.</summary>
    public event Action<string>? VoiceError;

    /// <summary>
    /// The talkback mic: <paramref name="channel"/> (0-based) of the playing device's input, or of <paramref name="device"/>
    /// where the driver allows another input device; −1 switches it off. True when the input is open.
    /// </summary>
    public async Task<bool> SetVoiceInputAsync(int channel, string? device = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["channel"] = channel };
        if (!string.IsNullOrEmpty(device))
            request["device"] = device;
        var reply = await Session.Connection.RequestAsync("setVoiceInput", request, ct).ConfigureAwait(false);
        return (bool?)reply["on"] ?? false;
    }

    /// <summary>Tests: 16 kHz 16-bit PCM into the voice detector as if the mic had heard it.</summary>
    public Task InjectVoiceAsync(byte[] pcm16, CancellationToken ct = default) =>
        Session.Connection.RequestAsync("injectVoice", new JsonObject { ["samples"] = Convert.ToBase64String(pcm16) }, ct);

    public async Task<(IReadOnlyList<DeviceTypeInfo> Types, DeviceInfo? Current)> ListDevicesAsync(CancellationToken ct = default)
    {
        var reply = await Session.Connection.RequestAsync("listDevices", cancellationToken: ct).ConfigureAwait(false);
        var types = (reply["types"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => new DeviceTypeInfo((string?)t["name"] ?? string.Empty, DeviceInfo.Strings(t["devices"]), DeviceInfo.Strings(t["inputs"])))
            .ToList();
        return (types, DeviceInfo.From(reply["current"]));
    }

    /// <summary>The MIDI inputs the engine can open.</summary>
    public async Task<IReadOnlyList<string>> ListMidiInputsAsync(CancellationToken ct = default)
    {
        var reply = await Session.Connection.RequestAsync("listDevices", cancellationToken: ct).ConfigureAwait(false);
        return DeviceInfo.Strings(reply["midiInputs"]);
    }

    /// <summary>Listens to the named MIDI inputs, or to all of them when <paramref name="names"/> is null (protocol 9). Returns the ones that opened.</summary>
    public async Task<IReadOnlyList<string>> SetMidiInputsAsync(IEnumerable<string>? names, CancellationToken ct = default)
    {
        var request = new JsonObject();
        if (names is null)
            request["all"] = true;
        else
            request["names"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        var reply = await Session.Connection.RequestAsync("setMidiInputs", request, ct).ConfigureAwait(false);
        return DeviceInfo.Strings(reply["open"]);
    }

    /// <summary>Tests: makes the engine raise the event a MIDI device would.</summary>
    public Task InjectMidiAsync(string kind, int channel, int number, int value, CancellationToken ct = default) =>
        Session.Connection.RequestAsync("injectMidi", new JsonObject { ["kind"] = kind, ["channel"] = channel, ["number"] = number, ["value"] = value }, ct);

    /// <summary>Opens a device. Null fields keep the current or default choice.</summary>
    /// <exception cref="EngineErrorException">With code "deviceError" when nothing could be opened.</exception>
    public async Task<DeviceInfo?> SetDeviceAsync(string? type, string? name, double sampleRate, int bufferSize, CancellationToken ct = default)
    {
        var request = new JsonObject();
        if (!string.IsNullOrEmpty(type)) request["deviceType"] = type;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (sampleRate > 0) request["sampleRate"] = sampleRate;
        if (bufferSize > 0) request["bufferSize"] = bufferSize;
        var reply = await Session.Connection.RequestAsync("setDevice", request, ct).ConfigureAwait(false);
        return DeviceInfo.From(reply["device"]);
    }

    /// <summary>The device that takes over when the main one is lost (REL-20); a null name clears it. True when it is connected now.</summary>
    public async Task<bool> SetFallbackDeviceAsync(string? type, string? name, CancellationToken ct = default)
    {
        var request = new JsonObject();
        if (!string.IsNullOrEmpty(type)) request["deviceType"] = type;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        var reply = await Session.Connection.RequestAsync("setFallbackDevice", request, ct).ConfigureAwait(false);
        return (bool?)reply["present"] ?? false;
    }

    /// <summary>Goes back from the fallback device to the main one with the clock running (REL-23).</summary>
    /// <exception cref="EngineErrorException">With code "deviceError" when the main device is not there or will not open.</exception>
    public async Task<DeviceInfo?> SwitchToMainAsync(CancellationToken ct = default)
    {
        var reply = await Session.Connection.RequestAsync("switchToMain", cancellationToken: ct).ConfigureAwait(false);
        return DeviceInfo.From(reply["device"]);
    }

    /// <summary>Tests: the engine sees the main (or fallback) device as unplugged or back. Returns the route it is on.</summary>
    public async Task<string> InjectDeviceLossAsync(bool present = false, bool fallback = false, double? silentLimitSec = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["present"] = present, ["device"] = fallback ? "fallback" : "main" };
        if (silentLimitSec is { } limit)
            request["silentLimitSec"] = limit;
        var reply = await Session.Connection.RequestAsync("injectDeviceLoss", request, ct).ConfigureAwait(false);
        return (string?)reply["route"] ?? "none";
    }

    public Task<LoadedSong> LoadSongAsync(string songId, IEnumerable<EngineTrack> tracks, double masterGainDb, long startFrame = 0, CancellationToken ct = default) =>
        LoadAsync(songId, tracks, masterGainDb, startFrame, next: false, ct);

    /// <summary>Loads the next entry into the engine's second slot while the current one plays (protocol 7, PB-11).</summary>
    public Task<LoadedSong> LoadNextSongAsync(string songId, IEnumerable<EngineTrack> tracks, double masterGainDb, CancellationToken ct = default) =>
        LoadAsync(songId, tracks, masterGainDb, 0, next: true, ct);

    private async Task<LoadedSong> LoadAsync(string songId, IEnumerable<EngineTrack> tracks, double masterGainDb, long startFrame, bool next, CancellationToken ct)
    {
        var list = new JsonArray();
        foreach (var t in tracks)
        {
            var track = new JsonObject
            {
                ["id"] = t.Id,
                ["file"] = t.File,
                ["output"] = new JsonObject { ["channel"] = t.Channel, ["width"] = t.Width },
                ["gainDb"] = t.GainDb,
                ["mute"] = t.Mute,
                ["solo"] = t.Solo,
                ["soloSafe"] = t.SoloSafe,
            };
            if (t.Generated is { } generated)
                track["generated"] = generated;
            if (t.Transposes)
                track["transposes"] = true;
            if (t.Renders.Count > 0)
            {
                track["renders"] = new JsonArray(t.Renders
                    .Select(r => (JsonNode?)new JsonObject { ["file"] = r.File, ["semitones"] = r.Semitones, ["tempo"] = r.Tempo }).ToArray());
            }
            list.Add(track);
        }

        var request = new JsonObject
        {
            ["songId"] = songId,
            ["tracks"] = list,
            ["masterGainDb"] = masterGainDb,
            ["startFrame"] = startFrame,
        };
        if (next)
            request["slot"] = "next";
        var reply = await Session.Connection.RequestAsync("loadSong", request, ct).ConfigureAwait(false);
        return LoadedSong.From(reply) ?? throw new EngineProtocolException("loadSong returned no song.");
    }

    public Task UnloadSongAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("unloadSong", cancellationToken: ct);

    public Task UnloadNextSongAsync(CancellationToken ct = default) =>
        Session.Connection.RequestAsync("unloadSong", new JsonObject { ["slot"] = "next" }, ct);

    /// <summary>Describes how the current song hands over to the next (protocol 7); resent whenever the plan changes.</summary>
    public Task SetTransitionAsync(EngineTransition transition, CancellationToken ct = default)
    {
        var request = new JsonObject { ["kind"] = transition.Type, ["at"] = transition.AtSec, ["gapSec"] = transition.GapSec, ["crossfadeSec"] = transition.CrossfadeSec };
        if (transition.AutoStartSec is { } auto)
            request["autoStartSec"] = auto;
        if (transition.Pad is { } pad)
        {
            var p = new JsonObject { ["key"] = pad.Key, ["fadeOut"] = pad.FadeOut };
            if (pad.ClickBpm is { } bpm)
                p["click"] = new JsonObject { ["bpm"] = bpm, ["beatsPerBar"] = pad.BeatsPerBar, ["channel"] = pad.ClickChannel, ["width"] = pad.ClickWidth };
            request["pad"] = p;
        }

        return Session.Connection.RequestAsync("setTransition", request, ct);
    }

    /// <summary>Play with a song cued: starts the next song (on the next click downbeat in a pad transition). <paramref name="retuneTo"/> moves a sounding pad to that pitch class.</summary>
    public Task StartNextAsync(int? retuneTo = null, CancellationToken ct = default)
    {
        var request = new JsonObject();
        if (retuneTo is { } key)
            request["retuneTo"] = key;
        return Session.Connection.RequestAsync("startNext", request, ct);
    }

    /// <summary>Makes the cued next song the current one while the current one is stopped (Auto-advance, ⏭); returns it.</summary>
    public async Task<LoadedSong?> PromoteNextAsync(CancellationToken ct = default)
    {
        var reply = await Session.Connection.RequestAsync("promoteNext", cancellationToken: ct).ConfigureAwait(false);
        return LoadedSong.From(reply);
    }

    public Task PlayAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("play", cancellationToken: ct);
    public Task PauseAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("pause", cancellationToken: ct);
    public Task StopAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("stop", cancellationToken: ct);

    /// <summary>Releases the hold the playhead waits at (PB-52).</summary>
    public Task GoAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("go", cancellationToken: ct);

    /// <summary>Stops now: a 100 ms fade, pads and the transition click off, the next song stays cued (UI-40).</summary>
    public Task PanicAsync(CancellationToken ct = default) => Session.Connection.RequestAsync("panic", cancellationToken: ct);

    public Task SeekAsync(long frame, CancellationToken ct = default) =>
        Session.Connection.RequestAsync("seek", new JsonObject { ["frame"] = frame }, ct);

    public Task SetTrackAsync(string trackId, double? gainDb = null, bool? mute = null, bool? solo = null, (int Channel, int Width)? output = null,
        bool? transposes = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["trackId"] = trackId };
        if (gainDb is { } g) request["gainDb"] = g;
        if (mute is { } m) request["mute"] = m;
        if (solo is { } s) request["solo"] = s;
        if (transposes is { } t) request["transposes"] = t;
        if (output is { } o) request["output"] = new JsonObject { ["channel"] = o.Channel, ["width"] = o.Width };
        return Session.Connection.RequestAsync("setTrack", request, ct);
    }

    /// <summary>
    /// Sends the arrangement as timed segments (PB-40). <paramref name="positionSec"/> is where the playhead
    /// resumes, in plan seconds; null keeps the engine's current frame. <paramref name="tempo"/> is the entry
    /// tempo as a factor (PB-60): plan time stays in source seconds and the engine plays it that much faster.
    /// Returns the plan length in plan frames.
    /// </summary>
    public async Task<long> SetPlanAsync(IEnumerable<PlanSegment> segments, double? positionSec = null, PlanGrid? grid = null, double tempo = 1,
        bool next = false, CancellationToken ct = default, IEnumerable<PlanHold>? holds = null, double? swapAtSec = null)
    {
        var list = new JsonArray();
        foreach (var s in segments)
        {
            var mute = new JsonArray();
            foreach (var id in s.Mute)
                mute.Add(id);
            var segment = new JsonObject
            {
                ["start"] = s.Start,
                ["end"] = s.End,
                ["src"] = s.Src,
                ["fadeIn"] = s.FadeIn,
                ["fadeOut"] = s.FadeOut,
                ["mute"] = mute,
            };
            if (s.Click)
                segment["click"] = true;
            if (s.Key != 0)
                segment["key"] = s.Key;
            if (s.SongKey is { } songKey)
                segment["songKey"] = songKey;
            if (s.Pad is { } pad)
                segment["pad"] = pad;
            list.Add(segment);
        }

        var request = new JsonObject { ["segments"] = list };
        if (next)
            request["slot"] = "next";
        if (Math.Abs(tempo - 1) > 1e-9)
            request["tempo"] = tempo;
        if (grid is not null)
            request["grid"] = new JsonObject { ["origin"] = grid.Origin, ["beat"] = grid.Beat, ["beatsPerBar"] = grid.BeatsPerBar };
        if (positionSec is { } position)
            request["position"] = position;
        if (holds is not null)
        {
            var marks = new JsonArray();
            foreach (var h in holds)
            {
                var mark = new JsonObject { ["at"] = h.AtSec, ["pad"] = h.Pad, ["click"] = h.Click };
                if (h.PadKey is { } padKey)
                    mark["padKey"] = padKey;
                marks.Add(mark);
            }

            request["holds"] = marks;
        }

        // Protocol 8: while playing, the plan takes effect at this plan second with no seek (ARR-90).
        if (swapAtSec is { } swapAt)
            request["swapAt"] = swapAt;
        var reply = await Session.Connection.RequestAsync("setPlan", request, ct).ConfigureAwait(false);
        return (long?)reply["frames"] ?? 0;
    }

    /// <summary>Opens a pad set (protocol 6, SL-47): one file per pitch class. Returns the pitch classes whose file is missing.</summary>
    public async Task<IReadOnlyList<int>> LoadPadSetAsync(string id, IReadOnlyDictionary<int, string> samples, CancellationToken ct = default)
    {
        var list = new JsonArray();
        foreach (var (pitchClass, file) in samples)
            list.Add(new JsonObject { ["pitchClass"] = pitchClass, ["file"] = file });
        var reply = await Session.Connection.RequestAsync("loadPadSet", new JsonObject { ["id"] = id, ["samples"] = list }, ct).ConfigureAwait(false);
        return (reply["keys"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(k => (bool?)k["missing"] ?? false)
            .Select(k => (int?)k["pitchClass"] ?? -1)
            .ToList();
    }

    /// <summary>Starts the pad in a pitch class, crossfading from another, or stops it with null (SL-42).</summary>
    public Task PlayPadAsync(int? pitchClass, CancellationToken ct = default) =>
        Session.Connection.RequestAsync("pad", new JsonObject { ["key"] = pitchClass }, ct);

    /// <summary>Pad volume, fade time, Follow song and output (SL-44, SL-45, PB-85); null leaves a value as it is.</summary>
    public Task SetPadAsync(double? gainDb = null, double? fadeSec = null, bool? follow = null, (int Channel, int Width)? output = null, CancellationToken ct = default)
    {
        var request = new JsonObject();
        if (gainDb is { } g) request["gainDb"] = g;
        if (fadeSec is { } f) request["fadeSec"] = f;
        if (follow is { } fo) request["follow"] = fo;
        if (output is { } o) request["output"] = new JsonObject { ["channel"] = o.Channel, ["width"] = o.Width };
        return Session.Connection.RequestAsync("pad", request, ct);
    }

    public Task SetMasterAsync(double gainDb, CancellationToken ct = default) =>
        Session.Connection.RequestAsync("setMaster", new JsonObject { ["gainDb"] = gainDb }, ct);

    /// <summary>What the engine has loaded and open, for reattaching after an app restart.</summary>
    public async Task<(LoadedSong? Song, DeviceInfo? Device, EngineSnapshot Snapshot)> GetStateAsync(CancellationToken ct = default)
    {
        var reply = await Session.Connection.RequestAsync("getState", cancellationToken: ct).ConfigureAwait(false);
        return (LoadedSong.From(reply["song"]), DeviceInfo.From(reply["device"]), EngineSnapshot.From(reply["snapshot"]!.AsObject()));
    }

    /// <summary>Tests only: renders through a null device and returns per-channel peak and RMS.</summary>
    public async Task<(IReadOnlyList<double> Peak, IReadOnlyList<double> Rms, EngineSnapshot Snapshot)> RenderOfflineAsync(int frames, int channels = 2, string? path = null, CancellationToken ct = default)
    {
        var request = new JsonObject { ["frames"] = frames, ["channels"] = channels };
        if (path is not null) request["path"] = path;
        var reply = await Session.Connection.RequestAsync("renderOffline", request, ct).ConfigureAwait(false);
        return (
            (reply["peak"] as JsonArray ?? []).Select(n => (double?)n ?? 0).ToList(),
            (reply["rms"] as JsonArray ?? []).Select(n => (double?)n ?? 0).ToList(),
            EngineSnapshot.From(reply["snapshot"]!.AsObject()));
    }

    private void OnEvent(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "snapshot":
                SnapshotReceived?.Invoke(EngineSnapshot.From(message));
                break;
            case "deviceChanged":
                DeviceChanged?.Invoke(DeviceInfo.From(message["device"]));
                break;
            case "deviceError":
                DeviceError?.Invoke((string?)message["message"] ?? "The audio device reported an error.");
                break;
            case "deviceLost":
                DeviceLost?.Invoke(new DeviceLoss((string?)message["name"] ?? string.Empty, (string?)message["route"] ?? "silent",
                    (string?)message["fallback"], DeviceInfo.From(message["device"])));
                break;
            case "deviceRestored":
                DeviceRestored?.Invoke((string?)message["name"] ?? string.Empty, DeviceInfo.From(message["device"]));
                break;
            case "mainDeviceBack":
                MainDeviceBack?.Invoke((string?)message["name"] ?? string.Empty);
                break;
            case "silentClockStopped":
                SilentClockStopped?.Invoke((double?)message["afterSec"] ?? 60);
                break;
            case "xrun":
                Xrun?.Invoke((int?)message["count"] ?? 0, (long?)message["frame"] ?? 0);
                break;
            case "midiInputsChanged":
                MidiInputsChanged?.Invoke(DeviceInfo.Strings(message["open"]), DeviceInfo.Strings(message["missing"]));
                break;
            case "utterance":
                UtteranceReceived?.Invoke(new Utterance(Convert.FromBase64String((string?)message["samples"] ?? string.Empty),
                    (double?)message["durationSec"] ?? 0, (double?)message["trailingSec"] ?? 0));
                break;
            case "voiceError":
                VoiceError?.Invoke((string?)message["message"] ?? "The voice input could not be opened.");
                break;
            case "fileMissing":
                FileMissing?.Invoke((string?)message["trackId"] ?? string.Empty, (string?)message["problem"] ?? "File missing");
                break;
            case "songReloaded":
                if (LoadedSong.From(message) is { } song && (string?)message["slot"] != "next")
                    SongReloaded?.Invoke(song);
                break;
            case "songChanged":
                SongChanged?.Invoke((string?)message["songId"] ?? string.Empty);
                break;
            case "songEnded":
                SongEnded?.Invoke((string?)message["songId"] ?? string.Empty);
                break;
            case "midiIn":
            {
                var kind = ((string?)message["kind"]) switch
                {
                    "note" => RECompose.Core.Controls.ControlKind.Note,
                    "cc" => RECompose.Core.Controls.ControlKind.ControlChange,
                    "pc" => RECompose.Core.Controls.ControlKind.ProgramChange,
                    _ => RECompose.Core.Controls.ControlKind.Key,
                };
                if (kind != RECompose.Core.Controls.ControlKind.Key)
                    MidiReceived?.Invoke(new RECompose.Core.Controls.MidiMessage(kind, (int?)message["channel"] ?? 1, (int?)message["number"] ?? 0, (int?)message["value"] ?? 0));
                break;
            }
        }
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
