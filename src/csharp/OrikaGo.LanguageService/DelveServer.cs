using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Starts and reaps `dlv dap` TCP servers. Shared by the F5 launch provider
    /// and the attach adapter launcher - both hand the port to the Debug
    /// Adapter Host via "$debugServer" (dlv dap speaks TCP only; the host must
    /// never spawn it over stdio).
    /// </summary>
    internal static class DelveServer
    {
        /// <summary>
        /// The dlv dap server of the most recent debug session. dlv exits by
        /// itself when its single session ends; this reference exists to reap a
        /// server whose session never started (connection failure, user cancel),
        /// which would otherwise linger until the next launch.
        /// </summary>
        private static Process _server;

        /// <summary>
        /// Starts `dlv dap` and returns the listening port.
        /// </summary>
        /// <param name="dlvPath">Full path of dlv.exe.</param>
        /// <param name="workingDirectory">Server working directory.</param>
        /// <param name="visibleConsole">
        /// True for F5 launches: the debuggee inherits dlv's console, which is
        /// where the Go program's stdio lives. False for attach - the target
        /// process already owns its own console.
        /// </param>
        public static async Task<int> StartAsync(string dlvPath, string workingDirectory, bool visibleConsole)
        {
            Process previous = Interlocked.Exchange(ref _server, null);
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

            // dlv picks the port (:0) and we read it back from the OS listener
            // table, filtered to dlv's own PID. Preselecting a port here instead
            // (bind :0, read it back, release, pass it to dlv) leaves a window in
            // which another process can take it: the readiness poll below would
            // then see SOMEONE listening, report success, and the proxy would
            // relay the debug session to that other service. Asking who owns the
            // socket removes the window rather than narrowing it.
            //
            // Reading the port from dlv's stdout is not an option: dlv runs
            // WITHOUT redirected stdio (see visibleConsole) so the debuggee can
            // inherit the console.
            var startInfo = new ProcessStartInfo
            {
                FileName = dlvPath,
                // --check-go-version=false: delve only "supports" the last two Go
                // releases and refuses binaries built by an older toolchain
                // outright (a modal error kills the session). A no-op when
                // versions match.
                Arguments = "dap --check-go-version=false --listen=127.0.0.1:0",
                WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = !visibleConsole,
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
                int port;
                while (true)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(GoStrings.DlvExitedEarly(process.ExitCode));
                    }
                    port = TcpListenerTable.FindLoopbackListenerPort(process.Id);
                    if (port != 0)
                    {
                        break;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new InvalidOperationException(GoStrings.DlvNotListening);
                    }
                    // ConfigureAwait(false): the attach path blocks on this task
                    // from the UI thread (GetResult) - a captured UI context
                    // here would deadlock.
                    await Task.Delay(100).ConfigureAwait(false);
                }

                _server = process;
                // The host talks to the relay, not to dlv directly - see
                // DelveProxy for the one behaviour it changes.
                return DelveProxy.Start(port);
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
    }
}
