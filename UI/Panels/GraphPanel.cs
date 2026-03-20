using NetManCat.Core;
using NetManCat.Models;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello Grafico — storico visuale real-time delle metriche per singola
/// connessione o processo, sia locali sia provenienti dai server remoti.
///
/// Funzionalità:
/// - Aggiornamento automatico ogni refresh (ScanService.ScanCompleted).
/// - Serie multiple selezionabili dalla lista laterale.
/// - Tre curve per serie: Latenza (ms), ↑ KB/s, ↓ KB/s.
/// - Tooltip con valori precisi al passaggio del mouse.
/// - Navigazione: scroll orizzontale sul canvas, zoom con Ctrl+Rotella.
/// - "Cancella tutto" per ripartire da zero (cancella anche i punti nel DB).
/// - Attiva automaticamente SQLite quando viene aggiunta la prima serie.
/// </summary>
public sealed class GraphPanel : UserControl, IDisposable
{
    private readonly ConfigManager    _config;
    private readonly GraphLogger      _graphLogger;
    private readonly WatchlistManager _watchlist;
    private readonly ScanService      _scanService;
    private readonly LatencyProbe     _latencyProbe;
    private readonly BandwidthTracker _bwTracker;

    // UI
    private readonly Panel           _sidebar;
    private readonly Panel           _chartArea;
    private readonly Label           _lblInfo;
    private readonly Label           _lblHint;
    private readonly Button          _btnClear;
    private readonly ComboBox        _cboMetric;
    private readonly CheckedListBox  _seriesList;

    // Canvas di disegno double-buffered
    private readonly ChartCanvas     _canvas;

    // Stato rendering
    private int   _viewOffsetPx = 0;   // scorrimento orizzontale in pixel
    private float _zoomX        = 1f;  // zoom moltiplicatore (1 = 1 pt per pixel)
    private Point _tooltipPt    = Point.Empty;
    private bool  _tooltipVisible;

    // Colori per serie (ciclico)
    private static readonly Color[] SeriesColors =
    {
        Color.FromArgb(0,   160, 220),
        Color.FromArgb(240, 100,  30),
        Color.FromArgb( 60, 180,  60),
        Color.FromArgb(200,  50, 200),
        Color.FromArgb(220, 190,   0),
        Color.FromArgb(180,  60,  60),
        Color.FromArgb( 40, 200, 180),
        Color.FromArgb(100,  80, 220),
    };

    // Cache punti per il rendering
    private Dictionary<string, List<GraphLogger.ChartPoint>> _pointsCache = new();
    private string? _selectedKey;

    // Evidenziazione serie (tasto doppio click nella lista)
    private readonly HashSet<string> _highlightedKeys = new(StringComparer.OrdinalIgnoreCase);

    // Zoom-box via drag mouse
    private bool  _dragging;
    private Point _dragStart;
    private Point _dragEnd;
    private bool  _zoomBoxVisible;

    // Quando true il prossimo RefreshChart rilegge tutto da SQLite (cambio serie o avvio)
    private bool _needsFullReload = true;

    private bool _panelDisposed;

    public GraphPanel(ConfigManager config, GraphLogger graphLogger,
                      WatchlistManager watchlist, ScanService scanService,
                      LatencyProbe latencyProbe, BandwidthTracker bwTracker)
    {
        _config       = config;
        _graphLogger  = graphLogger;
        _watchlist    = watchlist;
        _scanService  = scanService;
        _latencyProbe = latencyProbe;
        _bwTracker    = bwTracker;

        // ===================================================================
        // TOOLBAR top
        // ===================================================================
        var toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 38,
            BackColor = Color.FromArgb(28, 32, 55)
        };

        _lblInfo = new Label
        {
            Text      = "  📊 Grafico storico — nessuna serie attiva",
            ForeColor = Color.FromArgb(0, 180, 220),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(6, 10)
        };

        _cboMetric = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 140,
            Location      = new Point(440, 8),
            Font          = new Font("Segoe UI", 8f)
        };
        _cboMetric.Items.AddRange(new object[] { "Latenza (ms)", "↑ KB/s Upload", "↓ KB/s Download", "Loss %" });
        _cboMetric.SelectedIndex = 0;
        _cboMetric.SelectedIndexChanged += (_, _) => RefreshChart();

        _btnClear = new Button
        {
            Text      = "🗑  Cancella tutto",
            Location  = new Point(590, 7),
            Width     = 130, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(160, 40, 40),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        _btnClear.FlatAppearance.BorderSize = 0;
        _btnClear.Click += OnClearAll;

        // Pulsanti zoom
        var btnZoomIn = new Button
        {
            Text      = "🔍+",
            Location  = new Point(730, 7),
            Width     = 46, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 60, 100),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnZoomIn.FlatAppearance.BorderSize = 0;
        btnZoomIn.Click += (_, _) => { _zoomX = Math.Clamp(_zoomX * 1.4f, 0.1f, 16f); _canvas!.Invalidate(); };

        var btnZoomOut = new Button
        {
            Text      = "🔍−",
            Location  = new Point(778, 7),
            Width     = 46, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 60, 100),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnZoomOut.FlatAppearance.BorderSize = 0;
        btnZoomOut.Click += (_, _) => { _zoomX = Math.Clamp(_zoomX / 1.4f, 0.1f, 16f); _canvas!.Invalidate(); };

        var btnZoomReset = new Button
        {
            Text      = "⊙ Reset",
            Location  = new Point(826, 7),
            Width     = 62, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 70, 50),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnZoomReset.FlatAppearance.BorderSize = 0;
        btnZoomReset.Click += (_, _) => { _zoomX = 1f; _viewOffsetPx = 0; _zoomBoxVisible = false; _canvas!.Invalidate(); };

        // Screenshot
        var btnScreenshot = new Button
        {
            Text      = "📷 Screenshot",
            Location  = new Point(894, 7),
            Width     = 110, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 80, 80),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnScreenshot.FlatAppearance.BorderSize = 0;
        btnScreenshot.Click += OnScreenshot;

        // Salva CSV
        var btnSaveCsv = new Button
        {
            Text      = "💾 Salva CSV",
            Location  = new Point(1006, 7),
            Width     = 100, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 80, 40),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnSaveCsv.FlatAppearance.BorderSize = 0;
        btnSaveCsv.Click += OnSaveCsv;

        toolbar.Controls.Add(_lblInfo);
        toolbar.Controls.Add(new Label
        {
            Text      = "Metrica:",
            ForeColor = Color.Silver,
            AutoSize  = true,
            Location  = new Point(390, 12),
            Font      = new Font("Segoe UI", 8f)
        });
        toolbar.Controls.Add(_cboMetric);
        toolbar.Controls.Add(_btnClear);
        toolbar.Controls.Add(btnZoomIn);
        toolbar.Controls.Add(btnZoomOut);
        toolbar.Controls.Add(btnZoomReset);
        toolbar.Controls.Add(btnScreenshot);
        toolbar.Controls.Add(btnSaveCsv);

        // ===================================================================
        // SIDEBAR destra — lista serie
        // ===================================================================
        _sidebar = new Panel
        {
            Dock      = DockStyle.Right,
            Width     = 230,
            BackColor = Color.FromArgb(22, 26, 48)
        };

        var lblSeries = new Label
        {
            Text      = "SERIE ATTIVE",
            ForeColor = Color.FromArgb(0, 180, 220),
            Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(8, 8)
        };

        _seriesList = new CheckedListBox
        {
            Location         = new Point(4, 28),
            Size             = new Size(222, 0),   // Height impostato in OnSidebarResize
            BackColor        = Color.FromArgb(30, 36, 62),
            ForeColor        = Color.WhiteSmoke,
            Font             = new Font("Segoe UI", 8f),
            BorderStyle      = BorderStyle.None,
            CheckOnClick     = true,
            IntegralHeight   = false
        };
        _seriesList.ItemCheck            += OnSeriesItemCheck;
        _seriesList.SelectedIndexChanged += OnSeriesSelected;
        _seriesList.DoubleClick          += OnSeriesDoubleClick;

        var btnRemoveSeries = new Button
        {
            Text      = "✕  Rimuovi selezionata",
            Location  = new Point(4, 0),    // Y impostato in OnSidebarResize
            Width     = 222, Height = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 30, 30),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8f)
        };
        btnRemoveSeries.FlatAppearance.BorderSize = 0;
        btnRemoveSeries.Click += OnRemoveSelected;

        _lblHint = new Label
        {
            Text      = "Usa il menu contestuale\nnelle schede Monitor,\nAnalisi e Server per\naggiungereserie.",
            ForeColor = Color.FromArgb(100, 110, 140),
            Font      = new Font("Segoe UI", 7.5f),
            Location  = new Point(8, 0),    // Y impostato in OnSidebarResize
            Size      = new Size(214, 64)
        };

        _sidebar.Controls.Add(lblSeries);
        _sidebar.Controls.Add(_seriesList);
        _sidebar.Controls.Add(btnRemoveSeries);
        _sidebar.Controls.Add(_lblHint);
        _sidebar.SizeChanged += (_, _) => LayoutSidebar(btnRemoveSeries);

        // ===================================================================
        // CANVAS area grafico (double-buffered personalizzato)
        // ===================================================================
        _canvas = new ChartCanvas { Dock = DockStyle.Fill };
        _canvas.Paint       += OnChartPaint;
        _canvas.MouseMove   += OnChartMouseMove;
        _canvas.MouseLeave  += (_, _) => { _tooltipVisible = false; _dragging = false; _zoomBoxVisible = false; _canvas.Invalidate(); };
        _canvas.MouseWheel  += OnChartMouseWheel;
        _canvas.MouseDown   += OnChartMouseDown;
        _canvas.MouseUp     += OnChartMouseUp;

        _chartArea = new Panel { Dock = DockStyle.Fill };
        _chartArea.Controls.Add(_canvas);

        Controls.Add(_chartArea);
        Controls.Add(_sidebar);
        Controls.Add(toolbar);

        // ===================================================================
        // WIRING
        // ===================================================================
        _scanService.ScanCompleted       += OnScanCompleted;
        _graphLogger.SeriesChanged       += (_, _) => { if (IsHandleCreated) BeginInvoke(RebuildSeriesList); };

        RebuildSeriesList();
    }

    // -----------------------------------------------------------------------
    // Layout sidebar
    // -----------------------------------------------------------------------
    private void LayoutSidebar(Button btnRemoveSeries)
    {
        int h = _sidebar.Height;
        _seriesList.Location = new Point(4, 28);
        _seriesList.Size     = new Size(222, Math.Max(60, h - 28 - 30 - 70));
        int btnY = _seriesList.Bottom + 4;
        btnRemoveSeries.Location = new Point(4, btnY);
        _lblHint.Location        = new Point(8, btnY + 30);
    }

    // -----------------------------------------------------------------------
    // Ricostruzione lista serie
    // -----------------------------------------------------------------------
    private void RebuildSeriesList()
    {
        if (_panelDisposed) return;
        var active = _graphLogger.GetActiveSeries();

        _seriesList.ItemCheck -= OnSeriesItemCheck;
        _seriesList.Items.Clear();
        foreach (var key in active)
        {
            string label = _graphLogger.GetLabel(key);
            _seriesList.Items.Add(new SeriesItem(key, label), isChecked: true);
        }
        _seriesList.ItemCheck += OnSeriesItemCheck;

        bool hasSeries = active.Count > 0;
        _lblInfo.Text = hasSeries
            ? $"  📊 Grafico storico — {active.Count} serie attive"
            : "  📊 Grafico storico — nessuna serie attiva";

        _needsFullReload = true;  // forza rilettura SQLite per le nuove serie
        RefreshChart();
    }

    // -----------------------------------------------------------------------
    // Aggiornamento dati ad ogni scan
    // -----------------------------------------------------------------------
    private void OnScanCompleted(object? sender, List<TcpConnection> connections)
    {
        if (_panelDisposed) return;

        foreach (var key in _graphLogger.GetActiveSeries())
        {
            if (key.StartsWith("conn|", StringComparison.OrdinalIgnoreCase))
            {
                // "conn|ProcessName|RemoteIp|RemotePort"
                var parts = key.Split('|');
                if (parts.Length < 4) continue;
                string procName   = parts[1];
                string remoteIp   = parts[2];
                string remotePort = parts[3];

                var conn = connections.FirstOrDefault(c =>
                    string.Equals(c.ProcessName, procName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase) &&
                    c.RemotePort.ToString() == remotePort);

                if (conn == null) continue;

                double lat  = _latencyProbe.GetLatency(remoteIp);
                var (up, dn) = _bwTracker.Get(conn);
                double loss = _latencyProbe.GetLoss(remoteIp);
                _graphLogger.Record(key, lat, up, dn, loss);
                // Aggiorna la cache in memoria senza rileggere da SQLite
                AppendToCache(key, lat, up, dn, loss);
            }
            else if (key.StartsWith("proc|", StringComparison.OrdinalIgnoreCase))
            {
                // "proc|ProcessName"
                var parts = key.Split('|');
                if (parts.Length < 2) continue;
                string procName = parts[1];

                var matching = connections.Where(c =>
                    string.Equals(c.ProcessName, procName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count == 0) continue;

                double lat  = matching.Select(c => _latencyProbe.GetLatency(c.RemoteIp)).Where(v => v >= 0)
                                      .DefaultIfEmpty(-1).Average();
                double up   = matching.Sum(c => _bwTracker.Get(c).UpKbps);
                double dn   = matching.Sum(c => _bwTracker.Get(c).DownKbps);
                double loss = matching.Select(c => _latencyProbe.GetLoss(c.RemoteIp)).Where(v => v >= 0)
                                     .DefaultIfEmpty(-1).Average();
                _graphLogger.Record(key, lat, up, dn, loss);
                AppendToCache(key, lat, up, dn, loss);
            }
            // Le serie remote ("remote|…", "remoteproc|…") vengono alimentate da FeedRemoteSnapshot.
        }

        _canvas.Invalidate();  // ridisegna con i dati già in cache (no SQL read)
    }

    /// <summary>
    /// Registra un punto per una serie remota (chiamato da ServerPanel).
    /// </summary>
    public void RecordRemote(string seriesKey, double latency, double up, double dn, double loss)
    {
        _graphLogger.Record(seriesKey, latency, up, dn, loss);
    }

    /// <summary>
    /// Alimenta il grafico con uno snapshot ricevuto da un server remoto.
    /// Chiamato dal thread UI (via BeginInvoke in ServerPanel.OnSnapshotReceived).
    /// </summary>
    public void FeedRemoteSnapshot(string serverLabel, List<RemoteConnectionEntry> connections)
    {
        var activeSeries = _graphLogger.GetActiveSeries();

        foreach (var key in activeSeries)
        {
            if (key.StartsWith("remote|", StringComparison.OrdinalIgnoreCase))
            {
                // formato: remote|ServerLabel|ProcessName|RemoteIp|RemotePort
                var parts = key.Split('|');
                if (parts.Length < 5) continue;
                if (!string.Equals(parts[1], serverLabel, StringComparison.OrdinalIgnoreCase)) continue;

                string procName   = parts[2];
                string remoteIp   = parts[3];
                string remotePort = parts[4];

                var conn = connections.FirstOrDefault(c =>
                    string.Equals(c.ProcessName, procName,   StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.RemoteIp,    remoteIp,   StringComparison.OrdinalIgnoreCase) &&
                    c.RemotePort.ToString() == remotePort);

                if (conn == null) continue;
                _graphLogger.Record(key, conn.LatencyMs, conn.UpKbps, conn.DownKbps, conn.LossPercent);
                AppendToCache(key, conn.LatencyMs, conn.UpKbps, conn.DownKbps, conn.LossPercent);
            }
            else if (key.StartsWith("remoteproc|", StringComparison.OrdinalIgnoreCase))
            {
                // formato: remoteproc|ServerLabel|ProcessName
                var parts = key.Split('|');
                if (parts.Length < 3) continue;
                if (!string.Equals(parts[1], serverLabel, StringComparison.OrdinalIgnoreCase)) continue;

                string procName = parts[2];
                var matching = connections
                    .Where(c => string.Equals(c.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matching.Count == 0) continue;

                double lat  = matching.Select(c => c.LatencyMs).Where(v => v >= 0).DefaultIfEmpty(-1).Average();
                double loss = matching.Select(c => c.LossPercent).Where(v => v >= 0).DefaultIfEmpty(-1).Average();
                double up   = matching.Sum(c => c.UpKbps);
                double dn   = matching.Sum(c => c.DownKbps);
                _graphLogger.Record(key, lat, up, dn, loss);
                AppendToCache(key, lat, up, dn, loss);
            }
        }

        _canvas.Invalidate();  // ridisegna con i dati già in cache
    }

    // -----------------------------------------------------------------------
    // Refresh canvas
    // -----------------------------------------------------------------------

    /// <summary>
    /// Aggiorna la cache punti. Se _needsFullReload è true (cambio serie / avvio)
    /// rilegge tutto da SQLite; altrimenti usa i dati già in memoria e invalida solo il canvas.
    /// </summary>
    private void RefreshChart()
    {
        if (_panelDisposed) return;
        if (_needsFullReload)
        {
            _needsFullReload = false;
            _pointsCache.Clear();
            foreach (int i in Enumerable.Range(0, _seriesList.Items.Count))
            {
                if (!_seriesList.GetItemChecked(i)) continue;
                var item = (SeriesItem)_seriesList.Items[i];
                _pointsCache[item.Key] = _graphLogger.ReadPoints(item.Key, 600);
            }
        }
        _canvas.Invalidate();
    }

    /// <summary>
    /// Appende un nuovo punto direttamente alla cache in memoria, evitando la rilettura
    /// completa da SQLite ad ogni tick. La serie deve essere visibile (in _pointsCache).
    /// </summary>
    private void AppendToCache(string key, double lat, double up, double dn, double loss)
    {
        if (!_pointsCache.TryGetValue(key, out var list)) return;
        list.Add(new GraphLogger.ChartPoint(DateTime.Now, lat, up, dn, loss));
        // Mantieni al massimo 600 punti per non eccedere la memoria
        const int MaxPts = 600;
        if (list.Count > MaxPts) list.RemoveRange(0, list.Count - MaxPts);
    }

    // -----------------------------------------------------------------------
    // Rendering GDI+
    // -----------------------------------------------------------------------
    private void OnChartPaint(object? sender, PaintEventArgs e)
    {
        var g    = e.Graphics;
        var rect = _canvas.ClientRectangle;
        g.Clear(Color.FromArgb(18, 22, 40));

        if (_pointsCache.Count == 0)
        {
            DrawCenteredHint(g, rect, "Nessuna serie attiva — aggiungi connessioni dal menu contestuale");
            return;
        }

        // Margini area plot
        const int MarginL = 60, MarginR = 16, MarginT = 20, MarginB = 44;
        var plotRect = new Rectangle(MarginL, MarginT,
            rect.Width - MarginL - MarginR, rect.Height - MarginT - MarginB);
        if (plotRect.Width < 10 || plotRect.Height < 10) return;

        int metricIdx = _cboMetric.SelectedIndex;

        // Calcola range Y (tutti i valori di tutte le serie visibili)
        double yMax = 0;
        foreach (var kv in _pointsCache)
            foreach (var pt in kv.Value)
            {
                double v = MetricValue(pt, metricIdx);
                if (v >= 0 && v > yMax) yMax = v;
            }
        if (yMax < 1) yMax = 1;
        double yRange = yMax * 1.15;

        // Griglia
        DrawGrid(g, plotRect, yRange);

        // Assi
        using var axPen   = new Pen(Color.FromArgb(80, 90, 120));
        using var lblBrush = new SolidBrush(Color.FromArgb(140, 150, 175));
        var  lblFont = new Font("Segoe UI", 7f);
        g.DrawRectangle(axPen, plotRect);

        // Label asse Y
        for (int step = 0; step <= 4; step++)
        {
            double v   = yRange * (4 - step) / 4.0;
            int    yPx = plotRect.Top + (int)(plotRect.Height * step / 4.0);
            g.DrawString($"{v:F1}", lblFont, lblBrush, new PointF(2, yPx - 7));
        }

        // Numero totale punti per determinare escursione temporale
        int maxPts = _pointsCache.Values.Max(l => l.Count);
        if (maxPts == 0) { DrawCenteredHint(g, rect, "Nessun punto registrato — attendere il prossimo scan"); lblFont.Dispose(); return; }

        // Calcola scala X: quanti punti entrano nella vista
        float ptSpacing = Math.Max(1f, 2f * _zoomX);
        int   visiblePts = (int)(plotRect.Width / ptSpacing);

        // Label asse X (timestamps)
        if (_pointsCache.Values.FirstOrDefault(l => l.Count > 0) is { } refList && refList.Count > 0)
        {
            int xLabelCount = Math.Min(6, refList.Count);
            for (int li = 0; li < xLabelCount; li++)
            {
                int   ptIdx = refList.Count - 1 - (int)((double)li / (xLabelCount - 1) * (refList.Count - 1));
                ptIdx = Math.Max(0, Math.Min(ptIdx, refList.Count - 1));
                float xF  = plotRect.Left + plotRect.Width * (float)(refList.Count - 1 - ptIdx) / Math.Max(1, refList.Count - 1);
                // scrivi da destra a sinistra
                xF = plotRect.Right - (xF - plotRect.Left);
                string ts = refList[ptIdx].Ts.ToString("HH:mm:ss");
                g.DrawString(ts, lblFont, lblBrush, new PointF(Math.Max(plotRect.Left, xF - 22), plotRect.Bottom + 4));
            }
        }

        // Disegna curve (prima le non-highlight, poi le highlight in cima)
        int colorIdx = 0;
        var pairs = _pointsCache.Select((kv, i) => (kv.Key, kv.Value, i)).ToList();

        foreach (var (key, pts, idx) in pairs)
        {
            if (_highlightedKeys.Contains(key)) { colorIdx++; continue; }  // disegna dopo
            var col  = SeriesColors[colorIdx % SeriesColors.Length];
            colorIdx++;
            if (pts.Count < 2) continue;
            DrawCurve(g, plotRect, pts, metricIdx, yRange, ptSpacing, col, highlighted: false);
        }
        colorIdx = 0;
        foreach (var (key, pts, idx) in pairs)
        {
            var col  = SeriesColors[colorIdx % SeriesColors.Length];
            colorIdx++;
            if (!_highlightedKeys.Contains(key)) continue;  // già disegnata
            if (pts.Count < 2) continue;
            DrawCurve(g, plotRect, pts, metricIdx, yRange, ptSpacing, col, highlighted: true);
        }

        // Legenda
        DrawLegend(g, plotRect, metricIdx);

        // Zoom-box di selezione
        if (_zoomBoxVisible)
            DrawZoomBox(g, plotRect);

        // Tooltip
        if (_tooltipVisible && !_zoomBoxVisible)
            DrawTooltip(g, rect, plotRect, metricIdx, yRange, ptSpacing);

        lblFont.Dispose();
    }

    private void DrawGrid(Graphics g, Rectangle plotRect, double yRange)
    {
        using var gridPen = new Pen(Color.FromArgb(36, 44, 70));
        // Orizzontali
        for (int step = 1; step <= 4; step++)
        {
            int yPx = plotRect.Top + (int)(plotRect.Height * step / 4.0);
            g.DrawLine(gridPen, plotRect.Left, yPx, plotRect.Right, yPx);
        }
        // Verticali (ogni ~80 px)
        for (int x = plotRect.Left + 80; x < plotRect.Right; x += 80)
            g.DrawLine(gridPen, x, plotRect.Top, x, plotRect.Bottom);
    }

    private void DrawZoomBox(Graphics g, Rectangle plotRect)
    {
        int x1 = Math.Min(_dragStart.X, _dragEnd.X);
        int x2 = Math.Max(_dragStart.X, _dragEnd.X);
        x1 = Math.Max(x1, plotRect.Left);
        x2 = Math.Min(x2, plotRect.Right);
        if (x2 - x1 < 4) return;

        var zoomRect = new Rectangle(x1, plotRect.Top, x2 - x1, plotRect.Height);
        using var fill = new SolidBrush(Color.FromArgb(50, 0, 180, 220));
        g.FillRectangle(fill, zoomRect);
        using var border = new Pen(Color.FromArgb(180, 0, 200, 255));
        g.DrawRectangle(border, zoomRect);
        // etichette laterali
        using var fnt  = new Font("Segoe UI", 7.5f);
        using var lb   = new SolidBrush(Color.FromArgb(200, 0, 200, 255));
        g.DrawString("◀", fnt, lb, x1 - 10, plotRect.Top + 2);
        g.DrawString("▶", fnt, lb, x2 + 2,  plotRect.Top + 2);
    }

    private void DrawCurve(Graphics g, Rectangle plotRect,
                            List<GraphLogger.ChartPoint> pts, int metricIdx,
                            double yRange, float ptSpacing, Color col,
                            bool highlighted = false)
    {
        float lineW = highlighted ? 3f : 1.5f;
        using var pen = new Pen(col, lineW) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        if (highlighted)
        {
            // alone luminoso
            using var glow = new Pen(Color.FromArgb(80, col.R, col.G, col.B), lineW + 4f)
                { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            DrawCurvePath(g, glow, plotRect, pts, metricIdx, yRange, ptSpacing);
        }
        DrawCurvePath(g, pen, plotRect, pts, metricIdx, yRange, ptSpacing);
    }

    private static void DrawCurvePath(Graphics g, Pen pen, Rectangle plotRect,
                                       List<GraphLogger.ChartPoint> pts, int metricIdx,
                                       double yRange, float ptSpacing)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        bool first = true;
        float prevX = 0, prevY = 0;

        int startIdx = Math.Max(0, pts.Count - (int)(plotRect.Width / ptSpacing) - 1);
        for (int i = startIdx; i < pts.Count; i++)
        {
            double v = MetricValue(pts[i], metricIdx);
            if (v < 0) { first = true; continue; }

            float xF = plotRect.Right - (pts.Count - 1 - i) * ptSpacing;
            float yF = plotRect.Bottom - (float)(v / yRange * plotRect.Height);
            yF = Math.Clamp(yF, plotRect.Top, plotRect.Bottom);

            if (first) { first = false; prevX = xF; prevY = yF; continue; }
            path.AddLine(prevX, prevY, xF, yF);
            prevX = xF; prevY = yF;
        }
        g.DrawPath(pen, path);
        path.Dispose();
    }

    private void DrawLegend(Graphics g, Rectangle plotRect, int metricIdx)
    {
        var lblFont     = new Font("Segoe UI", 7.5f);
        var lblFontBold = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        int colorIdx = 0;
        int lx = plotRect.Left + 6;
        int ly = plotRect.Top  + 6;

        foreach (var kv in _pointsCache)
        {
            var col   = SeriesColors[colorIdx % SeriesColors.Length];
            colorIdx++;
            bool isHL = _highlightedKeys.Contains(kv.Key);
            string label = _graphLogger.GetLabel(kv.Key);
            if (label.Length > 28) label = label[..26] + "…";
            if (isHL) label = "★ " + label;

            using var brush = new SolidBrush(col);
            int rectH = isHL ? 12 : 10;
            g.FillRectangle(brush, lx, ly + 2, isHL ? 12 : 10, rectH);
            using var txtBrush = new SolidBrush(isHL ? Color.White : Color.FromArgb(210, 215, 230));
            g.DrawString(label, isHL ? lblFontBold : lblFont, txtBrush, lx + 16, ly);
            ly += 18;
        }
        lblFont.Dispose();
        lblFontBold.Dispose();
    }

    private void DrawTooltip(Graphics g, Rectangle full, Rectangle plotRect,
                              int metricIdx, double yRange, float ptSpacing)
    {
        Point mp = _tooltipPt;
        if (!plotRect.Contains(mp)) return;

        // Identifica il punto più vicino nella serie selezionata (o la prima)
        var pts = _selectedKey != null && _pointsCache.TryGetValue(_selectedKey, out var sp) ? sp
                : _pointsCache.Values.FirstOrDefault();
        if (pts == null || pts.Count == 0) return;

        // Indice punto sotto il cursore
        int idx = pts.Count - 1 - (int)((plotRect.Right - mp.X) / ptSpacing);
        idx = Math.Clamp(idx, 0, pts.Count - 1);
        var pt = pts[idx];

        string[] lines =
        {
            pt.Ts.ToString("HH:mm:ss"),
            $"Latenza : {(pt.Latency >= 0 ? $"{pt.Latency:F1} ms" : "—")}",
            $"↑ Upload: {pt.UpKbps:F2} KB/s",
            $"↓ Download: {pt.DownKbps:F2} KB/s",
            $"Loss    : {(pt.Loss    >= 0 ? $"{pt.Loss:F1} %" : "—")}",
        };

        var  fnt       = new Font("Segoe UI", 8f);
        int  lineH     = 16;
        int  padding   = 8;
        int  ttW       = 180;
        int  ttH       = lines.Length * lineH + padding * 2;
        int  ttX       = Math.Min(mp.X + 14, full.Right  - ttW - 4);
        int  ttY       = Math.Max(full.Top + 4, mp.Y - ttH / 2);

        g.FillRectangle(new SolidBrush(Color.FromArgb(220, 28, 34, 56)),
                        ttX, ttY, ttW, ttH);
        g.DrawRectangle(new Pen(Color.FromArgb(0, 180, 220)),
                        ttX, ttY, ttW, ttH);

        using var tb = new SolidBrush(Color.WhiteSmoke);
        for (int i = 0; i < lines.Length; i++)
            g.DrawString(lines[i], fnt, tb, ttX + padding, ttY + padding + i * lineH);

        // Linea verticale cursore
        using var cpPen = new Pen(Color.FromArgb(100, 0, 180, 220));
        g.DrawLine(cpPen, mp.X, plotRect.Top, mp.X, plotRect.Bottom);

        fnt.Dispose();
    }

    private static void DrawCenteredHint(Graphics g, Rectangle rect, string text)
    {
        using var f  = new Font("Segoe UI", 10f);
        using var b  = new SolidBrush(Color.FromArgb(80, 90, 120));
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, f, b, rect, sf);
    }

    // -----------------------------------------------------------------------
    // Calcola il valore metrica per il punto dato l'indice colonna
    // -----------------------------------------------------------------------
    private static double MetricValue(GraphLogger.ChartPoint pt, int idx) => idx switch
    {
        0 => pt.Latency,
        1 => pt.UpKbps,
        2 => pt.DownKbps,
        3 => pt.Loss,
        _ => 0
    };

    // -----------------------------------------------------------------------
    // Mouse
    // -----------------------------------------------------------------------
    private void OnChartMouseMove(object? sender, MouseEventArgs e)
    {
        _tooltipPt      = e.Location;
        _tooltipVisible = true;
        if (_dragging)
        {
            _dragEnd        = e.Location;
            _zoomBoxVisible = true;
        }
        _canvas.Invalidate();
    }

    private void OnChartMouseWheel(object? sender, MouseEventArgs e)
    {
        if (ModifierKeys.HasFlag(Keys.Control))
        {
            // Zoom centrato sulla posizione del cursore
            float delta = e.Delta > 0 ? 1.25f : 0.8f;
            _zoomX = Math.Clamp(_zoomX * delta, 0.1f, 16f);
        }
        else
        {
            // Scroll orizzontale
            _viewOffsetPx += e.Delta > 0 ? -40 : 40;
        }
        _canvas.Invalidate();
    }

    private void OnChartMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // Inizio drag per zoom-box
            _dragging       = true;
            _dragStart      = e.Location;
            _dragEnd        = e.Location;
            _zoomBoxVisible = false;
        }
        // Click sul canvas: seleziona la serie
        if (_seriesList.SelectedIndex >= 0)
            _selectedKey = ((SeriesItem)_seriesList.Items[_seriesList.SelectedIndex]).Key;
        _canvas.Invalidate();
    }

    private void OnChartMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        const int MarginL = 60, MarginR = 16, MarginT = 20, MarginB = 44;
        var plotRect = new Rectangle(MarginL, MarginT,
            _canvas.Width - MarginL - MarginR, _canvas.Height - MarginT - MarginB);

        int x1 = Math.Max(Math.Min(_dragStart.X, _dragEnd.X), plotRect.Left);
        int x2 = Math.Min(Math.Max(_dragStart.X, _dragEnd.X), plotRect.Right);
        int dragW = x2 - x1;

        if (dragW > 8 && plotRect.Width > 0)
        {
            // Zoom sul range selezionato: _zoomX scala in modo che la selezione riempia il canvas
            _zoomX = Math.Clamp(_zoomX * ((float)plotRect.Width / dragW), 0.1f, 16f);
        }
        _zoomBoxVisible = false;
        _canvas.Invalidate();
    }

    // -----------------------------------------------------------------------
    // Gestione lista serie
    // -----------------------------------------------------------------------
    private void OnSeriesItemCheck(object? sender, ItemCheckEventArgs e)
    {
        // Controlla/deseleziona visibilità senza rimuovere dal DB;
        // forza rilettura SQLite perché una serie potrebbe essere rientrata nel viewport
        _needsFullReload = true;
        BeginInvoke(RefreshChart);
    }

    private void OnSeriesSelected(object? sender, EventArgs e)
    {
        if (_seriesList.SelectedIndex >= 0)
            _selectedKey = ((SeriesItem)_seriesList.Items[_seriesList.SelectedIndex]).Key;
    }

    private void OnRemoveSelected(object? sender, EventArgs e)
    {
        if (_seriesList.SelectedIndex < 0) return;
        var item = (SeriesItem)_seriesList.Items[_seriesList.SelectedIndex];
        _graphLogger.RemoveSeries(item.Key);
        _highlightedKeys.Remove(item.Key);
        RebuildSeriesList();
    }

    private void OnSeriesDoubleClick(object? sender, EventArgs e)
    {
        // Double click → toggle evidenziazione della serie selezionata
        if (_seriesList.SelectedIndex < 0) return;
        var item = (SeriesItem)_seriesList.Items[_seriesList.SelectedIndex];
        if (!_highlightedKeys.Add(item.Key))
            _highlightedKeys.Remove(item.Key);
        _canvas.Invalidate();
    }

    private void OnClearAll(object? sender, EventArgs e)
    {
        if (MessageBox.Show(
                "Cancellare tutte le serie e tutti i dati storici del grafico?\n" +
                "L'operazione è irreversibile.",
                "Conferma cancellazione",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _graphLogger.ClearAll();
        _pointsCache.Clear();
        _selectedKey = null;
        _highlightedKeys.Clear();
        RebuildSeriesList();
    }

    // Screenshot del canvas → PNG
    private void OnScreenshot(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title            = "Salva screenshot grafico",
            Filter           = "PNG|*.png|JPEG|*.jpg",
            FileName         = $"NetManCat_grafico_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt       = "png",
            OverwritePrompt  = true
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        int w = _canvas.Width;
        int h = _canvas.Height;
        if (w <= 0 || h <= 0) return;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        _canvas.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));

        var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var fmt = ext == ".jpg" ? System.Drawing.Imaging.ImageFormat.Jpeg
                                 : System.Drawing.Imaging.ImageFormat.Png;
        bmp.Save(dlg.FileName, fmt);
        MessageBox.Show($"Screenshot salvato in:\n{dlg.FileName}", "Screenshot",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Esporta punti visibili in CSV
    private void OnSaveCsv(object? sender, EventArgs e)
    {
        if (_pointsCache.Count == 0)
        {
            MessageBox.Show("Nessun dato da esportare.", "Salva CSV",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Title           = "Esporta dati grafico in CSV",
            Filter          = "CSV|*.csv",
            FileName        = $"NetManCat_dati_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt      = "csv",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        using var sw = new System.IO.StreamWriter(dlg.FileName, append: false,
            encoding: System.Text.Encoding.UTF8);
        sw.WriteLine("Serie;Timestamp;Latenza_ms;Upload_KBps;Download_KBps;Loss_pct");
        foreach (var kv in _pointsCache)
        {
            string label = _graphLogger.GetLabel(kv.Key)
                .Replace(";", ",").Replace("\n", " ");
            foreach (var pt in kv.Value)
            {
                sw.WriteLine(
                    $"{label};" +
                    $"{pt.Ts:yyyy-MM-dd HH:mm:ss};" +
                    $"{(pt.Latency >= 0  ? pt.Latency.ToString("F2")  : "")};" +
                    $"{pt.UpKbps:F2};" +
                    $"{pt.DownKbps:F2};" +
                    $"{(pt.Loss >= 0 ? pt.Loss.ToString("F2") : "")}");
            }
        }
        MessageBox.Show($"Dati esportati in:\n{dlg.FileName}", "Salva CSV",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // -----------------------------------------------------------------------
    // API pubblica per i menu contestuali (MonitorPanel, AnalysisPanel, ServerPanel)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Aggiunge una connessione locale al grafico.
    /// </summary>
    public void AddConnection(TcpConnection conn)
    {
        string key   = $"conn|{conn.ProcessName}|{conn.RemoteIp}|{conn.RemotePort}";
        string label = $"{conn.ProcessName} → {conn.RemoteIp}:{conn.RemotePort}";
        _graphLogger.AddSeries(key, label);
        RebuildSeriesList();
    }

    /// <summary>
    /// Rimuove una connessione locale dal grafico.
    /// </summary>
    public void RemoveConnection(TcpConnection conn)
    {
        string key = $"conn|{conn.ProcessName}|{conn.RemoteIp}|{conn.RemotePort}";
        _graphLogger.RemoveSeries(key);
        RebuildSeriesList();
    }

    /// <summary>
    /// Aggiunge tutte le connessioni di un processo locale al grafico (serie processo aggregata).
    /// </summary>
    public void AddProcess(string processName)
    {
        string key   = $"proc|{processName}";
        string label = $"[proc] {processName}";
        _graphLogger.AddSeries(key, label);
        RebuildSeriesList();
    }

    /// <summary>
    /// Rimuove il processo locale dal grafico.
    /// </summary>
    public void RemoveProcess(string processName)
    {
        _graphLogger.RemoveSeries($"proc|{processName}");
        RebuildSeriesList();
    }

    /// <summary>
    /// Aggiunge una connessione remota (da ServerPanel snapshot).
    /// </summary>
    public void AddRemoteConnection(string serverLabel, RemoteConnectionEntry conn)
    {
        string key   = $"remote|{serverLabel}|{conn.ProcessName}|{conn.RemoteIp}|{conn.RemotePort}";
        string label = $"[{serverLabel}] {conn.ProcessName} → {conn.RemoteIp}:{conn.RemotePort}";
        _graphLogger.AddSeries(key, label);
        RebuildSeriesList();
    }

    /// <summary>
    /// Rimuove una connessione remota dal grafico.
    /// </summary>
    public void RemoveRemoteConnection(string serverLabel, RemoteConnectionEntry conn)
    {
        string key = $"remote|{serverLabel}|{conn.ProcessName}|{conn.RemoteIp}|{conn.RemotePort}";
        _graphLogger.RemoveSeries(key);
        RebuildSeriesList();
    }

    /// <summary>
    /// Aggiunge un processo remoto (aggregato) al grafico.
    /// </summary>
    public void AddRemoteProcess(string serverLabel, string processName)
    {
        string key   = $"remoteproc|{serverLabel}|{processName}";
        string label = $"[{serverLabel}][proc] {processName}";
        _graphLogger.AddSeries(key, label);
        RebuildSeriesList();
    }

    /// <summary>
    /// Rimuove un processo remoto dal grafico.
    /// </summary>
    public void RemoveRemoteProcess(string serverLabel, string processName)
    {
        _graphLogger.RemoveSeries($"remoteproc|{serverLabel}|{processName}");
        RebuildSeriesList();
    }

    // -----------------------------------------------------------------------
    // Dispose
    // -----------------------------------------------------------------------
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_panelDisposed)
        {
            _panelDisposed = true;
            _scanService.ScanCompleted -= OnScanCompleted;
        }
        base.Dispose(disposing);
    }

    // -----------------------------------------------------------------------
    // Tipi interni
    // -----------------------------------------------------------------------

    /// <summary>Elemento lista serie con chiave e label separati.</summary>
    private sealed class SeriesItem
    {
        public string Key   { get; }
        public string Label { get; }
        public SeriesItem(string key, string label) { Key = key; Label = label; }
        public override string ToString() => Label;
    }

    /// <summary>Panel con double-buffering attivato per il rendering GDI+ senza flickering.</summary>
    private sealed class ChartCanvas : Panel
    {
        public ChartCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint            |
                     ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }
    }
}


