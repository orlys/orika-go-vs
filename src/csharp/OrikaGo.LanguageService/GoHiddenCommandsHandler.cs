using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Base for the handlers that hide .NET-only commands on Go projects.
    /// <para>
    /// Project context-menu commands DO route through CPS command-group
    /// handlers, and here <see cref="CommandStatus.Invisible"/> is honoured -
    /// the items genuinely disappear (unlike main-menu commands, which can
    /// only be greyed out; see <see cref="GoNuGetCommandFilter"/>).
    /// </para>
    /// <para>
    /// Command set GUIDs and ids were read off the live IDE via
    /// DTE.Commands (name | Guid | ID), not guessed.
    /// </para>
    /// </summary>
    internal abstract class GoHiddenCommandsBase : IAsyncCommandGroupHandler
    {
        /// <summary>True for commands that should not appear on a Go project.</summary>
        protected abstract bool IsHidden(long commandId);

        public Task<CommandStatusResult> GetCommandStatusAsync(
            IImmutableSet<IProjectTree> items, long commandId, bool focused, string commandText, CommandStatus progressiveStatus)
        {
            if (IsHidden(commandId))
            {
                return Task.FromResult(new CommandStatusResult(
                    true, commandText, progressiveStatus | CommandStatus.Invisible));
            }
            return CommandStatusResult.Unhandled.AsTask();
        }

        public Task<bool> TryHandleCommandAsync(
            IImmutableSet<IProjectTree> items, long commandId, bool focused, long commandExecuteOptions,
            System.IntPtr variantArgIn, System.IntPtr variantArgOut)
        {
            // Visibility only - nothing is reimplemented.
            return Task.FromResult(false);
        }
    }

    /// <summary>「管理 NuGet 套件」: Go dependencies come from go.mod.</summary>
    [ExportCommandGroup("25FD982B-8CAE-4CBD-A440-E03FFCCDE106")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenNuGetCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 0x100 || id == 0x200;
    }

    /// <summary>「Pack」: NuGet packaging has no Go equivalent.</summary>
    [ExportCommandGroup("568ABDF7-D522-474D-9EED-34B5E5095BA5")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenPackCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 8192 || id == 8193;
    }

    /// <summary>
    /// 「Publish…」: the wizard produces .NET publish profiles (Azure,
    /// ClickOnce, folder) that mean nothing for a Go binary. Cross-compiling
    /// still works from the CLI - `dotnet publish -r linux-arm64` is wired to
    /// GOOS/GOARCH by the SDK.
    /// </summary>
    [ExportCommandGroup("1496A755-94DE-11D0-8C3F-00C04FC2AAE2")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenPublishCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 2005 || id == 2006;
    }

    /// <summary>「Modernize」: the .NET upgrade assistant.</summary>
    [ExportCommandGroup("31760A92-B75C-472D-B977-7CAEAB0AF122")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenModernizeCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 1280 || id == 1296;
    }

    /// <summary>
    /// 「Code Cleanup」: Roslyn formatting/fixers for C# and VB. Every command
    /// in this set belongs to that feature (run default/custom, configure, and
    /// their editor and solution variants), so the whole group is hidden -
    /// hiding only the known ids left the submenu container behind.
    /// </summary>
    [ExportCommandGroup("160961B3-909D-4B28-9353-A1BEF587B4A6")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenCodeCleanupCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => true;
    }

    /// <summary>「管理使用者祕密」: an ASP.NET Core feature.</summary>
    [ExportCommandGroup("9C5B3619-FD0B-467C-B06D-FBEB1496FB1A")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenUserSecretsCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 1792;
    }

    /// <summary>「加入 → 連線服務」: Azure/WCF/REST service references.</summary>
    [ExportCommandGroup("A114CF9C-BD45-4A48-92EF-D9BBBC0B3DF0")]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoHiddenConnectedServiceCommandsHandler : GoHiddenCommandsBase
    {
        protected override bool IsHidden(long id) => id == 17 || id == 19;
    }
}
