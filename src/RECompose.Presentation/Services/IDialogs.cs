// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Services;

public enum DuplicateChoice
{
    OpenExisting,
    ImportAnyway,
    Cancel,
}

/// <summary>Windows and pickers the view models need; the app implements them with Avalonia.</summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync();

    /// <summary>Shows the import review (SL-51). True = Import.</summary>
    Task<bool> ReviewImportAsync(ImportReviewViewModel review);

    /// <summary>"Already in library as {title}: Open it / Import anyway" (SL-55).</summary>
    Task<DuplicateChoice> AskDuplicateAsync(string existingTitle);

    /// <summary>Settings › Audio. True = Apply.</summary>
    Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings);
}
