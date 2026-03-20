using NetManCat.Models;
using System.Diagnostics;

namespace NetManCat.Core;

/// <summary>
/// Motore di alerting: confronta le metriche live con le soglie configurate
/// e genera eventi <see cref="AlertFired"/> quando una soglia viene superata.
///
/// Viene abbonato a <see cref="ScanService.ScanCompleted"/> e riceve
/// metriche di latenza/loss/jitter dalla <see cref="LatencyProbe"/> e
/// dalla <see cref="BandwidthTracker"/> tramite callback delegate.
///
/// NON esegue sonde di rete: delega a LatencyProbe già avviata in MainForm.
/// </summary>
public sealed class AlertEngine
{
    private readonly ConfigManager    _config;
    private readonly LatencyProbe     _latency;
    private readonly WatchlistManager _watchlist;

    /// <summary>
    /// Limite minimo di ms tra due alert identici (stessa chiave) per evitare
    /// lo spam di notifiche — default 60 secondi.
    /// </summary>
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(60);

    /// <summary>Ultima volta che un determinato alert è scattato (chiave → timestamp).</summary>
    private readonly Dictionary<string, DateTime> _lastFired = new();

    /// <summary>
    /// Scattato sul thread UI quando una metrica supera la soglia configurata.
    /// </summary>
    public event EventHandler<NetworkAlert>? AlertFired;

    public AlertEngine(ConfigManager config, LatencyProbe latency, WatchlistManager watchlist)
    {
        _config    = config;
        _latency   = latency;
        _watchlist = watchlist;
    }

    // -----------------------------------------------------------------------
    // Abbonamento al ScanService
    // -----------------------------------------------------------------------

    /// <summary>
    /// Collega AlertEngine a ScanService. Chiamato da MainForm in WireEvents().
    /// </summary>
    public void AttachToScanService(ScanService scan)
    {
        scan.ScanCompleted += OnScanCompleted;
    }

    // -----------------------------------------------------------------------
    // Handler principale
    // -----------------------------------------------------------------------

    private void OnScanCompleted(object? sender, List<TcpConnection> connections)
    {
        var thresholds = _config.Config.AlertThresholds;

        // Controlla solo le connessioni in watchlist
        var keys = _watchlist.Keys;
        foreach (var key in keys)
        {
            var parts = key.Split('|');
            if (parts.Length < 3) continue;
            string proc   = parts[0];
            string remIp  = parts[1];

            // Recupera metriche dalla LatencyProbe (in-memory, non fa nuove sonde)
            double latMs  = _latency.GetCachedLatencyMs(remIp);
            double lossP  = _latency.GetCachedLossPercent(remIp);
            double jitMs  = _latency.GetCachedJitterMs(remIp);

            // Latenza
            if (latMs > 0 && latMs > thresholds.LatencyMs)
                TryFire(key, AlertMetric.Latenza, latMs, thresholds.LatencyMs, proc, remIp);

            // Packet loss
            if (lossP > 0 && lossP > thresholds.LossPercent)
                TryFire(key + "|loss", AlertMetric.Perdita, lossP, thresholds.LossPercent, proc, remIp);

            // Jitter
            if (jitMs > 0 && jitMs > thresholds.JitterMs)
                TryFire(key + "|jitter", AlertMetric.Jitter, jitMs, thresholds.JitterMs, proc, remIp);
        }
    }

    // -----------------------------------------------------------------------
    // Logica di firing con cooldown
    // -----------------------------------------------------------------------

    private void TryFire(string cooldownKey, AlertMetric metric, double value,
                          double threshold, string process, string remoteIp)
    {
        var now = DateTime.Now;
        if (_lastFired.TryGetValue(cooldownKey, out DateTime last))
            if (now - last < _cooldown) return;

        _lastFired[cooldownKey] = now;

        var alert = new NetworkAlert
        {
            Timestamp  = now,
            Process    = process,
            RemoteIp   = remoteIp,
            Metric     = metric,
            Value      = value,
            Threshold  = threshold
        };

        try { AlertFired?.Invoke(this, alert); }
        catch (Exception ex) { Debug.WriteLine($"[AlertEngine] errore nel fire: {ex.Message}"); }
    }

    /// <summary>Cancella la cronologia cooldown (utile al cambio soglie).</summary>
    public void ResetCooldowns() => _lastFired.Clear();
}

// -----------------------------------------------------------------------
// Modelli dati per l'alert
// -----------------------------------------------------------------------

/// <summary>Tipo di metrica che ha scatenato l'alert.</summary>
public enum AlertMetric
{
    Latenza,
    Perdita,
    Jitter
}

/// <summary>
/// Record di un singolo alert generato da AlertEngine.
/// Usato per la notifica tray e per il log visuale in AlertLogPanel.
/// </summary>
public sealed class NetworkAlert
{
    public DateTime    Timestamp { get; init; }
    public string      Process   { get; init; } = "";
    public string      RemoteIp  { get; init; } = "";
    public AlertMetric Metric    { get; init; }
    public double      Value     { get; init; }
    public double      Threshold { get; init; }

    /// <summary>Descrizione leggibile della metrica superata.</summary>
    public string MetricLabel => Metric switch
    {
        AlertMetric.Latenza => "Latenza",
        AlertMetric.Perdita => "Packet Loss",
        AlertMetric.Jitter  => "Jitter",
        _                   => Metric.ToString()
    };

    /// <summary>Unità di misura per la metrica.</summary>
    public string Unit => Metric switch
    {
        AlertMetric.Perdita => "%",
        _                   => "ms"
    };

    /// <summary>Testo breve per il balloon tray.</summary>
    public string BalloonTitle => $"⚠ {MetricLabel} elevata — {Process}";

    /// <summary>Corpo del balloon tray.</summary>
    public string BalloonBody  =>
        $"{RemoteIp}  {Value:F1}{Unit}  (soglia: {Threshold:F0}{Unit})";
}
