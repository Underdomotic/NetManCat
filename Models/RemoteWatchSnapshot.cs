namespace NetManCat.Models;

/// <summary>
/// Pacchetto inviato dal server ai client contenente le connessioni
/// attualmente sotto osservazione (watchlist filtrata).
///
/// Trasporto: PacketCodec (JSON + LZ4), prefissato con 4 byte di lunghezza.
/// </summary>
public class RemoteWatchSnapshot
{
    /// <summary>
    /// Etichetta del nodo sorgente (Environment.MachineName del server).
    /// Il client sovrascrive questo campo con il nome configurato localmente.
    /// </summary>
    public string   SourceLabel { get; set; } = "";
    public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
    public List<RemoteConnectionEntry> Connections { get; set; } = new();
}

/// <summary>Singola connessione TCP inclusa nello snapshot, con metriche real-time.</summary>
public class RemoteConnectionEntry
{
    public string ProcessName { get; set; } = "";
    public string LocalIp     { get; set; } = "";
    public int    LocalPort   { get; set; }
    public string RemoteIp    { get; set; } = "";
    public int    RemotePort  { get; set; }
    public string State       { get; set; } = "";
    // Metriche campionate sul nodo server
    public double LatencyMs   { get; set; } = -1;
    public double UpKbps      { get; set; }
    public double DownKbps    { get; set; }
    public double LossPercent { get; set; } = -1;
}
