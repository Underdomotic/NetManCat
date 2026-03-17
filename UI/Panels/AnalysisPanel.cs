using NetManCat.Core;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello Analisi: mostra SOLO le connessioni aggiunte alla watchlist.
///
/// Funzionalità:
/// - Aggiornamento per-riga con preservazione scroll (no salto al top).
/// - Latenza RTT in ms misurata da LatencyProbe (ping ICMP asincrono).
/// - Raggruppamento per nome processo.
/// - Connessioni temporaneamente offline mostrate in grigio con stato "OFFLINE".
/// - Tasto destro su riga → "✕ Rimuovi dall'analisi".
/// </summary>
public sealed class AnalysisPanel : UserControl, IDisposable
{
    private readonly ConfigManager    _config;
    private readonly WatchlistManager  _watchlist;
    private readonly LatencyProbe      _latencyProbe;
    private readonly ScanService       _scanService;
    private readonly BandwidthTracker  _bwTracker;
    private readonly Font                 _boldFont;
    private readonly DoubleBufferedGrid   _grid;
    private readonly ContextMenuStrip   _contextMenu;
    private readonly Label _lblCount;
    private readonly Label _lblHint;
    private string? _rightClickedKey;
    private GroupHdr? _rightClickedGroup;
    private bool _disposed;
    private List<TcpConnection> _lastConnections = new();

    // Gruppi correntemente chiusi (chiave = nome processo o "OFFLINE")
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool         _groupsSeeded;
    private List<string> _allGroupKeys = new();

    // Identificatore riga intestazione di gruppo
    private sealed record GroupHdr(string Key, IReadOnlyList<string>? Keys = null);

    // Colori
    private static readonly Color HdrColor    = Color.FromArgb(80,  60,  20);
    private static readonly Color RowOnline   = Color.FromArgb(255, 248, 210);
    private static readonly Color RowOffline  = Color.FromArgb(235, 235, 235);
    private static readonly Color ForeOffline = Color.FromArgb(150, 150, 150);
    private static readonly Color ForeLatOk   = Color.FromArgb( 20, 100,  20);
    private static readonly Color ForeLatWarn = Color.FromArgb(165,  90,   0);
    private static readonly Color ForeLatBad  = Color.FromArgb(180,  20,  20);

    private const string TagHeader = "HDR";

    public AnalysisPanel(ConfigManager config, WatchlistManager watchlist,
                          LatencyProbe latencyProbe, ScanService scanService,
                          BandwidthTracker bwTracker)
    {
        _config       = config;
        _watchlist    = watchlist;
        _latencyProbe = latencyProbe;
        _scanService  = scanService;
        _bwTracker    = bwTracker;
        _boldFont     = new Font(this.Font, FontStyle.Bold);

        // ---- Header strip ------------------------------------------------
        _lblCount = new Label
        {
            Text     = "Nessuna connessione in analisi",
            AutoSize = true,
            Location = new Point(8, 10),
            Font     = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 70, 0)
        };
        _lblHint = new Label
        {
            Text      = "  ←  Vai su Monitor, click destro su una riga → \"Aggiungi all'analisi\"",
            AutoSize  = true,
            Location  = new Point(280, 11),
            ForeColor = Color.Gray,
            Font      = new Font(Font.FontFamily, 7.5f)
        };

        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
        headerPanel.Controls.Add(_lblCount);
        headerPanel.Controls.Add(_lblHint);

        // ---- DataGridView ------------------------------------------------
        _grid = new DoubleBufferedGrid
        {
            Dock                        = DockStyle.Fill,
            ReadOnly                    = true,
            AllowUserToAddRows          = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor             = Color.White,
            AllowUserToResizeRows       = false,
            RowHeadersVisible           = false,
            BorderStyle                 = BorderStyle.None,
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles   = false
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(80, 60, 20);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font      = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);

        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Proc",     HeaderText = "Processo",  FillWeight = 16 },
            new DataGridViewTextBoxColumn { Name = "Pid",      HeaderText = "PID",       FillWeight =  5 },
            new DataGridViewTextBoxColumn { Name = "LocalEP",  HeaderText = "Locale",    FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "RemoteEP", HeaderText = "Remoto",    FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "Latency",  HeaderText = "Latenza",   FillWeight =  9 },
            new DataGridViewTextBoxColumn { Name = "Up",       HeaderText = "↑ KB/s",    FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "Down",     HeaderText = "↓ KB/s",    FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "Loss",     HeaderText = "Loss %",    FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "State",    HeaderText = "Stato",     FillWeight = 10 },
        });

        _grid.SelectionChanged += OnSelectionChanged;

        // ---- Context menu ------------------------------------------------
        _contextMenu = new ContextMenuStrip();
        var menuRemove = new ToolStripMenuItem("✕  Rimuovi dall'analisi");
        menuRemove.Click += (_, _) =>
        {
            if (_rightClickedKey != null)
            {
                _watchlist.RemoveKey(_rightClickedKey);
                _rightClickedKey = null;
            }
        };
        var menuRemoveAll = new ToolStripMenuItem("✕  Rimuovi tutto il gruppo dall'analisi");
        menuRemoveAll.Click += (_, _) =>
        {
            if (_rightClickedGroup?.Keys != null)
                foreach (var k in _rightClickedGroup.Keys)
                    _watchlist.RemoveKey(k);
        };
        var menuExpandAll   = new ToolStripMenuItem("▼  Espandi tutti");
        var menuCollapseAll = new ToolStripMenuItem("▶  Riduci tutti");
        menuExpandAll.Click   += (_, _) => { _collapsed.Clear(); if (_watchlist.Count > 0) _scanService.RequestRefresh(); };
        menuCollapseAll.Click += (_, _) => { _collapsed.Clear(); _collapsed.UnionWith(_allGroupKeys); if (_watchlist.Count > 0) _scanService.RequestRefresh(); };
        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            menuRemove,
            new ToolStripSeparator(),
            menuRemoveAll,
            new ToolStripSeparator(),
            menuExpandAll,
            menuCollapseAll
        });
        _contextMenu.Opening   += OnContextMenuOpening;
        _grid.ContextMenuStrip  = _contextMenu;
        _grid.CellClick        += OnGridCellClick;
        _grid.MouseDown        += OnGridMouseDown;

        Controls.Add(_grid);
        Controls.Add(headerPanel);

        _scanService.ScanCompleted  += OnScanCompleted;
        _watchlist.WatchlistChanged += OnWatchlistChanged;

        // Aggiorna subito lo stato hint
        RefreshHint();
    }

    // -----------------------------------------------------------------------
    // Avvia/ferma il timer in base al fatto che ci siano item in watchlist
    // -----------------------------------------------------------------------
    private void OnWatchlistChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired) { Invoke(() => OnWatchlistChanged(sender, e)); return; }
        RefreshHint();
        if (_watchlist.Count > 0)
        {
            _latencyProbe.Start();
            // Aggiornamento immediato dai dati dell'ultimo scan
            ProcessConnections(_lastConnections);
        }
        else
        {
            _latencyProbe.Stop();
            _grid.Rows.Clear();
            _lblCount.Text = "Nessuna connessione in analisi";
        }
    }

    private void RefreshHint()
    {
        _lblHint.Visible = _watchlist.Count == 0;
    }

    // -----------------------------------------------------------------------
    // Tick principale
    // -----------------------------------------------------------------------
    private void OnScanCompleted(object? sender, List<TcpConnection> connections)
    {
        if (_disposed) return;
        _lastConnections = connections;
        if (_watchlist.Count > 0) ProcessConnections(connections);
    }

    private void ProcessConnections(List<TcpConnection> connections)
    {
        try
        {
            var (online, offlineKeys) = _watchlist.Partition(connections);
            _latencyProbe.UpdateTargets(online.Select(c => c.RemoteIp));
            _bwTracker.Update(online);
            UpdateGrid(online, offlineKeys);
            int total = online.Count + offlineKeys.Count;
            _lblCount.Text = total == 0
                ? "Nessuna connessione in analisi"
                : $"★ {total} connessioni in analisi  ({online.Count} online, {offlineKeys.Count} offline)";
        }
        catch (Exception ex)
        {
            _lblCount.Text = $"Errore: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    // Aggiornamento griglia con preservazione scroll
    // -----------------------------------------------------------------------
    private void UpdateGrid(List<TcpConnection> online, List<string> offlineKeys)
    {
        int     firstVisible = _grid.FirstDisplayedScrollingRowIndex;
        string? selectedKey  = GetSelectedKey();

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        // ---- Raggruppamento per nome processo (connessioni online) ----------
        var byProcess = online
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!_groupsSeeded)
        {
            foreach (var g in byProcess) _collapsed.Add(g.Key);
            if (offlineKeys.Count > 0) _collapsed.Add("OFFLINE");
            _groupsSeeded = true;
        }
        _allGroupKeys = byProcess.Select(g => g.Key).ToList();
        if (offlineKeys.Count > 0) _allGroupKeys.Add("OFFLINE");

        foreach (var group in byProcess)
        {
            var  wKeys = group.Select(c => WatchlistManager.MakeKey(c)).ToList();
            bool open  = !_collapsed.Contains(group.Key);
            AddGroupHeader(group.Key, group.Count(), isOffline: false, wKeys);
            if (open)
                foreach (var conn in group.OrderBy(c => c.RemoteIp).ThenBy(c => c.RemotePort))
                    AddOnlineRow(conn, selectedKey);
        }

        // ---- Connessioni offline (watchlist ma non rilevate) ---------------
        if (offlineKeys.Count > 0)
        {
            bool open = !_collapsed.Contains("OFFLINE");
            AddGroupHeader("OFFLINE", offlineKeys.Count, isOffline: true, offlineKeys);
            if (open)
                foreach (var key in offlineKeys.OrderBy(k => k))
                    AddOfflineRow(key, selectedKey);
        }

        _grid.ResumeLayout();

        if (firstVisible > 0 && _grid.Rows.Count > firstVisible)
        {
            try { _grid.FirstDisplayedScrollingRowIndex = firstVisible; }
            catch { /* fuori range: ignora */ }
        }
    }

    // -----------------------------------------------------------------------
    // Intestazione di gruppo processo
    // -----------------------------------------------------------------------
    private void AddGroupHeader(string groupName, int count, bool isOffline,
                                 IReadOnlyList<string>? keys = null)
    {
        Color  bg       = isOffline ? Color.FromArgb(100, 100, 100) : HdrColor;
        string suffix   = count == 1 ? "connessione" : "connessioni";
        bool   collapsed = _collapsed.Contains(isOffline ? "OFFLINE" : groupName);
        string arrow    = collapsed ? "▶" : "▼";
        string colKey   = isOffline ? "OFFLINE" : groupName;

        int rowIdx = _grid.Rows.Add(
            $"{arrow}  {groupName}",
            $"{count} {suffix}",
            "", "", "", "", "", "", "");

        var row      = _grid.Rows[rowIdx];
        row.Tag      = new GroupHdr(colKey, keys);
        row.Height   = 22;

        foreach (DataGridViewCell cell in row.Cells)
        {
            cell.Style.BackColor          = bg;
            cell.Style.ForeColor          = Color.White;
            cell.Style.Font               = _boldFont;
            cell.Style.SelectionBackColor = bg;
            cell.Style.SelectionForeColor = Color.White;
        }
    }

    // -----------------------------------------------------------------------
    // Riga online con latenza misurata
    // -----------------------------------------------------------------------
    private void AddOnlineRow(TcpConnection conn, string? selectedKey)
    {
        string localEp  = $"{conn.LocalIp}:{conn.LocalPort}";
        string remoteEp = conn.RemoteIp is "0.0.0.0" or "::" or "0:0:0:0:0:0:0:0"
                          ? "—"
                          : $"{conn.RemoteIp}:{conn.RemotePort}";

        double latMs    = _latencyProbe.GetLatency(conn.RemoteIp);
        string latLabel = latMs < 0 ? "—" : $"{latMs:F1} ms";

        var   (upKbps, downKbps) = _bwTracker.Get(conn);
        string upLabel   = upKbps   > 0 ? $"{upKbps:F1}"   : "—";
        string downLabel = downKbps > 0 ? $"{downKbps:F1}" : "—";
        double lossVal   = _latencyProbe.GetLoss(conn.RemoteIp);
        string lossLabel = lossVal >= 0 ? $"{lossVal:F0}%" : "—";

        int rowIdx = _grid.Rows.Add(
            conn.ProcessName,
            conn.Pid,
            localEp,
            remoteEp,
            latLabel,
            upLabel,
            downLabel,
            lossLabel,
            GetStateName(conn.State)
        );

        var row = _grid.Rows[rowIdx];
        string wKey   = WatchlistManager.MakeKey(conn);
        row.Tag       = wKey;
        row.DefaultCellStyle.BackColor = RowOnline;
        row.DefaultCellStyle.Font      = new Font(_grid.Font, FontStyle.Bold);

        // Colora cella latenza in base alle soglie configurate
        double threshold = _config.Config.AlertThresholds.LatencyMs;
        if (latMs >= 0)
        {
            Color latColor = latMs > threshold         ? ForeLatBad  :
                             latMs > threshold * 0.8   ? ForeLatWarn : ForeLatOk;
            row.Cells["Latency"].Style.ForeColor = latColor;
        }
        // Colora Loss% in base alla soglia configurata
        double lossThreshold = _config.Config.AlertThresholds.LossPercent;
        if (lossVal > lossThreshold)
            row.Cells["Loss"].Style.ForeColor = ForeLatBad;
        else if (lossVal > 0)
            row.Cells["Loss"].Style.ForeColor = ForeLatWarn;

        if (wKey == selectedKey) row.Selected = true;
    }

    // -----------------------------------------------------------------------
    // Riga offline (processo/connessione non più rilevata)
    // -----------------------------------------------------------------------
    private void AddOfflineRow(string key, string? selectedKey)
    {
        // key = "ProcessName|RemoteIP|RemotePort"
        var parts = key.Split('|');
        string proc   = parts.Length > 0 ? parts[0] : key;
        string remote = parts.Length > 2 ? $"{parts[1]}:{parts[2]}" : "—";

        int rowIdx = _grid.Rows.Add(
            proc,
            "—",
            "—",
            remote,
            "—", "—", "—", "—",
            "OFFLINE"
        );

        var row = _grid.Rows[rowIdx];
        row.Tag = key;
        row.DefaultCellStyle.BackColor = RowOffline;
        row.DefaultCellStyle.ForeColor = ForeOffline;

        if (key == selectedKey) row.Selected = true;
    }

    // -----------------------------------------------------------------------
    // Context menu
    // -----------------------------------------------------------------------
    private void OnGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        _rightClickedKey   = null;
        _rightClickedGroup = null;
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        var row = _grid.Rows[hit.RowIndex];
        if (row.Tag is string k)   // data row: watchlist key
        {
            _rightClickedKey = k;
            _grid.ClearSelection();
            row.Selected = true;
        }
        else if (row.Tag is GroupHdr hdr)
        {
            _rightClickedGroup = hdr;
        }
    }

    // Collapse/expand al click sinistro su una riga intestazione di gruppo
    private void OnGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not GroupHdr hdr) return;
        if (!_collapsed.Add(hdr.Key))
            _collapsed.Remove(hdr.Key);
        if (!_disposed && _watchlist.Count > 0)
            _scanService.RequestRefresh();
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool isSingle = _rightClickedKey   != null;
        bool isGroup  = _rightClickedGroup != null;
        if (!isSingle && !isGroup) { e.Cancel = true; return; }

        _contextMenu.Items[0].Visible = isSingle;   // Rimuovi singolo
        _contextMenu.Items[1].Visible = isGroup;    // Sep1
        _contextMenu.Items[2].Visible = isGroup;    // Rimuovi tutto gruppo
        _contextMenu.Items[3].Visible = isGroup;    // Sep2
        _contextMenu.Items[4].Visible = isGroup;    // Espandi tutti
        _contextMenu.Items[5].Visible = isGroup;    // Riduci tutti
        if (isGroup)
        {
            int n = _rightClickedGroup!.Keys?.Count ?? 0;
            _contextMenu.Items[2].Text = $"✕  Rimuovi tutto il gruppo dall'analisi ({n} connessioni)";
        }
    }

    // -----------------------------------------------------------------------
    // Selezione: impedisce header
    // -----------------------------------------------------------------------
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.Tag is GroupHdr)
                row.Selected = false;
    }

    private string? GetSelectedKey()
    {
        foreach (DataGridViewRow r in _grid.SelectedRows)
            if (r.Tag is string k)
                return k;
        return null;
    }

    // -----------------------------------------------------------------------
    // Nome stato
    // -----------------------------------------------------------------------
    private static string GetStateName(TcpState s) => s switch
    {
        TcpState.Established => "ESTABLISHED",
        TcpState.Listen      => "LISTEN",
        TcpState.CloseWait   => "CLOSE_WAIT",
        TcpState.TimeWait    => "TIME_WAIT",
        TcpState.FinWait1    => "FIN_WAIT_1",
        TcpState.FinWait2    => "FIN_WAIT_2",
        TcpState.SynSent     => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.LastAck     => "LAST_ACK",
        TcpState.Closing     => "CLOSING",
        TcpState.DeleteTcb   => "DELETE_TCB",
        TcpState.Closed      => "CLOSED",
        _                    => s.ToString().ToUpperInvariant()
    };

    // -----------------------------------------------------------------------
    // DoubleBufferedGrid
    // -----------------------------------------------------------------------
    private sealed class DoubleBufferedGrid : DataGridView
    {
        public DoubleBufferedGrid() => DoubleBuffered = true;
    }

    // -----------------------------------------------------------------------
    // Dispose
    // -----------------------------------------------------------------------
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
            _scanService.ScanCompleted -= OnScanCompleted;
        base.Dispose(disposing);
        if (disposing) _boldFont.Dispose();
    }
}
