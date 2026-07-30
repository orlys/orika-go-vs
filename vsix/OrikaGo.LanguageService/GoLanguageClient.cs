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
using StreamJsonRpc;
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
    public sealed class GoLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
    {
        private const string LogSource = "OrikaGo.LanguageService";

        private readonly IServiceProvider _serviceProvider;
        private Process _serverProcess;
        private JsonRpc _rpc;
        private FileSystemWatcher[] _watchers;
        private string _workspaceRoot;

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
        /// This is the only channel available: VS sends
        /// <c>Capabilities.Workspace.DidChangeWatchedFiles = new DynamicRegistrationSetting(false)</c>
        /// in "initialize", so gopls cannot register its own watchers via
        /// client/registerCapability.
        /// </para>
        /// <para>
        /// The trailing entries are exclusions. The documented syntax is "glob patterns
        /// following the standard in .gitignore", and VS implements that literally: the
        /// patterns are handed to <c>IWorkspaceItemFilterService.CreateFileMatcher</c>,
        /// whose rule parser treats a leading '!' as a negation
        /// (<c>SingleRuleMatcher.FromGlob</c>: <c>if (span[0] == '!') action = FilterResult.NotMatch;</c>)
        /// and whose aggregate matcher reverses the rule list and stops at the first hit,
        /// so the *last* matching pattern wins. Two consequences shape the list below:
        /// includes must come first, and a negation-only list would match nothing at all
        /// (an unmatched path yields FilterResult.Unknown, which the matcher reports as
        /// "no match").
        /// </para>
        /// <para>
        /// The excluded directories mirror the <c>directoryFilters</c> in
        /// <see cref="InitializationOptions"/>; without them VS forwards changes for trees
        /// gopls has been told to ignore, which on a large solution is pure noise (the
        /// SDK writes into bin/ and obj/ on every build). vendor/ is deliberately *not*
        /// excluded: it is part of the build in vendor mode, so gopls does need to hear
        /// about it, and it is absent from directoryFilters for the same reason.
        /// </para>
        /// <para>
        /// Two limits worth knowing. Exclusions filter notifications, they do not narrow
        /// what VS watches - the file-watcher subscription is workspace-wide and the
        /// patterns are evaluated per event - so the saving is in LSP traffic and gopls
        /// work, not in OS-level watching. And the only code that reads this property is
        /// the Open Folder host (<c>OpenFolderServices.OnWorkspaceFileSystemChangedAsync</c>);
        /// in .sln mode nothing consumes it, which is why <see cref="StartFileWatchers"/>
        /// runs the same patterns through client-side FileSystemWatchers and forwards
        /// the events over the rpc itself - solution mode gets watched-file events
        /// from there instead.
        /// </para>
        /// </summary>
        public IEnumerable<string> FilesToWatch => new[]
        {
            "**/*.go",
            "**/go.mod",
            "**/go.sum",
            "**/go.work",

            // Excludes, last-match-wins: these must stay after the includes above.
            "!**/node_modules/**",
            "!**/bin/**",
            "!**/obj/**",
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
                LogError("gopls.exe was not found on PATH, in GOBIN, in GOPATH\\bin, or in %USERPROFILE%\\go\\bin. " +
                         "Install it with: go install golang.org/x/tools/gopls@latest");
                return null;
            }

            string workspaceRoot = await GetWorkspaceRootAsync(token);
            _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                ? workspaceRoot
                : null;
            string workingDirectory = _workspaceRoot
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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
            // The watchers and the rpc belong to the server instance being torn down.
            DisposeWatchers();
            _rpc = null;

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
            // Not before "initialized": pushing workspace/didChangeWatchedFiles at
            // any earlier point would violate the LSP lifecycle.
            StartFileWatchers();
            return Task.CompletedTask;
        }

        /// <summary>Middle layer is not needed; messages pass through untouched.</summary>
        public object MiddleLayer => null;

        /// <summary>No custom server-to-client messages are handled.</summary>
        public object CustomMessageTarget => null;

        /// <summary>
        /// Captures the JsonRpc connection to gopls. This is the channel the file
        /// watchers below use to hand-deliver workspace/didChangeWatchedFiles.
        /// </summary>
        public Task AttachForCustomMessageAsync(JsonRpc rpc)
        {
            _rpc = rpc;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Client-side replacement for the watched-files channel that solution mode
        /// lacks. VS only consumes <see cref="FilesToWatch"/> in Open Folder mode, and
        /// it hardcodes DidChangeWatchedFiles dynamicRegistration=false so gopls cannot
        /// register its own watcher - so in .sln/.slnx mode (the product's primary
        /// mode) nothing would ever tell gopls that `go get` in a terminal, or the
        /// SDK's GoEnsureMod target, rewrote go.mod/go.sum/go.work on disk, and it
        /// would keep serving a stale module graph until VS restarts. These watchers
        /// close that gap by forwarding the same patterns as
        /// <see cref="FilesToWatch"/> straight over the rpc.
        /// ponytail: no debounce - gopls dedups changes per snapshot; add coalescing
        /// only if large workspaces show notification pressure.
        /// </summary>
        private void StartFileWatchers()
        {
            DisposeWatchers();

            string root = _workspaceRoot;
            if (root == null || _rpc == null)
            {
                return;
            }

            var watchers = new List<FileSystemWatcher>(4);
            foreach (string filter in new[] { "*.go", "go.mod", "go.sum", "go.work" })
            {
                try
                {
                    var watcher = new FileSystemWatcher(root, filter)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Created += (s, e) => NotifyFileChange(e.FullPath, 1);
                    watcher.Changed += (s, e) => NotifyFileChange(e.FullPath, 2);
                    watcher.Deleted += (s, e) => NotifyFileChange(e.FullPath, 3);
                    watcher.Renamed += (s, e) =>
                    {
                        NotifyFileChange(e.OldFullPath, 3);
                        NotifyFileChange(e.FullPath, 1);
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[OrikaGo] file watcher for '" + filter + "' failed: " + ex.Message);
                }
            }
            _watchers = watchers.ToArray();
        }

        /// <summary>
        /// Forwards one file event to gopls as workspace/didChangeWatchedFiles
        /// (1=Created, 2=Changed, 3=Deleted). Excluded trees mirror the negations in
        /// <see cref="FilesToWatch"/>: build output and node_modules are noise gopls
        /// was told to ignore via directoryFilters anyway.
        /// </summary>
        private void NotifyFileChange(string fullPath, int changeType)
        {
            JsonRpc rpc = _rpc;
            if (rpc == null || IsExcludedPath(fullPath))
            {
                return;
            }

            try
            {
                string uri = new Uri(fullPath).AbsoluteUri;
                _ = rpc.NotifyWithParameterObjectAsync(
                    "workspace/didChangeWatchedFiles",
                    new { changes = new[] { new { uri, type = changeType } } });
            }
            catch (Exception ex)
            {
                // A dead rpc (server restart in flight) is not an error worth surfacing.
                Debug.WriteLine("[OrikaGo] didChangeWatchedFiles forward failed: " + ex.Message);
            }
        }

        private static bool IsExcludedPath(string fullPath)
        {
            foreach (string segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void DisposeWatchers()
        {
            FileSystemWatcher[] watchers = Interlocked.Exchange(ref _watchers, null);
            if (watchers == null)
            {
                return;
            }
            foreach (FileSystemWatcher watcher in watchers)
            {
                try
                {
                    watcher.Dispose();
                }
                catch (Exception)
                {
                    // Disposing a watcher whose directory vanished can throw; ignore.
                }
            }
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
        /// Locates gopls.exe. Order: every directory on PATH, then GOBIN, then each
        /// GOPATH entry's bin\, then %USERPROFILE%\go\bin. GOBIN/GOPATH are read from
        /// the process environment AND from `go env` - `go env -w` persists them into
        /// Go's env file, invisible to GetEnvironmentVariable, and `go install`
        /// honours exactly those values, so the probe must too or the extension's own
        /// "go install golang.org/x/tools/gopls@latest" remediation would install to
        /// a place this method never looks.
        /// </summary>
        private static string FindGopls()
        {
            var directories = new List<string>();

            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string rawDir in pathVariable.Split(Path.PathSeparator))
            {
                directories.Add(rawDir.Trim().Trim('"'));
            }

            string envGobin = Environment.GetEnvironmentVariable("GOBIN");
            if (!string.IsNullOrEmpty(envGobin))
            {
                directories.Add(envGobin.Trim());
            }
            string envGopath = Environment.GetEnvironmentVariable("GOPATH");
            if (!string.IsNullOrEmpty(envGopath))
            {
                foreach (string entry in envGopath.Split(Path.PathSeparator))
                {
                    if (entry.Trim().Length > 0)
                    {
                        directories.Add(Path.Combine(entry.Trim(), "bin"));
                    }
                }
            }

            string[] goEnv = RunGoEnv("GOBIN", "GOPATH");
            if (goEnv.Length > 0 && goEnv[0].Length > 0)
            {
                directories.Add(goEnv[0]);
            }
            if (goEnv.Length > 1 && goEnv[1].Length > 0)
            {
                foreach (string entry in goEnv[1].Split(Path.PathSeparator))
                {
                    if (entry.Trim().Length > 0)
                    {
                        directories.Add(Path.Combine(entry.Trim(), "bin"));
                    }
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                directories.Add(Path.Combine(userProfile, "go", "bin"));
            }

            foreach (string dir in directories)
            {
                if (string.IsNullOrEmpty(dir))
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
                    // Malformed entry (invalid characters) - skip it.
                }
            }

            return null;
        }

        /// <summary>
        /// Runs `go env <names>` and returns one trimmed line per requested name
        /// (positional; unset values come back as empty strings). Returns an empty
        /// array when the go command is unavailable or misbehaves.
        /// </summary>
        private static string[] RunGoEnv(params string[] names)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "go",
                    Arguments = "env " + string.Join(" ", names),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return Array.Empty<string>();
                    }
                    if (process.ExitCode != 0)
                    {
                        return Array.Empty<string>();
                    }

                    // Positional lines; an unset variable is an EMPTY line, so no
                    // RemoveEmptyEntries here or the mapping would shift.
                    string[] lines = output.Replace("\r", string.Empty).Split('\n');
                    var values = new string[names.Length];
                    for (int i = 0; i < names.Length; i++)
                    {
                        values[i] = i < lines.Length ? lines[i].Trim() : string.Empty;
                    }
                    return values;
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
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
