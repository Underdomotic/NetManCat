using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
    // v1.1: dizionario indicizzato per chiave → (TcpClient, Stream)
    // lo stream può essere NetworkStream (plain) o SslStream (TLS)
    private readonly ConcurrentDictionary<string, (TcpClient client, Stream stream)> _clients = new();
    private bool _disposed;
    private X509Certificate2? _tlsCert; // certificato TLS caricato all'avvio (v1.1)

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
    /// Se <paramref name="tlsCfg"/> è abilitato, carica il certificato PFX
    /// e negozia TLS con ogni client (v1.1).
    /// </summary>
    public void Start(int port, TlsConfig? tlsCfg = null)
    {
        if (IsRunning) return;

        // Carica il certificato TLS se abilitato (v1.1)
        if (tlsCfg?.Enabled == true && !string.IsNullOrEmpty(tlsCfg.PfxPath))
        {
            try
            {
                _tlsCert = new X509Certificate2(tlsCfg.PfxPath, tlsCfg.PfxPassword,
                    X509KeyStorageFlags.PersistKeySet);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"TLS: certificato non caricato — {ex.Message}. Avvio in chiaro.");
                _tlsCert = null;
            }
        }
        else
        {
            _tlsCert = null;
        }

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
        {
            kv.Value.stream.Dispose();
            kv.Value.client.Dispose();
        }
        _clients.Clear();
        _tlsCert?.Dispose();
        _tlsCert = null;
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

                // v1.1: negozia TLS se il certificato è stato caricato
                Stream stream = client.GetStream();
                if (_tlsCert != null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate          = _tlsCert,
                                ClientCertificateRequired  = false,
                                EnabledSslProtocols        = System.Security.Authentication.SslProtocols.Tls13
                                                           | System.Security.Authentication.SslProtocols.Tls12
                            }, ct);
                        stream = ssl;
                    }
                    catch (Exception ex)
                    {
                        ErrorOccurred?.Invoke(this, $"TLS handshake fallito con {key}: {ex.Message}");
                        ssl.Dispose();
                        client.Dispose();
                        continue;
                    }
                }

                _clients[key] = (client, stream);
                ClientConnected?.Invoke(this, key);

                // Ogni client viene gestito in un task separato
                _ = Task.Run(() => HandleClient(client, stream, key, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { ErrorOccurred?.Invoke(this, ex.Message); }
        }
    }

    private async Task HandleClient(TcpClient client, Stream stream, string key, CancellationToken ct)
    {
        // Il server invia dati ai client tramite SendToAllAsync; i client non
        // trasmettono nulla. Dreniamo in silenzio qualsiasi byte in arrivo
        // (es. keep-alive TCP) tenendo la connessione aperta fino alla
        // cancellazione o alla chiusura del client.
        try
        {
            var drain = new byte[256];
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(drain, ct);
                if (n == 0) break; // client disconnesso
            }
        }
        catch (OperationCanceledException) { }
        catch { /* socket closed */ }
        finally
        {
            _clients.TryRemove(key, out _);
            stream.Dispose();
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
        var lenBuf = BitConverter.GetBytes(packet.Length);
        var failed = new List<string>();

        foreach (var kv in _clients)
        {
            try
            {
                var s = kv.Value.stream; // può essere NetworkStream o SslStream (v1.1)
                await s.WriteAsync(lenBuf);
                await s.WriteAsync(packet);
                await s.FlushAsync();
            }
            catch { failed.Add(kv.Key); }
        }
        foreach (var key in failed)
        {
            if (_clients.TryRemove(key, out var dead))
            {
                dead.stream.Dispose();
                dead.client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
