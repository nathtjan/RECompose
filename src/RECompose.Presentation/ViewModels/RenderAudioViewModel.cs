// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Export;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>One file the render can make, in its group of the list.</summary>
public sealed class RenderSourceChoice(string group, RenderSource source) : ObservableObject
{
    private bool _isChecked;

    public string Group { get; } = group;
    public RenderSource Source { get; } = source;
    public string Label => Source.Kind is RenderSourceKind.Bus or RenderSourceKind.Track ? Source.Name : Source.Label;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>Song › Render audio…: which files to make and how closely they follow the mixer.</summary>
public sealed class RenderAudioViewModel : ObservableObject
{
    private RenderFormat _format = RenderFormat.Wav24;
    private bool _respectMute = true, _respectSolo = true, _respectTrackVolume = true, _respectMasterVolume = true, _respectArrangementMutes = true;

    public RenderAudioViewModel(string songTitle, IEnumerable<RenderSourceChoice> sources)
    {
        SongTitle = songTitle;
        Sources = sources.ToList();
        Sources.FirstOrDefault(s => s.Source.Kind == RenderSourceKind.Mix)?.IsChecked = true;
    }

    public string SongTitle { get; }
    public IReadOnlyList<RenderSourceChoice> Sources { get; }
    public IReadOnlyList<RenderFormat> Formats { get; } = Enum.GetValues<RenderFormat>();

    public RenderFormat Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    public bool RespectMute
    {
        get => _respectMute;
        set => Set(ref _respectMute, value);
    }

    public bool RespectSolo
    {
        get => _respectSolo;
        set => Set(ref _respectSolo, value);
    }

    public bool RespectTrackVolume
    {
        get => _respectTrackVolume;
        set => Set(ref _respectTrackVolume, value);
    }

    public bool RespectMasterVolume
    {
        get => _respectMasterVolume;
        set => Set(ref _respectMasterVolume, value);
    }

    public bool RespectArrangementMutes
    {
        get => _respectArrangementMutes;
        set => Set(ref _respectArrangementMutes, value);
    }

    public RenderOptions Options() => new()
    {
        Sources = Sources.Where(s => s.IsChecked).Select(s => s.Source).ToList(),
        Format = Format,
        RespectMute = RespectMute,
        RespectSolo = RespectSolo,
        RespectTrackVolume = RespectTrackVolume,
        RespectMasterVolume = RespectMasterVolume,
        RespectArrangementMutes = RespectArrangementMutes,
    };
}
