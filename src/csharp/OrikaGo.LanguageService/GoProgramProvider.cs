using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.Debugger.Interop;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Reports "this process contains a program my engine can debug" to the
    /// attach pipeline. Registered as the engine's "ProgramProvider" in
    /// goproj.pkgdef.
    /// <para>
    /// This is the piece whose absence made every attach fail with
    /// HRESULT 0x8971001E long before the adapter launcher was ever consulted:
    /// with no program provider, the shell finds no program belonging to this
    /// engine inside the target process and gives up. Modelled on the
    /// JavaScript/TypeScript debug adapter's provider, which is the one
    /// in-box example of a Debug Adapter Host engine doing LOCAL attach.
    /// </para>
    /// </summary>
    [ComVisible(true)]
    [Guid(ClsidString)]
    public sealed class GoProgramProvider : IDebugProgramProvider2
    {
        /// <summary>Must match the CLSID + "ProgramProvider" entries in goproj.pkgdef.</summary>
        public const string ClsidString = "3E8C1A47-9D26-4B85-BF03-71A5E9C4D682";

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int EFail = unchecked((int)0x80004005);

        /// <summary>enum_PROVIDER_FLAGS.PFLAG_GET_PROGRAM_NODES</summary>
        private const uint FlagGetProgramNodes = 0x10;

        /// <summary>enum_PROVIDER_FIELDS.PFIELD_PROGRAM_NODES</summary>
        private const uint FieldProgramNodes = 0x1;

        int IDebugProgramProvider2.GetProviderProcessData(
            enum_PROVIDER_FLAGS flags,
            IDebugDefaultPort2 port,
            AD_PROCESS_ID processId,
            CONST_GUID_ARRAY engineFilter,
            PROVIDER_PROCESS_DATA[] processArray)
        {
            if (processArray == null || processArray.Length == 0)
            {
                return EFail;
            }
            processArray[0] = default(PROVIDER_PROCESS_DATA);

            if (((uint)flags & FlagGetProgramNodes) == 0)
            {
                return 1; // S_FALSE: nothing to contribute for this query.
            }

            int pid = (int)processId.dwProcessId;
            string exePath = TryGetProcessImagePath(pid);
            if (exePath == null || !IsGoBinary(exePath))
            {
                return 1;
            }

            var node = (IDebugProgramNode2)new GoProgramNode(pid, GoDebugLaunchProvider.DelveEngineGuid, "Go Debugger (Delve)");
            IntPtr[] nodes = { Marshal.GetComInterfaceForObject(node, typeof(IDebugProgramNode2)) };
            IntPtr members = Marshal.AllocCoTaskMem(IntPtr.Size * nodes.Length);
            Marshal.Copy(nodes, 0, members, nodes.Length);

            processArray[0].Fields = (enum_PROVIDER_FIELDS)FieldProgramNodes;
            processArray[0].ProgramNodes.Members = members;
            processArray[0].ProgramNodes.dwCount = (uint)nodes.Length;
            return 0;
        }

        int IDebugProgramProvider2.GetProviderProgramNode(
            enum_PROVIDER_FLAGS flags,
            IDebugDefaultPort2 port,
            AD_PROCESS_ID processId,
            ref Guid guidEngine,
            ulong programId,
            out IDebugProgramNode2 programNode)
        {
            programNode = null;
            return EFail;
        }

        int IDebugProgramProvider2.WatchForProviderEvents(
            enum_PROVIDER_FLAGS flags,
            IDebugDefaultPort2 port,
            AD_PROCESS_ID processId,
            CONST_GUID_ARRAY engineFilter,
            ref Guid guidLaunchingEngine,
            IDebugPortNotify2 ad7EventCallback)
        {
            return 0;
        }

        int IDebugProgramProvider2.SetLocale(ushort wLangID) => 0;

        /// <summary>
        /// A Go binary carries the runtime build info magic that `go version
        /// &lt;exe&gt;` reads. Matching on it keeps "Go Debugger (Delve)" out of the
        /// code-type list for every unrelated process in the attach dialog.
        /// ponytail: scans the file once per query; the attach dialog asks for
        /// a handful of processes at a time.
        /// </summary>
        private static bool IsGoBinary(string exePath)
        {
            // "\xff Go buildinf:" - the header the Go linker writes into every
            // binary (runtime/debug.ReadBuildInfo / cmd/go's version command).
            byte[] magic = { 0xFF, 0x20, 0x47, 0x6F, 0x20, 0x62, 0x75, 0x69, 0x6C, 0x64, 0x69, 0x6E, 0x66, 0x3A };
            try
            {
                using (var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var window = new byte[64 * 1024];
                    int carry = magic.Length - 1;
                    int offset = 0;
                    while (true)
                    {
                        int read = stream.Read(window, offset, window.Length - offset);
                        if (read <= 0)
                        {
                            return false;
                        }
                        int available = offset + read;
                        for (int i = 0; i + magic.Length <= available; i++)
                        {
                            bool hit = true;
                            for (int j = 0; j < magic.Length; j++)
                            {
                                if (window[i + j] != magic[j]) { hit = false; break; }
                            }
                            if (hit)
                            {
                                return true;
                            }
                        }
                        // Keep the tail so a match spanning two windows is not missed.
                        Buffer.BlockCopy(window, available - carry, window, 0, carry);
                        offset = carry;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TryGetProcessImagePath(int pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var builder = new StringBuilder(4096);
                int size = builder.Capacity;
                return NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size)
                    ? builder.ToString(0, size)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }

    /// <summary>
    /// The single program the provider above reports for a Go process. Only
    /// the engine identity and the host PID are meaningful; the _V7 members
    /// are legacy and stay unimplemented, as in the in-box providers.
    /// </summary>
    internal sealed class GoProgramNode : IDebugProgramNode2
    {
        private const int EFail = unchecked((int)0x80004005);

        private readonly int _processId;
        private readonly Guid _engineGuid;
        private readonly string _engineName;

        public GoProgramNode(int processId, Guid engineGuid, string engineName)
        {
            _processId = processId;
            _engineGuid = engineGuid;
            _engineName = engineName;
        }

        int IDebugProgramNode2.GetEngineInfo(out string engineName, out Guid engineGuid)
        {
            engineName = _engineName;
            engineGuid = _engineGuid;
            return 0;
        }

        int IDebugProgramNode2.GetHostPid(AD_PROCESS_ID[] pHostProcessId)
        {
            if (pHostProcessId == null || pHostProcessId.Length == 0)
            {
                return EFail;
            }
            pHostProcessId[0].ProcessIdType = 0u; // AD_PROCESS_ID_SYSTEM
            pHostProcessId[0].dwProcessId = (uint)_processId;
            return 0;
        }

        int IDebugProgramNode2.GetHostName(enum_GETHOSTNAME_TYPE dwHostNameType, out string processName)
        {
            processName = null;
            return EFail;
        }

        int IDebugProgramNode2.GetProgramName(out string programName)
        {
            programName = null;
            return EFail;
        }

        int IDebugProgramNode2.Attach_V7(IDebugProgram2 pMDMProgram, IDebugEventCallback2 pCallback, uint dwReason) => EFail;

        int IDebugProgramNode2.DetachDebugger_V7() => EFail;

        int IDebugProgramNode2.GetHostMachineName_V7(out string hostMachineName)
        {
            hostMachineName = null;
            return EFail;
        }
    }
}
