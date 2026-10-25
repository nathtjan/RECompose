// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Presentation.ViewModels;

/// <summary>Audio and MIDI devices as the reliability features see them (REL-20, REL-50).</summary>
public sealed partial class MainViewModel
{
    private bool _fallbackPresent = false;

    internal void SetFallbackPresent(bool present) => _fallbackPresent = present;
    private IReadOnlyList<string> _midiInputs = [];
}
