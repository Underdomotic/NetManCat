using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;

namespace NetManCat.Core;

/// <summary>
/// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
/// Monitor di rete event-driven via ETW (Event Tracing for Windows) — v1.2
/// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
///
/// Cattura eventi TCP di connessione/disconnessione dal kernel Windows tramite
/// il provider NetworkTCPIP.
///
/// Vantaggi rispetto al polling GetExtendedTcpTable:
///   ✓ Connessioni brevi (< 500 ms) catturate con timestamp esatto del kernel
///   ✓ Overhead CPU quasi zero a riposo (event-driven)
///   ✓ Visibilità su connessioni HTTP/S che durano pochi ms
///   ✓ Integrazione non invasiva: GetExtendedTcpTable resta come fonte di verità
///
/// Requisiti:
///   - Privilegi di amministratore (già richiesti da NetManCat)
///   - Windows 8.1+ / Windows Server 2012 R2+
///   - NuGet: Microsoft.Diagnostics.Tracing.TraceEvent
///
/// Integrazione con ScanService:
///   EtwNetworkMonitor alimenta una coda di connessioni effimere.
///   ScanService può unire questa coda con i risultati del polling.
/// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
/// </summary>
public sealed class EtwNetworkMonitor : IDisposable
{
    // Nome univoco per la sessione ETW (evita conflitti tra istanze)
    private static readonly string SessionName =
        $"NetManCat-ETW-{Environment.ProcessId}";

    private TraceEventSession? _session;
    private Thread?            _thread;
    private bool               _disposed;

    /// <summary>
    /// Scattato quando il kernel segnala l'apertura di una connessione TCP.
    /// Parametri: pid, localEndpoint ("ip:port"), remoteEndpoint ("ip:port")
    /// </summary>
    public event Action<int, string, string>? ConnectionOpened;

    /// <summary>
    /// Scattato quando il kernel segnala la chiusura di una connessione TCP.
    /// </summary>
    public event Action<int, string, string>? ConnectionClosed;

    /// <summary>True se la sessione ETW è attiva.</summary>
    public bool IsRunning { get; private set; }

    // -----------------------------------------------------------------------
    // API pubblica
    // -----------------------------------------------------------------------

    /// <summary>Avvia la sessione ETW in un thread dedicato. Richiede admin.</summary>
    public void Start()
    {
        if (IsRunning || _disposed) return;
        _thread = new Thread(RunSessionLoop)
        {
            IsBackground = true,
            Name         = "EtwNetworkMonitor"
        };
        _thread.Start();
        IsRunning = true;
    }

    /// <summary>Ferma la sessione ETW e rilascia le risorse.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try
        {
            _session?.Stop();
            _session?.Dispose();
            _session = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EtwNetworkMonitor] Stop: {ex.Message}");
        }
        _thread?.Join(3_000);
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // -----------------------------------------------------------------------
    // Loop ETW (thread dedicato)
    // -----------------------------------------------------------------------

    private void RunSessionLoop()
    {
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            // Abilita gli eventi TCP/IP del kernel
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            // Handler IPv4
            _session.Source.Kernel.TcpIpConnect      += OnTcpConnect;
            _session.Source.Kernel.TcpIpConnectIPV6   += OnTcpConnectV6;
            _session.Source.Kernel.TcpIpDisconnect    += OnTcpDisconnect;
            _session.Source.Kernel.TcpIpDisconnectIPV6 += OnTcpDisconnectV6;

            // Blocca fino a Stop()
            _session.Source.Process();
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[EtwNetworkMonitor] privilegi insufficienti: {ex.Message}");
            IsRunning = false;
        }
        catch (Exception ex) when (!_disposed)
        {
            Debug.WriteLine($"[EtwNetworkMonitor] errore sessione: {ex.Message}");
            IsRunning = false;
        }
    }

    // -----------------------------------------------------------------------
    // Handler eventi kernel IPv4
    // -----------------------------------------------------------------------

    private void OnTcpConnect(TcpIpConnectTraceData data)
    {
        try
        {
            ConnectionOpened?.Invoke(data.ProcessID,
                $"{data.saddr}:{data.sport}",
                $"{data.daddr}:{data.dport}");
        }
        catch (Exception ex) { Debug.WriteLine($"[EtwNetworkMonitor] OnTcpConnect: {ex.Message}"); }
    }

    private void OnTcpDisconnect(TcpIpTraceData data)
    {
        try
        {
            ConnectionClosed?.Invoke(data.ProcessID,
                $"{data.saddr}:{data.sport}",
                $"{data.daddr}:{data.dport}");
        }
        catch (Exception ex) { Debug.WriteLine($"[EtwNetworkMonitor] OnTcpDisconnect: {ex.Message}"); }
    }

    // -----------------------------------------------------------------------
    // Handler eventi kernel IPv6
    // -----------------------------------------------------------------------

    private void OnTcpConnectV6(TcpIpV6ConnectTraceData data)
    {
        try
        {
            ConnectionOpened?.Invoke(data.ProcessID,
                $"[{data.saddr}]:{data.sport}",
                $"[{data.daddr}]:{data.dport}");
        }
        catch (Exception ex) { Debug.WriteLine($"[EtwNetworkMonitor] OnTcpConnectV6: {ex.Message}"); }
    }

    private void OnTcpDisconnectV6(TcpIpV6TraceData data)
    {
        try
        {
            ConnectionClosed?.Invoke(data.ProcessID,
                $"[{data.saddr}]:{data.sport}",
                $"[{data.daddr}]:{data.dport}");
        }
        catch (Exception ex) { Debug.WriteLine($"[EtwNetworkMonitor] OnTcpDisconnectV6: {ex.Message}"); }
    }
}

