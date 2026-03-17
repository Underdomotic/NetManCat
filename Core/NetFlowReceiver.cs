using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetManCat.Core;

/// <summary>
/// Riceve e decodifica query di flusso da:
///   - NetFlow v5   (porta 2055 di default)
///   - NetFlow v9   (porta 2055 di default)
///   - IPFIX / NFv10 (porta 4739 di default)
///   - sFlow v4/v5  (porta 6343 di default)
///
/// Non richiede agenti esterni: si mette in ascolto su UDP e
/// analizza ogni datagramma in arrivo.
/// </summary>
public sealed class NetFlowReceiver : IDisposable
{
    // ── Ports ──────────────────────────────────────────────────────────────
    public const int DefaultNetFlowPort = 2055;
    public const int DefaultIpfixPort   = 4739;
    public const int DefaultSFlowPort   = 6343;

    // ── stato pubblico ─────────────────────────────────────────────────────
    public bool NetFlowListening  { get; private set; }
    public bool IpfixListening    { get; private set; }
    public bool SFlowListening    { get; private set; }

    // ── flussi ricevuti (thread-safe) ──────────────────────────────────────
    private readonly ConcurrentQueue<FlowRecord> _queue = new();
    public IReadOnlyCollection<FlowRecord> Drain()
    {
        var list = new List<FlowRecord>();
        while (_queue.TryDequeue(out var r)) list.Add(r);
        return list;
    }

    // ── stats ──────────────────────────────────────────────────────────────
    private int _netflowPkts;
    private int _sflowPkts;
    private int _ipfixPkts;
    public  int NetFlowPackets => _netflowPkts;
    public  int SFlowPackets   => _sflowPkts;
    public  int IpfixPackets   => _ipfixPkts;

    /// <summary>Scattato ogni volta che arriva un nuovo flusso decodificato.</summary>
    public event EventHandler<FlowRecord>? FlowArrived;

    // ── UDP listeners ──────────────────────────────────────────────────────
    private UdpClient? _udpNetFlow;
    private UdpClient? _udpIpfix;
    private UdpClient? _udpSFlow;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    // ══════════════════════════════════════════════════════════════════════
    // API pubblica
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Avvia tutti e tre i listener (ignora porta 0 o già occupata).</summary>
    public void StartAll(int netflowPort = DefaultNetFlowPort,
                         int ipfixPort   = DefaultIpfixPort,
                         int sflowPort   = DefaultSFlowPort)
    {
        StartNetFlow(netflowPort);
        StartIpfix(ipfixPort);
        StartSFlow(sflowPort);
    }

    public void StartNetFlow(int port = DefaultNetFlowPort)
    {
        if (NetFlowListening || port <= 0) return;
        try
        {
            _udpNetFlow        = new UdpClient(port);
            NetFlowListening   = true;
            _ = ListenLoop(_udpNetFlow, ParseNetFlowOrIpfix, "NetFlow", _cts.Token);
        }
        catch { /* porta occupata o no admin */ }
    }

    public void StartIpfix(int port = DefaultIpfixPort)
    {
        if (IpfixListening || port <= 0) return;
        try
        {
            _udpIpfix       = new UdpClient(port);
            IpfixListening  = true;
            _ = ListenLoop(_udpIpfix, ParseNetFlowOrIpfix, "IPFIX", _cts.Token);
        }
        catch { }
    }

    public void StartSFlow(int port = DefaultSFlowPort)
    {
        if (SFlowListening || port <= 0) return;
        try
        {
            _udpSFlow      = new UdpClient(port);
            SFlowListening = true;
            _ = ListenLoop(_udpSFlow, ParseSFlow, "sFlow", _cts.Token);
        }
        catch { }
    }

    public void StopAll()
    {
        _cts.Cancel();
        _udpNetFlow?.Close(); _udpNetFlow  = null; NetFlowListening = false;
        _udpIpfix?.Close();   _udpIpfix    = null; IpfixListening   = false;
        _udpSFlow?.Close();   _udpSFlow    = null; SFlowListening   = false;
        _cts = new CancellationTokenSource();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Loop di ricezione
    // ══════════════════════════════════════════════════════════════════════

    private async Task ListenLoop(UdpClient udp, Func<byte[], IPAddress, List<FlowRecord>> parser,
                                   string proto, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                var records = parser(result.Buffer, result.RemoteEndPoint.Address);
                foreach (var r in records)
                {
                    r.Protocol = proto;
                    r.Exporter = result.RemoteEndPoint.Address.ToString();
                    _queue.Enqueue(r);
                    FlowArrived?.Invoke(this, r);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* datagram malformato / socket chiuso */ }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Parsing NetFlow v5 / v9 / IPFIX
    // ══════════════════════════════════════════════════════════════════════

    private List<FlowRecord> ParseNetFlowOrIpfix(byte[] data, IPAddress exporter)
    {
        var list = new List<FlowRecord>();
        if (data.Length < 4) return list;

        ushort version = (ushort)((data[0] << 8) | data[1]);

        if (version == 5)
        {
            Interlocked.Increment(ref _netflowPkts);
            ParseV5(data, list);
        }
        else if (version == 9)
        {
            Interlocked.Increment(ref _netflowPkts);
            // NetFlow v9 è template-based; estraiamo solo il count senza template cache
            ParseV9Header(data, list);
        }
        else if (version == 10)
        {
            Interlocked.Increment(ref _ipfixPkts);
            // IPFIX (NFv10) — struttura identica a NFv9 sulla fronte header
            ParseV9Header(data, list);
        }
        return list;
    }

    // ─── NetFlow v5 (record a dimensione fissa: 48 byte ognuno) ───────────
    private static void ParseV5(byte[] d, List<FlowRecord> list)
    {
        if (d.Length < 24) return;
        int count  = (d[2] << 8) | d[3];
        int offset = 24;   // header = 24 byte
        const int RecSize = 48;

        for (int i = 0; i < count && offset + RecSize <= d.Length; i++, offset += RecSize)
        {
            var r = new FlowRecord();
            r.SrcIp    = ReadIp(d, offset);
            r.DstIp    = ReadIp(d, offset + 4);
            r.SrcPort  = (ushort)((d[offset + 32] << 8) | d[offset + 33]);
            r.DstPort  = (ushort)((d[offset + 34] << 8) | d[offset + 35]);
            r.IpProto  = d[offset + 38];
            r.Packets  = ReadU32(d, offset + 16);
            r.Bytes    = ReadU32(d, offset + 20);
            r.Timestamp = DateTime.UtcNow;
            list.Add(r);
        }
    }

    // ─── NetFlow v9 / IPFIX: solo header-level parse (count flowsets) ────
    private static void ParseV9Header(byte[] d, List<FlowRecord> list)
    {
        if (d.Length < 20) return;
        // Creiamo un unico FlowRecord riassuntivo per segnalare il pacchetto ricevuto
        var r = new FlowRecord
        {
            SrcIp    = IPAddress.None,
            DstIp    = IPAddress.None,
            Bytes    = (uint)d.Length,
            Packets  = 1,
            Timestamp = DateTime.UtcNow
        };
        list.Add(r);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Parsing sFlow v4 / v5
    // ══════════════════════════════════════════════════════════════════════

    private List<FlowRecord> ParseSFlow(byte[] data, IPAddress exporter)
    {
        var list = new List<FlowRecord>();
        if (data.Length < 28) return list;

        Interlocked.Increment(ref _sflowPkts);

        int version = (int)ReadU32(data, 0);
        if (version != 5 && version != 4) return list;

        // sFlow v5: offset 28 = numero samples
        uint numSamples = ReadU32(data, 24);
        int  offset     = 28;

        for (uint s = 0; s < numSamples && offset + 8 <= data.Length; s++)
        {
            uint sampleType   = ReadU32(data, offset);
            uint sampleLength = ReadU32(data, offset + 4);
            int  sEnd         = offset + 8 + (int)sampleLength;
            offset += 8;

            // Tipo 1 = Flow sample
            if (sampleType == 1 && offset + 28 <= data.Length)
            {
                var r = new FlowRecord { Timestamp = DateTime.UtcNow };
                // Campioni raw Ethernet: estrai IP src/dst se presente
                // (l'header del campione raw è 20 byte prima dei dati)
                int dataOff = offset + 28; // dopo header campione
                if (dataOff + 20 <= data.Length)
                {
                    // Tentativo lettura IP da dataOff + 12 (typ. Ethernet frame)
                    if (dataOff + 34 <= data.Length)
                    {
                        byte ethertype1 = data[dataOff + 24];
                        byte ethertype2 = data[dataOff + 25];
                        if (ethertype1 == 0x08 && ethertype2 == 0x00 &&
                            dataOff + 46 <= data.Length)
                        {
                            r.SrcIp   = ReadIp(data, dataOff + 26);
                            r.DstIp   = ReadIp(data, dataOff + 30);
                            r.IpProto = data[dataOff + 23];
                        }
                    }
                }
                r.Bytes   = sampleLength;
                r.Packets = 1;
                list.Add(r);
            }
            offset = sEnd;
        }
        return list;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers binari
    // ══════════════════════════════════════════════════════════════════════

    private static IPAddress ReadIp(byte[] d, int offset) =>
        (offset + 4 <= d.Length)
        ? new IPAddress(new byte[] { d[offset], d[offset+1], d[offset+2], d[offset+3] })
        : IPAddress.None;

    private static uint ReadU32(byte[] d, int offset) =>
        (offset + 4 <= d.Length)
        ? (uint)((d[offset] << 24) | (d[offset+1] << 16) | (d[offset+2] << 8) | d[offset+3])
        : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }
}

// ──────────────────────────────────────────────────────────────────────────
// Modello dati flusso
// ──────────────────────────────────────────────────────────────────────────

public sealed class FlowRecord
{
    public string    Protocol  { get; set; } = "";
    public string    Exporter  { get; set; } = "";
    public IPAddress SrcIp     { get; set; } = IPAddress.None;
    public IPAddress DstIp     { get; set; } = IPAddress.None;
    public ushort    SrcPort   { get; set; }
    public ushort    DstPort   { get; set; }
    public byte      IpProto   { get; set; }
    public uint      Packets   { get; set; }
    public uint      Bytes     { get; set; }
    public DateTime  Timestamp { get; set; }

    public string SrcStr => SrcIp == IPAddress.None ? "—" : SrcIp.ToString();
    public string DstStr => DstIp == IPAddress.None ? "—" : DstIp.ToString();
    public string ProtoStr => IpProto switch
    {
        6  => "TCP",
        17 => "UDP",
        1  => "ICMP",
        _  => IpProto == 0 ? "—" : IpProto.ToString()
    };
}
