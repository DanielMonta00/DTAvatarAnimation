using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

// Who is listening on a TCP port. The inference servers bind with SO_REUSEADDR, which on Windows lets a second server take a port the
// first one already holds, without an error (the connections then go to whichever of them the system picks). So "is a server there?" is
// asked of the operating system and not of the server: connecting would take the server's one client slot for a moment, and while
// it serves somebody else a probe can time out as if nobody were there (which is how a second server got started next to the first).
public static class FvpServerHygiene
{
    // Process ids that listen on `port` (IPv4). A process whose id is not known (not Windows, or the table could not be read) is -1.
    public static List<int> ListenerPids(int port)
    {
        var pids = new List<int>();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            ReadListenTable(port, pids);
            return pids;
        }
        catch (Exception) { pids.Clear(); }      // no iphlpapi: fall through to the portable answer
#endif
        try
        {
            foreach (System.Net.IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port) { pids.Add(-1); break; }
        }
        catch (Exception) { }
        return pids;
    }

    public static bool PortInUse(int port) => ListenerPids(port).Count > 0;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    const int AfInet = 2, TcpTableOwnerPidListener = 3, RowSize = 24;      // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid

    static void ReadListenTable(int port, List<int> pids)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);     // asks how big
        for (int attempt = 0; attempt < 4 && size > 0; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (rc == 122) continue;                                                           // ERROR_INSUFFICIENT_BUFFER: the table grew meanwhile
                if (rc != 0) throw new InvalidOperationException("GetExtendedTcpTable " + rc);
                int rows = Marshal.ReadInt32(buf);
                for (int i = 0; i < rows; i++)
                {
                    IntPtr row = IntPtr.Add(buf, 4 + i * RowSize);
                    int raw = Marshal.ReadInt32(row, 8);                                           // network byte order in the low 16 bits
                    int p = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (p == port) pids.Add(Marshal.ReadInt32(row, 20));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
#endif
}
