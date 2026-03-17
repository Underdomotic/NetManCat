using NetManCat.Core;

namespace NetManCat.Core;

/// <summary>
/// Gestisce la lista delle connessioni che l'utente vuole monitorare in dettaglio.
///
/// Chiave stabile: "ProcessName|RemoteIP|RemotePort"
/// Sopravvive alle riconnessioni (cambio porta locale) e ai riavvii del processo
/// purché nome + destinazione rimangano gli stessi.
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
        if (added) WatchlistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(TcpConnection c)
    {
        bool removed;
        lock (_watched) removed = _watched.Remove(MakeKey(c));
        if (removed) WatchlistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveKey(string key)
    {
        bool removed;
        lock (_watched) removed = _watched.Remove(key);
        if (removed) WatchlistChanged?.Invoke(this, EventArgs.Empty);
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

        foreach (var c in connections)
        {
            string key = MakeKey(c);
            if (_watched.Contains(key))
            {
                online.Add(c);
                onlineKeys.Add(key);
            }
        }

        var offline = new List<string>();
        lock (_watched)
            foreach (var k in _watched)
                if (!onlineKeys.Contains(k))
                    offline.Add(k);

        return (online, offline);
    }
}
