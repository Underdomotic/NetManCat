namespace NetManCat.Models;

/// <summary>
/// Singolo campionamento di metriche per una connessione TCP.
/// Usato sia per il buffer in-memory sia per la scrittura su SQLite.
/// </summary>
public class MetricRecord
{
    public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
    public int      Pid         { get; set; }
    public string   ProcessName { get; set; } = "";
    public string   RemoteIp    { get; set; } = "";
    public int      RemotePort  { get; set; }
    public double   LatencyMs   { get; set; }
    public double   UpKbps      { get; set; }
    public double   DownKbps    { get; set; }
    public double   LossPercent { get; set; }
}
