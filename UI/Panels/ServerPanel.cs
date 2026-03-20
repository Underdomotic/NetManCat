using NetManCat.Core;
using NetManCat.Models;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello Server/Client:
/// - Toggle per attivare/disattivare la modalità server TCP (trasmette solo le
///   connessioni in watchlist ai client connessi).
/// - Elenco dei server remoti con stato di connessione e context menu
///   per Connetti / Disconnetti / Riconnetti / Rimuovi.
/// - "Testa connessione" per verifica TCP rapida (timeout 3 s).
/// - Griglia inferiore: connessioni ricevute dai server, raggruppate per
///   nome server assegnato → processo → connessione.
/// </summary>
public sealed class ServerPanel : UserControl, IDisposable
{
    private readonly ConfigManager    _config;
    private readonly TcpServerHost    _serverHost;
    private readonly WatchlistManager _watchlist;
    private readonly ScanService      _scanService;
    private readonly LatencyProbe     _latencyProbe;
    private readonly BandwidthTracker _bwTracker;

    // Connettori client: chiave = "IP:Porta"
    private readonly Dictionary<string, TcpClientConnector> _connectors =
        new(StringComparer.OrdinalIgnoreCase);

    // Snapshot ricevuti: chiave = "IP:Porta" del connettore
    private readonly Dictionary<string, RemoteWatchSnapshot> _received =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _receivedLock = new();

    private bool _disposed;

    // --- UI ---
    private readonly Button        _btnToggle;
    private readonly Label         _lblStatus;
    private readonly TextBox       _txtLabel;
    private readonly TextBox       _txtIp;
    private readonly NumericUpDown _nudPort;
    private readonly Button        _btnTest;
    private readonly SrvGrid       _gridServers;
    private readonly SrvGrid       _gridReceived;
    private readonly ContextMenuStrip _ctxMenu;
    private readonly Font          _boldFont;
    private int _rightClickIdx = -1;

    // Context menu griglia connessioni ricevute
    private readonly ContextMenuStrip _ctxReceived;
    // Riga selezionata con il tasto destro nella griglia received
    private RemoteConnectionEntry? _rightClickedRemote;
    private string                 _rightClickedRemoteServer = "";
    private string                 _rightClickedRemoteProc   = "";

    // Delegate collegati da MainForm per il Grafico
    public Action<string, RemoteConnectionEntry>? OnAddRemoteToGraph;
    public Action<string, RemoteConnectionEntry>? OnRemoveRemoteFromGraph;
    public Action<string, string>?                OnAddRemoteProcToGraph;
    public Action<string, string>?                OnRemoveRemoteProcFromGraph;
    /// <summary>Invocato ad ogni snapshot ricevuto: alimenta in real-time le serie remote del grafico.</summary>
    public Action<string, List<RemoteConnectionEntry>>? OnFeedGraphSnapshot;

    // Stato collasso griglia connessioni ricevute
    private readonly HashSet<string> _collapsedSrv  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedProc = new(StringComparer.OrdinalIgnoreCase);
    private sealed record SrvHdr(string ConnKey);
    private sealed record ProcHdr(string ConnKey, string ProcName);
    private sealed record ConnRow(string ServerLabel, RemoteConnectionEntry Conn);

    private static readonly Color HdrServers  = Color.FromArgb(50,  50,  90);
    private static readonly Color HdrReceived = Color.FromArgb(35,  90,  50);
    private static readonly Color ColConn     = Color.FromArgb(0,  128,   0);
    private static readonly Color ColDisc     = Color.FromArgb(150, 40,  40);
    private static readonly Color ColWait     = Color.FromArgb(160, 110,  0);

    /// <summary>Scatenato quando il server TCP viene avviato (true) o fermato (false).</summary>
    public event EventHandler<bool>? ServerToggled;

    public ServerPanel(ConfigManager config, TcpServerHost serverHost,
                       WatchlistManager watchlist, ScanService scanService,
                       LatencyProbe latencyProbe, BandwidthTracker bwTracker)
    {
        _config       = config;
        _serverHost   = serverHost;
        _watchlist    = watchlist;
        _scanService  = scanService;
        _latencyProbe = latencyProbe;
        _bwTracker    = bwTracker;
        _boldFont     = new Font(this.Font, FontStyle.Bold);

        // ===================================================================
        // AREA TOP  (toggle + form aggiungi + testa)
        // ===================================================================
        var topTable = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            Height      = 78,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(8, 4, 8, 2)
        };
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 2));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        // Riga 0: toggle server + status
        var toggleRow = new Panel { Dock = DockStyle.Fill };
        _btnToggle = new Button
        {
            Text      = "▶  Avvia Server",
            Width     = 160, Height = 28,
            Location  = new Point(0, 3),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };
        _btnToggle.FlatAppearance.BorderSize = 0;
        _lblStatus = new Label
        {
            Text      = "Server TCP inattivo — trasmette solo le connessioni in ★ Analisi",
            Location  = new Point(168, 9),
            AutoSize  = true,
            ForeColor = Color.Gray,
            Font      = new Font(Font.FontFamily, 8f)
        };
        toggleRow.Controls.Add(_btnToggle);
        toggleRow.Controls.Add(_lblStatus);
        topTable.Controls.Add(toggleRow, 0, 0);

        // Riga 1: separatore
        var sep = new Panel { Dock = DockStyle.Fill, BackColor = Color.LightGray };
        topTable.Controls.Add(sep, 0, 1);

        // Riga 2: form aggiungi + testa
        var addRow = new Panel { Dock = DockStyle.Fill };
        new Label { Text = "Nome:", Location = new Point(0, 10),   AutoSize = true, Parent = addRow };
        _txtLabel = new TextBox { Location = new Point(42, 7),  Width = 120, Parent = addRow };
        new Label { Text = "IP:",   Location = new Point(172, 10),  AutoSize = true, Parent = addRow };
        _txtIp    = new TextBox { Location = new Point(190, 7),  Width = 150, Parent = addRow };
        new Label { Text = "Porta:",Location = new Point(350, 10),  AutoSize = true, Parent = addRow };
        _nudPort  = new NumericUpDown
        {
            Location = new Point(396, 7), Width = 72,
            Minimum  = 1, Maximum = 65535, Value = 9100,
            Parent   = addRow
        };
        var btnAdd = new Button
        {
            Text     = "+ Aggiungi",
            Location = new Point(476, 6), Width = 90, Height = 24,
            Parent   = addRow
        };
        _btnTest = new Button
        {
            Text      = "⚡ Testa connessione",
            Location  = new Point(574, 6), Width = 140, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 130, 50),
            ForeColor = Color.White,
            Parent    = addRow
        };
        topTable.Controls.Add(addRow, 0, 2);

        // ===================================================================
        // CONTEXT MENU su gridServers (tasto destro su una riga)
        // ===================================================================
        _ctxMenu = new ContextMenuStrip();
        var miConnect    = new ToolStripMenuItem("🔌  Connetti");
        var miDisconnect = new ToolStripMenuItem("⏏  Disconnetti");
        var miReconnect  = new ToolStripMenuItem("🔄  Riconnetti");
        var miRemove     = new ToolStripMenuItem("✕  Rimuovi");
        _ctxMenu.Items.AddRange(new ToolStripItem[]
            { miConnect, miDisconnect, miReconnect, new ToolStripSeparator(), miRemove });
        _ctxMenu.Opening   += OnContextMenuOpening;
        miConnect.Click    += (_, _) => ConnectAt(_rightClickIdx);
        miDisconnect.Click += (_, _) => DisconnectAt(_rightClickIdx);
        miReconnect.Click  += (_, _) => ReconnectAt(_rightClickIdx);
        miRemove.Click     += (_, _) => RemoveAt(_rightClickIdx);

        // ===================================================================
        // GRIGLIA SERVER
        // ===================================================================
        _gridServers = new SrvGrid
        {
            Dock                        = DockStyle.Fill,
            ReadOnly                    = true,
            AllowUserToAddRows          = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            RowHeadersVisible           = false,
            AllowUserToResizeRows       = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor             = Color.White,
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle                 = BorderStyle.None,
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles   = false,
            ContextMenuStrip            = _ctxMenu
        };
        StyleHdr(_gridServers, HdrServers);
        _gridServers.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Label",    HeaderText = "Nome",        FillWeight = 22 },
            new DataGridViewTextBoxColumn { Name = "Ip",       HeaderText = "IP",          FillWeight = 26 },
            new DataGridViewTextBoxColumn { Name = "Port",     HeaderText = "Porta",       FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Status",   HeaderText = "Stato",       FillWeight = 22 },
            new DataGridViewTextBoxColumn { Name = "LastData", HeaderText = "Ultimo dato", FillWeight = 20 },
        });
        _gridServers.MouseDown += OnServerGridMouseDown;

        // ===================================================================
        // GRIGLIA CONNESSIONI RICEVUTE
        // ===================================================================
        _gridReceived = new SrvGrid
        {
            Dock                        = DockStyle.Fill,
            ReadOnly                    = true,
            AllowUserToAddRows          = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            RowHeadersVisible           = false,
            AllowUserToResizeRows       = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor             = Color.White,
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle                 = BorderStyle.None,
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles   = false
        };
        StyleHdr(_gridReceived, HdrReceived);
        _gridReceived.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Proc",   HeaderText = "Processo", FillWeight = 20 },
            new DataGridViewTextBoxColumn { Name = "Remote", HeaderText = "Remoto",   FillWeight = 22 },
            new DataGridViewTextBoxColumn { Name = "State",  HeaderText = "Stato",    FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "Lat",    HeaderText = "Latenza",  FillWeight = 11 },
            new DataGridViewTextBoxColumn { Name = "UpKb",   HeaderText = "KB/s ↑",   FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "DnKb",   HeaderText = "KB/s ↓",   FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Loss",   HeaderText = "Loss%",    FillWeight =  9 },
            new DataGridViewTextBoxColumn { Name = "Time",   HeaderText = "Agg.",     FillWeight =  6 },
        });

        // Context menu griglia connessioni ricevute
        _ctxReceived = new ContextMenuStrip();
        var miAddGraph     = new ToolStripMenuItem("📊  Aggiungi al Grafico");
        var miRemGraph     = new ToolStripMenuItem("📊  Rimuovi dal Grafico");
        var miAddProcGraph = new ToolStripMenuItem("📊  Aggiungi processo al Grafico");
        var miRemProcGraph = new ToolStripMenuItem("📊  Rimuovi processo dal Grafico");
        miAddGraph.Click     += (_, _) => { if (_rightClickedRemote != null) OnAddRemoteToGraph?.Invoke(_rightClickedRemoteServer, _rightClickedRemote); };
        miRemGraph.Click     += (_, _) => { if (_rightClickedRemote != null) OnRemoveRemoteFromGraph?.Invoke(_rightClickedRemoteServer, _rightClickedRemote); };
        miAddProcGraph.Click += (_, _) => { if (!string.IsNullOrEmpty(_rightClickedRemoteProc)) OnAddRemoteProcToGraph?.Invoke(_rightClickedRemoteServer, _rightClickedRemoteProc); };
        miRemProcGraph.Click += (_, _) => { if (!string.IsNullOrEmpty(_rightClickedRemoteProc)) OnRemoveRemoteProcFromGraph?.Invoke(_rightClickedRemoteServer, _rightClickedRemoteProc); };
        _ctxReceived.Items.AddRange(new ToolStripItem[] { miAddGraph, miRemGraph, new ToolStripSeparator(), miAddProcGraph, miRemProcGraph });
        _ctxReceived.Opening += OnReceivedCtxOpening;
        _gridReceived.ContextMenuStrip = _ctxReceived;
        _gridReceived.MouseDown += OnReceivedGridMouseDown;

        // ===================================================================
        // SPLITTER orizzontale: top = server list, bottom = received data
        // ===================================================================
        var split = new SplitContainer
        {
            Dock        = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor   = Color.FromArgb(180, 200, 220)
        };
        LayoutEventHandler? splitLayoutOnce = null;
        splitLayoutOnce = (s, _) =>
        {
            var sc = (SplitContainer)s!;
            if (sc.Height < 80) return;          // dimensioni non ancora reali
            sc.Layout -= splitLayoutOnce;
            try { sc.SplitterWidth = 5;  } catch { }
            try { sc.Panel1MinSize = 70; } catch { }
            try { sc.Panel2MinSize = 60; } catch { }
            try
            {
                sc.SplitterDistance = Math.Clamp(200, 70, sc.Height - 60 - sc.SplitterWidth);
            }
            catch { }
        };
        split.Layout += splitLayoutOnce;

        var tbl1 = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        tbl1.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        tbl1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tbl1.Controls.Add(MakeHdrLabel("SERVER REMOTI CONFIGURATI", HdrServers), 0, 0);
        tbl1.Controls.Add(_gridServers, 0, 1);
        split.Panel1.Controls.Add(tbl1);

        var tbl2 = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        tbl2.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        tbl2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tbl2.Controls.Add(MakeHdrLabel("CONNESSIONI RICEVUTE DALLA RETE", HdrReceived), 0, 0);
        tbl2.Controls.Add(_gridReceived, 0, 1);
        split.Panel2.Controls.Add(tbl2);

        Controls.Add(split);
        Controls.Add(topTable);

        // ===================================================================
        // WIRING
        // ===================================================================
        _btnToggle.Click      += OnToggleServer;
        btnAdd.Click          += OnAddServer;
        _btnTest.Click        += OnTestConnection;
        _config.ConfigChanged += (_, _) => RefreshServersGrid();
        _scanService.ScanCompleted += OnScanCompleted;
        _gridReceived.CellClick    += OnReceivedGridCellClick;

        RefreshServersGrid();
    }

    // -----------------------------------------------------------------------
    // SCAN → broadcast watchlist ai client connessi
    // -----------------------------------------------------------------------
    private void OnScanCompleted(object? sender, List<TcpConnection> conns)
    {
        if (_disposed || !_serverHost.IsRunning || _watchlist.Count == 0) return;

        var watched = conns.Where(c => _watchlist.IsWatched(c)).ToList();
        if (watched.Count == 0) return;

        var snap = new RemoteWatchSnapshot
        {
            SourceLabel = Environment.MachineName,
            Timestamp   = DateTime.UtcNow,
            Connections = watched.Select(c =>
            {
                var (up, dn) = _bwTracker.Get(c);
                return new RemoteConnectionEntry
                {
                    ProcessName = c.ProcessName,
                    LocalIp     = c.LocalIp,
                    LocalPort   = c.LocalPort,
                    RemoteIp    = c.RemoteIp,
                    RemotePort  = c.RemotePort,
                    State       = c.State.ToString().ToUpperInvariant(),
                    LatencyMs   = _latencyProbe.GetLatency(c.RemoteIp),
                    UpKbps      = up,
                    DownKbps    = dn,
                    LossPercent = _latencyProbe.GetLoss(c.RemoteIp)
                };
            }).ToList()
        };
        byte[] packet = PacketCodec.Encode(snap);
        _ = _serverHost.SendToAllAsync(packet);
    }

    // -----------------------------------------------------------------------
    // TOGGLE SERVER
    // -----------------------------------------------------------------------
    private void OnToggleServer(object? sender, EventArgs e)
    {
        if (_serverHost.IsRunning)
        {
            _serverHost.Stop();
            _btnToggle.Text      = "▶  Avvia Server";
            _btnToggle.BackColor = Color.FromArgb(0, 120, 215);
            _lblStatus.Text      = "Server TCP inattivo — trasmette solo le connessioni in ★ Analisi";
            _lblStatus.ForeColor = Color.Gray;
            _config.Config.ServerMode.Enabled = false;
            _config.Save();
            ServerToggled?.Invoke(this, false);
        }
        else
        {
            int port = _config.Config.ServerMode.Port;
            try
            {
                _serverHost.Start(port, _config.Config.Tls);
                _btnToggle.Text      = "■  Ferma Server";
                _btnToggle.BackColor = Color.FromArgb(196, 43, 28);
                _lblStatus.Text      = $"Server attivo su :{port}  |  client: 0  |  ★ {_watchlist.Count} in analisi";
                _lblStatus.ForeColor = Color.FromArgb(0, 128, 0);
                _config.Config.ServerMode.Enabled = true;
                _config.Save();
                _serverHost.ClientConnected    += (_, _) => UpdateClientCount();
                _serverHost.ClientDisconnected += (_, _) => UpdateClientCount();
                ServerToggled?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Impossibile avviare il server sulla porta {port}:\n{ex.Message}",
                    "Errore avvio server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // Esposto pubblicamente per il menu tray di MainForm
    public void ToggleServer() => OnToggleServer(null, EventArgs.Empty);

    private void UpdateClientCount()
    {
        if (InvokeRequired) { Invoke(UpdateClientCount); return; }
        if (!_serverHost.IsRunning) return;
        _lblStatus.Text = $"Server attivo su :{_config.Config.ServerMode.Port}" +
                          $"  |  client connessi: {_serverHost.ConnectedClients}" +
                          $"  |  ★ {_watchlist.Count} in analisi";
    }

    // -----------------------------------------------------------------------
    // TESTA CONNESSIONE
    // -----------------------------------------------------------------------
    private async void OnTestConnection(object? sender, EventArgs e)
    {
        string ip  = _txtIp.Text.Trim();
        int    port = (int)_nudPort.Value;
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Inserire prima un IP nel campo.", "Test connessione",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _btnTest.Enabled = false;
        _btnTest.Text    = "…";
        try
        {
            using var tc  = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(3000);
            await tc.ConnectAsync(ip, port, cts.Token);
            MessageBox.Show($"✓  Connessione a {ip}:{port} riuscita!",
                "Test connessione", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch
        {
            MessageBox.Show($"✗  Impossibile raggiungere {ip}:{port}\n(timeout 3 secondi)",
                "Test connessione", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnTest.Enabled = true;
            _btnTest.Text    = "⚡ Testa connessione";
        }
    }

    // -----------------------------------------------------------------------
    // AGGIUNGI SERVER
    // -----------------------------------------------------------------------
    private void OnAddServer(object? sender, EventArgs e)
    {
        string ip    = _txtIp.Text.Trim();
        string label = _txtLabel.Text.Trim();
        int    port  = (int)_nudPort.Value;
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Inserire un indirizzo IP o hostname valido.",
                "Campo obbligatorio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _config.Config.Servers.Add(new ServerEntry
        {
            Label = string.IsNullOrWhiteSpace(label) ? ip : label,
            Ip    = ip,
            Port  = port
        });
        _config.Save();
        RefreshServersGrid();
        _txtIp.Clear();
        _txtLabel.Clear();
    }

    // -----------------------------------------------------------------------
    // CONTEXT MENU — click destro su riga griglia server
    // -----------------------------------------------------------------------
    private void OnServerGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _gridServers.HitTest(e.X, e.Y);
        _rightClickIdx = hit.RowIndex;
        if (hit.RowIndex >= 0)
        {
            _gridServers.ClearSelection();
            _gridServers.Rows[hit.RowIndex].Selected = true;
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        int  idx    = _rightClickIdx;
        bool valid  = idx >= 0 && idx < _config.Config.Servers.Count;
        if (!valid) { e.Cancel = true; return; }

        string key     = ConnKey(idx);
        bool   hasConn = _connectors.TryGetValue(key, out var con);
        bool   isConn  = hasConn && con!.IsConnected;

        _ctxMenu.Items[0].Enabled = !isConn;  // Connetti
        _ctxMenu.Items[1].Enabled =  isConn;  // Disconnetti
        _ctxMenu.Items[2].Enabled = hasConn;  // Riconnetti
        // [3] = separator
        _ctxMenu.Items[4].Enabled = true;     // Rimuovi
    }

    private string ConnKey(int idx)
    {
        var s = _config.Config.Servers[idx];
        return $"{s.Ip}:{s.Port}";
    }

    private void ConnectAt(int idx)
    {
        if (idx < 0 || idx >= _config.Config.Servers.Count) return;
        var    s   = _config.Config.Servers[idx];
        string key = $"{s.Ip}:{s.Port}";
        if (_connectors.ContainsKey(key)) return;

        var con = new TcpClientConnector(s, _config.Config.Tls);
        con.SnapshotReceived    += OnSnapshotReceived;
        con.ConnectionStateChanged += (_, connected) =>
        {
            s.Connected = connected;
            SetServerRowStatus(idx, connected ? "Connesso" : "Disconnesso",
                               connected ? ColConn : ColDisc);
        };
        con.ErrorOccurred += (_, msg) =>
            SetServerRowStatus(idx, $"Errore: {msg[..Math.Min(msg.Length, 28)]}", ColDisc);

        _connectors[key] = con;
        SetServerRowStatus(idx, "Connessione…", ColWait);
        con.Start();
    }

    private void DisconnectAt(int idx)
    {
        if (idx < 0 || idx >= _config.Config.Servers.Count) return;
        string key = ConnKey(idx);
        if (_connectors.Remove(key, out var con))
        {
            con.Stop();
            con.Dispose();
        }
        _config.Config.Servers[idx].Connected = false;
        SetServerRowStatus(idx, "Disconnesso", ColDisc);
    }

    private void ReconnectAt(int idx)
    {
        if (idx < 0 || idx >= _config.Config.Servers.Count) return;
        string key = ConnKey(idx);
        if (_connectors.TryGetValue(key, out var con))
        {
            SetServerRowStatus(idx, "Riconnessione…", ColWait);
            con.Reconnect();
        }
        else
        {
            ConnectAt(idx);
        }
    }

    private void RemoveAt(int idx)
    {
        if (idx < 0 || idx >= _config.Config.Servers.Count) return;
        var s   = _config.Config.Servers[idx];
        var res = MessageBox.Show(
            $"Rimuovere il server \"{s.Label}\" ({s.Ip}:{s.Port})?",
            "Conferma rimozione", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (res != DialogResult.Yes) return;

        DisconnectAt(idx);
        _config.Config.Servers.RemoveAt(idx);
        _config.Save();
        RefreshServersGrid();
    }

    // -----------------------------------------------------------------------
    // SNAPSHOT RICEVUTO (thread pool → marshal to UI thread)
    // -----------------------------------------------------------------------
    private void OnSnapshotReceived(object? sender, RemoteWatchSnapshot snap)
    {
        // Usa il label configurato localmente per identificare il server sorgente
        string connKey    = _connectors
            .FirstOrDefault(kv => ReferenceEquals(kv.Value, sender)).Key ?? "";
        string dispLabel  = "";
        if (!string.IsNullOrEmpty(connKey))
        {
            var entry = _config.Config.Servers
                .FirstOrDefault(s => $"{s.Ip}:{s.Port}" == connKey);
            dispLabel = entry?.Label ?? snap.SourceLabel;
        }
        else dispLabel = snap.SourceLabel;

        snap.SourceLabel = dispLabel;

        lock (_receivedLock)
            _received[connKey] = snap;

        if (!IsHandleCreated || _disposed) return;
        BeginInvoke(() =>
        {
            RebuildReceivedGrid();
            // Aggiorna colonna "Ultimo dato" nella griglia server
            int rowIdx = _config.Config.Servers
                .FindIndex(s => $"{s.Ip}:{s.Port}" == connKey);
            if (rowIdx >= 0 && rowIdx < _gridServers.Rows.Count)
                _gridServers.Rows[rowIdx].Cells["LastData"].Value =
                    snap.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            // Alimenta le serie remote del grafico con i dati dello snapshot
            OnFeedGraphSnapshot?.Invoke(snap.SourceLabel, snap.Connections);
        });
    }

    // -----------------------------------------------------------------------
    // CLICK su riga griglia ricevuta → espandi/riduci gruppo
    // -----------------------------------------------------------------------
    private void OnReceivedGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var tag = _gridReceived.Rows[e.RowIndex].Tag;
        if (tag is SrvHdr sh)
        {
            if (!_collapsedSrv.Remove(sh.ConnKey))
                _collapsedSrv.Add(sh.ConnKey);
            RebuildReceivedGrid();
        }
        else if (tag is ProcHdr ph)
        {
            string procKey = $"{ph.ConnKey}|{ph.ProcName}";
            if (!_collapsedProc.Remove(procKey))
                _collapsedProc.Add(procKey);
            RebuildReceivedGrid();
        }
    }

    // Tasto destro su griglia received: identifica riga e popola campi per context menu
    private void OnReceivedGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        _rightClickedRemote       = null;
        _rightClickedRemoteServer = "";
        _rightClickedRemoteProc   = "";
        var hit = _gridReceived.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        var row = _gridReceived.Rows[hit.RowIndex];
        if (row.Tag is ConnRow cr)
        {
            _rightClickedRemote       = cr.Conn;
            _rightClickedRemoteServer = cr.ServerLabel;
            _rightClickedRemoteProc   = cr.Conn.ProcessName;
        }
        else if (row.Tag is ProcHdr ph)
        {
            var srv = _config.Config.Servers.FirstOrDefault(s => $"{s.Ip}:{s.Port}" == ph.ConnKey);
            _rightClickedRemoteServer = srv?.Label ?? ph.ConnKey;
            _rightClickedRemoteProc   = ph.ProcName;
        }
    }

    private void OnReceivedCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool hasConn = _rightClickedRemote != null;
        bool hasProc = !string.IsNullOrEmpty(_rightClickedRemoteProc);
        if (!hasConn && !hasProc) { e.Cancel = true; return; }
        _ctxReceived.Items[0].Visible = hasConn;   // Aggiungi connessione al Grafico
        _ctxReceived.Items[1].Visible = hasConn;   // Rimuovi connessione dal Grafico
        _ctxReceived.Items[2].Visible = hasProc;   // Separator
        _ctxReceived.Items[3].Visible = hasProc;   // Aggiungi processo al Grafico
        _ctxReceived.Items[4].Visible = hasProc;   // Rimuovi processo dal Grafico
    }

    private void RebuildReceivedGrid()
    {
        if (_disposed) return;
        int firstVisible = _gridReceived.FirstDisplayedScrollingRowIndex;
        _gridReceived.SuspendLayout();
        _gridReceived.Rows.Clear();

        List<(string connKey, RemoteWatchSnapshot snap)> snaps;
        lock (_receivedLock)
            snaps = _received.Select(kv => (kv.Key, kv.Value)).ToList();

        var orderedServers = _config.Config.Servers
            .Select((s, i) => (connKey: $"{s.Ip}:{s.Port}", label: s.Label, idx: i))
            .ToList();

        // Colore header processo: leggermente più chiaro di HdrReceived
        var procHdrColor = Color.FromArgb(55, 115, 68);

        foreach (var srv in orderedServers)
        {
            var found = snaps.FirstOrDefault(x => x.connKey == srv.connKey);
            if (found.snap == null) continue;

            var    snap         = found.snap;
            bool   srvCollapsed = _collapsedSrv.Contains(srv.connKey);
            string srvArrow     = srvCollapsed ? "▶" : "▼";
            string ts           = snap.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            int    cnt          = snap.Connections.Count;

            // Aggregate server
            var    withLat   = snap.Connections.Where(c => c.LatencyMs >= 0).ToList();
            string srvLatLbl = withLat.Count > 0 ? $"~{withLat.Average(c => c.LatencyMs):F1} ms" : "—";
            double srvUp     = snap.Connections.Sum(c => c.UpKbps);
            double srvDn     = snap.Connections.Sum(c => c.DownKbps);
            string srvUpLbl  = srvUp > 0 ? $"Σ {srvUp:F1}" : "—";
            string srvDnLbl  = srvDn > 0 ? $"Σ {srvDn:F1}" : "—";

            // ── Riga intestazione server ──────────────────────────────────
            int hdrIdx = _gridReceived.Rows.Add(
                $"{srvArrow}  {srv.label}",
                $"{cnt} conn.",
                "",
                srvLatLbl,
                srvUpLbl,
                srvDnLbl,
                "",
                ts);

            var hdrRow = _gridReceived.Rows[hdrIdx];
            hdrRow.Tag    = new SrvHdr(srv.connKey);
            hdrRow.Height = 22;
            foreach (DataGridViewCell cell in hdrRow.Cells)
            {
                cell.Style.BackColor          = HdrReceived;
                cell.Style.ForeColor          = Color.White;
                cell.Style.Font               = _boldFont;
                cell.Style.SelectionBackColor = HdrReceived;
                cell.Style.SelectionForeColor = Color.White;
            }

            if (srvCollapsed) continue;

            // ── Righe dati raggruppate per processo ───────────────────────
            foreach (var group in snap.Connections
                         .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key))
            {
                string procKey       = $"{srv.connKey}|{group.Key}";
                bool   procCollapsed = _collapsedProc.Contains(procKey);
                string procArrow     = procCollapsed ? "  ▶" : "  ▼";
                var    conns         = group.ToList();

                // Aggregate processo
                var    withLatP   = conns.Where(c => c.LatencyMs >= 0).ToList();
                string procLatLbl = withLatP.Count > 0 ? $"~{withLatP.Average(c => c.LatencyMs):F1} ms" : "—";
                double procUp     = conns.Sum(c => c.UpKbps);
                double procDn     = conns.Sum(c => c.DownKbps);
                string procUpLbl  = procUp > 0 ? $"Σ {procUp:F1}" : "—";
                string procDnLbl  = procDn > 0 ? $"Σ {procDn:F1}" : "—";

                // ── Riga intestazione processo ────────────────────────────
                int pHdrIdx = _gridReceived.Rows.Add(
                    $"{procArrow}  {group.Key}",
                    $"{conns.Count} conn.",
                    "",
                    procLatLbl,
                    procUpLbl,
                    procDnLbl,
                    "",
                    "");

                var pHdrRow = _gridReceived.Rows[pHdrIdx];
                pHdrRow.Tag    = new ProcHdr(srv.connKey, group.Key);
                pHdrRow.Height = 20;
                foreach (DataGridViewCell cell in pHdrRow.Cells)
                {
                    cell.Style.BackColor          = procHdrColor;
                    cell.Style.ForeColor          = Color.White;
                    cell.Style.Font               = _boldFont;
                    cell.Style.SelectionBackColor = procHdrColor;
                    cell.Style.SelectionForeColor = Color.White;
                }

                if (procCollapsed) continue;

                foreach (var c in conns.OrderBy(c => c.RemoteIp).ThenBy(c => c.RemotePort))
                {
                    string latLabel  = c.LatencyMs   >= 0 ? $"{c.LatencyMs:F1} ms" : "—";
                    string upLabel   = c.UpKbps      > 0  ? $"{c.UpKbps:F1}"       : "—";
                    string downLabel = c.DownKbps    > 0  ? $"{c.DownKbps:F1}"     : "—";
                    string lossLabel = c.LossPercent >= 0 ? $"{c.LossPercent:F0}%" : "—";

                    int ri = _gridReceived.Rows.Add(
                        $"      {c.ProcessName}",
                        $"{c.RemoteIp}:{c.RemotePort}",
                        c.State,
                        latLabel,
                        upLabel,
                        downLabel,
                        lossLabel,
                        "");

                    var row = _gridReceived.Rows[ri];
                    row.Tag = new ConnRow(srv.label, c);   // tag per context menu Grafico
                    row.DefaultCellStyle.BackColor = Color.FromArgb(235, 248, 235);

                    if (c.LatencyMs >= 0)
                        row.Cells["Lat"].Style.ForeColor =
                            c.LatencyMs < 50  ? ColConn :
                            c.LatencyMs < 150 ? ColWait : ColDisc;

                    if (c.LossPercent > 0)
                        row.Cells["Loss"].Style.ForeColor =
                            c.LossPercent < 5 ? ColWait : ColDisc;
                }
            }
        }

        _gridReceived.ResumeLayout();
        if (firstVisible > 0 && _gridReceived.Rows.Count > firstVisible)
        {
            try { _gridReceived.FirstDisplayedScrollingRowIndex = firstVisible; } catch { }
        }
    }

    // -----------------------------------------------------------------------
    // AGGIORNA GRIGLIA SERVER
    // -----------------------------------------------------------------------
    private void RefreshServersGrid()
    {
        if (InvokeRequired) { Invoke(RefreshServersGrid); return; }
        _gridServers.Rows.Clear();
        foreach (var s in _config.Config.Servers)
        {
            string key  = $"{s.Ip}:{s.Port}";
            bool   conn = _connectors.TryGetValue(key, out var con) && con.IsConnected;
            string last = "—";
            lock (_receivedLock)
                if (_received.TryGetValue(key, out var snap))
                    last = snap.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            int ri = _gridServers.Rows.Add(s.Label, s.Ip, s.Port,
                                            conn ? "Connesso" : "Disconnesso", last);
            _gridServers.Rows[ri].Cells["Status"].Style.ForeColor = conn ? ColConn : ColDisc;
        }
    }

    private void SetServerRowStatus(int idx, string status, Color fg)
    {
        if (InvokeRequired) { Invoke(() => SetServerRowStatus(idx, status, fg)); return; }
        if (idx < 0 || idx >= _gridServers.Rows.Count) return;
        _gridServers.Rows[idx].Cells["Status"].Value            = status;
        _gridServers.Rows[idx].Cells["Status"].Style.ForeColor  = fg;
    }

    // -----------------------------------------------------------------------
    // HELPERS
    // -----------------------------------------------------------------------
    private static Label MakeHdrLabel(string text, Color bg) => new()
    {
        Text      = text,
        Dock      = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = bg,
        Padding   = new Padding(6, 0, 0, 0)
    };

    private static void StyleHdr(DataGridView g, Color bg)
    {
        g.ColumnHeadersDefaultCellStyle.BackColor = bg;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 8f, FontStyle.Bold);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _scanService.ScanCompleted -= OnScanCompleted;
            foreach (var con in _connectors.Values) con.Dispose();
            _connectors.Clear();
            _boldFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class SrvGrid : DataGridView
    {
        public SrvGrid() => DoubleBuffered = true;
    }
}
