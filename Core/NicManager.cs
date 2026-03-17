using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetManCat.Core;

/// <summary>
/// Informazioni su una scheda di rete attiva.
/// </summary>
/// <param name="Id">GUID identificativo dell'interfaccia.</param>
/// <param name="DisplayName">Nome leggibile con tipo e indirizzi, es. "[ETH] Ethernet  [192.168.1.100]".</param>
/// <param name="Addresses">Tutti gli indirizzi IPv4 assegnati a questa scheda.</param>
public sealed record NicInfo(string Id, string DisplayName, string[] Addresses);

/// <summary>
/// Enumera le schede di rete attive con i relativi indirizzi IPv4.
/// Supporta Ethernet, Gigabit, WiFi, Fibra/SFP, PPP, VPN tunnel, ecc.
/// </summary>
public static class NicManager
{
    /// <summary>
    /// Restituisce tutte le interfacce di rete attive che hanno
    /// almeno un indirizzo IPv4 unicast assegnato.
    /// </summary>
    public static List<NicInfo> GetActiveNics()
    {
        var list = new List<NicInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)   continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var addrs = nic.GetIPProperties()
                    .UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToArray();

                if (addrs.Length == 0) continue;

                string tag     = GetTypeTag(nic.NetworkInterfaceType);
                string addrStr = string.Join(", ", addrs);
                string display = $"{tag} {nic.Name}  [{addrStr}]";

                list.Add(new NicInfo(nic.Id, display, addrs));
            }
        }
        catch { /* privilegi insufficienti o WMI non disponibile */ }

        return list;
    }

    private static string GetTypeTag(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet        => "[ETH]",
        NetworkInterfaceType.GigabitEthernet => "[GIG]",
        NetworkInterfaceType.FastEthernetT   => "[ETH]",
        NetworkInterfaceType.FastEthernetFx  => "[FX]",
        NetworkInterfaceType.Wireless80211   => "[WiFi]",
        NetworkInterfaceType.Ppp             => "[PPP]",
        NetworkInterfaceType.Tunnel          => "[VPN]",
        NetworkInterfaceType.Fddi            => "[FDDI]",
        NetworkInterfaceType.Slip            => "[SFP]",
        _                                    => "[NET]"
    };
}
