namespace NetManCat.Core;

/// <summary>
/// Servizio centralizzato di scansione TCP.
///
/// Esegue UNA sola chiamata a <see cref="ProcessNetworkScanner.GetConnections"/>
/// per intervallo e distribuisce i risultati via <see cref="ScanCompleted"/>
/// a tutti i pannelli in ascolto — elimina scan duplicati e dimezza il carico
/// CPU quando Monitor e Analisi sono entrambi aperti.
///
/// Gira sul thread UI tramite System.Windows.Forms.Timer: i subscriber non
/// devono chiamare Invoke.
/// </summary>
public sealed class ScanService : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _disposed;
    private List<TcpConnection> _lastResult = new();

    /// <summary>Fired on the UI thread after each successful scan.</summary>
    public event EventHandler<List<TcpConnection>>? ScanCompleted;

    /// <summary>Ultimo scan disponibile (vuoto prima del primo tick).</summary>
    public IReadOnlyList<TcpConnection> LastResult => _lastResult;

    public ScanService(int intervalMs)
    {
        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(intervalMs, 500)
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    /// <summary>Aggiorna l'intervallo di scan (min 500 ms).</summary>
    public void SetInterval(int ms) => _timer.Interval = Math.Max(ms, 500);

    /// <summary>
    /// Forza un ciclo di scan immediato senza attendere il prossimo tick.
    /// Utile quando la watchlist cambia o l'utente vuole un refresh immediato.
    /// </summary>
    public void RequestRefresh()
    {
        if (_disposed) return;
        _timer.Stop();
        OnTick(null, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _timer.Stop();
        try
        {
            _lastResult = ProcessNetworkScanner.GetConnections();
            ScanCompleted?.Invoke(this, _lastResult);
        }
        catch { /* scan fallita: ignora, non bloccare il loop */ }
        finally
        {
            if (!_disposed) _timer.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
