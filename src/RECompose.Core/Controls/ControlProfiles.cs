// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Planning;
using RECompose.Core.Sections;

namespace RECompose.Core.Controls;

/// <summary>A named set of bindings for a rig or a venue (LIVE-51). One is in force at a time.</summary>
public sealed record ControlProfile(string Name, List<ControlBinding> Bindings);

/// <summary>Profiles, the global layer every profile shares, and how a binding's target is found (LIVE-50…55).</summary>
public static class ControlProfiles
{
    public const string DefaultName = "Default";

    /// <summary>Panic and the profile switches work in every profile, so a profile can never leave you stuck in it.</summary>
    public static bool IsGlobal(ControlAction action) => action is ControlAction.Panic or ControlAction.NextProfile or ControlAction.SelectProfile;

    /// <summary>
    /// The profiles and the global layer from settings. Settings written before profiles had one list: it becomes the
    /// Default profile, and its Panic and profile switches the global layer.
    /// </summary>
    public static (List<ControlProfile> Profiles, List<ControlBinding> Global) From(
        List<ControlBinding>? legacy, List<ControlProfile>? profiles, List<ControlBinding>? global)
    {
        if (profiles is { Count: > 0 })
            return ([.. profiles.Select(p => p with { Bindings = [.. p.Bindings] })], [.. global ?? ControlDefaults.Global]);
        var old = legacy ?? [.. ControlDefaults.PageTurner, .. ControlDefaults.Global];
        return ([new ControlProfile(DefaultName, [.. old.Where(b => !IsGlobal(b.Action))])], [.. global ?? old.Where(b => IsGlobal(b.Action))]);
    }

    /// <summary>The profile after <paramref name="active"/>, round to the first.</summary>
    public static string Next(IReadOnlyList<ControlProfile> profiles, string active)
    {
        var index = profiles.Select(p => p.Name).ToList().IndexOf(active);
        return profiles[(index + 1) % profiles.Count].Name;
    }

    /// <summary>
    /// "Note 60 ch 1 is also Pads from a keyboard (Notes 36–72 ch 1). The narrower binding acts." when the new binding
    /// shares an input with another one in force; null when it does not.
    /// </summary>
    public static string? Overlap(IEnumerable<ControlBinding> inForce, ControlBinding added)
    {
        var other = inForce.FirstOrDefault(b => !ReferenceEquals(b, added) && b.Input.Overlaps(added.Input) && (b.Action, b.Target) != (added.Action, added.Target));
        if (other is null)
            return null;
        var outcome = other.Input.Specificity == added.Input.Specificity ? "Both act." : "The narrower binding acts.";
        return $"{added.Display} is also {ControlDefaults.Name(other.Action, other.Target)} ({other.Display}). {outcome}";
    }

    /// <summary>
    /// A section jump by type: the next plan item of that type, else (none ahead) the last section of that type up to
    /// here, else the first. Null when the song has none.
    /// </summary>
    public static (string? ItemId, string? SectionId)? FindSection(SongPlan plan, double planSec, SectionType type)
    {
        var index = plan.Items.Count == 0 ? -1 : plan.ItemIndexAt(planSec);
        for (var i = index + 1; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (item.Kind == PlanItemKind.Section && item.Length > 0 && item.Section?.Type == type)
                return (item.Id, null);
        }

        var here = index >= 0 ? plan.Items[index].Section?.Index ?? int.MaxValue : -1;
        var info = plan.Sections.LastOrDefault(s => s.Type == type && s.Index <= here) ?? plan.Sections.FirstOrDefault(s => s.Type == type);
        return info is null ? null : (null, info.Id);
    }

    /// <summary>A controller value (0–127) as a level on a fader from <paramref name="minDb"/> to <paramref name="maxDb"/>, linear as on screen.</summary>
    public static double FaderDb(int value, double minDb, double maxDb) =>
        Math.Round(minDb + Math.Clamp(value, 0, 127) / 127.0 * (maxDb - minDb), 1);

    public static int? PitchClass(ControlBinding binding, int note) =>
        binding.Target is { } target ? int.TryParse(target, CultureInfo.InvariantCulture, out var pc) ? pc : null : note % 12;
}

/// <summary>
/// Soft takeover: a fader whose controller is somewhere else than the level on screen moves only once the controller
/// reaches the level or crosses it, so a preset change never makes the next touch jump. Moved elsewhere (the screen, a
/// preset), it lets go again.
/// </summary>
public sealed class SoftTakeover
{
    private sealed class State
    {
        public double? Last;
        public double Applied = double.NaN;
        public bool Caught;
    }

    private readonly Dictionary<string, State> _states = [];

    /// <summary>True when the controller now has <paramref name="key"/>; the caller then sets the level to <paramref name="controlDb"/>.</summary>
    public bool Accept(string key, double controlDb, double currentDb)
    {
        if (!_states.TryGetValue(key, out var state))
            _states[key] = state = new State();
        if (state.Caught && Math.Abs(currentDb - state.Applied) > 0.2)
            state.Caught = false;
        if (!state.Caught)
            state.Caught = Math.Abs(controlDb - currentDb) <= 1.0 || (state.Last is { } last && (last - currentDb) * (controlDb - currentDb) <= 0);
        state.Last = controlDb;
        if (state.Caught)
            state.Applied = controlDb;
        return state.Caught;
    }

    /// <summary>The level the controller set has been changed by the caller (a floor, a clamp).</summary>
    public void Applied(string key, double db)
    {
        if (_states.TryGetValue(key, out var state))
            state.Applied = db;
    }
}
