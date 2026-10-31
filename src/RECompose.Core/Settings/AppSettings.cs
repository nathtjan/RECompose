// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;

namespace RECompose.Core.Settings;

/// <summary>Settings › Audio (SET-10).</summary>
public sealed record AudioSettings
{
    /// <summary>Driver type as the engine names it ("Windows Audio", "ASIO", "CoreAudio"); null = system default.</summary>
    public string? DeviceType { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>0 = the device's rate.</summary>
    public double SampleRate { get; init; }

    public int BufferSize { get; init; } = 256;

    /// <summary>The device that takes over when the main one is lost (REL-20); null = none.</summary>
    public string? FallbackDeviceType { get; init; }
    public string? FallbackDeviceName { get; init; }

    /// <summary>Go back to the main device at the next section boundary once it returns (REL-23).</summary>
    public bool AutoSwitchBack { get; init; } = true;

    /// <summary>Logical outputs (PB-85): 0-based first channel and width.</summary>
    public int MainChannel { get; init; }
    public int MainWidth { get; init; } = 2;
    public int CueChannel { get; init; }
    public int CueWidth { get; init; } = 2;

    /// <summary>
    /// The talkback mic (VC-1): a 0-based channel of the device's input, or −1 for voice off (the mic is opened only once
    /// one is chosen). <see cref="VoiceDevice"/> is another input device where the driver allows it.
    /// </summary>
    public int VoiceChannel { get; init; } = -1;
    public string? VoiceDevice { get; init; }

    /// <summary>Push-to-talk instead of always listening (VC-1, the Talk control).</summary>
    public bool VoicePushToTalk { get; init; }

    /// <summary>Ask before acting (VC-46).</summary>
    public bool VoiceConfirm { get; init; }

    /// <summary>Ignore speech while the guide is talking (VC-40).</summary>
    public bool VoiceGate { get; init; } = true;

    /// <summary>A section name alone means go there (VC-1).</summary>
    public bool VoiceBareNames { get; init; } = true;

    public double VoiceMinConfidence { get; init; } = 0.6;

    /// <summary>The MD confirm output (PB-85, VC-50): spoken confirmations for the MD's ear; null = off.</summary>
    public int? MdChannel { get; init; }
    public int MdWidth { get; init; } = 2;
    public double MdGainDb { get; init; } = -12;

    /// <summary>The Pad output (PB-85); null plays pads on Main, the default.</summary>
    public int? PadChannel { get; init; }
    public int? PadWidth { get; init; }

    /// <summary>
    /// Where each bus plays. The group buses and Pads appear once they are mapped; custom buses always appear.
    /// A bus not listed plays where it did before buses: Click and Guide on Cue, the rest on Main.
    /// </summary>
    public List<BusRoute>? Buses { get; init; }

    /// <summary>The buses the user added; the rest are the track groups and Pads.</summary>
    public IEnumerable<string> CustomBuses =>
        (Buses ?? []).Select(b => b.Name).Where(n => !TrackGroups.LaneOrder.Contains(n) && n != TrackGroups.Pads);

    /// <summary>The channel count the mapped buses need: the end of the furthest channel any of them plays on.</summary>
    public int BusChannelsNeeded =>
        (Buses ?? []).Select(b => Resolve(b.Output, b.Name)).Select(r => r.Channel + r.Width).DefaultIfEmpty(0).Max();

    /// <summary>The output a bus is mapped to: "main", "cue", "3-4" or "3".</summary>
    public string BusOutput(string bus) =>
        Buses?.FirstOrDefault(b => b.Name == bus)?.Output
        ?? (bus is TrackGroups.Click or TrackGroups.Guide ? "cue" : "main");

    /// <summary>Where pads play: the Pads bus if mapped, else the Pad output, else Main.</summary>
    public (int Channel, int Width) ResolvePad() =>
        Buses?.Any(b => b.Name == TrackGroups.Pads) == true ? Resolve(BusOutput(TrackGroups.Pads), TrackGroups.Pads)
        : PadChannel is { } channel ? (channel, PadWidth == 1 ? 1 : 2)
        : (MainChannel, MainWidth);

    /// <summary>
    /// Resolves a track output ("bus", "bus:Name", "main", "cue", "3-4") to device channels; <paramref name="group"/> is the
    /// track's group, whose bus "bus" means. A named bus that is gone falls back to the group bus.
    /// </summary>
    public (int Channel, int Width) Resolve(string output, string group)
    {
        var o = OutputRef.Parse(output);
        if (o.Kind == OutputKind.Bus)
        {
            var bus = o.Bus is { } name && (Buses?.Any(b => b.Name == name) == true) ? name : group;
            o = OutputRef.Parse(BusOutput(bus));
        }

        return o.Kind switch
        {
            OutputKind.Main or OutputKind.Bus => (MainChannel, MainWidth),
            OutputKind.Cue => (CueChannel, CueWidth),
            _ => (o.Channel, o.Width),
        };
    }
}

/// <summary>One bus and the output it plays on: "main", "cue", "3-4" or "3" (never another bus).</summary>
public sealed record BusRoute(string Name, string Output);

/// <summary>The pads' own settings (SL-44, SL-45).</summary>
public sealed record PadSettings
{
    public double GainDb { get; init; } = -6;

    /// <summary>1, 2, 4 or 8 seconds.</summary>
    public double FadeSec { get; init; } = 4;

    public bool Follow { get; init; }
}

/// <summary>A window's top-left corner in screen pixels and its size in device-independent units.</summary>
public sealed record WindowBounds(int X, int Y, double Width, double Height);

/// <summary>Settings › MIDI out (SET-30).</summary>
public sealed record MidiOutSettings
{
    /// <summary>The output port; null = none on Windows, the virtual "RECompose" source on macOS.</summary>
    public string? Port { get; init; }

    /// <summary>1–16.</summary>
    public int Channel { get; init; } = 1;
    public int Velocity { get; init; } = 127;
    public int LengthMs { get; init; } = 100;

    /// <summary>What MIDI note 60 is called: "C3" or "C4".</summary>
    public string MiddleC { get; init; } = "C3";

    /// <summary>The section-type notes the user changed (AUTO-21); the rest are the defaults.</summary>
    public Dictionary<RECompose.Core.Sections.SectionType, int>? Notes { get; init; }

    /// <summary>Send the section note again on each pass of a repeat (AUTO-21).</summary>
    public bool ResendOnRepeat { get; init; } = true;

    /// <summary>Starting or resuming mid-section sends the current section's note (AUTO-21).</summary>
    public bool ResendOnPlay { get; init; } = true;
}

/// <summary>settings.json (DM-90).</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public AudioSettings Audio { get; init; } = new();

    /// <summary>"dark", "light" or "contrast" (SET-40). Prepare defaults to dark for now.</summary>
    public string Appearance { get; init; } = "dark";

    public bool ShowImportReview { get; init; } = true;
    public bool FollowPlayhead { get; init; } = true;

    /// <summary>When a jump or a vamp takes effect: EndOfSection, NextBar, NextBeat or Immediately (PB-44, SET-20).</summary>
    public string JumpTiming { get; init; } = "EndOfSection";

    /// <summary>After a jump to a section the plan does not play again, the written sections that follow it play on (PB-47, SET-20).</summary>
    public bool ContinueAsWritten { get; init; }

    /// <summary>The render cache may grow to this many gigabytes before the oldest renders go (REL-15, SET-60).</summary>
    public int RenderBudgetGb { get; init; } = 30;

    /// <summary>Keys, pedals and controllers (LIVE-51); null = the page-turner profile.</summary>
    public List<RECompose.Core.Controls.ControlBinding>? Bindings { get; init; }
    public string? LastSongId { get; init; }

    /// <summary>The main window was maximised when the app last closed; it opens that way again.</summary>
    public bool WindowMaximized { get; init; }

    /// <summary>The main window's place and size when not maximised; null until it has closed once.</summary>
    public WindowBounds? Window { get; init; }

    /// <summary>The service shown in the sidebar (DM-80 currentServiceId).</summary>
    public string? LastServiceId { get; init; }

    /// <summary>The service entry that was loaded (DM-80 loadedEntryId); it is loaded again at start-up.</summary>
    public string? LastEntryId { get; init; }

    /// <summary>
    /// ARR-12 "remember this choice": variant id → "service" (edits from a service make a custom arrangement) or "all"
    /// (they change the variant). May be null in files written before milestone 4.
    /// </summary>
    public Dictionary<string, string>? VariantScopes { get; init; }

    /// <summary>Pad volume, fade time and Follow song; null in files written before pads existed.</summary>
    public PadSettings? Pads { get; init; }

    /// <summary>MIDI out; null in files written before milestone 8.</summary>
    public MidiOutSettings? MidiOut { get; init; }

    /// <summary>Settings › Updates: download new releases in the background (PLAT-70). A check by hand still works when off.</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>Settings › Playback "Replace guide calls" (AUTO-30).</summary>
    public bool ReplaceGuideCalls { get; init; } = true;

    /// <summary>Settings › Assistant (AS-1); null in files written before milestone 9. The API key is in the OS credential store.</summary>
    public AssistantSettings? Assistant { get; init; }
}

/// <summary>Settings › Assistant (AS-1).</summary>
public sealed record AssistantSettings
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";

    /// <summary>Who answers: <see cref="Anthropic"/> (the default) or <see cref="OpenAI"/>. Each keeps its own key and model.</summary>
    public string Provider { get; init; } = Anthropic;

    /// <summary>Sent as <c>anthropic-workspace-id</c> when set.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The Anthropic model.</summary>
    public string Model { get; init; } = RECompose.Core.Assistant.AssistantPrompt.DefaultModel;

    public string OpenAIModel { get; init; } = RECompose.Core.Assistant.AssistantPrompt.OpenAIDefaultModel;

    /// <summary>Changes during Perform: true = ask me to confirm (the default), false = not allowed (AS-45).</summary>
    public bool ConfirmInPerform { get; init; } = true;
}
