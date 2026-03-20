using Microsoft.Data.Sqlite;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Gestisce le serie del Grafico:
///   - Persistenza delle sottoscrizioni (quali connessioni/processi monitorare) su SQLite.
///   - Scrittura dei campioni nel tempo nella tabella chart_points.
///   - Lettura dei punti per il rendering del grafico.
///   - Attiva automaticamente SqliteLogger quando la prima serie viene aggiunta.
///
/// Chiave serie (SeriesKey):
///   connessione locale : "conn|ProcessName|RemoteIp|RemotePort"
///   processo locale    : "proc|ProcessName"
///   connessione remota : "remote|ServerLabel|ProcessName|RemoteIp|RemotePort"
///   processo remoto    : "remoteproc|ServerLabel|ProcessName"
/// </summary>
public sealed class GraphLogger : IDisposable
{
    private readonly SqliteLogger _sqliteLogger;
    private readonly ConfigManager _config;
    // Il grafico usa un file DB separato per non interferire con SqliteLogger
    // (che scrive su netmancat.db da un task in background).
    private string _dbPath = "netmancat_graph.db";
    private bool _disposed;

    // Connessione persistente al file grafico (WAL mode, evita apertura/chiusura per ogni scrittura)
    private SqliteConnection? _conn;

    // Insieme delle serie attive (in memoria, persistite anche su DB)
    private readonly HashSet<string> _activeSeries = new(StringComparer.OrdinalIgnoreCase);
    // Cache in memoria dei label: elimina accessi SQLite nel paint loop.
    private readonly Dictionary<string, string> _labelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seriesLock = new();

    /// <summary>Scatenato quando la lista delle serie cambia (aggiunta/rimozione).</summary>
    public event EventHandler? SeriesChanged;

    public GraphLogger(SqliteLogger sqliteLogger, ConfigManager config)
    {
        _sqliteLogger = sqliteLogger;
        _config       = config;
    }

    // -----------------------------------------------------------------------
    // Inizializzazione DB e caricamento serie
    // -----------------------------------------------------------------------

    /// <summary>
    /// Inizializza le tabelle e carica le serie persistite.
    /// Deve essere chiamato dopo aver impostato il percorso DB.
    /// </summary>
    /// <summary>
    /// Inizializza il DB grafico. Deriva il percorso aggiungendo il suffisso "_graph" al
    /// nome del DB principale, così SqliteLogger e GraphLogger non condividono mai lo stesso file.
    /// </summary>
    public void Init(string mainDbPath)
    {
        string dir  = System.IO.Path.GetDirectoryName(
                          System.IO.Path.GetFullPath(mainDbPath)) ?? ".";
        string name = System.IO.Path.GetFileNameWithoutExtension(mainDbPath);
        _dbPath = System.IO.Path.Combine(dir, name + "_graph.db");
        EnsureTables();
        LoadSeries();
    }

    private void EnsureTables()
    {
        var conn = GetConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS chart_subscriptions (
                series_key TEXT PRIMARY KEY NOT NULL,
                label      TEXT NOT NULL,
                added_at   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chart_points (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                series_key TEXT    NOT NULL,
                ts         TEXT    NOT NULL,
                latency    REAL    NOT NULL DEFAULT -1,
                up_kbps    REAL    NOT NULL DEFAULT 0,
                down_kbps  REAL    NOT NULL DEFAULT 0,
                loss_pct   REAL    NOT NULL DEFAULT -1
            );
            CREATE INDEX IF NOT EXISTS idx_chart_points_key_ts
                ON chart_points(series_key, ts);";
        cmd.ExecuteNonQuery();
    }

    private void LoadSeries()
    {
        var conn = GetConnection();
        using var cmd  = conn.CreateCommand();
        // Carica sia la chiave sia il label per popolare la cache in memoria
        cmd.CommandText = "SELECT series_key, label FROM chart_subscriptions";
        using var rdr = cmd.ExecuteReader();
        lock (_seriesLock)
        {
            _activeSeries.Clear();
            _labelCache.Clear();
            while (rdr.Read())
            {
                string key   = rdr.GetString(0);
                string label = rdr.GetString(1);
                _activeSeries.Add(key);
                _labelCache[key] = label;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Gestione serie
    // -----------------------------------------------------------------------

    public bool IsActive(string seriesKey)
    {
        lock (_seriesLock) return _activeSeries.Contains(seriesKey);
    }

    public IReadOnlyList<string> GetActiveSeries()
    {
        lock (_seriesLock) return _activeSeries.ToArray();
    }

    /// <summary>
    /// Aggiunge una serie al grafico; attiva SqliteLogger se è la prima.
    /// </summary>
    public void AddSeries(string seriesKey, string label)
    {
        bool added;
        lock (_seriesLock)
        {
            added = _activeSeries.Add(seriesKey);
            if (added) _labelCache[seriesKey] = label;  // aggiorna cache prima del DB
        }
        if (!added) return;

        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO chart_subscriptions (series_key, label, added_at)
                VALUES ($key, $label, $ts)";
            cmd.Parameters.AddWithValue("$key",   seriesKey);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$ts",    DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphLogger] AddSeries error: {ex.Message}");
        }

        EnsureSqliteLoggerActive();
        SeriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSeries(string seriesKey)
    {
        bool removed;
        lock (_seriesLock)
        {
            removed = _activeSeries.Remove(seriesKey);
            if (removed) _labelCache.Remove(seriesKey);
        }
        if (!removed) return;

        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM chart_subscriptions WHERE series_key = $key";
            cmd.Parameters.AddWithValue("$key", seriesKey);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphLogger] RemoveSeries error: {ex.Message}");
        }

        SeriesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rimuove tutte le serie e cancella tutti i punti storici.
    /// </summary>
    public void ClearAll()
    {
        lock (_seriesLock) { _activeSeries.Clear(); _labelCache.Clear(); }

        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM chart_subscriptions; DELETE FROM chart_points;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphLogger] ClearAll error: {ex.Message}");
        }

        SeriesChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // Scrittura punti
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registra un campione per la serie indicata se essa è attiva.
    /// </summary>
    public void Record(string seriesKey, double latencyMs, double upKbps, double downKbps, double lossPercent)
    {
        if (!IsActive(seriesKey)) return;

        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO chart_points (series_key, ts, latency, up_kbps, down_kbps, loss_pct)
                VALUES ($key, $ts, $lat, $up, $dn, $loss)";
            cmd.Parameters.AddWithValue("$key",  seriesKey);
            cmd.Parameters.AddWithValue("$ts",   DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$lat",  latencyMs);
            cmd.Parameters.AddWithValue("$up",   upKbps);
            cmd.Parameters.AddWithValue("$dn",   downKbps);
            cmd.Parameters.AddWithValue("$loss", lossPercent);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Punto perso ma l'UI non viene bloccata (es. lock file, disco pieno)
            System.Diagnostics.Debug.WriteLine($"[GraphLogger] Record error: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Lettura punti
    // -----------------------------------------------------------------------

    public record ChartPoint(DateTime Ts, double Latency, double UpKbps, double DownKbps, double Loss);

    /// <summary>
    /// Legge gli ultimi <paramref name="maxPoints"/> campioni per la serie indicata.
    /// </summary>
    public List<ChartPoint> ReadPoints(string seriesKey, int maxPoints = 500)
    {
        var result = new List<ChartPoint>();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ts, latency, up_kbps, down_kbps, loss_pct
                FROM chart_points
                WHERE series_key = $key
                ORDER BY id DESC
                LIMIT $max";
            cmd.Parameters.AddWithValue("$key", seriesKey);
            cmd.Parameters.AddWithValue("$max", maxPoints);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new ChartPoint(
                    DateTime.Parse(rdr.GetString(0)).ToLocalTime(),
                    rdr.GetDouble(1),
                    rdr.GetDouble(2),
                    rdr.GetDouble(3),
                    rdr.GetDouble(4)));
            }
            result.Reverse(); // ordine cronologico crescente
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphLogger] ReadPoints error: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Restituisce il label dalla cache in memoria — nessun accesso SQLite nel paint loop.
    /// </summary>
    public string GetLabel(string seriesKey)
    {
        lock (_seriesLock)
            if (_labelCache.TryGetValue(seriesKey, out string? lbl)) return lbl;
        return seriesKey;
    }

    // -----------------------------------------------------------------------
    // Helper privati
    // -----------------------------------------------------------------------

    private void EnsureSqliteLoggerActive()
    {
        if (_sqliteLogger.IsActive) return;
        string path = _config.Config.Logging.SqlitePath;
        if (string.IsNullOrWhiteSpace(path)) path = "netmancat.db";
        // NON sovrascrivere _dbPath: il grafico usa il proprio file "_graph.db"
        _sqliteLogger.Start(path);
        _config.Config.Logging.Mode = "sqlite";
        _config.NotifyChanged();
    }

    /// <summary>
    /// Restituisce la connessione persistente al file grafico, creandola (o ricreandola)
    /// se non ancora aperta. Imposta WAL mode e busy_timeout una sola volta.
    /// </summary>
    private SqliteConnection GetConnection()
    {
        if (_conn == null || _conn.State != System.Data.ConnectionState.Open)
        {
            _conn?.Dispose();
            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();
            // WAL: consente letture concorrenti mentre si scrive.
            // busy_timeout: ritenta per 2 secondi prima di sollevare SQLITE_BUSY.
            using var p = _conn.CreateCommand();
            p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000;";
            p.ExecuteNonQuery();
        }
        return _conn;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn?.Close();
        _conn?.Dispose();
        _conn = null;
    }
}
