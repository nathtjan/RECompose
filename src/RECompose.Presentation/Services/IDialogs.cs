// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Services;

public enum DuplicateChoice
{
    OpenExisting,
    ImportAnyway,
    Cancel,
}

/// <summary>Name and date of a service (SL-2).</summary>
public sealed record ServiceDetails(string Name, DateTime? Date);

/// <summary>Where an edit of a variant made from a service goes (ARR-12).</summary>
public enum VariantScope
{
    /// <summary>Only this service: the entry gets a custom copy.</summary>
    ThisService,

    /// <summary>The variant itself, for every service that plays it.</summary>
    AllServices,
}

public sealed record VariantScopeAnswer(VariantScope Scope, bool Remember);

/// <summary>Windows and pickers the view models need; the app implements them with Avalonia.</summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync();

    /// <summary>Shows the import review (SL-51). True = Import.</summary>
    Task<bool> ReviewImportAsync(ImportReviewViewModel review);

    /// <summary>"Already in library as {title}: Open it / Import anyway" (SL-55).</summary>
    Task<DuplicateChoice> AskDuplicateAsync(string existingTitle);

    /// <summary>A confirmation for a destructive or surprising action (UI-60). True = the confirm button.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    /// <summary>Settings › Audio. True = Apply.</summary>
    Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings);

    /// <summary>The service check (REL-1): the items with their Fix buttons.</summary>
    Task ShowServiceCheckAsync(ServiceCheckViewModel check);

    /// <summary>"RECompose closed unexpectedly…" with the choices of REL-31.</summary>
    Task<ResumeChoice> AskResumeAsync(ResumeQuestion question);

    /// <summary>Settings › Playback and › Controls (SET-20, SET-28). Edits the view model in place.</summary>
    Task EditControlsAsync(ControlsViewModel controls);

    /// <summary>New service, Duplicate service and Rename (SL-2): a name and an optional date and time. Null = Cancel.</summary>
    Task<ServiceDetails?> AskServiceDetailsAsync(string title, string confirmLabel, ServiceDetails initial);

    /// <summary>ARR-12: change the variant for every service, or only for this one. Null = Cancel.</summary>
    Task<VariantScopeAnswer?> AskVariantScopeAsync(string variantName, int otherEntries);

    /// <summary>A short name ("Save as variant…", a mix preset name). Null = Cancel.</summary>
    Task<string?> AskNameAsync(string title, string label, string initial, string confirmLabel);

    /// <summary>One of several choices (the replacement for a deleted variant, ARR-11). Null = Cancel.</summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> choices, string confirmLabel);

    /// <summary>The transition popover (SL-21). True = Save.</summary>
    Task<bool> EditTransitionAsync(TransitionViewModel transition);
}
