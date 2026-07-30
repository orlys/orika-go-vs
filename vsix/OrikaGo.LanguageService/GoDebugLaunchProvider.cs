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
    /// </summary>
    [Export(typeof(IDebugLaunchProvider))]
    [AppliesTo("OrikaGo")]
    [Order(1000)]
    internal sealed class GoDebugLaunchProvider : DebugLaunchProviderBase
    {
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
                    "找不到 Go 執行檔：" + (executable ?? "(GoOutputPath 未設定)") + "。請先建置專案。");
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
                    throw new FileNotFoundException(
                        "找不到 dlv.exe（delve 偵錯器）。已探測 " + GoToolLocator.ProbeDescription + "。" +
                        "請安裝：go install github.com/go-delve/delve/cmd/dlv@latest");
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

            var startInfo = new ProcessStartInfo
            {
                FileName = dlvPath,
                Arguments = "dap --listen=127.0.0.1:0",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(startInfo);
            try
            {
                // First stdout line: "DAP server listening at: 127.0.0.1:<port>"
                var readLine = process.StandardOutput.ReadLineAsync();
                var completed = await Task.WhenAny(readLine, Task.Delay(TimeSpan.FromSeconds(10)));
                string line = completed == readLine ? readLine.Result : null;

                Match match = line != null
                    ? Regex.Match(line, @"listening at:.*:(\d+)", RegexOptions.CultureInvariant)
                    : Match.Empty;
                if (!match.Success)
                {
                    throw new InvalidOperationException(
                        "dlv dap 未如預期啟動" + (line != null ? "，輸出：" + line : "（逾時）") + "。");
                }

                // Keep both pipes drained so dlv can never block on a full buffer.
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();

                _dlvServer = process;
                return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
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
