using System;
using System.Collections.Concurrent;
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
                        // echo the address the request asked for. Concurrent:
                        // the two pumps run at the same time, one writing it and
                        // one reading/removing, and a plain Dictionary corrupts
                        // (or throws) under that.
                        var pendingReads = new ConcurrentDictionary<int, string>();

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
                    // The scan covers everything buffered rather than a fixed
                    // prefix: DAP allows headers beyond Content-Length, and a
                    // cap smaller than the real header block would never find
                    // the terminator, leaving the pump waiting forever on a
                    // frame it had already received in full.
                    int headerEnd = IndexOfHeaderEnd(pending);
                    if (headerEnd < 0)
                    {
                        break;
                    }
                    string head = Encoding.ASCII.GetString(pending.GetRange(0, headerEnd).ToArray());
                    Match lengthMatch = Regex.Match(head,
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

        /// <summary>
        /// Byte offset of the CRLFCRLF that ends the header block, or -1.
        /// </summary>
        private static int IndexOfHeaderEnd(List<byte> pending)
        {
            for (int i = 0; i + 3 < pending.Count; i++)
            {
                if (pending[i] == (byte)'\r' && pending[i + 1] == (byte)'\n' &&
                    pending[i + 2] == (byte)'\r' && pending[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static void RecordReadMemoryRequest(string msg, ConcurrentDictionary<int, string> pendingReads)
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

        private static string RewriteReadMemoryFailure(string msg, ConcurrentDictionary<int, string> pendingReads)
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
            if (!pendingReads.TryRemove(requestSeqValue, out address) || !IsAddressLiteral(address))
            {
                // The address is echoed into hand-built JSON below, so anything
                // that is not plainly an address is replaced rather than escaped
                // - a reference containing a quote or a backslash would produce
                // a malformed response and drop the session.
                address = "0x0";
            }

            // Same shape delve returns for a zero-length read it does accept:
            // success, the requested address, no data.
            return "{\"type\":\"response\"," +
                   "\"request_seq\":" + requestSeqValue.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"success\":true," +
                   "\"command\":\"readMemory\"," +
                   "\"body\":{\"address\":\"" + address + "\",\"unreadableBytes\":0}," +
                   "\"seq\":" + (seq.Success ? seq.Groups[1].Value : "0") + "}";
        }

        /// <summary>
        /// True for the hex addresses delve and the host actually exchange
        /// ("0x4a1c20"); false for anything needing JSON escaping.
        /// </summary>
        private static bool IsAddressLiteral(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32)
            {
                return false;
            }
            foreach (char c in value)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                          (c >= 'A' && c <= 'F') || c == 'x' || c == 'X';
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
