using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Hides NuGet's package-management commands on Go projects.
    /// <para>
    /// NuGet places "Manage NuGet Packages..." onto the shell's shared project
    /// context menu and sets its visibility from
    /// <c>GetIsSolutionOpen()</c> alone (NuGetPackage.
    /// BeforeQueryStatusForAddPackageDialog) - project capabilities only gate
    /// Enabled, which is why a correctly-capability'd Go project still showed
    /// the item and answered "The project ... is unsupported" when clicked.
    /// </para>
    /// <para>
    /// Replacing the menu does not help either: unlike the Dependencies node,
    /// the project ROOT node's context menu does not go through
    /// IProjectItemContextMenuProvider (verified - the shared menu kept
    /// showing). The supported way to override a command's state for a
    /// specific project type is this one: a command-group handler that
    /// reports the command Invisible.
    /// </para>
    /// </summary>
    [ExportCommandGroup(NuGetDialogCmdSetGuid)]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenNuGetCommandsHandler : IAsyncCommandGroupHandler
    {
        /// <summary>NuGet's guidNuGetDialogCmdSet.</summary>
        private const string NuGetDialogCmdSetGuid = "25fd982b-8cae-4cbd-a440-e03ffccde106";

        /// <summary>PkgCmdIDList.cmdidAddPackageDialog / ...ForSolution.</summary>
        private const long ManagePackagesDialog = 0x100;
        private const long ManagePackagesForSolutionDialog = 0x200;

        public Task<CommandStatusResult> GetCommandStatusAsync(
            IImmutableSet<IProjectTree> items, long commandId, bool focused, string commandText, CommandStatus progressiveStatus)
        {
            if (commandId == ManagePackagesDialog || commandId == ManagePackagesForSolutionDialog)
            {
                // Handled + Invisible: the command disappears from the menu for
                // Go projects and stays untouched everywhere else.
                return Task.FromResult(new CommandStatusResult(
                    true, commandText, progressiveStatus | CommandStatus.Invisible));
            }
            return CommandStatusResult.Unhandled.AsTask();
        }

        public Task<bool> TryHandleCommandAsync(
            IImmutableSet<IProjectTree> items, long commandId, bool focused, long commandExecuteOptions,
            System.IntPtr variantArgIn, System.IntPtr variantArgOut)
        {
            // Nothing to execute - the commands are hidden, not reimplemented.
            return Task.FromResult(false);
        }
    }
}
