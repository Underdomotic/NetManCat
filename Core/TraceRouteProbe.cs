using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetManCat.Core;

/// <summary>
/// Risultato di un singolo hop nel percorso traceroute.
/// </summary>
public sealed class HopResult
{
    public int    HopNumber { get; init; }
    public string IpAddress { get; set; } = "*";
    public string Hostname  { get; set; } = "*";
    public double Rtt1      { get; set; } = -1;
    public double Rtt2      { get; set; } = -1;
    public double Rtt3      { get; set; } = -1;

    public double AvgRtt =>
        new[] { Rtt1, Rtt2, Rtt3 }.Where(r => r >= 0).DefaultIfEmpty(-1).Average();

    public int Loss =>
        new[] { Rtt1, Rtt2, Rtt3 }.Count(r => r < 0) * 100 / 3;  // 0, 33, 66, 100

    public bool IsTimeout => Rtt1 < 0 && Rtt2 < 0 && Rtt3 < 0;
}

/// <summary>
/// Esegue un traceroute ICMP asincrono verso una destinazione.
///
/// Algoritmo:
///   Per ogni TTL da 1 a MaxHops invia 3 ping ICMP con TTL fisso.
///   - TtlExpired → il router intermedio ha risposto: registra IP e RTT.
///   - Success    → abbiamo raggiunto la destinazione: fine del trace.
///   - Timeout    → hop silenzioso (* * *).
///   Si risolve il reverse-DNS di ogni hop in background (non bloccante).
///
/// Gli eventi sono lanciati sul thread pool; chi li gestisce deve fare
/// InvokeRequired / BeginInvoke se accede a controlli UI.
/// </summary>
public sealed class TraceRouteProbe : IDisposable
{
    public const int MaxHops      = 30;
    public const int TimeoutMs    = 1_200;
    public const int ProbesPerHop = 3;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    // -----------------------------------------------------------------------
    // Eventi pubblici
    // -----------------------------------------------------------------------

    /// <summary>Lanciato a ogni hop scoperto (anche parziale, con solo Rtt1).</summary>
    public event EventHandler<HopResult>? HopDiscovered;

    /// <summary>Lanciato quando il traceroute è completato o annullato.</summary>
    public event EventHandler<List<HopResult>>? TraceCompleted;

    /// <summary>Lanciato in caso di errore pre-trace (es. DNS fallito).</summary>
    public event EventHandler<string>? TraceFailed;

    // -----------------------------------------------------------------------
    // API pubblica
    // -----------------------------------------------------------------------

    /// <summary>
    /// Avvia il traceroute verso <paramref name="target"/> (IP o hostname).
    /// Un eventuale traceroute precedente viene annullato.
    /// </summary>
    public void Start(string target)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Task.Run(() => RunTraceAsync(target, _cts.Token));
    }

    /// <summary>Annulla il traceroute in corso.</summary>
    public void Stop() => _cts?.Cancel();

    // -----------------------------------------------------------------------
    // Logica interna
    // -----------------------------------------------------------------------

    private async Task RunTraceAsync(string target, CancellationToken ct)
    {
        var hops = new List<HopResult>();
        try
        {
            // --- Risoluzione indirizzo ---
            IPAddress? destAddr = null;
            if (!IPAddress.TryParse(target, out destAddr))
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(target).WaitAsync(ct);
                    destAddr  = entry.AddressList
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (OperationCanceledException) { return; }
                catch { }

                if (destAddr == null)
                {
                    TraceFailed?.Invoke(this, $"Impossibile risolvere '{target}'");
                    return;
                }
            }

            string destStr = destAddr.ToString();

            // --- Loop TTL ---
            for (int ttl = 1; ttl <= MaxHops && !ct.IsCancellationRequested; ttl++)
            {
                var hop        = new HopResult { HopNumber = ttl };
                bool reachedDest = false;

                for (int probe = 0; probe < ProbesPerHop && !ct.IsCancellationRequested; probe++)
                {
                    using var ping    = new Ping();
                    var       options = new PingOptions(ttl, dontFragment: true);
                    try
                    {
                        var reply = await ping
                            .SendPingAsync(destAddr, TimeoutMs, new byte[32], options)
                            .WaitAsync(ct);

                        bool gotReply = reply.Status == IPStatus.Success ||
                                        reply.Status == IPStatus.TtlExpired;

                        double rtt = gotReply ? (double)reply.RoundtripTime : -1;

                        if (probe == 0)      hop.Rtt1 = rtt;
                        else if (probe == 1) hop.Rtt2 = rtt;
                        else                 hop.Rtt3 = rtt;

                        // Cattura IP del nodo per il primo probe che risponde
                        if (gotReply && reply.Address != null && hop.IpAddress == "*")
                        {
                            hop.IpAddress = reply.Address.ToString();
                            // Reverse DNS in background — non blocca il loop
                            _ = ResolveAsync(hop, reply.Address, ct);
                        }

                        if (reply.Status == IPStatus.Success)
                            reachedDest = true;
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* timeout / ICMP non disponibile: RTT rimane -1 */ }
                }

                hops.Add(hop);
                HopDiscovered?.Invoke(this, hop);

                if (reachedDest) break;
            }

            TraceCompleted?.Invoke(this, hops);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TraceFailed?.Invoke(this, ex.Message); }
    }

    private static async Task ResolveAsync(HopResult hop, IPAddress addr, CancellationToken ct)
    {
        try
        {
            var entry    = await Dns.GetHostEntryAsync(addr.ToString())
                               .WaitAsync(TimeSpan.FromSeconds(3), ct);
            hop.Hostname = entry.HostName;
        }
        catch
        {
            hop.Hostname = addr.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
