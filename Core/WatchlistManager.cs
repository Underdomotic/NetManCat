using System.Diagnostics;
using System.Text.Json;

namespace NetManCat.Core;

/// <summary>
/// Gestisce la lista delle connessioni che l'utente vuole monitorare in dettaglio.
///
/// Chiave stabile: "ProcessName|RemoteIP|RemotePort"
/// Sopravvive alle riconnessioni (cambio porta locale) e ai riavvii del processo
/// purché nome + destinazione rimangano gli stessi.
///
/// v1.2: la watchlist viene salvata su file JSON (netmancat_watchlist.json)
/// e ricaricata all'avvio — sopravvive al riavvio dell'applicazione.
/// </summary>
public sealed class WatchlistManager
{
    private readonly HashSet<string> _watched =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scatenato ogni volta che un item viene aggiunto o rimosso.</summary>
    public event EventHandler? WatchlistChanged;

    /// <summary>Numero di connessioni attualmente sotto osservazione.</summary>
    public int Count => _watched.Count;

    // -----------------------------------------------------------------------
    // Costruisce la chiave stabile per una connessione
    // -----------------------------------------------------------------------
    public static string MakeKey(TcpConnection c) =>
        $"{c.ProcessName}|{c.RemoteIp}|{c.RemotePort}";

    // -----------------------------------------------------------------------
    // Query
    // -----------------------------------------------------------------------
    public bool IsWatched(TcpConnection c) =>
        _watched.Contains(MakeKey(c));

    public bool IsWatchedKey(string key) =>
        _watched.Contains(key);

    /// <summary>Snapshot immutabile delle chiavi attualmente monitorate.</summary>
    public IReadOnlyCollection<string> Keys
    {
        get { lock (_watched) return _watched.ToArray(); }
    }

    // -----------------------------------------------------------------------
    // Mutatori
    // -----------------------------------------------------------------------
    public void Add(TcpConnection c)
    {
        bool added;
        lock (_watched) added = _watched.Add(MakeKey(c));
        if (added) { WatchlistChanged?.Invoke(this, EventArgs.Empty); SaveIfPathSet(); }
    }

    public void Remove(TcpConnection c)
    {
        bool removed;
        lock (_watched) removed = _watched.Remove(MakeKey(c));
        if (removed) { WatchlistChanged?.Invoke(this, EventArgs.Empty); SaveIfPathSet(); }
    }

    public void RemoveKey(string key)
    {
        bool removed;
        lock (_watched) removed = _watched.Remove(key);
        if (removed) { WatchlistChanged?.Invoke(this, EventArgs.Empty); SaveIfPathSet(); }
    }

    // -----------------------------------------------------------------------
    // Persistenza JSON (v1.2)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Salva la watchlist corrente su file JSON.
    /// Chiamato automaticamente ad ogni modifica tramite <see cref="SaveIfPathSet"/>.
    /// </summary>
    public void Save(string filePath)
    {
        try
        {
            string[] snapshot;
            lock (_watched) snapshot = _watched.ToArray();
            var json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WatchlistManager] errore salvataggio: {ex.Message}");
        }
    }

    /// <summary>
    /// Carica la watchlist da file JSON all'avvio.
    /// Ignora silenziosamente il file se non esiste o è corrotto.
    /// </summary>
    public void Load(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json  = File.ReadAllText(filePath);
            var keys  = JsonSerializer.Deserialize<string[]>(json);
            if (keys == null) return;
            bool changed = false;
            lock (_watched)
                foreach (var k in keys)
                    if (!string.IsNullOrWhiteSpace(k))
                        changed |= _watched.Add(k);
            if (changed) WatchlistChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WatchlistManager] errore caricamento: {ex.Message}");
        }
    }

    /// <summary>
    /// Percorso del file JSON su cui salvare automaticamente.
    /// Impostato da MainForm dopo l'inizializzazione.
    /// </summary>
    public string? AutoSavePath { get; set; }

    /// <summary>Salva su <see cref="AutoSavePath"/> se impostato.</summary>
    private void SaveIfPathSet()
    {
        if (!string.IsNullOrWhiteSpace(AutoSavePath))
            Save(AutoSavePath!);
    }

    /// <summary>
    /// Dato un elenco di connessioni attive, restituisce quelle nella watchlist
    /// e quelle nella watchlist ma attualmente offline.
    /// </summary>
    public (List<TcpConnection> Online, List<string> OfflineKeys)
        Partition(List<TcpConnection> connections)
    {
        var onlineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var online     = new List<TcpConnection>();

        // Scatta un snapshot thread-safe della watchlist corrente
        string[] watchedSnapshot;
        lock (_watched) watchedSnapshot = _watched.ToArray();
        var watchedSet = new HashSet<string>(watchedSnapshot, StringComparer.OrdinalIgnoreCase);

        foreach (var c in connections)
        {
            string key = MakeKey(c);
            if (watchedSet.Contains(key))
            {
                online.Add(c);
                onlineKeys.Add(key);
            }
        }

        var offline = new List<string>();
        foreach (var k in watchedSnapshot)
            if (!onlineKeys.Contains(k))
                offline.Add(k);

        return (online, offline);
    }
}
