using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Task = System.Threading.Tasks.Task;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Language client that launches gopls (the official Go language server) over stdio
    /// and connects it to Visual Studio's LSP infrastructure for .go files.
    /// Provides completion, hover, signature help, go-to-definition, find references,
    /// rename, formatting, and diagnostics.
    /// </summary>
    [Export(typeof(ILanguageClient))]
    [ContentType(GoContentTypeDefinitions.ContentTypeName)]
    public sealed class GoLanguageClient : ILanguageClient, IDisposable
    {
        private const string LogSource = "OrikaGo.LanguageService";

        private readonly IServiceProvider _serviceProvider;
        private Process _serverProcess;

        [ImportingConstructor]
        public GoLanguageClient([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Display name shown in Visual Studio (e.g. in the language client status messages).
        /// </summary>
        public string Name => "Orika Go Language Service";

        /// <summary>
        /// Deliberately null: this client opts out of workspace/didChangeConfiguration and
        /// pushes every setting once through <see cref="InitializationOptions"/> instead, so
        /// gopls is fully configured by the time "initialize" returns and never depends on a
        /// post-startup settings round-trip.
        /// </summary>
        public IEnumerable<string> ConfigurationSections => null;

        /// <summary>
        /// Settings pushed to gopls in the LSP "initialize" request.
        /// <para>
        /// gopls flattens hierarchical setting names before dispatching them
        /// (internal/lsp/source/options.go: <c>split := strings.Split(name, "."); name = split[len(split)-1]</c>),
        /// so the <c>ui.*</c> / <c>build.*</c> prefixes used in the gopls documentation are
        /// presentation grouping only. The wire format is a flat object, which is what we emit.
        /// </para>
        /// <para>
        /// An unrecognised key is not ignored: it produces
        /// <c>"Invalid settings: unexpected gopls setting ..."</c> as an Error-severity
        /// window/showMessage on every solution open. Only keys that exist in
        /// <c>gopls api-json</c> -> <c>.Options.User[].Name</c> are sent.
        /// </para>
        /// </summary>
        public object InitializationOptions => new Dictionary<string, object>
        {
            // Without this gopls answers textDocument/semanticTokens/full with
            // "semantictokens are disabled" and .go files fall back to plain-text colouring.
            ["semanticTokens"] = true,

            // staticcheck's SA/S/ST checks on top of the default vet-style analyzers.
            ["staticcheck"] = true,

            // Completion of a function inserts its parameters as editable placeholders.
            ["usePlaceholders"] = true,

            // gofumpt is stricter than gofmt and rewrites code on format; leave it opt-in.
            ["gofumpt"] = false,

            // gopls defaults "hints" to an empty map, which disables inlay hints outright.
            // All seven hint kinds implemented by gopls v0.14.x are enabled here.
            ["hints"] = new Dictionary<string, bool>
            {
                ["assignVariableTypes"] = true,
                ["compositeLiteralFields"] = true,
                ["compositeLiteralTypes"] = true,
                ["constantValues"] = true,
                ["functionTypeParameters"] = true,
                ["parameterNames"] = true,
                ["rangeVariableTypes"] = true,
            },

            // Analyzer toggles beyond the default set. "shadow" is listed explicitly at its
            // default of false because it is noisy enough that its absence should be intentional.
            ["analyses"] = new Dictionary<string, bool>
            {
                ["unusedparams"] = true,
                ["shadow"] = false,
            },

            // Keep gopls from walking build output. gopls' own default only excludes
            // node_modules; bin/ and obj/ are added for the SDK's output layout.
            ["directoryFilters"] = new[] { "-**/node_modules", "-**/bin", "-**/obj" },

            // Extra flags (e.g. -tags) for the underlying go/packages loads.
            ["buildFlags"] = new string[0],

            // Extra environment for the go command, on top of the inherited process environment.
            ["env"] = new Dictionary<string, string>(),
        };

        /// <summary>
        /// Glob patterns Visual Studio watches on gopls' behalf, forwarding matches as
        /// workspace/didChangeWatchedFiles.
        /// <para>
        /// These are edits the editor never sees: the SDK's GoEnsureMod target runs
        /// `go mod init` / `go mod edit -go=&lt;LangVersion&gt;` and GoEnsureWorkspace runs
        /// `go work use` (both guarded so they only fire when the file actually needs
        /// changing, but a LangVersion change or a new project does rewrite go.mod/go.work
        /// mid-build); `go build` refreshes go.sum; and `go get` / `go mod tidy` run from a
        /// terminal change go.mod, go.sum and .go files. Without these patterns gopls keeps
        /// serving a stale module graph until the changed file happens to be opened.
        /// </para>
        /// </summary>
        public IEnumerable<string> FilesToWatch => new[]
        {
            "**/*.go",
            "**/go.mod",
            "**/go.sum",
            "**/go.work",
        };

        /// <summary>
        /// Show the gold bar notification if the server fails to initialize.
        /// </summary>
        public bool ShowNotificationOnInitializeFailed => true;

        public event AsyncEventHandler<EventArgs> StartAsync;

#pragma warning disable CS0067 // The event is part of the ILanguageClient contract; VS raises stop internally.
        public event AsyncEventHandler<EventArgs> StopAsync;
#pragma warning restore CS0067

        /// <summary>
        /// Launches gopls and returns a Connection over its stdin/stdout.
        /// Returns null (no crash) when gopls cannot be located or started.
        /// </summary>
        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            string goplsPath = FindGopls();
            if (goplsPath == null)
            {
                LogError("gopls.exe was not found on PATH or in %USERPROFILE%\\go\\bin. " +
                         "Install it with: go install golang.org/x/tools/gopls@latest");
                return null;
            }

            string workingDirectory = await GetWorkspaceRootAsync(token);
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = goplsPath,
                Arguments = BuildGoplsArguments(),
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Visual Studio calls ActivateAsync again when it restarts the client (solution
            // reload, server crash). Without this the previous gopls keeps running with no
            // reader on its stdout and is never reaped - one orphan per restart.
            TerminateServer();

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                if (!process.Start())
                {
                    LogError("Failed to start gopls process at '" + goplsPath + "'.");
                    process.Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogError("Exception starting gopls at '" + goplsPath + "': " + ex);
                process.Dispose();
                return null;
            }

            // Drain stderr so the gopls process never blocks on a full pipe; forward to debug output.
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.WriteLine("[gopls] " + e.Data);
                }
            };
            process.BeginErrorReadLine();

            // Drop the reference once gopls is gone so TerminateServer/Dispose never touch
            // a recycled PID.
            process.Exited += (sender, e) =>
            {
                if (ReferenceEquals(_serverProcess, sender))
                {
                    _serverProcess = null;
                }
            };

            _serverProcess = process;
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        /// <summary>
        /// Kills the running gopls, if any. gopls normally exits when Visual Studio closes
        /// its stdin, but that only happens on a clean LSP shutdown; a crashed or hung server,
        /// or a client restart, leaves it alive with nobody draining its pipes.
        /// </summary>
        private void TerminateServer()
        {
            Process process = Interlocked.Exchange(ref _serverProcess, null);
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                // Already gone, or access denied on a PID we no longer own: nothing to reap.
                Debug.WriteLine("[gopls] terminate failed: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>
        /// MEF disposes exported parts when Visual Studio shuts down; that is the last
        /// chance to reap gopls before it is orphaned.
        /// </summary>
        public void Dispose()
        {
            TerminateServer();
        }

        /// <summary>
        /// Called once the client has been loaded into VS; signals that the server may be started.
        /// </summary>
        public async Task OnLoadedAsync()
        {
            AsyncEventHandler<EventArgs> startAsync = StartAsync;
            if (startAsync != null)
            {
                await startAsync.InvokeAsync(this, EventArgs.Empty);
            }
        }

        public Task OnServerInitializedAsync()
        {
            return Task.CompletedTask;
        }

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            string details = initializationState?.StatusMessage;
            if (string.IsNullOrEmpty(details))
            {
                details = initializationState?.InitializationException?.Message ?? "unknown error";
            }

            LogError("gopls language server failed to initialize: " + details);

            var failureContext = new InitializationFailureContext
            {
                FailureMessage = "Orika Go Language Service could not start gopls (" + details + "). " +
                                 "Verify that gopls is installed and on PATH (go install golang.org/x/tools/gopls@latest).",
            };
            return Task.FromResult(failureContext);
        }

        /// <summary>
        /// Command line for the gopls server process.
        /// <para>
        /// RPC tracing is verbose and costs throughput, so it is opt-in: set the environment
        /// variable ORIKA_GOPLS_RPCTRACE to 1/true/yes before launching Visual Studio to have
        /// gopls log every LSP message to stderr (surfaced in the debug output).
        /// </para>
        /// </summary>
        private static string BuildGoplsArguments()
        {
            return IsEnvFlagEnabled("ORIKA_GOPLS_RPCTRACE")
                ? "serve -rpc.trace"
                : "serve";
        }

        /// <summary>
        /// Treats an environment variable as a boolean switch. Only explicit affirmative
        /// values enable it; anything unset, empty or unrecognised is off.
        /// </summary>
        private static bool IsEnvFlagEnabled(string variableName)
        {
            string raw;
            try
            {
                raw = Environment.GetEnvironmentVariable(variableName);
            }
            catch (SecurityException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            raw = raw.Trim();
            return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Locates gopls.exe: every directory on PATH first, then %USERPROFILE%\go\bin.
        /// </summary>
        private static string FindGopls()
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string rawDir in pathVariable.Split(Path.PathSeparator))
            {
                string dir = rawDir.Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(dir, "gopls.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry (invalid characters) - skip it.
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                string fallback = Path.Combine(userProfile, "go", "bin", "gopls.exe");
                if (File.Exists(fallback))
                {
                    return fallback;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the root directory of the currently opened solution or folder, when available.
        /// Works for both solution mode and Open Folder mode (IVsSolution reports the folder root).
        /// </summary>
        private async Task<string> GetWorkspaceRootAsync(CancellationToken token)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_serviceProvider?.GetService(typeof(SVsSolution)) is IVsSolution solution)
                {
                    if (solution.GetSolutionInfo(out string solutionDirectory, out _, out _) == 0 &&
                        !string.IsNullOrEmpty(solutionDirectory))
                    {
                        return solutionDirectory;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OrikaGo] Could not determine workspace root: " + ex);
            }
            finally
            {
                await TaskScheduler.Default;
            }

            return null;
        }

        private static void LogError(string message)
        {
            Debug.WriteLine("[OrikaGo] " + message);
            ActivityLog.TryLogError(LogSource, message);
        }
    }
}
