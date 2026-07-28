using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
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
    public sealed class GoLanguageClient : ILanguageClient
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
        /// No client-pushed workspace/didChangeConfiguration sections; gopls uses its defaults.
        /// </summary>
        public IEnumerable<string> ConfigurationSections => null;

        /// <summary>
        /// Initialization options passed to gopls in the LSP "initialize" request.
        /// </summary>
        public object InitializationOptions => null;

        /// <summary>
        /// File watching is handled by gopls itself via workspace/didChangeWatchedFiles registration.
        /// </summary>
        public IEnumerable<string> FilesToWatch => null;

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
                Arguments = "serve",
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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

            _serverProcess = process;
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
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
