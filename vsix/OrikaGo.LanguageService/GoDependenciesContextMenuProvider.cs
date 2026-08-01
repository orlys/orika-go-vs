using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Swaps the context menu of the Go project's Dependencies ROOT node from
    /// the shell's shared IDM_VS_CTXT_REFERENCEROOT (which carries "Add Project
    /// Reference...", "Manage NuGet Packages..." and other .NET-only
    /// placements) to the private menu defined in OrikaGoPackage.vsct, whose
    /// only content is "Add Go Module Reference...". Same extension point the
    /// managed project system itself uses (its DependenciesContextMenuProvider
    /// does this mapping at a lower Order); child nodes fall through to the
    /// default providers untouched.
    /// </summary>
    [Export(typeof(IProjectItemContextMenuProvider))]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoDependenciesContextMenuProvider : IProjectItemContextMenuProvider
    {
        private static readonly Guid OrikaGoCmdSet = new Guid("1F6D3B85-42A9-4E0C-9B7D-E85C2A94F316");
        private const int MenuGoDependenciesContext = 0x2000;
        private const int MenuGoProjectContext = 0x2001;

        public bool TryGetContextMenu(IProjectTree projectItem, out Guid menuCommandGuid, out int menuCommandId)
        {
            if (projectItem != null)
            {
                // The dependencies root carries the managed project system's
                // "DependenciesRootNode" custom flag.
                if (projectItem.Flags.Contains("DependenciesRootNode"))
                {
                    menuCommandGuid = OrikaGoCmdSet;
                    menuCommandId = MenuGoDependenciesContext;
                    return true;
                }

                // The project root goes to the private menu too, purely to
                // escape NuGet's placement on the shared project menu (see
                // OrikaGoPackage.vsct); the standard groups are re-hosted
                // there so the menu keeps its usual contents.
                if (projectItem.Flags.Contains(ProjectTreeFlags.Common.ProjectRoot))
                {
                    menuCommandGuid = OrikaGoCmdSet;
                    menuCommandId = MenuGoProjectContext;
                    return true;
                }
            }

            menuCommandGuid = default;
            menuCommandId = 0;
            return false;
        }

        public bool TryGetMixedItemsContextMenu(IEnumerable<IProjectTree> projectItems, out Guid menuCommandGuid, out int menuCommandId)
        {
            menuCommandGuid = default;
            menuCommandId = 0;
            return false;
        }
    }
}
