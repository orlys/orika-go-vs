using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Hides NuGet's global entry points (Tools → NuGet Package Manager →
    /// Package Manager Console / Manage NuGet Packages for Solution / Package
    /// Manager Settings) while a Go project is the active one.
    /// <para>
    /// Why a priority command target: the CPS command-group handler
    /// (<see cref="GoHiddenNuGetCommandsHandler"/>) only participates in
    /// command routing for PROJECT context menus. Main-menu commands never
    /// reach it, so the Tools entries survived. A priority command target is
    /// the one hook that sees every command's QueryStatus, main menu
    /// included.
    /// </para>
    /// <para>
    /// Scoped by the uiContextGoProject UI context - the same
    /// ActiveProjectCapability rule the package already declares - so the
    /// moment a C# (or any non-Go) project is active, NuGet's menu comes back
    /// untouched. The GUID check runs first and rejects everything else in a
    /// couple of comparisons, so the cost on unrelated commands is
    /// negligible.
    /// </para>
    /// <para>
    /// RESULT IS "disabled", NOT "hidden": OLECMDF_INVISIBLE is only honoured
    /// for commands whose own .vsct definition carries the DynamicVisibility
    /// flag, and NuGet's do not - so the shell keeps drawing them and only
    /// the missing OLECMDF_ENABLED takes effect. Greying them out is
    /// therefore the ceiling for a command we do not own; removing the items
    /// outright would mean disabling the NuGet extension for the whole IDE.
    /// </para>
    /// </summary>
    internal sealed class GoNuGetCommandFilter : IOleCommandTarget
    {
        private static readonly Guid NuGetDialogCmdSet = new Guid("25fd982b-8cae-4cbd-a440-e03ffccde106");
        private static readonly Guid NuGetConsoleCmdSet = new Guid("1E8A55F6-C18D-407F-91C8-94B02AE1CED6");

        // NuGet's PkgCmdIDList: dialog set -> AddPackageDialog / ...ForSolution;
        // console set -> PowerConsole (Package Manager Console) / SourceSettings.
        private const uint AddPackageDialog = 0x100;
        private const uint AddPackageDialogForSolution = 0x200;
        private const uint PowerConsole = 0x100;
        private const uint SourceSettings = 0x200;

        private readonly IVsMonitorSelection _monitorSelection;
        private readonly uint _goContextCookie;

        public GoNuGetCommandFilter(IVsMonitorSelection monitorSelection)
        {
            _monitorSelection = monitorSelection;
            var contextGuid = new Guid(OrikaGoPackage.UiContextGuidString);
            if (_monitorSelection != null)
            {
                _monitorSelection.GetCmdUIContextCookie(ref contextGuid, out _goContextCookie);
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (!IsNuGetGlobalCommand(pguidCmdGroup, prgCmds) || !IsGoProjectActive())
            {
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }

            for (int i = 0; i < cCmds; i++)
            {
                if (IsHiddenCommand(pguidCmdGroup, prgCmds[i].cmdID))
                {
                    prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_INVISIBLE);
                }
            }
            return VSConstants.S_OK;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // Only visibility is overridden; execution is left to NuGet.
            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private static bool IsNuGetGlobalCommand(Guid cmdGroup, OLECMD[] cmds)
        {
            if (cmdGroup != NuGetDialogCmdSet && cmdGroup != NuGetConsoleCmdSet)
            {
                return false;
            }
            if (cmds == null)
            {
                return false;
            }
            foreach (OLECMD cmd in cmds)
            {
                if (IsHiddenCommand(cmdGroup, cmd.cmdID))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsHiddenCommand(Guid cmdGroup, uint cmdId)
        {
            if (cmdGroup == NuGetDialogCmdSet)
            {
                return cmdId == AddPackageDialog || cmdId == AddPackageDialogForSolution;
            }
            if (cmdGroup == NuGetConsoleCmdSet)
            {
                return cmdId == PowerConsole || cmdId == SourceSettings;
            }
            return false;
        }

        private bool IsGoProjectActive()
        {
            if (_monitorSelection == null || _goContextCookie == 0)
            {
                return false;
            }
            return ErrorHandler.Succeeded(_monitorSelection.IsCmdUIContextActive(_goContextCookie, out int active))
                && active != 0;
        }
    }
}

