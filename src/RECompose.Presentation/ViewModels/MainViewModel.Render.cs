// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>The render cache (REL-10…16). Filled in by the render work package.</summary>
public sealed partial class MainViewModel
{
    private RenderNeed RenderNeedFor(ServiceEntry entry, Song song) => new(0, 0);

    private void RenderServiceNow()
    {
    }

    private void CleanRenderCache()
    {
    }
}
