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

    // ── v1.1 ────────────────────────────────────────────────────────────

    /// <summary>Sonda TCP per misurare RTT reale anche dove ICMP è bloccato.</summary>
    public TcpProbeConfig TcpProbe { get; set; } = new();

    /// <summary>Cifratura TLS per la comunicazione server/client.</summary>
    public TlsConfig Tls { get; set; } = new();
}

/// <summary>
/// Sonda TCP per la misurazione della latenza RTT (v1.1).
/// Fallback automatico a ICMP se il probe TCP non riesce.
/// </summary>
public class TcpProbeConfig
{
    /// <summary>Abilita il probe TCP in parallelo all'ICMP.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Porta su cui connettersi per misurare il RTT (default 443).</summary>
    public int Port { get; set; } = 443;

    /// <summary>Timeout in ms per il connect() di prova (default 2000).</summary>
    public int TimeoutMs { get; set; } = 2_000;
}

/// <summary>
/// Configurazione TLS per la comunicazione server/client (v1.1).
/// </summary>
public class TlsConfig
{
    /// <summary>Abilita TLS sulla connessione server/client.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Percorso del file .pfx con il certificato server.
    /// Lasciare vuoto per usare il certificato generato da create-cert.ps1.
    /// </summary>
    public string PfxPath { get; set; } = "netmancat-sign.pfx";

    /// <summary>Password del file PFX.</summary>
    public string PfxPassword { get; set; } = "NetManCat2026!";
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
