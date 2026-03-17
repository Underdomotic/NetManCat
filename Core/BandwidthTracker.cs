using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NetManCat.Core;

/// <summary>
/// Traccia la banda utilizzata per singola connessione TCP tramite
/// GetPerTcpConnectionEStats (IP Helper API, Windows Vista+).
///
/// Richiede privilegi Administrator per abilitare la raccolta dati.
/// Se non disponibile (errore P/Invoke), restituisce silenziosamente 0.
/// </summary>
public sealed class BandwidthTracker : IDisposable
{
    // Stato per connessione
    private sealed class ConnStats
    {
        public ulong  PrevBytesOut;
        public ulong  PrevBytesIn;
        public long   PrevTickMs;
        public double UpKbps;
        public double DownKbps;
        public bool   Enabled;
    }

    private readonly ConcurrentDictionary<string, ConnStats> _states = new();
    private bool _disposed;

    // ── P/Invoke ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    // TCP_ESTATS_DATA_RW_v0: EnableCollection (BOOLEAN = 1 byte)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TCP_ESTATS_DATA_RW { public byte EnableCollection; }

    // TCP_ESTATS_DATA_ROD_v0 — layout esatto da tcpestats.h (Windows SDK).
    // Dimensione attesa: 6×8 + 5×4 + pad4 + 8 + 4 + pad4 + 8 = 96 byte.
    // ThruBytesAcked e ThruBytesReceived sono ULONG64, non ULONG.
    // Con LayoutKind.Sequential il runtime inserisce automaticamente il padding
    // di allineamento prima di ogni ulong, quindi il Marshal.SizeOf risultante
    // è 96 — il valore che GetPerTcpConnectionEStats si aspetta.
    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_ROD
    {
        public ulong DataBytesOut;
        public ulong DataSegsOut;
        public ulong DataBytesIn;
        public ulong DataSegsIn;
        public ulong SegsIn;
        public ulong SegsOut;
        public uint  SoftErrors;
        public uint  SoftErrorReason;
        public uint  SndUna;
        public uint  SndNxt;
        public uint  SndMax;
        // 4 byte di padding implicito (allineamento 8-byte per il campo seguente)
        public ulong ThruBytesAcked;      // era uint — SBAGLIATO: causa rodSz=80 → API fallisce
        public uint  RcvNxt;
        // 4 byte di padding implicito
        public ulong ThruBytesReceived;   // era uint — SBAGLIATO: stesso problema
    }

    private const int TcpConnectionEstatsData = 1;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int type,
        IntPtr rw,  uint rwVer,  int rwSz,
        IntPtr ros, uint rosVer, int rosSz,
        IntPtr rod, uint rodVer, int rodSz);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint SetPerTcpConnectionEStats(
        ref MIB_TCPROW row, int type,
        IntPtr rw, uint rwVer, int rwSz, uint offset);

    // ── API pubblica ──────────────────────────────────────────────────────

    /// <summary>Banda per la connessione (chiamare dopo Update).</summary>
    public (double UpKbps, double DownKbps) Get(TcpConnection conn)
    {
        var s = _states.GetValueOrDefault(Key(conn));
        return s is null ? (0, 0) : (s.UpKbps, s.DownKbps);
    }

    /// <summary>
    /// Aggiorna le statistiche per tutte le connessioni fornite.
    /// Da chiamare una volta per ciclo di scan (già sul thread UI).
    /// </summary>
    public void Update(IEnumerable<TcpConnection> connections)
    {
        if (_disposed) return;

        long nowMs  = Environment.TickCount64;
        var  active = new HashSet<string>();

        foreach (var conn in connections)
        {
            // Considera solo connessioni con IP remoto valido
            if (conn.RemoteIp is "0.0.0.0" or "::" or "") continue;

            string k = Key(conn);
            active.Add(k);

            var row = ToRow(conn);
            var st  = _states.GetOrAdd(k, _ => new ConnStats());

            // Abilita la raccolta dati: SetPerTcpConnectionEStats richiede admin.
            // Solo se la chiamata precedente ha avuto successo (ret==0) segniamo Enabled.
            if (!st.Enabled)
            {
                try
                {
                    uint sret = EnableCollection(ref row);
                    if (sret != 0) continue;  // fallito (non-admin, conn chiusa, ecc.) — riprova al prossimo ciclo
                    st.Enabled = true;
                }
                catch { continue; }
            }

            // Query ROD (read-only dynamic)
            int    rodSz  = Marshal.SizeOf<TCP_ESTATS_DATA_ROD>();
            IntPtr rodPtr = Marshal.AllocHGlobal(rodSz);
            try
            {
                // Azzera il buffer per evitare valori garbage
                for (int i = 0; i < rodSz; i++)
                    Marshal.WriteByte(rodPtr, i, 0);

                uint ret = GetPerTcpConnectionEStats(
                    ref row, TcpConnectionEstatsData,
                    IntPtr.Zero, 0, 0,
                    IntPtr.Zero, 0, 0,
                    rodPtr, 0, rodSz);

                if (ret != 0)
                {
                    // Connessione scomparsa o errore: resetta per riprovare al prossimo ciclo
                    st.Enabled = false;
                    continue;
                }

                var rod = Marshal.PtrToStructure<TCP_ESTATS_DATA_ROD>(rodPtr);

                if (st.PrevTickMs > 0)
                {
                    double elapsed = (nowMs - st.PrevTickMs) / 1000.0;
                    if (elapsed > 0.05)  // evita divisioni per intervalli microscopici
                    {
                        ulong dOut = rod.DataBytesOut >= st.PrevBytesOut
                                     ? rod.DataBytesOut - st.PrevBytesOut : 0;
                        ulong dIn  = rod.DataBytesIn  >= st.PrevBytesIn
                                     ? rod.DataBytesIn  - st.PrevBytesIn  : 0;
                        st.UpKbps   = dOut / 1024.0 / elapsed;
                        st.DownKbps = dIn  / 1024.0 / elapsed;
                    }
                }
                st.PrevBytesOut = rod.DataBytesOut;
                st.PrevBytesIn  = rod.DataBytesIn;
                st.PrevTickMs   = nowMs;
            }
            catch { /* ignora connessioni con problemi P/Invoke */ }
            finally { Marshal.FreeHGlobal(rodPtr); }
        }

        // Rimuovi connessioni non più attive
        foreach (var k in _states.Keys.ToArray())
            if (!active.Contains(k))
                _states.TryRemove(k, out _);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static uint EnableCollection(ref MIB_TCPROW row)
    {
        int    sz  = Marshal.SizeOf<TCP_ESTATS_DATA_RW>();
        IntPtr ptr = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(new TCP_ESTATS_DATA_RW { EnableCollection = 1 }, ptr, false);
            return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, ptr, 0, sz, 0);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>
    /// Costruisce il MIB_TCPROW direttamente dai valori raw del kernel
    /// (salvati in TcpConnection da ProcessNetworkScanner) per evitare
    /// qualsiasi conversione IP/porta che potrebbe non combaciare esattamente.
    /// </summary>
    private static MIB_TCPROW ToRow(TcpConnection c) => new MIB_TCPROW
    {
        dwState      = c.RawState,
        dwLocalAddr  = c.RawLocalAddr,
        dwLocalPort  = c.RawLocalPort,
        dwRemoteAddr = c.RawRemoteAddr,
        dwRemotePort = c.RawRemotePort
    };

    private static string Key(TcpConnection c) =>
        $"{c.LocalIp}:{c.LocalPort}|{c.RemoteIp}:{c.RemotePort}";

    public void Dispose() => _disposed = true;
}
