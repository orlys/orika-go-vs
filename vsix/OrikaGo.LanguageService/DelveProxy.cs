using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// A one-connection DAP relay that sits between Visual Studio's Debug
    /// Adapter Host and `dlv dap`, for the sole purpose of silencing delve's
    /// "unknown memoryReference" rejections.
    /// <para>
    /// Why it exists: on every stop, the host probes readMemory with the
    /// frame's instruction pointer (count=0). delve's readMemory only accepts
    /// references it handed out itself - isAddressable() covers just strings
    /// and slices (service/dap/server.go) - so a raw PC address is always
    /// rejected, and the failure surfaces to the user as an error on every
    /// single breakpoint hit. The engine metric MemoryReferencesAreAddresses=0
    /// does not stop the probe (verified).
    /// </para>
    /// <para>
    /// What it does: forwards both directions byte-for-byte, except that a
    /// FAILED readMemory response whose message says "unknown memoryReference"
    /// is rewritten into a successful empty read - which is exactly what delve
    /// itself answers for a count=0 read of a reference it does know. Real
    /// reads (a string or slice variable's own reference) still go through
    /// delve untouched, and every other message is passed along verbatim.
    /// ponytail: single client, no pooling - dlv dap serves exactly one
    /// session anyway.
    /// </para>
    /// </summary>
    internal static class DelveProxy
    {
        private static readonly Regex UnknownMemoryReference =
            new Regex("\"command\"\\s*:\\s*\"readMemory\"", RegexOptions.CultureInvariant);

        /// <summary>
        /// Starts listening on a free loopback port and relays the first
        /// connection to <paramref name="delvePort"/>. Returns the port the
        /// host should connect to.
        /// </summary>
        public static int Start(int delvePort)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    using (TcpClient host = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    using (var delve = new TcpClient())
                    {
                        listener.Stop();
                        await delve.ConnectAsync(IPAddress.Loopback, delvePort).ConfigureAwait(false);
                        NetworkStream hostStream = host.GetStream();
                        NetworkStream delveStream = delve.GetStream();

                        // seq -> memoryReference, so a rewritten response can
                        // echo the address the request asked for.
                        var pendingReads = new Dictionary<int, string>();

                        Task up = PumpAsync(hostStream, delveStream, msg =>
                        {
                            RecordReadMemoryRequest(msg, pendingReads);
                            return msg;
                        });
                        Task down = PumpAsync(delveStream, hostStream, msg => RewriteReadMemoryFailure(msg, pendingReads));
                        await Task.WhenAny(up, down).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // The session ended (or never started); nothing to clean up
                    // beyond the sockets already disposed above.
                }
                finally
                {
                    try { listener.Stop(); } catch (Exception) { }
                }
            });

            return proxyPort;
        }

        /// <summary>
        /// Reads Content-Length framed messages from <paramref name="from"/>,
        /// passes each through <paramref name="transform"/>, and writes the
        /// result to <paramref name="to"/> with a corrected header.
        /// </summary>
        private static async Task PumpAsync(Stream from, Stream to, Func<string, string> transform)
        {
            var buffer = new byte[16384];
            var pending = new List<byte>(16384);

            while (true)
            {
                int read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }
                for (int i = 0; i < read; i++)
                {
                    pending.Add(buffer[i]);
                }

                while (true)
                {
                    // Headers are ASCII; the body is UTF-8 and counted in bytes.
                    string head = Encoding.ASCII.GetString(pending.ToArray(), 0, Math.Min(pending.Count, 256));
                    int headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0)
                    {
                        break;
                    }
                    Match lengthMatch = Regex.Match(head.Substring(0, headerEnd),
                        @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!lengthMatch.Success)
                    {
                        return; // Unframeable stream: bail out rather than corrupt it.
                    }
                    int bodyLength = int.Parse(lengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    int total = headerEnd + 4 + bodyLength;
                    if (pending.Count < total)
                    {
                        break;
                    }

                    byte[] bodyBytes = pending.GetRange(headerEnd + 4, bodyLength).ToArray();
                    pending.RemoveRange(0, total);

                    string body = Encoding.UTF8.GetString(bodyBytes);
                    string outBody = transform(body);
                    byte[] outBytes = Encoding.UTF8.GetBytes(outBody);
                    byte[] header = Encoding.ASCII.GetBytes(
                        "Content-Length: " + outBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

                    await to.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                    await to.WriteAsync(outBytes, 0, outBytes.Length).ConfigureAwait(false);
                    await to.FlushAsync().ConfigureAwait(false);
                }
            }
        }

        private static void RecordReadMemoryRequest(string msg, Dictionary<int, string> pendingReads)
        {
            if (msg.IndexOf("\"readMemory\"", StringComparison.Ordinal) < 0 ||
                msg.IndexOf("\"request\"", StringComparison.Ordinal) < 0)
            {
                return;
            }
            Match seq = Regex.Match(msg, "\"seq\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);
            Match reference = Regex.Match(msg, "\"memoryReference\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);
            if (seq.Success && reference.Success)
            {
                pendingReads[int.Parse(seq.Groups[1].Value, CultureInfo.InvariantCulture)] = reference.Groups[1].Value;
            }
        }

        private static string RewriteReadMemoryFailure(string msg, Dictionary<int, string> pendingReads)
        {
            if (msg.IndexOf("unknown memoryReference", StringComparison.Ordinal) < 0 ||
                !UnknownMemoryReference.IsMatch(msg))
            {
                return msg;
            }

            Match requestSeq = Regex.Match(msg, "\"request_seq\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);
            Match seq = Regex.Match(msg, "\"seq\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);
            if (!requestSeq.Success)
            {
                return msg;
            }

            int requestSeqValue = int.Parse(requestSeq.Groups[1].Value, CultureInfo.InvariantCulture);
            string address;
            if (!pendingReads.TryGetValue(requestSeqValue, out address))
            {
                address = "0x0";
            }
            pendingReads.Remove(requestSeqValue);

            // Same shape delve returns for a zero-length read it does accept:
            // success, the requested address, no data.
            return "{\"type\":\"response\"," +
                   "\"request_seq\":" + requestSeqValue.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"success\":true," +
                   "\"command\":\"readMemory\"," +
                   "\"body\":{\"address\":\"" + address + "\",\"unreadableBytes\":0}," +
                   "\"seq\":" + (seq.Success ? seq.Groups[1].Value : "0") + "}";
        }
    }
}
