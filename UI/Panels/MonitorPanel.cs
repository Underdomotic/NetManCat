using NetManCat.Core;
using NetManCat.Models;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello Monitor: connessioni TCP raggruppate per stato (albero flat).
///
/// Strategia di aggiornamento:
///   1. Salva posizione scroll e chiave riga selezionata.
///   2. Ricostruisce la griglia con intestazioni gruppo + righe figlio.
///   3. Ripristina scroll e selezione → la scrollbar NON salta mai al top.
///
/// Double-buffering abilitato via DoubleBufferedGrid per eliminare il flickering.
/// </summary>
public sealed class MonitorPanel : UserControl, IDisposable
{
    private readonly ConfigManager    _config;
    private readonly MetricsBuffer    _buffer;
    private readonly WatchlistManager _watchlist;
    private readonly ScanService      _scanService;
    private readonly Font             _boldFont;
    private readonly DoubleBufferedGrid _grid;
    private readonly ContextMenuStrip   _contextMenu;
    private readonly Label    _lblTotal;
    private readonly Label    _lblElapsed;
    private readonly Label    _lblWatched;
    private readonly ComboBox _cboGroupBy;
    private readonly CheckBox _chkCompact;
    private readonly ComboBox _cboNic;
    private readonly Button   _btnRefreshNic;
    private List<NicInfo>     _nicList = new();

    private TcpConnection? _rightClickedConn;
    private GroupHdr?      _rightClickedGroup;
    private bool _disposed;

    // Delegate collegati da MainForm per aggiungere/rimuovere dal Grafico
    public Action<TcpConnection>? OnAddToGraph;
    public Action<TcpConnection>? OnRemoveFromGraph;
    public Action<string>?        OnAddProcessToGraph;
    public Action<string>?        OnRemoveProcessFromGraph;

    // Gruppi correntemente chiusi (chiave = nome stato o nome processo)
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool         _groupsSeeded;
    private List<string> _allGroupKeys = new();

    // Identificatore riga intestazione di gruppo (collapse e context menu)
    private sealed record GroupHdr(string Key, IReadOnlyList<TcpConnection>? Conns = null);

    // Colori righe monitorate
    private static readonly Color RowWatched     = Color.FromArgb(255, 248, 210);
    private static readonly Color RowWatchedFont = Color.FromArgb(130, 80,  0);

    // Colori intestazioni di gruppo
    private static readonly Color HdrEstablished = Color.FromArgb(28,  90, 168);
    private static readonly Color HdrListen      = Color.FromArgb(40, 130,  55);
    private static readonly Color HdrTransient   = Color.FromArgb(160, 100,  20);
    private static readonly Color HdrOther       = Color.FromArgb( 75,  75,  85);
    private static readonly Color HdrProcess     = Color.FromArgb( 45, 100, 130);

    // Colori sfondo righe connessione
    private static readonly Color RowEstablished = Color.FromArgb(235, 245, 255);
    private static readonly Color RowListen      = Color.FromArgb(238, 252, 238);
    private static readonly Color RowTransient   = Color.FromArgb(255, 250, 225);
    private static readonly Color RowOther       = Color.FromArgb(248, 248, 248);

    public MonitorPanel(ConfigManager config, MetricsBuffer buffer, WatchlistManager watchlist, ScanService scanService)
    {
        _config      = config;
        _buffer      = buffer;
        _watchlist   = watchlist;
        _scanService = scanService;
        _boldFont    = new Font(this.Font, FontStyle.Bold);

        // --- Header ---
        _lblTotal = new Label
        {
            Text     = "Connessioni attive: —",
            AutoSize = true,
            Location = new Point(8, 10)
        };
        _lblElapsed = new Label
        {
            Text      = "",
            AutoSize  = true,
            Location  = new Point(220, 10),
            ForeColor = Color.Gray,
            Font      = new Font(Font.FontFamily, 7.5f)
        };
        _lblWatched = new Label
        {
            Text      = "",
            AutoSize  = true,
            Location  = new Point(400, 10),
            ForeColor = Color.FromArgb(130, 80, 0),
            Font      = new Font(Font.FontFamily, 7.5f, FontStyle.Bold)
        };
        _cboGroupBy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location      = new Point(600, 7),
            Width         = 165,
            Font          = new Font(Font.FontFamily, 7.5f)
        };
        _cboGroupBy.Items.AddRange(new object[] { "Raggruppa per Stato", "Raggruppa per Processo" });
        _cboGroupBy.SelectedIndex = 0;
        _cboGroupBy.SelectedIndexChanged += (_, _) =>
        {
            _collapsed.Clear();
            _groupsSeeded = false;
            _scanService.RequestRefresh();
        };
        _chkCompact = new CheckBox
        {
            Text       = "Solo analizzati",
            AutoSize   = true,
            Location   = new Point(785, 9),
            Font       = new Font(Font.FontFamily, 7.5f),
            ForeColor  = Color.FromArgb(130, 80, 0),
            Appearance = Appearance.Button,
            FlatStyle  = FlatStyle.Flat,
        };
        _chkCompact.CheckedChanged += (_, _) => _scanService.RequestRefresh();

        // --- Riga 2: filtro scheda di rete ---
        var lblNic = new Label
        {
            Text      = "Scheda:",
            AutoSize  = true,
            Location  = new Point(8, 42),
            Font      = new Font(Font.FontFamily, 7.5f),
            ForeColor = Color.FromArgb(60, 60, 80)
        };
        _cboNic = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location      = new Point(64, 39),
            Width         = 590,
            Font          = new Font(Font.FontFamily, 7.5f)
        };
        _btnRefreshNic = new Button
        {
            Text      = "↻ Schede",
            Location  = new Point(663, 38),
            Width     = 80,
            Height    = 22,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font(Font.FontFamily, 7.5f),
            ForeColor = Color.FromArgb(30, 80, 140)
        };
        _cboNic.SelectedIndexChanged += (_, _) => _scanService.RequestRefresh();
        _btnRefreshNic.Click         += (_, _) => { RefreshNicList(); _scanService.RequestRefresh(); };
        RefreshNicList();

        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 66 };
        headerPanel.Controls.Add(_lblTotal);
        headerPanel.Controls.Add(_lblElapsed);
        headerPanel.Controls.Add(_lblWatched);
        headerPanel.Controls.Add(_cboGroupBy);
        headerPanel.Controls.Add(_chkCompact);
        headerPanel.Controls.Add(lblNic);
        headerPanel.Controls.Add(_cboNic);
        headerPanel.Controls.Add(_btnRefreshNic);

        // --- DataGridView con double-buffering (elimina flickering) ---
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
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(50, 50, 70);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font      = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);

        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            // Colonna indicatore: ★ per le righe nella watchlist
            new DataGridViewTextBoxColumn { Name = "Watch",   HeaderText = "",         FillWeight =  3, MinimumWidth = 24 },
            new DataGridViewTextBoxColumn { Name = "Pid",     HeaderText = "PID",      FillWeight =  5 },
            new DataGridViewTextBoxColumn { Name = "Proc",    HeaderText = "Processo", FillWeight = 18 },
            new DataGridViewTextBoxColumn { Name = "LocalEP", HeaderText = "Locale",   FillWeight = 16 },
            new DataGridViewTextBoxColumn { Name = "RemoteEP",HeaderText = "Remoto",   FillWeight = 16 },
            new DataGridViewTextBoxColumn { Name = "State",   HeaderText = "Stato",    FillWeight = 11 },
        });

        // Impedisce la selezione delle righe intestazione di gruppo
        _grid.SelectionChanged += OnSelectionChanged;

        // --- Context menu tasto destro ---
        _contextMenu = new ContextMenuStrip();
        var menuAdd    = new ToolStripMenuItem("★  Aggiungi all'analisi");
        var menuRemove = new ToolStripMenuItem("✕  Rimuovi dall'analisi");
        menuAdd.Click    += (_, _) => { if (_rightClickedConn != null) _watchlist.Add(_rightClickedConn);    };
        menuRemove.Click += (_, _) => { if (_rightClickedConn != null) _watchlist.Remove(_rightClickedConn); };
        var menuAddAll = new ToolStripMenuItem("★  Aggiungi tutti all'analisi");
        menuAddAll.Click += (_, _) =>
        {
            if (_rightClickedGroup?.Conns != null)
                foreach (var c in _rightClickedGroup.Conns)
                    _watchlist.Add(c);
        };
        var menuExpandAll   = new ToolStripMenuItem("▼  Espandi tutti");
        var menuCollapseAll = new ToolStripMenuItem("▶  Riduci tutti");
        menuExpandAll.Click   += (_, _) => { _collapsed.Clear(); _scanService.RequestRefresh(); };
        menuCollapseAll.Click += (_, _) => { _collapsed.Clear(); _collapsed.UnionWith(_allGroupKeys); _scanService.RequestRefresh(); };
        // v1.2: Grafico
        var menuSep2         = new ToolStripSeparator();
        var menuAddGraph     = new ToolStripMenuItem("📊  Aggiungi al Grafico");
        var menuRemoveGraph  = new ToolStripMenuItem("📊  Rimuovi dal Grafico");
        var menuAddProcGraph = new ToolStripMenuItem("📊  Aggiungi processo al Grafico");
        var menuRemProcGraph = new ToolStripMenuItem("📊  Rimuovi processo dal Grafico");
        menuAddGraph.Click     += (_, _) => { if (_rightClickedConn != null) OnAddToGraph?.Invoke(_rightClickedConn); };
        menuRemoveGraph.Click  += (_, _) => { if (_rightClickedConn != null) OnRemoveFromGraph?.Invoke(_rightClickedConn); };
        menuAddProcGraph.Click += (_, _) => { if (_rightClickedGroup != null) OnAddProcessToGraph?.Invoke(_rightClickedGroup.Key); };
        menuRemProcGraph.Click += (_, _) => { if (_rightClickedGroup != null) OnRemoveProcessFromGraph?.Invoke(_rightClickedGroup.Key); };
        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            menuAdd, menuRemove,            // [0] [1]
            new ToolStripSeparator(),       // [2]
            menuAddAll,                     // [3]
            new ToolStripSeparator(),       // [4]
            menuExpandAll,                  // [5]
            menuCollapseAll,                // [6]
            menuSep2,                       // [7]
            menuAddGraph, menuRemoveGraph,  // [8] [9]
            menuAddProcGraph, menuRemProcGraph // [10] [11]
        });
        _contextMenu.Opening   += OnContextMenuOpening;
        _grid.ContextMenuStrip  = _contextMenu;
        _grid.CellClick        += OnGridCellClick;
        _grid.MouseDown        += OnGridMouseDown;

        // Refresh visuale immediato al cambio watchlist
        _watchlist.WatchlistChanged += (_, _) =>
        {
            if (!_disposed) _scanService.RequestRefresh();
        };

        Controls.Add(_grid);
        Controls.Add(headerPanel);

        _scanService.ScanCompleted += OnScanCompleted;
    }

    private void OnScanCompleted(object? sender, List<TcpConnection> connections)
    {
        if (_disposed) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UpdateGrid(connections);
        sw.Stop();
        int watchedCount = connections.Count(c => _watchlist.IsWatched(c));
        _lblTotal.Text   = $"Connessioni attive: {connections.Count}";
        _lblElapsed.Text = $"UI {sw.ElapsedMilliseconds} ms";
        _lblWatched.Text = watchedCount > 0 ? $"  ★ {watchedCount} in analisi" : "";
    }

    // -----------------------------------------------------------------------
    // Aggiornamento griglia con preservazione scroll e selezione
    // -----------------------------------------------------------------------
    private void RefreshNicList()
    {
        _nicList = NicManager.GetActiveNics();
        string? current = _cboNic.SelectedIndex > 0 && _cboNic.SelectedIndex <= _nicList.Count
            ? _nicList[_cboNic.SelectedIndex - 1].Id
            : null;

        _cboNic.BeginUpdate();
        _cboNic.Items.Clear();
        _cboNic.Items.Add("Tutte le schede di rete");
        foreach (var n in _nicList) _cboNic.Items.Add(n.DisplayName);

        // Ripristina selezione precedente (per Id)
        int idx = current == null ? 0
            : _nicList.FindIndex(n => n.Id == current) + 1;
        _cboNic.SelectedIndex = Math.Max(0, Math.Min(idx, _cboNic.Items.Count - 1));
        _cboNic.EndUpdate();
    }

    private void UpdateGrid(List<TcpConnection> connections)
    {
        // Applica filtro scheda di rete se selezionata
        if (_cboNic.SelectedIndex > 0 && _cboNic.SelectedIndex <= _nicList.Count)
        {
            var addrs = _nicList[_cboNic.SelectedIndex - 1].Addresses;
            connections = connections
                .Where(c => Array.IndexOf(addrs, c.LocalIp) >= 0)
                .ToList();
        }

        int     firstVisible = _grid.FirstDisplayedScrollingRowIndex;
        string? selectedKey  = GetSelectedKey();
        bool    byProcess    = _cboGroupBy.SelectedIndex == 1;
        bool    compact      = _chkCompact.Checked;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        if (byProcess)
        {
            // ---- Raggruppa per Processo ----------------------------------
            var groups = connections
                .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!_groupsSeeded)
            {
                foreach (var g in groups) _collapsed.Add(g.Key);
                _groupsSeeded = true;
            }
            _allGroupKeys = groups.Select(g => g.Key).ToList();

            foreach (var group in groups)
            {
                string procKey = group.Key;
                var    conns   = group.OrderBy(c => c.RemoteIp)
                                      .ThenBy(c => c.RemotePort)
                                      .ToList();
                var visible = compact
                    ? conns.Where(c => _watchlist.IsWatched(c)).ToList()
                    : conns;
                if (compact && visible.Count == 0) continue;

                bool open = !_collapsed.Contains(procKey);
                AddGroupHeader(procKey, procKey, HdrProcess,
                               compact ? visible.Count : conns.Count, conns);
                if (open)
                    foreach (var conn in visible)
                        AddConnectionRow(conn, selectedKey);
            }
        }
        else
        {
            // ---- Raggruppa per Stato (default) ---------------------------
            var groups = connections
                .GroupBy(c => c.State)
                .OrderBy(g => GetStateOrder(g.Key))
                .ToList();

            if (!_groupsSeeded)
            {
                foreach (var g in groups) _collapsed.Add(GetStateName(g.Key));
                _groupsSeeded = true;
            }
            _allGroupKeys = groups.Select(g => GetStateName(g.Key)).ToList();

            foreach (var group in groups)
            {
                string stateKey = GetStateName(group.Key);
                var    conns    = group.OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                                       .ThenBy(c => c.RemoteIp)
                                       .ThenBy(c => c.RemotePort)
                                       .ToList();
                var visible = compact
                    ? conns.Where(c => _watchlist.IsWatched(c)).ToList()
                    : conns;
                if (compact && visible.Count == 0) continue;

                bool open = !_collapsed.Contains(stateKey);
                AddGroupHeader(stateKey, stateKey, GetHeaderColor(group.Key),
                               compact ? visible.Count : conns.Count);
                if (open)
                    foreach (var conn in visible)
                        AddConnectionRow(conn, selectedKey);
            }
        }

        _grid.ResumeLayout();

        if (firstVisible > 0 && _grid.Rows.Count > firstVisible)
        {
            try { _grid.FirstDisplayedScrollingRowIndex = firstVisible; }
            catch { /* Posizione fuori range dopo riorganizzazione: ignora */ }
        }
    }

    // -----------------------------------------------------------------------
    // Aggiunge riga intestazione di gruppo (non selezionabile)
    // -----------------------------------------------------------------------
    private void AddGroupHeader(string key, string label, Color bg, int count,
                                 IReadOnlyList<TcpConnection>? conns = null)
    {
        bool   collapsed = _collapsed.Contains(key);
        string arrow     = collapsed ? "▶" : "▼";
        string suffix    = count == 1 ? "connessione" : "connessioni";

        int rowIdx = _grid.Rows.Add(
            "",
            $"{arrow}  {label}",
            $"{count} {suffix}",
            "", "", "");

        var row      = _grid.Rows[rowIdx];
        row.Tag      = new GroupHdr(key, conns);
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
    // Aggiunge riga dati per una singola connessione
    // -----------------------------------------------------------------------
    private void AddConnectionRow(TcpConnection conn, string? selectedKey)
    {
        string localEp  = $"{conn.LocalIp}:{conn.LocalPort}";
        string remoteEp = conn.RemoteIp is "0.0.0.0" or "::" or "0:0:0:0:0:0:0:0"
                          ? "—"
                          : $"{conn.RemoteIp}:{conn.RemotePort}";
        string key      = GetConnectionKey(conn);
        bool   watched  = _watchlist.IsWatched(conn);

        int rowIdx = _grid.Rows.Add(
            watched ? "★" : "",
            conn.Pid,
            conn.ProcessName,
            localEp,
            remoteEp,
            GetStateName(conn.State)
        );

        var row = _grid.Rows[rowIdx];
        row.Tag = conn;   // TcpConnection come tag (usato dal context menu)

        if (watched)
        {
            // Righe monitorate: sfondo dorato + testo scuro in evidenza
            row.DefaultCellStyle.BackColor = RowWatched;
            row.DefaultCellStyle.ForeColor = RowWatchedFont;
            row.DefaultCellStyle.Font      = _boldFont;
        }
        else
        {
            row.DefaultCellStyle.BackColor = GetRowColor(conn.State);
        }

        // Ripristina la selezione se era su questa riga nel ciclo precedente
        if (key == selectedKey)
            row.Selected = true;
    }

    // -----------------------------------------------------------------------
    // Context menu: right-click
    // -----------------------------------------------------------------------
    private void OnGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        _rightClickedConn  = null;
        _rightClickedGroup = null;
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        var row = _grid.Rows[hit.RowIndex];
        if (row.Tag is TcpConnection conn)
        {
            _rightClickedConn = conn;
            _grid.ClearSelection();
            row.Selected = true;
        }
        else if (row.Tag is GroupHdr hdr && hdr.Conns != null)
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
        // Toggle: se non presente aggiunge (chiude), se presente rimuove (apre)
        if (!_collapsed.Add(hdr.Key))
            _collapsed.Remove(hdr.Key);
        _scanService.RequestRefresh();
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool isSingle = _rightClickedConn  != null;
        bool isGroup  = _rightClickedGroup?.Conns != null;
        if (!isSingle && !isGroup) { e.Cancel = true; return; }

        bool watched = isSingle && _watchlist.IsWatched(_rightClickedConn!);
        _contextMenu.Items[0].Visible = isSingle && !watched;   // Aggiungi singolo
        _contextMenu.Items[1].Visible = isSingle &&  watched;   // Rimuovi singolo
        _contextMenu.Items[2].Visible = isGroup;                // Sep1
        _contextMenu.Items[3].Visible = isGroup;                // Aggiungi tutti
        _contextMenu.Items[4].Visible = isGroup;                // Sep2
        _contextMenu.Items[5].Visible = true;                   // Espandi tutti
        _contextMenu.Items[6].Visible = true;                   // Riduci tutti

        // Grafico: disponibile solo per connessioni/processi già in analisi (watchlist),
        // altrimenti LatencyProbe non ha dati e manderebbero serie vuote al grafico.
        bool groupHasWatched = isGroup &&
            _rightClickedGroup!.Conns!.Any(c => _watchlist.IsWatched(c));
        bool showGraph = (isSingle && watched) || groupHasWatched;

        _contextMenu.Items[7].Visible  = showGraph;             // Sep3
        _contextMenu.Items[8].Visible  = isSingle && watched;   // Aggiungi connessione al Grafico
        _contextMenu.Items[9].Visible  = isSingle && watched;   // Rimuovi connessione dal Grafico
        _contextMenu.Items[10].Visible = groupHasWatched;       // Aggiungi processo al Grafico
        _contextMenu.Items[11].Visible = groupHasWatched;       // Rimuovi processo dal Grafico

        if (isGroup)
        {
            int n = _rightClickedGroup!.Conns!.Count;
            _contextMenu.Items[3].Text = $"★  Aggiungi tutti all'analisi ({n} connessioni)";
        }
    }

    // -----------------------------------------------------------------------
    // Impedisce la selezione delle righe intestazione
    // -----------------------------------------------------------------------
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.Tag is GroupHdr)
                row.Selected = false;
    }

    // -----------------------------------------------------------------------
    // Helpers: chiave univoca connessione
    // -----------------------------------------------------------------------
    private string? GetSelectedKey()
    {
        foreach (DataGridViewRow r in _grid.SelectedRows)
            if (r.Tag is TcpConnection conn)
                return GetConnectionKey(conn);
        return null;
    }

    private static string GetConnectionKey(TcpConnection c) =>
        $"{c.Pid}:{c.LocalIp}:{c.LocalPort}:{c.RemoteIp}:{c.RemotePort}";

    // -----------------------------------------------------------------------
    // Helpers: ordinamento, nomi, colori
    // -----------------------------------------------------------------------
    private static int GetStateOrder(TcpState s) => s switch
    {
        TcpState.Established => 0,
        TcpState.Listen      => 1,
        TcpState.CloseWait   => 2,
        TcpState.TimeWait    => 3,
        TcpState.FinWait1    => 4,
        TcpState.FinWait2    => 5,
        TcpState.SynSent     => 6,
        TcpState.SynReceived => 7,
        TcpState.LastAck     => 8,
        TcpState.Closing     => 9,
        TcpState.DeleteTcb   => 10,
        TcpState.Closed      => 11,
        _                    => 99
    };

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

    private static Color GetHeaderColor(TcpState s) => s switch
    {
        TcpState.Established                                             => HdrEstablished,
        TcpState.Listen                                                  => HdrListen,
        TcpState.CloseWait or TcpState.TimeWait
            or TcpState.FinWait1 or TcpState.FinWait2                   => HdrTransient,
        _                                                                => HdrOther
    };

    private static Color GetRowColor(TcpState s) => s switch
    {
        TcpState.Established                                             => RowEstablished,
        TcpState.Listen                                                  => RowListen,
        TcpState.CloseWait or TcpState.TimeWait
            or TcpState.FinWait1 or TcpState.FinWait2                   => RowTransient,
        _                                                                => RowOther
    };

    // -----------------------------------------------------------------------
    // DoubleBufferedGrid: espone DoubleBuffered = true per eliminare flickering
    // -----------------------------------------------------------------------
    private sealed class DoubleBufferedGrid : DataGridView
    {
        public DoubleBufferedGrid() => DoubleBuffered = true;
    }

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
