using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace NetManCat.Core;

/// <summary>
/// Scopre switch e router nel sistema con diversi metodi:
///  1. ARP table locale (GetIpNetTable2) → gateway + vicini diretti
///  2. Default gateway via routing table (GetIpForwardTable2)
///  3. SNMP comunità "public" su porta 161 → sysDescr (OID 1.3.6.1.2.1.1.1.0)
///     e sysName (OID 1.3.6.1.2.1.1.5.0) — dati pubblici standard
///  4. Fingerprint per tipo: Cisco / HP/Aruba / Juniper / MikroTik / generico
/// </summary>
public sealed class NetworkDeviceScanner : IDisposable
{
    private readonly ConcurrentDictionary<string, NetworkDevice> _devices = new();
    private bool _scanning;

    public IReadOnlyCollection<NetworkDevice> Devices => _devices.Values.ToArray();

    /// <summary>Scattato ogni volta che un dispositivo viene aggiunto o aggiornato.</summary>
    public event EventHandler<NetworkDevice>? DeviceUpdated;

    // ══════════════════════════════════════════════════════════════════════
    // API pubblica
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Avvia una scansione completa in background.
    /// <para><paramref name="withSnmp"/>=true interroga anche la porta 161 (SNMP community 'public')
    /// per ottenere sysDescr/sysName/uptime. Richiede qualche secondo in più.</para>
    /// </summary>
    public void ScanAsync(bool withSnmp = false)
    {
        if (_scanning) return;
        _scanning = true;
        Task.Run(async () =>
        {
            try   { await DoScanAsync(withSnmp); }
            finally { _scanning = false; }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Scansione
    // ══════════════════════════════════════════════════════════════════════

    private async Task DoScanAsync(bool withSnmp)
    {
        // 1. Default gateways
        var gateways = GetDefaultGateways();

        // 2. ARP table
        var arpEntries = GetArpEntries();

        // Unisci e de-duplica per IP
        var candidates = new Dictionary<string, NetworkDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var gw in gateways)
        {
            var d = new NetworkDevice
            {
                IpAddress  = gw,
                DeviceType = DeviceType.Router,
                Source     = "Gateway"
            };
            candidates[gw] = d;
        }

        foreach (var (ip, mac) in arpEntries)
        {
            if (!candidates.TryGetValue(ip, out var d))
            {
                d = new NetworkDevice { IpAddress = ip, Source = "ARP" };
                candidates[ip] = d;
            }
            d.MacAddress = mac;
            if (d.DeviceType == DeviceType.Unknown)
                d.DeviceType = GuessByMac(mac);
        }

        // 3. PTR DNS (veloce, non sospetto) — SNMP solo se esplicitamente richiesto
        var enrichTasks = candidates.Values
            .Select(dev => Task.Run(() => EnrichDevice(dev, withSnmp)))
            .ToArray();

        await Task.WhenAll(enrichTasks);

        // Pubblica i risultati
        foreach (var dev in candidates.Values)
        {
            _devices[dev.IpAddress] = dev;
            DeviceUpdated?.Invoke(this, dev);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Default gateways
    // ══════════════════════════════════════════════════════════════════════

    private static List<string> GetDefaultGateways()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(gw.Address.ToString());
                }
            }
        }
        catch { }
        return result.ToList();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ARP table (GetIpNetTable2)
    // ══════════════════════════════════════════════════════════════════════

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    // Struttura semplificata MIB_IPNET_ROW2 per AF_INET
    // Layout: indirizzo (28 byte per sockaddr_storage) + InterfaceIndex(4) + InterfaceLuid(8)
    //         + PhysicalAddress(32) + PhysicalAddressLength(4) + State(4) + ...
    // Usiamo solo la parte che ci serve con offset fissi sicuri
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNET_TABLE2_HEADER
    {
        public uint NumEntries;
    }

    private static List<(string Ip, string Mac)> GetArpEntries()
    {
        var result = new List<(string, string)>();
        try
        {
            uint ret = GetIpNetTable2(2 /* AF_INET */, out IntPtr tablePtr);
            if (ret != 0 || tablePtr == IntPtr.Zero) return result;
            try
            {
                uint count = (uint)Marshal.ReadInt32(tablePtr);
                // Ogni riga MIB_IPNET_ROW2 = 88 byte su x64 Windows
                const int RowSize = 88;
                IntPtr rowPtr = tablePtr + 4; // dopo NumEntries

                for (uint i = 0; i < count; i++, rowPtr += RowSize)
                {
                    // Address.sin_addr è a offset 4 dall'inizio del sockaddr_in dentro sockaddr_storage
                    // sockaddr_storage inizia a offset 0; sa_family (2 byte) + port (2 byte) + addr (4 byte)
                    byte af1 = Marshal.ReadByte(rowPtr, 0);
                    byte af2 = Marshal.ReadByte(rowPtr, 1);
                    if (af1 != 2 || af2 != 0) continue; // AF_INET = 2

                    byte b1 = Marshal.ReadByte(rowPtr, 4);
                    byte b2 = Marshal.ReadByte(rowPtr, 5);
                    byte b3 = Marshal.ReadByte(rowPtr, 6);
                    byte b4 = Marshal.ReadByte(rowPtr, 7);
                    string ip = $"{b1}.{b2}.{b3}.{b4}";
                    if (ip == "0.0.0.0") continue;

                    // PhysicalAddressLength a offset 44 (dopo sockaddr_storage[28] + idx[4] + luid[8] + phys[32]? — no)
                    // Layout reale: [0] sockaddr_storage(28) + [28] IfIndex(4) + [32] Luid(8) + [40] PhysAddr(32) + [72] PhysAddrLen(4) + [76] State(4) + [80] Flags(4) + [84] ReachabilityTime(4)
                    int physLen = Marshal.ReadInt32(rowPtr, 72);
                    if (physLen < 6) { result.Add((ip, "")); continue; }

                    var mac = new StringBuilder();
                    for (int b = 0; b < 6; b++)
                    {
                        if (b > 0) mac.Append(':');
                        mac.Append(Marshal.ReadByte(rowPtr, 40 + b).ToString("X2"));
                    }
                    result.Add((ip, mac.ToString()));
                }
            }
            finally { FreeMibTable(tablePtr); }
        }
        catch { }
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Arricchimento: PTR DNS + SNMP sysDescr
    // ══════════════════════════════════════════════════════════════════════

    private static async Task EnrichDevice(NetworkDevice dev, bool withSnmp)
    {
        // PTR DNS (sempre)
        try
        {
            var entry = await Dns.GetHostEntryAsync(dev.IpAddress);
            dev.Hostname = entry.HostName;
        }
        catch { dev.Hostname = "—"; }

        if (!withSnmp) return;  // SNMP interroga porta 161 — solo su richiesta esplicita

        // SNMP GET sysDescr (OID 1.3.6.1.2.1.1.1.0) e sysName (OID 1.3.6.1.2.1.1.5.0)
        dev.SysDescr = await SnmpGet(dev.IpAddress, "1.3.6.1.2.1.1.1.0", timeoutMs: 1500);
        dev.SysName  = await SnmpGet(dev.IpAddress, "1.3.6.1.2.1.1.5.0", timeoutMs: 1500);

        // Fingerprint tipo dispositivo da sysDescr
        if (!string.IsNullOrEmpty(dev.SysDescr))
            dev.DeviceType = FingerprintType(dev.SysDescr, dev.DeviceType);

        // Uptime (OID 1.3.6.1.2.1.1.3.0)
        string uptimeRaw = await SnmpGet(dev.IpAddress, "1.3.6.1.2.1.1.3.0", timeoutMs: 1000);
        dev.UptimeRaw = uptimeRaw;

        // ifNumber — quante interfacce (aggiunge insight su switch)
        string ifCount = await SnmpGet(dev.IpAddress, "1.3.6.1.2.1.2.1.0", timeoutMs: 1000);
        if (int.TryParse(ifCount, out int ifs) && ifs > 0)
            dev.InterfaceCount = ifs;
    }

    /// <summary>
    /// SNMP v1 GET minimale (community "public").
    /// Costruisce il pacchetto BER/ASN.1 manualmente senza dipendenze aggiuntive.
    /// </summary>
    private static async Task<string> SnmpGet(string ip, string oid, int timeoutMs = 1500)
    {
        try
        {
            byte[] packet = BuildSnmpGetPacket(oid);
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            var ep = new IPEndPoint(IPAddress.Parse(ip), 161);
            await udp.SendAsync(packet, packet.Length, ep);

            using var cts = new CancellationTokenSource(timeoutMs);
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(cts.Token); }
            catch { return ""; }

            return ParseSnmpResponse(res.Buffer);
        }
        catch { return ""; }
    }

    // ── BER/ASN.1 builder per SNMP v1 GET ────────────────────────────────

    private static byte[] BuildSnmpGetPacket(string oid)
    {
        // Community = "public"
        byte[] community = Encoding.ASCII.GetBytes("public");
        byte[] oidBytes  = EncodeOid(oid);

        // VarBind: SEQUENCE { OID, NULL }
        byte[] varBind = Tlv(0x30, Concat(Tlv(0x06, oidBytes), new byte[] { 0x05, 0x00 }));
        // VarBindList: SEQUENCE { varBind }
        byte[] varBindList = Tlv(0x30, varBind);
        // GetRequest PDU: [0] { requestId, error, errorIndex, varBindList }
        byte[] pdu = Tlv(0xA0, Concat(
            new byte[] { 0x02, 0x01, 0x01 },  // requestId = 1
            new byte[] { 0x02, 0x01, 0x00 },  // error = 0
            new byte[] { 0x02, 0x01, 0x00 },  // errorIndex = 0
            varBindList));
        // Message: SEQUENCE { version(0), community, pdu }
        byte[] msg = Tlv(0x30, Concat(
            new byte[] { 0x02, 0x01, 0x00 },  // version = 0 (v1)
            Tlv(0x04, community),
            pdu));
        return msg;
    }

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var len = EncodeLength(value.Length);
        var result = new byte[1 + len.Length + value.Length];
        result[0] = tag;
        len.CopyTo(result, 1);
        value.CopyTo(result, 1 + len.Length);
        return result;
    }

    private static byte[] EncodeLength(int len)
    {
        if (len < 128) return new byte[] { (byte)len };
        if (len < 256) return new byte[] { 0x81, (byte)len };
        return new byte[] { 0x82, (byte)(len >> 8), (byte)(len & 0xFF) };
    }

    private static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(int.Parse).ToArray();
        if (parts.Length < 2) return Array.Empty<byte>();
        var bytes = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        for (int i = 2; i < parts.Length; i++)
        {
            int val = parts[i];
            if (val == 0) { bytes.Add(0); continue; }
            var subid = new Stack<byte>();
            while (val > 0)
            {
                subid.Push((byte)((val & 0x7F) | (subid.Count > 0 ? 0x80 : 0)));
                val >>= 7;
            }
            bytes.AddRange(subid);
        }
        return bytes.ToArray();
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var a in arrays) { a.CopyTo(result, offset); offset += a.Length; }
        return result;
    }

    // ── Parsing risposta SNMP ────────────────────────────────────────────

    private static string ParseSnmpResponse(byte[] data)
    {
        try
        {
            // Naviga: SEQUENCE → skip community/version → GetResponse PDU [0xA2]
            // → skip requestId/error/errorIndex → VarBindList → VarBind → valore
            int i = 0;
            if (!SkipTlvHeader(data, ref i, 0x30)) return ""; // msg
            if (!SkipTlvValue(data, ref i, 0x02))  return ""; // version
            if (!SkipTlvValue(data, ref i, 0x04))  return ""; // community
            if (!SkipTlvHeader(data, ref i, 0xA2)) return ""; // GetResponse PDU
            if (!SkipTlvValue(data, ref i, 0x02))  return ""; // requestId
            if (!SkipTlvValue(data, ref i, 0x02))  return ""; // error
            if (!SkipTlvValue(data, ref i, 0x02))  return ""; // errorIndex
            if (!SkipTlvHeader(data, ref i, 0x30)) return ""; // VarBindList
            if (!SkipTlvHeader(data, ref i, 0x30)) return ""; // VarBind
            if (!SkipTlvValue(data, ref i, 0x06))  return ""; // OID

            if (i >= data.Length) return "";
            byte tag = data[i++];
            int  len = ReadLength(data, ref i);
            if (i + len > data.Length) return "";

            // OctetString (0x04), Integer (0x02), TimeTicks (0x43)
            if (tag == 0x04) return Encoding.UTF8.GetString(data, i, len).Trim('\0', '\r', '\n');
            if (tag == 0x02 || tag == 0x43)
            {
                long v = 0;
                for (int b = 0; b < len; b++) v = (v << 8) | data[i + b];
                return tag == 0x43 ? FormatTimeticks(v) : v.ToString();
            }
        }
        catch { }
        return "";
    }

    private static bool SkipTlvHeader(byte[] d, ref int i, byte expectedTag)
    {
        if (i >= d.Length || d[i] != expectedTag) return false;
        i++;
        ReadLength(d, ref i);
        return true;
    }

    private static bool SkipTlvValue(byte[] d, ref int i, byte expectedTag)
    {
        if (i >= d.Length || d[i] != expectedTag) return false;
        i++;
        int len = ReadLength(d, ref i);
        i += len;
        return true;
    }

    private static int ReadLength(byte[] d, ref int i)
    {
        if (i >= d.Length) return 0;
        int b = d[i++];
        if (b < 128) return b;
        int extra = b & 0x7F;
        int len = 0;
        for (int j = 0; j < extra && i < d.Length; j++, i++)
            len = (len << 8) | d[i];
        return len;
    }

    private static string FormatTimeticks(long ticks)
    {
        // TimeTicks = centesimi di secondo dall'avvio
        long secs = ticks / 100;
        return $"{secs / 86400}d {(secs % 86400) / 3600:00}h {(secs % 3600) / 60:00}m";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Fingerprint
    // ══════════════════════════════════════════════════════════════════════

    private static DeviceType FingerprintType(string descr, DeviceType current)
    {
        string d = descr.ToLowerInvariant();
        if (d.Contains("cisco"))                              return DeviceType.Router;
        if (d.Contains("catalyst") || d.Contains("nexus"))   return DeviceType.Switch;
        if (d.Contains("juniper") || d.Contains("junos"))    return DeviceType.Router;
        if (d.Contains("mikrotik") || d.Contains("routeros"))return DeviceType.Router;
        if (d.Contains("aruba") || d.Contains("hp procurve"))return DeviceType.Switch;
        if (d.Contains("netgear") || d.Contains("d-link"))   return DeviceType.Switch;
        if (d.Contains("ubiquiti") || d.Contains("edgeos"))  return DeviceType.Router;
        if (d.Contains("switch"))                             return DeviceType.Switch;
        if (d.Contains("router"))                             return DeviceType.Router;
        return current;
    }

    private static DeviceType GuessByMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return DeviceType.Unknown;
        string oui = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (oui.Length < 6) return DeviceType.Unknown;
        string prefix = oui[..6];
        // OUI noti per apparati di rete (lista parziale dei più comuni)
        return prefix switch
        {
            "001A2B" or "000BBE" or "0021A0" => DeviceType.Router, // Cisco
            "3C5AB4" or "5C5015" or "F07D68" => DeviceType.Switch, // HP/Aruba
            "00163E" or "0017F2"              => DeviceType.Router, // Juniper
            "4C5E0C" or "DC2C6E"              => DeviceType.Router, // MikroTik
            _                                 => DeviceType.Unknown
        };
    }

    public void Dispose() { /* nulla da liberare – nessun socket o handle aperto */ }
}

// ──────────────────────────────────────────────────────────────────────────
// Modello dati dispositivo
// ──────────────────────────────────────────────────────────────────────────

public enum DeviceType { Unknown, Router, Switch }

public sealed class NetworkDevice
{
    public string     IpAddress        { get; set; } = "";
    public string     MacAddress       { get; set; } = "";
    public string     Hostname         { get; set; } = "—";
    public string     SysName          { get; set; } = "";
    public string     SysDescr         { get; set; } = "";
    public string     UptimeRaw        { get; set; } = "";
    public int        InterfaceCount   { get; set; }
    public DeviceType DeviceType       { get; set; } = DeviceType.Unknown;
    public string     Source           { get; set; } = "";

    public string TypeIcon => DeviceType switch
    {
        DeviceType.Router => "🌐",
        DeviceType.Switch => "🔀",
        _                 => "❓"
    };

    public string DisplayName => !string.IsNullOrEmpty(SysName) && SysName != "—"
                                 ? SysName
                                 : (!string.IsNullOrEmpty(Hostname) && Hostname != "—"
                                    ? Hostname
                                    : IpAddress);
}
