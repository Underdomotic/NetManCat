using System.Text.Json.Serialization;

namespace NetManCat.Models;

/// <summary>
/// Radice della configurazione. Corrisponde a netmancat.json.
/// </summary>
public class AppConfig
{
    /// <summary>Intervallo di aggiornamento UI in millisecondi.</summary>
    public int RefreshIntervalMs { get; set; } = 2000;

    public AlertThresholds AlertThresholds { get; set; } = new();
    public ServerModeConfig ServerMode     { get; set; } = new();
    public List<ServerEntry> Servers       { get; set; } = new();
    public LoggingConfig Logging           { get; set; } = new();

    /// <summary>Algoritmo di compressione TCP: "lz4" | "brotli".</summary>
    public string Compression { get; set; } = "lz4";
}

/// <summary>
/// Soglie oltre le quali scatta un alert visivo.
/// </summary>
public class AlertThresholds
{
    public double LatencyMs   { get; set; } = 100;
    public double LossPercent { get; set; } = 5;
    public double JitterMs    { get; set; } = 20;
}

/// <summary>
/// Configurazione della modalità server TCP.
/// </summary>
public class ServerModeConfig
{
    public bool Enabled { get; set; } = false;
    public int  Port    { get; set; } = 9100;
}

/// <summary>
/// Configurazione del sistema di logging.
/// </summary>
public class LoggingConfig
{
    /// <summary>"memory" = buffer circolare in-RAM, "sqlite" = persistenza su DB.</summary>
    public string Mode          { get; set; } = "memory";
    public string SqlitePath    { get; set; } = "netmancat.db";
    public int    RetentionDays { get; set; } = 30;
}
