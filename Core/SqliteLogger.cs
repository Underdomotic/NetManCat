using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Logger asincrono su SQLite — attivabile e disattivabile a caldo.
/// Usa un Channel boundato per evitare pressione sulla memoria.
/// </summary>
public sealed class SqliteLogger : IDisposable
{
    private readonly Channel<MetricRecord> _channel =
        Channel.CreateBounded<MetricRecord>(new BoundedChannelOptions(5_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private string _dbPath = "netmancat.db";
    private bool _disposed;

    /// <summary>True se il worker di scrittura è attivo.</summary>
    public bool IsActive => _workerTask is { IsCompleted: false };

    /// <summary>
    /// Avvia il logger SQLite sul percorso indicato.
    /// Crea le tabelle se non esistono.
    /// </summary>
    public void Start(string dbPath)
    {
        if (IsActive) return;
        _dbPath = dbPath;
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    /// <summary>
    /// Ferma il logger. Attende fino a 3 secondi per il flush finale.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _workerTask?.Wait(TimeSpan.FromSeconds(3));
        _workerTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Accoda un record per la scrittura asincrona.
    /// Se il logger non è attivo, il record viene ignorato silenziosamente.
    /// </summary>
    public void Log(MetricRecord record)
    {
        if (IsActive)
            _channel.Writer.TryWrite(record);
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        InitDb();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                MetricRecord record = await _channel.Reader.ReadAsync(ct);
                WriteRecord(record);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Errore silenzioso per non bloccare il loop di scrittura
            }
        }
    }

    private void InitDb()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS metrics (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ts        TEXT    NOT NULL,
                pid       INTEGER NOT NULL,
                process   TEXT    NOT NULL,
                remote_ip TEXT    NOT NULL,
                port      INTEGER NOT NULL,
                latency   REAL    NOT NULL,
                up_kbps   REAL    NOT NULL,
                down_kbps REAL    NOT NULL,
                loss_pct  REAL    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_metrics_ts ON metrics(ts);";
        cmd.ExecuteNonQuery();
    }

    private void WriteRecord(MetricRecord r)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO metrics (ts, pid, process, remote_ip, port, latency, up_kbps, down_kbps, loss_pct)
            VALUES ($ts, $pid, $proc, $ip, $port, $lat, $up, $down, $loss)";
        cmd.Parameters.AddWithValue("$ts",   r.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$pid",  r.Pid);
        cmd.Parameters.AddWithValue("$proc", r.ProcessName);
        cmd.Parameters.AddWithValue("$ip",   r.RemoteIp);
        cmd.Parameters.AddWithValue("$port", r.RemotePort);
        cmd.Parameters.AddWithValue("$lat",  r.LatencyMs);
        cmd.Parameters.AddWithValue("$up",   r.UpKbps);
        cmd.Parameters.AddWithValue("$down", r.DownKbps);
        cmd.Parameters.AddWithValue("$loss", r.LossPercent);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
