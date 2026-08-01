using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Project = EnvDTE.Project;
using Task = System.Threading.Tasks.Task;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Package hosting the "加入 Go 模組參考..." project-context-menu command.
    /// It writes a &lt;GoModuleReference&gt; item into the selected .goproj; the
    /// SDK's GoRestoreModules target materializes it with "go get" on the next
    /// build. The project declares HandlesOwnReload, so CPS picks up the edit
    /// without prompting.
    /// </summary>
    // RegisterUsing=CodeBase is load-bearing: the default emits only an
    // assembly display name ("Assembly"="OrikaGo.LanguageService, ...,
    // PublicKeyToken=null") into the pkgdef, which the shell cannot resolve
    // for a non-GAC extension assembly - the package never loads and the
    // CTMENU merge silently reads nothing. CodeBase pins
    // "$PackageFolder$\OrikaGo.LanguageService.dll".
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true, RegisterUsing = RegistrationMethod.CodeBase)]
    [Guid(PackageGuidString)]
    // Version 2, not 1: version 1 was once installed while the ctmenu resource
    // was missing from the assembly (no VSPackage.resx/MergeWithCTO yet), and
    // the shell caches the merge PER VERSION - it never re-reads a version it
    // has already processed, so the fixed resource stayed invisible until the
    // version changed. Bump this whenever the vsct changes.
    [ProvideMenuResource("Menus.ctmenu", 5)]
    // Lights up the vsct's uiContextGoProject when the ACTIVE project carries
    // the OrikaGo capability (declared by Orika.NET.Sdk), so the command only
    // appears on .goproj project nodes - evaluated by the shell without
    // loading this package.
    [ProvideUIContextRule(UiContextGuidString,
        name: "OrikaGoProjectActive",
        expression: "OrikaGo",
        termNames: new[] { "OrikaGo" },
        termValues: new[] { "ActiveProjectCapability:OrikaGo" })]
    public sealed class OrikaGoPackage : AsyncPackage
    {
        public const string PackageGuidString = "9C4E9A2B-7D31-4F5C-A1E8-52B60D3F8E74";
        public const string UiContextGuidString = "A7B54C29-8E13-4D6F-92A5-3D1E7F60C8B2";
        private static readonly Guid CommandSet = new Guid("1F6D3B85-42A9-4E0C-9B7D-E85C2A94F316");
        private const int CmdidAddGoModuleReference = 0x0100;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var commandId = new CommandID(CommandSet, CmdidAddGoModuleReference);
                commandService.AddCommand(new OleMenuCommand(ExecuteAddGoModuleReference, commandId));
            }
        }

        private void ExecuteAddGoModuleReference(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(GetService(typeof(SDTE)) is DTE dte))
                {
                    return;
                }

                string projectPath = GetActiveGoProjectPath(dte);
                if (projectPath == null)
                {
                    return; // UI context should prevent this; fail quiet.
                }

                var dialog = new AddModuleReferenceDialog();
                if (dialog.ShowModal() != true)
                {
                    return;
                }

                AddOrUpdateReference(projectPath, dialog.ModulePath, dialog.ModuleVersion);

                dte.StatusBar.Text = dialog.ModuleVersion.Length > 0
                    ? GoStrings.ReferenceAddedPinned(dialog.ModulePath, dialog.ModuleVersion)
                    : GoStrings.ReferenceAddedLatest(dialog.ModulePath);
            }
            catch (Exception ex)
            {
                VsShellUtilities.ShowMessageBox(
                    this,
                    GoStrings.AddReferenceFailed(ex.Message),
                    GoStrings.MessageBoxTitle,
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        /// <summary>Full path of the selected project when it is a .goproj; otherwise null.</summary>
        private static string GetActiveGoProjectPath(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(dte.ActiveSolutionProjects is Array projects) || projects.Length == 0)
            {
                return null;
            }
            var project = projects.GetValue(0) as Project;
            string fullName = project?.FullName;
            return fullName != null &&
                   fullName.EndsWith(".goproj", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(fullName)
                ? fullName
                : null;
        }

        /// <summary>
        /// Inserts or updates &lt;GoModuleReference Include="module" Version="v..." /&gt;
        /// in the project file. The MSBuild construction model is used (isolated
        /// ProjectCollection, so VS's own loaded copy is untouched) because it
        /// preserves the file's formatting and reuses an existing ItemGroup.
        /// An empty version means "latest": the Version attribute is omitted/removed.
        /// </summary>
        private static void AddOrUpdateReference(string projectPath, string modulePath, string version)
        {
            using (var collection = new ProjectCollection())
            {
                ProjectRootElement root = ProjectRootElement.Open(projectPath, collection);

                ProjectItemElement existing = root.Items.FirstOrDefault(i =>
                    i.ItemType == "GoModuleReference" &&
                    string.Equals(i.Include, modulePath, StringComparison.Ordinal));

                if (existing == null)
                {
                    ProjectItemGroupElement group =
                        root.Items.FirstOrDefault(i => i.ItemType == "GoModuleReference")?.Parent as ProjectItemGroupElement
                        ?? root.AddItemGroup();
                    ProjectItemElement item = group.AddItem("GoModuleReference", modulePath);
                    if (version.Length > 0)
                    {
                        item.AddMetadata("Version", version, expressAsAttribute: true);
                    }
                }
                else
                {
                    ProjectMetadataElement meta = existing.Metadata.FirstOrDefault(m => m.Name == "Version");
                    if (version.Length == 0)
                    {
                        if (meta != null)
                        {
                            existing.RemoveChild(meta);
                        }
                    }
                    else if (meta != null)
                    {
                        meta.Value = version;
                    }
                    else
                    {
                        existing.AddMetadata("Version", version, expressAsAttribute: true);
                    }
                }

                root.Save();
            }
        }
    }
}
