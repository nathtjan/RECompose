// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;

namespace RECompose.Core.Voice;

/// <summary>The words of voice feedback: log lines (VC-60), captions (VC-55) and the MD's confirmations (VC-50).</summary>
public static class VoiceLines
{
    public const string GuideTalking = "the guide track was talking";
    public const string LowConfidence = "low confidence";
    public const string NotPlaying = "not playing";

    public static string Timing(JumpTiming timing) => timing switch
    {
        JumpTiming.NextBar => "at the next bar",
        JumpTiming.NextBeat => "on the next beat",
        JumpTiming.Immediately => "now",
        _ => "at the end of this section",
    };

    /// <summary>VC-60 for an accepted command; <paramref name="alreadyNext"/> when the plan already does it (VC-36).</summary>
    public static string Log(VoiceResolution r, bool alreadyNext = false)
    {
        if (alreadyNext)
            return $"Voice: {r.Label} (already next)";
        return r.Kind switch
        {
            VoiceResultKind.Cancel => "Voice: cancel",
            VoiceResultKind.Confirm => "Voice: yes",
            VoiceResultKind.Stop => "Voice: stop the tracks",
            VoiceResultKind.Ignored => Ignored(r.Command, r.Problem ?? VoiceResolution.NoSection, r.Label),
            _ => r.Action!.Kind switch
            {
                LiveActionKind.MoveOn => "Voice: move on",
                LiveActionKind.Vamp => $"Voice: vamp {r.Label}".TrimEnd(),
                LiveActionKind.RepeatOnce => $"Voice: repeat {r.Label}".TrimEnd(),
                LiveActionKind.End => "Voice: end",
                _ => $"Voice: {r.Label}, {Timing(r.Action.Timing)}",
            },
        };
    }

    /// <summary>VC-60 for a dropped command: the section when there is one, otherwise what was heard.</summary>
    public static string Ignored(VoiceCommand? command, string reason, string? label = null, string? heard = null)
    {
        var what = label ?? (command?.Target is { } t ? Capitalise(t.Text) : $"\"{heard ?? string.Join(' ', command?.Keywords ?? [])}\"");
        return $"Voice ignored: {what} ({reason})";
    }

    /// <summary>The second line of the VC-55 caption: what was done.</summary>
    public static string Caption(VoiceResolution r, bool alreadyNext = false) => alreadyNext
        ? $"{r.Label} is already next"
        : r.Kind switch
        {
            VoiceResultKind.Cancel => "Cancelled",
            VoiceResultKind.Confirm => "Confirmed",
            VoiceResultKind.Stop => "Stopping the tracks",
            VoiceResultKind.Ignored => r.Problem == VoiceResolution.NoSection ? "didn't catch a section" : $"ignored — {r.Problem}",
            _ => Said(r.Action!, r.Label),
        };

    /// <summary>VC-50: what the MD hears ("Verse two next").</summary>
    public static string Confirmation(VoiceResolution r, bool alreadyNext = false) => alreadyNext
        ? $"{VoiceText.Spoken(r.Label ?? string.Empty)} is already next".TrimStart()
        : r.Kind switch
        {
            VoiceResultKind.Cancel => "Cancelled",
            VoiceResultKind.Ignored => "Didn't catch that",
            VoiceResultKind.Action => Confirmation(r.Action!, r.Label),
            _ => string.Empty,
        };

    /// <summary>VC-50 for any live action (also pedal and keys, LIVE-54).</summary>
    public static string Confirmation(LiveAction action, string? label) => action.Kind switch
    {
        LiveActionKind.MoveOn => "Moving on",
        LiveActionKind.Vamp or LiveActionKind.Loop => "Vamping",
        LiveActionKind.RepeatOnce => "Repeat",
        LiveActionKind.End => "Ending after this",
        _ => $"{VoiceText.Spoken(label ?? string.Empty)} next".TrimStart(),
    };

    private static string Said(LiveAction action, string? label) => action.Kind switch
    {
        LiveActionKind.MoveOn => "Moving on",
        LiveActionKind.Vamp or LiveActionKind.Loop => $"Vamping {label}".TrimEnd(),
        LiveActionKind.RepeatOnce => $"Repeat {label}".TrimEnd(),
        LiveActionKind.End => "Ending after this",
        _ => $"{label} next",
    };

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
