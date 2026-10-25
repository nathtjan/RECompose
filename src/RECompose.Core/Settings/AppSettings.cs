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

    /// <summary>Logical outputs (PB-85): 0-based first channel and width.</summary>
    public int MainChannel { get; init; }
    public int MainWidth { get; init; } = 2;
    public int CueChannel { get; init; }
    public int CueWidth { get; init; } = 2;

    /// <summary>The Pad output (PB-85); null plays pads on Main, the default.</summary>
    public int? PadChannel { get; init; }
    public int? PadWidth { get; init; }

    /// <summary>Where pads play: the Pad output, or Main when it isn't set.</summary>
    public (int Channel, int Width) ResolvePad() =>
        PadChannel is { } channel ? (channel, PadWidth == 1 ? 1 : 2) : (MainChannel, MainWidth);

    /// <summary>Resolves a track output ("main", "cue", "3-4") to device channels.</summary>
    public (int Channel, int Width) Resolve(string output)
    {
        var o = OutputRef.Parse(output);
        return o.Kind switch
        {
            OutputKind.Main => (MainChannel, MainWidth),
            OutputKind.Cue => (CueChannel, CueWidth),
            _ => (o.Channel, o.Width),
        };
    }
}

/// <summary>The pads' own settings (SL-44, SL-45).</summary>
public sealed record PadSettings
{
    public double GainDb { get; init; } = -6;

    /// <summary>1, 2, 4 or 8 seconds.</summary>
    public double FadeSec { get; init; } = 4;

    public bool Follow { get; init; }
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

    /// <summary>Keys, pedals and controllers (LIVE-51); null = the page-turner profile.</summary>
    public List<RECompose.Core.Controls.ControlBinding>? Bindings { get; init; }
    public string? LastSongId { get; init; }

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
}
