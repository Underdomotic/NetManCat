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

    /// <summary>
    /// Non serializzato su JSON — stato run-time della connessione.
    /// </summary>
    [JsonIgnore]
    public bool Connected { get; set; } = false;
}
