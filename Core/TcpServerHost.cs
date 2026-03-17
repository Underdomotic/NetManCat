using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Server TCP che accetta connessioni dai client NetManCat.
/// Riceve metriche compresse con <see cref="PacketCodec"/> e le aggrega.
/// Attivabile/disattivabile a caldo senza riavvio dell'applicazione.
/// </summary>
public sealed class TcpServerHost : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private bool _disposed;

    /// <summary>True se il server è in ascolto.</summary>
    public bool IsRunning => _acceptTask is { IsCompleted: false };

    /// <summary>Numero di client attualmente connessi.</summary>
    public int ConnectedClients => _clients.Count;

    /// <summary>Scatenato quando un client si connette (argomento: endpoint remoto).</summary>
    public event EventHandler<string>? ClientConnected;

    /// <summary>Scatenato quando un client si disconnette.</summary>
    public event EventHandler<string>? ClientDisconnected;

    /// <summary>Scatenato in caso di errore non fatale.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Avvia il server TCP sulla porta specificata.
    /// </summary>
    public void Start(int port)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
    }

    /// <summary>
    /// Ferma il server e disconnette tutti i client.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _acceptTask?.Wait(TimeSpan.FromSeconds(3));
        _acceptTask = null;
        _listener   = null;
        _cts?.Dispose();
        _cts = null;

        foreach (var kv in _clients)
            kv.Value.Dispose();
        _clients.Clear();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(ct);
                string key = client.Client.RemoteEndPoint?.ToString()
                             ?? Guid.NewGuid().ToString();
                _clients[key] = client;
                ClientConnected?.Invoke(this, key);

                // Ogni client viene gestito in un task separato
                _ = Task.Run(() => HandleClient(client, key, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { ErrorOccurred?.Invoke(this, ex.Message); }
        }
    }

    private async Task HandleClient(TcpClient client, string key, CancellationToken ct)
    {
        // Il server invia dati ai client tramite SendToAllAsync; i client non
        // trasmettono nulla. Dreniamo in silenzio qualsiasi byte in arrivo
        // (es. keep-alive TCP) tenendo la connessione aperta fino alla
        // cancellazione o alla chiusura del client.
        try
        {
            var stream = client.GetStream();
            var drain  = new byte[256];
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(drain, ct);
                if (n == 0) break;  // client disconnesso
            }
        }
        catch (OperationCanceledException) { }
        catch { /* socket closed */ }
        finally
        {
            _clients.TryRemove(key, out _);
            client.Dispose();
            ClientDisconnected?.Invoke(this, key);
        }
    }

    /// <summary>Legge esattamente <paramref name="count"/> byte dallo stream.</summary>
    private static async Task<int> ReadExact(
        NetworkStream stream, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, count - total), ct);
            if (n == 0) break; // connessione chiusa
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Invia <paramref name="packet"/> a tutti i client connessi.
    /// I client che falliscono vengono rimossi.
    /// </summary>
    public async Task SendToAllAsync(byte[] packet)
    {
        if (_clients.IsEmpty) return;
        var lenBuf  = BitConverter.GetBytes(packet.Length);
        var failed  = new List<string>();

        foreach (var kv in _clients)
        {
            try
            {
                var stream = kv.Value.GetStream();
                await stream.WriteAsync(lenBuf);
                await stream.WriteAsync(packet);
                await stream.FlushAsync();
            }
            catch { failed.Add(kv.Key); }
        }
        foreach (var key in failed)
        {
            _clients.TryRemove(key, out var dead);
            dead?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
