using System.Text.Json.Serialization;

namespace NetManCat.Models;

/// <summary>
/// Rappresenta un server NetManCat remoto a cui connettersi.
/// </summary>
public class ServerEntry
{
    public string Label { get; set; } = "";
    public string Ip    { get; set; } = "";
    public int    Port  { get; set; } = 9100;

    // ── v1.1 ────────────────────────────────────────────────────────────

    /// <summary>
    /// Thumbprint SHA-1 del certificato TLS atteso dal server.
    /// Se vuoto e TLS è abilitato, viene accettato qualsiasi certificato self-signed
    /// proveniente dallo stesso IP (trust-on-first-use).
    /// </summary>
    public string TlsThumbprint { get; set; } = "";

    /// <summary>
    /// Non serializzato su JSON — stato run-time della connessione.
    /// </summary>
    [JsonIgnore]
    public bool Connected { get; set; } = false;

    /// <summary>
    /// Non serializzato su JSON — thumbprint ricevuto all'ultima connessione TLS (TOFU).
    /// </summary>
    [JsonIgnore]
    public string LastSeenThumbprint { get; set; } = "";
}
