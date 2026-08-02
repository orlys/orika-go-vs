using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Debugger.DebugAdapterHost.Interfaces;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Debug Adapter Host adapter launcher for the Go engine, registered via
    /// the "AdapterLauncher" value in goproj.pkgdef. Its job is the ATTACH
    /// path: 偵錯 → 附加至處理序 selects a PID and the code type "Go Debugger
    /// (Delve)", the host asks this class to prepare the session, and it
    /// starts `dlv dap`, then rewrites the launch JSON to
    /// {"$debugServer":port, "request":"attach", "mode":"local",
    /// "processId":pid} - the host connects to the TCP port instead of
    /// spawning an adapter (dlv has no stdio mode), same trick as F5.
    /// F5 launches pass through untouched: GoDebugLaunchProvider already
    /// supplies a complete configuration.
    /// </summary>
    [ComVisible(true)]
    [Guid(ClsidString)]
    public sealed class GoAdapterLauncher : IAdapterLauncher
    {
        /// <summary>Must match the CLSID + "AdapterLauncher" entries in goproj.pkgdef.</summary>
        public const string ClsidString = "B7A3F2D9-5C81-4E6A-9F42-8D0E3C7B6A25";

        public void Initialize(IDebugAdapterHostContext context)
        {
        }

        public void UpdateLaunchOptions(IAdapterLaunchInfo launchInfo)
        {
            if (launchInfo.LaunchType != LaunchType.Attach)
            {
                return; // F5/Ctrl+F5: GoDebugLaunchProvider owns the configuration.
            }

            string dlv = GoToolLocator.Find("dlv.exe");
            if (dlv == null)
            {
                throw new FileNotFoundException(GoStrings.DlvMissing);
            }

            int pid = launchInfo.AttachProcessId;

            // Working directory: the target's own directory when readable, so
            // any relative paths dlv reports line up with the binary.
            string workingDirectory = null;
            try
            {
                workingDirectory = Path.GetDirectoryName(
                    System.Diagnostics.Process.GetProcessById(pid).MainModule.FileName);
            }
            catch (Exception)
            {
                // Access denied on MainModule (elevated target etc.) - dlv will
                // fail attaching to such a process anyway with its own message.
            }

            // Sync-over-async: UpdateLaunchOptions is synchronous by contract.
            // Worst case is the 10s listen timeout; typical is <300ms.
            int port = DelveServer.StartAsync(dlv, workingDirectory, visibleConsole: false)
                .GetAwaiter().GetResult();

            launchInfo.LaunchJson =
                "{\"$debugServer\":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"type\":\"go\",\"request\":\"attach\",\"mode\":\"local\"," +
                "\"processId\":" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        public ITargetHostProcess LaunchAdapter(IAdapterLaunchInfo launchInfo, ITargetHostInterop targetInterop)
        {
            // Never reached: every configuration this launcher produces carries
            // "$debugServer", which makes the host connect instead of spawning.
            throw new NotSupportedException("The Go debug adapter is reached via $debugServer, not spawned.");
        }
    }
}
