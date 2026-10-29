// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Planning;
using RECompose.Core.Sections;

namespace RECompose.Core.Voice;

public enum VoiceResultKind
{
    /// <summary>A live action to apply (or to propose in confirm mode).</summary>
    Action,
    Cancel,
    Confirm,

    /// <summary>Stop the tracks with a 1 s fade.</summary>
    Stop,
    Ignored,
}

/// <summary>What a command means now. <see cref="Label"/> names the target ("Verse 2"); <see cref="Problem"/> says why it was ignored.</summary>
public sealed record VoiceResolution(VoiceCommand Command, VoiceResultKind Kind, LiveAction? Action = null, string? Label = null, string? Problem = null)
{
    public const string NoSection = "no matching section";

    /// <summary>The same thing asked twice (VC-45): intent, action and target.</summary>
    public string Key => $"{Command.Intent}|{Action?.Kind}|{Action?.SectionId}|{Action?.ItemId}|{Action?.Timing}";
}

/// <summary>Resolving a command against the song and the remaining plan (VC-30, SKIP of VC-20).</summary>
public static class VoiceResolver
{
    public static IReadOnlyList<VoiceResolution> Resolve(IEnumerable<VoiceCommand> commands, SongPlan plan, double nowSec) =>
        commands.Select(c => Resolve(c, plan, nowSec)).ToList();

    public static VoiceResolution Resolve(VoiceCommand command, SongPlan plan, double nowSec)
    {
        var index = plan.Items.Count == 0 ? -1 : plan.ItemIndexAt(nowSec);
        var current = index >= 0 ? Info(plan, plan.Items[index].Section?.Id) : null;
        var timing = command.Timing;
        switch (command.Intent)
        {
            case VoiceIntent.Cancel:
                return new VoiceResolution(command, VoiceResultKind.Cancel);
            case VoiceIntent.Confirm:
                return new VoiceResolution(command, VoiceResultKind.Confirm);
            case VoiceIntent.Stop:
                return new VoiceResolution(command, VoiceResultKind.Stop);
            case VoiceIntent.MoveOn:
                return Act(command, LiveActionKind.MoveOn, timing, null, null, current?.Label);
            case VoiceIntent.Vamp:
                return Act(command, LiveActionKind.Vamp, timing, null, null, current?.Label);
            case VoiceIntent.End:
                return Act(command, LiveActionKind.End, timing, null, null, null);
            case VoiceIntent.Repeat:
            {
                if (command.Target is null)
                    return Act(command, LiveActionKind.RepeatOnce, timing, null, null, current?.Label);
                var t = command.Target;
                var info = t is { Number: null, Ordinal: null, Last: false, Name: null } && current?.Type == t.Type ? current : Find(t, plan, index, current);
                if (info is null)
                    return new VoiceResolution(command, VoiceResultKind.Ignored, Problem: VoiceResolution.NoSection);

                // "chorus again" while the chorus plays repeats it; said elsewhere it goes back to it.
                return info.Id == current?.Id
                    ? Act(command, LiveActionKind.RepeatOnce, timing, null, null, info.Label)
                    : Act(command, LiveActionKind.JumpToSection, timing, info.Id, null, info.Label);
            }

            case VoiceIntent.Jump:
            {
                if (command.Target is null)
                    return new VoiceResolution(command, VoiceResultKind.Ignored, Problem: VoiceResolution.NoSection);
                var info = Find(command.Target, plan, index, current);
                if (info is not null)
                    return Act(command, LiveActionKind.JumpToSection, timing, info.Id, null, info.Label);

                // "take it to the end" with no Ending section: end the song.
                return command.Target.Type is SectionType.Ending or SectionType.Outro
                    ? Act(command, LiveActionKind.End, timing, null, null, null)
                    : new VoiceResolution(command, VoiceResultKind.Ignored, Problem: VoiceResolution.NoSection);
            }

            case VoiceIntent.Skip:
                return Skip(command, plan, index, current);
            default:
                return new VoiceResolution(command, VoiceResultKind.Ignored);
        }
    }

    /// <summary>SKIP: when the next section is the one named (or none is named), jump to what follows it.</summary>
    private static VoiceResolution Skip(VoiceCommand command, SongPlan plan, int index, SectionInfo? current)
    {
        var next = NextSection(plan, index);
        if (next < 0)
            return Act(command, LiveActionKind.End, command.Timing, null, null, null);

        var nextInfo = Info(plan, plan.Items[next].Section?.Id);
        if (command.Target is not null)
        {
            var named = Find(command.Target, plan, index, current);
            if (named is null)
                return new VoiceResolution(command, VoiceResultKind.Ignored, Problem: VoiceResolution.NoSection);
            if (named.Id != nextInfo?.Id)
                return new VoiceResolution(command, VoiceResultKind.Ignored, Label: named.Label, Problem: $"{named.Label} is not next");
        }

        var after = -1;
        for (var i = next + 1; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Length > 0)
            {
                after = i;
                break;
            }
        }

        if (after < 0 || plan.Items[after].Kind == PlanItemKind.Tail)
            return Act(command, LiveActionKind.End, command.Timing, null, null, null);
        var target = plan.Items[after];
        return Act(command, LiveActionKind.JumpToItem, command.Timing, null, target.Id, target.Label);
    }

    /// <summary>VC-30 rules 1 to 4.</summary>
    public static SectionInfo? Find(SectionRef reference, SongPlan plan, int index, SectionInfo? current)
    {
        var sections = plan.Sections;
        if (reference.Name is { } name)
        {
            return sections.FirstOrDefault(s => VoiceText.Normalize(s.Section.Name) == name)
                ?? sections
                    .Select(s => (Info: s, Distance: VoiceText.Levenshtein(VoiceText.Normalize(s.Section.Name), name)))
                    .Where(x => x.Distance <= 2)
                    .OrderBy(x => x.Distance)
                    .Select(x => x.Info)
                    .FirstOrDefault();
        }

        var ofType = sections.Where(s => s.Type == reference.Type).OrderBy(s => s.Index).ToList();
        if (ofType.Count == 0)
            return null;

        if (reference.Number is { } number)
            return ofType.FirstOrDefault(s => TrailingNumber(s.Section.Name) == number) ?? ofType.ElementAtOrDefault(number - 1);
        if (reference.Ordinal is { } ordinal)
            return ofType.ElementAtOrDefault(ordinal - 1);

        var remaining = new List<SectionInfo>();
        for (var i = index + 1; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Kind == PlanItemKind.Section && Info(plan, plan.Items[i].Section?.Id) is { } info && info.Type == reference.Type)
                remaining.Add(info);
        }

        if (reference.Last)
            return remaining.LastOrDefault() ?? ofType[^1];
        return remaining.FirstOrDefault()
            ?? ofType.FirstOrDefault(s => s.Index > (current?.Index ?? -1))
            ?? ofType[0];
    }

    private static int NextSection(SongPlan plan, int index)
    {
        for (var i = index + 1; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Kind == PlanItemKind.Section && plan.Items[i].Length > 0)
                return i;
        }

        return -1;
    }

    private static SectionInfo? Info(SongPlan plan, string? id) => id is null ? null : plan.Sections.FirstOrDefault(s => s.Id == id);

    private static int? TrailingNumber(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static VoiceResolution Act(VoiceCommand command, LiveActionKind kind, JumpTiming timing, string? sectionId, string? itemId, string? label) =>
        new(command, VoiceResultKind.Action, new LiveAction(kind, timing, itemId, sectionId, ActionSource.Voice), label);
}
