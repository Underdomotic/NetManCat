using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Sonda ICMP asincrona per misurare la latenza RTT (ms) e la percentuale di
/// perdita pacchetti verso IP remoti.
///
/// Intervallo di probe: 2 secondi (indipendente dal refresh UI).
/// Timeout per singolo ping: 1500 ms.
/// Finestra mobile per il calcolo della loss: 20 campioni.
/// </summary>
public sealed class LatencyProbe : IDisposable
{
    private readonly ConcurrentDictionary<string, double> _cache    = new();
    private readonly ConcurrentDictionary<string, double> _cacheTcp = new(); // v1.1
    // Finestra mobile: true = successo, false = timeout/errore
    private readonly ConcurrentDictionary<string, Queue<bool>> _lossWindow = new();
    // Finestra mobile RTT per calcolo jitter (deviazione media assoluta)
    private readonly ConcurrentDictionary<string, Queue<double>> _rttWindow = new();
    private readonly object _lock = new();
    private HashSet<string> _targets = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;

    // Configurazione TCP probe (v1.1) — aggiornabile a caldo
    private TcpProbeConfig _tcpProbeCfg = new();

    private const int ProbeIntervalMs = 2_000;
    private const int PingTimeoutMs   = 1_500;
    private const int LossWindowSize  = 20;

    // -----------------------------------------------------------------------
    // API pubblica
    // -----------------------------------------------------------------------

    /// <summary>
    /// Latenza RTT in ms dell'ultima sonda verso <paramref name="ip"/>.
    /// Restituisce -1 se non ancora misurata o se la destinazione è irraggiungibile.
    /// </summary>
    public double GetLatency(string ip) =>
        _cache.TryGetValue(ip, out var v) ? v : -1;

    /// <summary>
    /// Percentuale di perdita pacchetti verso <paramref name="ip"/> sulla finestra
    /// mobile degli ultimi 20 ping. Restituisce -1 se non ci sono ancora campioni.
    /// </summary>
    public double GetLoss(string ip)
    {
        if (!_lossWindow.TryGetValue(ip, out var q)) return -1;
        lock (q)
        {
            if (q.Count == 0) return -1;
            int failures = q.Count(x => !x);
            return failures * 100.0 / q.Count;
        }
    }

    /// <summary>
    /// RTT TCP in ms dell'ultima sonda verso <paramref name="ip"/> (v1.1).
    /// Restituisce -1 se non abilitato o non ancora misurato.
    /// </summary>
    public double GetTcpLatency(string ip) =>
        _cacheTcp.TryGetValue(ip, out var v) ? v : -1;

    // -----------------------------------------------------------------------
    // Metodi cached per AlertEngine (non aprono nuove sonde)
    // -----------------------------------------------------------------------

    /// <summary>Latenza ICMP cached in ms. Restituisce -1 se non disponibile.</summary>
    public double GetCachedLatencyMs(string ip) => GetLatency(ip);

    /// <summary>Packet loss % cached. Restituisce -1 se non disponibile.</summary>
    public double GetCachedLossPercent(string ip) => GetLoss(ip);

    /// <summary>
    /// Jitter in ms calcolato come deviazione media assoluta degli ultimi RTT.
    /// Restituisce -1 se la finestra ha meno di 2 campioni.
    /// </summary>
    public double GetCachedJitterMs(string ip)
    {
        if (!_rttWindow.TryGetValue(ip, out var q)) return -1;
        lock (q)
        {
            if (q.Count < 2) return -1;
            double mean = q.Average();
            return q.Average(v => Math.Abs(v - mean));
        }
    }

    /// <summary>
    /// Aggiorna la configurazione del probe TCP a caldo (v1.1).
    /// </summary>
    public void SetTcpProbeConfig(TcpProbeConfig cfg) => _tcpProbeCfg = cfg;

    /// <summary>
    /// Aggiorna l'elenco degli IP da sondare.
    /// Chiamato ad ogni refresh dalla AnalysisPanel.
    /// </summary>
    public void UpdateTargets(IEnumerable<string> ips)
    {
        lock (_lock)
            _targets = new HashSet<string>(
                ips.Where(ip => ip is not ("0.0.0.0" or "::" or "0:0:0:0:0:0:0:0")),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Avvia il loop di probe in background.</summary>
    public void Start()
    {
        if (_task is { IsCompleted: false }) return;
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => ProbeLoop(_cts.Token));
    }

    /// <summary>Ferma il loop di probe.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _task?.Wait(3_000);
        _task = null;
        _cts?.Dispose();
        _cts  = null;
    }

    // -----------------------------------------------------------------------
    // Loop interno
    // -----------------------------------------------------------------------
    private async Task ProbeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HashSet<string> snapshot;
            lock (_lock) snapshot = new HashSet<string>(_targets);

            if (snapshot.Count > 0)
            {
                // ICMP e TCP probe in parallelo (TCP solo se abilitato)
                var cfg = _tcpProbeCfg;
                var tasks = snapshot
                    .SelectMany(ip =>
                    {
                        var list = new List<Task> { PingOneAsync(ip, ct) };
                        if (cfg.Enabled)
                            list.Add(TcpProbeOneAsync(ip, cfg.Port, cfg.TimeoutMs, ct));
                        return list;
                    })
                    .ToArray();
                await Task.WhenAll(tasks);
            }

            try { await Task.Delay(ProbeIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PingOneAsync(string ip, CancellationToken ct)
    {
        bool success = false;
        try
        {
            using var ping  = new Ping();
            var reply = await ping.SendPingAsync(ip, PingTimeoutMs);
            success   = reply.Status == IPStatus.Success;
            _cache[ip] = success ? reply.RoundtripTime : -1;
        }
        catch
        {
            _cache[ip] = -1;
        }
        // Aggiorna finestra mobile loss
        var q = _lossWindow.GetOrAdd(ip, _ => new Queue<bool>());
        lock (q)
        {
            q.Enqueue(success);
            if (q.Count > LossWindowSize)
                q.Dequeue();
        }
        // Aggiorna finestra mobile RTT per jitter (solo campioni validi > 0)
        if (success && _cache.TryGetValue(ip, out double rtt) && rtt > 0)
        {
            var rttQ = _rttWindow.GetOrAdd(ip, _ => new Queue<double>());
            lock (rttQ)
            {
                rttQ.Enqueue(rtt);
                if (rttQ.Count > LossWindowSize)
                    rttQ.Dequeue();
            }
        }
    }

    // -----------------------------------------------------------------------
    // -----------------------------------------------------------------------
    // Probe TCP (v1.1)
    // -----------------------------------------------------------------------
    private async Task TcpProbeOneAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await socket.ConnectAsync(ip, port, cts.Token);
            sw.Stop();
            _cacheTcp[ip] = sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            // Timeout o cancellazione: non aggiorniamo la cache (mantiene l'ultimo valore)
        }
        catch
        {
            _cacheTcp[ip] = -1; // porta chiusa / host irraggiungibile
        }
    }

    // -----------------------------------------------------------------------
    // Dispose
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
