using System.Text.Json;
using System.Text.Json.Serialization;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Gestisce la lettura e scrittura di netmancat.json.
/// Sincronizzazione bidirezionale tra file JSON e configurazione in memoria.
/// </summary>
public class ConfigManager
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "netmancat.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Configurazione corrente in memoria.</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>Scatenato dopo ogni chiamata a <see cref="Apply"/>.</summary>
    public event EventHandler? ConfigChanged;

    /// <summary>
    /// Carica la configurazione da disco.
    /// Se il file non esiste, lo crea con i valori di default.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            Save(); // crea il file con i valori di default
            return;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            // File corrotto: ripristina i default e sovrascrive
            Config = new AppConfig();
            Save();
        }
    }

    /// <summary>
    /// Scrive la configurazione corrente su disco senza notifica.
    /// </summary>
    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Scrittura fallita (es. permessi): non blocca l'app
        }
    }

    /// <summary>
    /// Applica una nuova configurazione, la salva su disco e
    /// notifica tutti i listener tramite <see cref="ConfigChanged"/>.
    /// </summary>
    public void Apply(AppConfig updated)
    {
        Config = updated;
        Save();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}
