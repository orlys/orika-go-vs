using System;
using System.Net;
using System.Runtime.InteropServices;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Answers "which loopback port is process N listening on?".
    /// <para>
    /// <see cref="System.Net.NetworkInformation.IPGlobalProperties.GetActiveTcpListeners"/>
    /// reports endpoints but not their owners, which is not enough: a readiness
    /// check that only asks "is anything listening on this port?" cannot tell
    /// the process it started apart from whatever else happened to bind the
    /// same port. GetExtendedTcpTable carries the owning PID, so dlv can be let
    /// pick its own port (:0) and be asked afterwards which one it got - there
    /// is then no window in which the port could belong to someone else.
    /// </para>
    /// </summary>
    internal static class TcpListenerTable
    {
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint MIB_TCP_STATE_LISTEN = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            // Stored network-byte-order in the low two bytes, per MSDN.
            public byte LocalPort1;
            public byte LocalPort2;
            public byte LocalPort3;
            public byte LocalPort4;
            public uint RemoteAddr;
            public byte RemotePort1;
            public byte RemotePort2;
            public byte RemotePort3;
            public byte RemotePort4;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(
            IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        /// <summary>
        /// Returns the loopback port <paramref name="processId"/> is listening
        /// on, or 0 when it is not listening (yet) or the table cannot be read.
        /// Only IPv4 loopback is considered - that is what dlv is told to bind.
        /// </summary>
        public static int FindLoopbackListenerPort(int processId)
        {
            int size = 0;
            int result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (result != ERROR_INSUFFICIENT_BUFFER && result != 0)
            {
                return 0;
            }

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                {
                    return 0;
                }

                int rowCount = Marshal.ReadInt32(table);
                IntPtr row = table + sizeof(int);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                uint loopback = (uint)IPAddress.HostToNetworkOrder(unchecked((int)0x7F000001));

                for (int i = 0; i < rowCount; i++)
                {
                    var entry = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(row, typeof(MIB_TCPROW_OWNER_PID));
                    row += rowSize;

                    if (entry.OwningPid != (uint)processId ||
                        entry.State != MIB_TCP_STATE_LISTEN ||
                        entry.LocalAddr != loopback)
                    {
                        continue;
                    }
                    return (entry.LocalPort1 << 8) | entry.LocalPort2;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }

            return 0;
        }
    }
}
