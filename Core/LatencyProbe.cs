using System.Collections.Concurrent;
using System.Net.NetworkInformation;

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
    private readonly ConcurrentDictionary<string, double> _cache = new();
    // Finestra mobile: true = successo, false = timeout/errore
    private readonly ConcurrentDictionary<string, Queue<bool>> _lossWindow = new();
    private readonly object _lock = new();
    private HashSet<string> _targets = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;

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
                // Tutte le ping in parallelo per non bloccare il ciclo
                var tasks = snapshot.Select(ip => PingOneAsync(ip, ct)).ToArray();
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
