using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Connettore TCP persistente verso un server NetManCat remoto.
/// Riconnessione automatica con backoff esponenziale (max 30 secondi).
/// </summary>
public sealed class TcpClientConnector : IDisposable
{
    private readonly ServerEntry _server;
    private TcpConfig? _tlsCfg; // v1.1
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _connectTask;
    private bool _disposed;

    // Classe interna per portare insieme TlsConfig al costruttore senza romperlo
    private sealed record TcpConfig(bool TlsEnabled, string PfxPath, string PfxPassword);

    /// <summary>True se la connessione TCP è attiva.</summary>
    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>Scatenato quando lo stato della connessione cambia (true=connesso).</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Scatenato quando arriva uno snapshot di connessioni dal server.</summary>
    public event EventHandler<RemoteWatchSnapshot>? SnapshotReceived;

    /// <summary>Scatenato in caso di errore non fatale.</summary>
    public event EventHandler<string>? ErrorOccurred;

    public TcpClientConnector(ServerEntry server, TlsConfig? tls = null)
    {
        _server = server;
        if (tls?.Enabled == true)
            _tlsCfg = new TcpConfig(true, tls.PfxPath, tls.PfxPassword);
    }

    /// <summary>Avvia il ciclo di connessione in background.</summary>
    public void Start()
    {
        if (_connectTask is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _connectTask = Task.Run(() => ConnectLoop(_cts.Token));
    }

    /// <summary>Ferma il connettore e chiude la connessione corrente.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _client?.Close();
        _connectTask?.Wait(TimeSpan.FromSeconds(2));
        _connectTask = null;
        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        _server.Connected = false;
    }

    /// <summary>Forza una riconnessione: ferma il ciclo corrente e lo riavvia.</summary>
    public void Reconnect() { Stop(); Start(); }

    /// <summary>
    /// Invia un pacchetto al server remoto.
    /// Formato: [4 byte lunghezza][payload LZ4].
    /// Silenzioso se non connesso.
    /// </summary>
    public async Task SendAsync(byte[] packet)
    {
        if (_client is not { Connected: true }) return;
        try
        {
            // Usa lo stream corrente (plain o TLS)
            Stream s = _activeStream ?? _client.GetStream();
            await s.WriteAsync(BitConverter.GetBytes(packet.Length));
            await s.WriteAsync(packet);
            await s.FlushAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    // Stream attivo (v1.1): può essere NetworkStream o SslStream
    private Stream? _activeStream;

    private async Task ConnectLoop(CancellationToken ct)
    {
        int delayMs = 1_000; // backoff iniziale 1 secondo

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client = new TcpClient();

                // KeepAlive per rilevare disconnessioni silenti
                _client.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                await _client.ConnectAsync(_server.Ip, _server.Port, ct);
                delayMs = 1_000; // reset backoff dopo connessione riuscita

                // v1.1: negozia TLS se configurato
                Stream stream = _client.GetStream();
                if (_tlsCfg?.TlsEnabled == true)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                        (_, cert, __, sslError) =>
                        {
                            // Trust-on-first-use: accetta qualsiasi self-signed se
                            // TlsThumbprint è vuoto; altrimenti verifica il thumbprint.
                            if (sslError != SslPolicyErrors.None &&
                                sslError != SslPolicyErrors.RemoteCertificateChainErrors)
                                return false;
                            if (!string.IsNullOrEmpty(_server.TlsThumbprint))
                                return string.Equals(
                                    cert?.GetCertHashString(),
                                    _server.TlsThumbprint,
                                    StringComparison.OrdinalIgnoreCase);
                            // Nessun thumbprint configurato: salva per display (TOFU)
                            _server.LastSeenThumbprint = cert?.GetCertHashString() ?? "";
                            return true;
                        });
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = _server.Ip,
                            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13
                                                | System.Security.Authentication.SslProtocols.Tls12
                        }, ct);
                    stream = ssl;
                }
                _activeStream = stream;

                _server.Connected = true;
                ConnectionStateChanged?.Invoke(this, true);

                // Legge snapshot inviati dal server (pacchetti length-prefixed)
                var lenBuf = new byte[4];
                while (!ct.IsCancellationRequested && _client.Connected)
                {
                    int read = await ReadExact(stream, lenBuf, 4, ct);
                    if (read < 4) break;

                    int packetLen = BitConverter.ToInt32(lenBuf, 0);
                    if (packetLen <= 0 || packetLen > 1_048_576) break;

                    var packet = new byte[packetLen];
                    read = await ReadExact(stream, packet, packetLen, ct);
                    if (read < packetLen) break;

                    try
                    {
                        var snap = PacketCodec.Decode<RemoteWatchSnapshot>(packet);
                        if (snap is not null)
                            SnapshotReceived?.Invoke(this, snap);
                    }
                    catch { /* pacchetto malformato: ignora */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"{_server.Ip}:{_server.Port} — {ex.Message}");
            }
            finally
            {
                _server.Connected = false;
                ConnectionStateChanged?.Invoke(this, false);
                _activeStream?.Dispose();
                _activeStream = null;
                _client?.Dispose();
                _client = null;
            }

            if (!ct.IsCancellationRequested)
            {
                // Backoff esponenziale: 1s → 2s → 4s → ... → 30s max
                await Task.Delay(delayMs, ct).ContinueWith(_ => { });
                delayMs = Math.Min(delayMs * 2, 30_000);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static async Task<int> ReadExact(
        Stream stream, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
