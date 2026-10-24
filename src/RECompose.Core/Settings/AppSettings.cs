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

/// <summary>settings.json (DM-90).</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public AudioSettings Audio { get; init; } = new();

    /// <summary>"dark", "light" or "contrast" (SET-40). Prepare defaults to dark for now.</summary>
    public string Appearance { get; init; } = "dark";

    public bool ShowImportReview { get; init; } = true;
    public bool FollowPlayhead { get; init; } = true;
    public string? LastSongId { get; init; }
}
