// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;
using RECompose.Core.Sections;

namespace RECompose.Core.Voice;

/// <summary>One word the app itself puts out (the guide track, a spoken clip, an MD confirmation), in plan seconds.</summary>
public sealed record SpokenWord(string Word, double Start, double End);

public enum VoiceVerdict
{
    Accept,
    LowConfidence,
    Duplicate,
}

/// <summary>
/// The guide-bleed gate (VC-40): a command is dropped when its utterance overlaps, within 300 ms, a moment where the app was
/// saying one of the command's keywords. "Chorus" over the guide's "Chorus" is dropped; "bridge" over it is not.
/// </summary>
public static class VoiceGate
{
    public const double MarginSec = 0.3;

    public static bool IsEcho(IReadOnlyList<string> keywords, double startSec, double endSec, IEnumerable<SpokenWord> spoken)
    {
        var set = new HashSet<string>(keywords, StringComparer.Ordinal);
        return spoken.Any(w => w.Start <= endSec + MarginSec && w.End >= startSec - MarginSec && set.Contains(w.Word));
    }

    /// <summary>The guide's words where the plan plays them between two plan times (the analysis is in source time).</summary>
    public static IEnumerable<SpokenWord> GuideWords(SongPlan plan, IEnumerable<GuideWord> words, double fromSec, double toSec)
    {
        var list = words as IReadOnlyList<GuideWord> ?? words.ToList();
        foreach (var item in plan.Items)
        {
            if (!item.HasSource || item.Length <= 0 || item.PlanEnd < fromSec || item.PlanStart > toSec)
                continue;
            foreach (var word in list)
            {
                if (word.End < item.SrcStart || word.Start >= item.SrcEnd)
                    continue;
                var start = item.PlanStart + (word.Start - item.SrcStart);
                var end = item.PlanStart + (word.End - item.SrcStart);
                if (end < fromSec || start > toSec)
                    continue;
                foreach (var w in VoiceText.Words(word.Text))
                    yield return new SpokenWord(w, start, end);
            }
        }
    }

    /// <summary>The words of a clip the app plays (a confirmation, a spoken call) over its span.</summary>
    public static IEnumerable<SpokenWord> Clip(string text, double startSec, double endSec) =>
        VoiceText.Words(text).Select(w => new SpokenWord(w, startSec, endSec));
}

/// <summary>Confidence and de-duplication (VC-45).</summary>
public sealed class VoiceSafeguards
{
    public const double DuplicateWindowSec = 2;

    private (string Key, double At)? _last;

    /// <summary>VC-1 minimum confidence.</summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>Accept, or why not. The clock is a steady one in seconds, not plan time: the same words twice while paused are still a duplicate.</summary>
    public VoiceVerdict Check(VoiceResolution resolution, double confidence, double clockSec)
    {
        if (confidence < MinConfidence)
            return VoiceVerdict.LowConfidence;
        var key = resolution.Key;
        if (_last is { } last && last.Key == key && clockSec - last.At < DuplicateWindowSec)
            return VoiceVerdict.Duplicate;
        _last = (key, clockSec);
        return VoiceVerdict.Accept;
    }
}
