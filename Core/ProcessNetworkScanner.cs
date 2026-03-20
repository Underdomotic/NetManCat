using System.Net;
using System.Runtime.InteropServices;

namespace NetManCat.Core;

/// <summary>
/// Enumera le connessioni TCP attive del sistema tramite
/// GetExtendedTcpTable (IP Helper API).
/// Richiede privilegi amministrativi per ottenere i nomi dei processi.
///
/// Ottimizzazione CPU: i nomi dei processi sono cached per PID con TTL di 10s.
/// Process.GetProcessById è costoso (~0.5 ms × N connessioni × frequenza scan);
/// con la cache la maggior parte delle chiamate è O(1) sul Dictionary.
/// </summary>
public static class ProcessNetworkScanner
{
    // -------------------------------------------------------------------
    // P/Invoke — IP Helper API
    // -------------------------------------------------------------------
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TcpTableClass dwTableClass,
        int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    private enum TcpTableClass
    {
        TCP_TABLE_OWNER_PID_ALL = 5
    }

    private const int AF_INET  = 2;  // IPv4
    private const uint NO_ERROR = 0;

    // -------------------------------------------------------------------
    // Cache PID → nome processo con TTL di 10 secondi.
    // Process.GetProcessById apre un handle al kernel per ogni PID — su sistemi
    // con 200+ connessioni e scan ogni 2s il risparmio è enorme.
    // -------------------------------------------------------------------
    private static readonly Dictionary<int, (string Name, long ExpiresAt)> _nameCache = new();
    private static long _nextCacheClean;

    // -------------------------------------------------------------------
    // API pubblica
    // -------------------------------------------------------------------

    /// <summary>
    /// Restituisce l'elenco delle connessioni TCP attive (tutti gli stati).
    /// </summary>
    public static List<TcpConnection> GetConnections()
    {
        var result = new List<TcpConnection>();

        int buffSize = 0;
        // Prima chiamata: ottieni la dimensione necessaria del buffer
        GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true,
                            AF_INET, TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(buffSize);
        try
        {
            uint ret = GetExtendedTcpTable(buffer, ref buffSize, true,
                                           AF_INET, TcpTableClass.TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != NO_ERROR) return result;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr  = buffer + 4;
            int    rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr += rowSize;

                // Le porte in MIB_TCPROW sono in network byte order
                int localPort  = NetworkToHostPort(row.dwLocalPort);
                int remotePort = NetworkToHostPort(row.dwRemotePort);

                string procName = GetProcessName((int)row.dwOwningPid);

                result.Add(new TcpConnection
                {
                    Pid         = (int)row.dwOwningPid,
                    ProcessName = procName,
                    LocalIp     = new IPAddress(row.dwLocalAddr).ToString(),
                    LocalPort   = localPort,
                    RemoteIp    = new IPAddress(row.dwRemoteAddr).ToString(),
                    RemotePort  = remotePort,
                    State       = (TcpState)row.dwState,
                    RawState      = row.dwState,
                    RawLocalAddr  = row.dwLocalAddr,
                    RawLocalPort  = row.dwLocalPort,
                    RawRemoteAddr = row.dwRemoteAddr,
                    RawRemotePort = row.dwRemotePort
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    // -------------------------------------------------------------------
    // Helpers privati
    // -------------------------------------------------------------------

    private static int NetworkToHostPort(uint networkPort) =>
        (int)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));

    private static string GetProcessName(int pid)
    {
        long now = Environment.TickCount64;

        // Pulizia periodica: rimuovi entry scadute quando la cache supera 256 entry
        if (_nameCache.Count > 256 && now >= _nextCacheClean)
        {
            _nextCacheClean = now + 30_000;
            var expired = _nameCache.Where(kv => now >= kv.Value.ExpiresAt)
                                    .Select(kv => kv.Key)
                                    .ToList();
            foreach (var k in expired) _nameCache.Remove(k);
        }

        if (_nameCache.TryGetValue(pid, out var cached) && now < cached.ExpiresAt)
            return cached.Name;

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            string name = proc.ProcessName;
            _nameCache[pid] = (name, now + 10_000); // 10s TTL
            return name;
        }
        catch
        {
            string fallback = $"PID:{pid}";
            _nameCache[pid] = (fallback, now + 5_000); // 5s TTL on error
            return fallback;
        }
    }
}

// -------------------------------------------------------------------
// Tipi di supporto
// -------------------------------------------------------------------

/// <summary>Stato di una connessione TCP (MIB_TCP_STATE).</summary>
public enum TcpState
{
    Closed      = 1,
    Listen      = 2,
    SynSent     = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1    = 6,
    FinWait2    = 7,
    CloseWait   = 8,
    Closing     = 9,
    LastAck     = 10,
    TimeWait    = 11,
    DeleteTcb   = 12
}

/// <summary>Dati di una singola connessione TCP rilevata.</summary>
public class TcpConnection
{
    public int     Pid         { get; set; }
    public string  ProcessName { get; set; } = "";
    public string  LocalIp     { get; set; } = "";
    public int     LocalPort   { get; set; }
    public string  RemoteIp    { get; set; } = "";
    public int     RemotePort  { get; set; }
    public TcpState State      { get; set; }

    // Campi raw dal kernel (MIB_TCPROW_OWNER_PID) — usati da BandwidthTracker
    // per evitare conversioni IP/porta che potrebbero non combaciare esattamente.
    internal uint RawState      { get; set; }
    internal uint RawLocalAddr  { get; set; }
    internal uint RawLocalPort  { get; set; }
    internal uint RawRemoteAddr { get; set; }
    internal uint RawRemotePort { get; set; }
}
