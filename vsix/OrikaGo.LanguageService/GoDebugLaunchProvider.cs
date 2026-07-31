using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem.VS.Debug;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// F5 for .goproj projects: hands the built Go executable to the delve DAP
    /// engine (dlv dap via Visual Studio's Debug Adapter Host; registration in
    /// goproj.pkgdef). Ctrl+F5 (NoDebug) runs the executable directly without
    /// delve. Breakpoints, stepping, locals and goroutine stacks all flow
    /// through DAP once the engine attaches - nothing per-feature to do here.
    /// <para>
    /// Exported BOTH ways because the pipelines differ: with the
    /// LaunchProfiles capability (which the managed design-time targets bring,
    /// and whose subsystem owns the project's F5 plumbing - removing it makes
    /// Debug.Start unavailable outright), LaunchProfilesDebugLaunchProvider is
    /// the only IDebugLaunchProvider ever consulted, and it delegates to the
    /// highest-Order IDebugProfileLaunchTargetsProvider whose SupportsProfile
    /// says yes. The plain IDebugLaunchProvider export covers the no-profiles
    /// pipeline for completeness.
    /// </para>
    /// </summary>
    [Export(typeof(IDebugLaunchProvider))]
    [Export(typeof(IDebugProfileLaunchTargetsProvider))]
    [AppliesTo("OrikaGo")]
    [Order(9999999)] // must outrank every built-in provider or F5 silently goes elsewhere
    internal sealed class GoDebugLaunchProvider : DebugLaunchProviderBase, IDebugProfileLaunchTargetsProvider
    {
        /// <summary>Every profile of a .goproj debugs the Go binary via delve.</summary>
        public bool SupportsProfile(ILaunchProfile profile) => true;

        public Task OnBeforeLaunchAsync(DebugLaunchOptions launchOptions, ILaunchProfile profile) => Task.CompletedTask;

        public Task OnAfterLaunchAsync(DebugLaunchOptions launchOptions, ILaunchProfile profile) => Task.CompletedTask;

        public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(DebugLaunchOptions launchOptions, ILaunchProfile profile)
            => QueryDebugTargetsAsync(launchOptions);

        /// <summary>Engine GUID registered under AD7Metrics\Engine in goproj.pkgdef.</summary>
        public static readonly Guid DelveEngineGuid = new Guid("2A5D6E81-4C9B-45E2-B8F3-9D0C7A1E6F24");

        [ImportingConstructor]
        public GoDebugLaunchProvider(ConfiguredProject configuredProject)
            : base(configuredProject)
        {
        }

        public override Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
            => Task.FromResult(true);

        public override async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(DebugLaunchOptions launchOptions)
        {
            IProjectProperties properties = ConfiguredProject.Services.ProjectPropertiesProvider.GetCommonProperties();
            string executable = await properties.GetEvaluatedPropertyValueAsync("GoOutputPath");
            string arguments = await properties.GetEvaluatedPropertyValueAsync("StartArguments");
            string workingDirectory = await properties.GetEvaluatedPropertyValueAsync("MSBuildProjectDirectory");

            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                throw new FileNotFoundException(
                    GoStrings.GoExecutableMissing(executable ?? "(GoOutputPath?)"));
            }

            var settings = new DebugLaunchSettings(launchOptions)
            {
                LaunchOperation = DebugLaunchOperation.CreateProcess,
                Executable = executable,
                Arguments = arguments ?? string.Empty,
                CurrentDirectory = workingDirectory,
            };

            if ((launchOptions & DebugLaunchOptions.NoDebug) == DebugLaunchOptions.NoDebug)
            {
                // Ctrl+F5: plain run, no delve involved.
                settings.LaunchDebugEngineGuid = DebuggerEngines.NativeOnlyEngine;
            }
            else
            {
                string dlv = GoToolLocator.Find("dlv.exe");
                if (dlv == null)
                {
                    throw new FileNotFoundException(GoStrings.DlvMissing);
                }

                // dlv dap speaks TCP only - it cannot be spawned by the Debug
                // Adapter Host over stdio. So it is started HERE, and the port it
                // reports is handed to the host via "$debugServer": the host then
                // connects instead of launching an adapter process itself.
                int port = await StartDlvDapServerAsync(dlv, workingDirectory);

                settings.LaunchDebugEngineGuid = DelveEngineGuid;
                // The remaining (non-$) properties are what the host forwards as
                // the DAP launch-request arguments: dlv exec mode debugs the
                // already-built binary (Debug builds compile with -gcflags
                // "all=-N -l", so symbols and locals are intact).
                settings.Options = BuildLaunchOptions(port, executable, arguments, workingDirectory);
            }

            return new IDebugLaunchSettings[] { settings };
        }

        /// <summary>
        /// The dlv dap server of the most recent debug session. dlv exits by
        /// itself when its single session ends; this reference exists to reap a
        /// server whose session never started (connection failure, user cancel),
        /// which would otherwise linger until the next F5.
        /// </summary>
        private static Process _dlvServer;

        private static async Task<int> StartDlvDapServerAsync(string dlvPath, string workingDirectory)
        {
            Process previous = System.Threading.Interlocked.Exchange(ref _dlvServer, null);
            if (previous != null)
            {
                try
                {
                    if (!previous.HasExited)
                    {
                        previous.Kill();
                    }
                }
                catch (Exception) { }
                previous.Dispose();
            }

            // The port is picked HERE (bind :0, read it back, release) instead of
            // letting dlv pick one, because dlv must run WITHOUT redirected stdio:
            // the debuggee inherits dlv's console, and that console window is
            // where the Go program's stdout/stdin live during F5 - redirect
            // dlv's pipes and the program runs headless with its output shunted
            // into the Output window only.
            // ponytail: tiny bind-then-reuse race window; dlv fails fast and the
            // poll below reports it if the port gets stolen in between.
            int port;
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var startInfo = new ProcessStartInfo
            {
                FileName = dlvPath,
                // --check-go-version=false: delve only "supports" the last two Go
                // releases and refuses binaries built by an older toolchain
                // outright (a modal error kills the F5). The DWARF it reads is
                // stable across that gap in practice; a no-op when versions match.
                Arguments = "dap --check-go-version=false --listen=127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var process = Process.Start(startInfo);
            try
            {
                // Wait until dlv actually listens before handing the port to the
                // Debug Adapter Host (it connects immediately, no retry). The
                // check must NOT open a connection - dlv dap serves a single
                // client, and a probe connect would consume the session - so the
                // OS listener table is consulted instead.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (true)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(GoStrings.DlvExitedEarly(process.ExitCode));
                    }
                    bool listening = false;
                    foreach (System.Net.IPEndPoint listener in
                             System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    {
                        if (listener.Port == port)
                        {
                            listening = true;
                            break;
                        }
                    }
                    if (listening)
                    {
                        break;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new InvalidOperationException(GoStrings.DlvNotListening(port));
                    }
                    await Task.Delay(100);
                }

                _dlvServer = process;
                return port;
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception) { }
                process.Dispose();
                throw;
            }
        }

        private static string BuildLaunchOptions(int debugServerPort, string executable, string arguments, string workingDirectory)
        {
            // Hand-rolled JSON (no serializer dependency): every value goes
            // through JsonString below, so paths with backslashes and quotes
            // survive.
            var json = new StringBuilder();
            json.Append("{");
            json.Append("\"$debugServer\":").Append(debugServerPort).Append(',');
            json.Append("\"type\":\"go\",");
            json.Append("\"request\":\"launch\",");
            json.Append("\"mode\":\"exec\",");
            json.Append("\"stopOnEntry\":false,");
            // Keep the Threads window to USER goroutines; runtime internals
            // otherwise drown it (dlv >= 1.7.3).
            json.Append("\"hideSystemGoroutines\":true,");
            json.Append("\"program\":").Append(JsonString(executable)).Append(',');
            json.Append("\"cwd\":").Append(JsonString(workingDirectory));
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                json.Append(",\"args\":[");
                string[] parts = SplitCommandLine(arguments);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }
                    json.Append(JsonString(parts[i]));
                }
                json.Append(']');
            }
            json.Append("}");
            return json.ToString();
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>
        /// Splits StartArguments the way a shell would: whitespace-separated,
        /// double quotes group. dlv's DAP args field wants an array, not a
        /// single command line.
        /// </summary>
        private static string[] SplitCommandLine(string commandLine)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }
            return parts.ToArray();
        }
    }
}
