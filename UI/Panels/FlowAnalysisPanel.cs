using NetManCat.Core;
using NetManCat.Models;
using System.Net.NetworkInformation;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello "Analisi Flusso":
///   - Traceroute ICMP hop-by-hop verso un IP/hostname
///   - Ping continuo con grafico latenza (120 campioni = 2 min)
///   - Metriche real-time: latenza, jitter, perdita pacchetti
///   - Connessioni TCP attive del sistema verso l'IP target
///   - Ricezione NetFlow v5/v9 / IPFIX / sFlow con statistiche live
///   - Rilevamento switch e router in rete (ARP + SNMP)
/// </summary>
public sealed class FlowAnalysisPanel : UserControl, IDisposable
{
    private readonly ConfigManager        _config;
    private readonly ScanService          _scanService;
    private readonly TraceRouteProbe      _tracer = new();
    private readonly NetFlowReceiver      _flowReceiver;
    private readonly NetworkDeviceScanner _devScanner;

    // --- Ping continuo ---
    private readonly System.Windows.Forms.Timer _pingTimer;
    private readonly System.Windows.Forms.Timer _flowTimer;
    private bool    _pingBusy;
    private int     _pingSent;
    private int     _pingLost;
    private double  _lastRtt   = -1;
    private double  _prevRtt   = -1;
    private double  _jitter    = 0;
    private string  _targetIp  = "";
    private bool    _disposed;

    private readonly object        _histLock = new();
    private readonly Queue<double> _latHistory = new(121);

    // --- UI ---
    private readonly TextBox          _txtTarget;
    private readonly Button           _btnTrace;
    private readonly Button           _btnStop;
    private readonly CheckBox         _chkPing;
    private readonly Label            _lblStatus;
    private readonly FlowMetricsGrid  _gridMetrics;
    private readonly FlowGrid         _gridTrace;
    private readonly FlowGrid         _gridConns;
    private readonly LatencyChart     _chart;

    // --- NetFlow UI ---
    private readonly FlowGrid  _gridFlows;
    private readonly Label     _lblNfNetFlow;
    private readonly Label     _lblNfSFlow;
    private readonly Label     _lblNfIpfix;
    private readonly Button    _btnNfStart;
    private readonly Button    _btnNfStop;
    private ulong _totalFlowBytes;
    private ulong _totalFlowPkts;

    // --- Devices UI ---
    private readonly FlowGrid  _gridDevices;
    private readonly Button    _btnScanDevices;
    private readonly CheckBox  _chkSnmp;
    private readonly Label     _lblDevicesStatus;

    // --- Colori ---
    private static readonly Color HdrTrace   = Color.FromArgb(70,  45, 120);
    private static readonly Color HdrMetrics = Color.FromArgb(30,  80, 140);
    private static readonly Color HdrConns   = Color.FromArgb(40,  90,  50);
    private static readonly Color HdrFlow    = Color.FromArgb(20,  90, 110);
    private static readonly Color HdrDevices = Color.FromArgb(80,  55,  10);
    private static readonly Color ColOk      = Color.FromArgb(20, 130,  20);
    private static readonly Color ColWarn    = Color.FromArgb(170, 100,  0);
    private static readonly Color ColBad     = Color.FromArgb(180,  20,  20);
    private static readonly Color ColNone    = Color.FromArgb(140, 140, 140);

    public FlowAnalysisPanel(ConfigManager config, ScanService scanService,
                              NetFlowReceiver flowReceiver, NetworkDeviceScanner devScanner)
    {
        _config       = config;
        _scanService  = scanService;
        _flowReceiver = flowReceiver;
        _devScanner   = devScanner;

        _pingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pingTimer.Tick += OnPingTick;

        _flowTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _flowTimer.Tick += OnFlowTimerTick;

        // ===================================================================
        // HEADER BAR
        // ===================================================================
        var lblIp = new Label
        {
            Text     = "IP / Host:",
            AutoSize = true,
            Location = new Point(8, 13),
            Font     = new Font(Font.FontFamily, 8.5f)
        };

        _txtTarget = new TextBox
        {
            Location        = new Point(78, 9),
            Width           = 220,
            Font            = new Font(Font.FontFamily, 9f),
            PlaceholderText = "es. 8.8.8.8 oppure hostname"
        };

        _btnTrace = new Button
        {
            Text      = "▶  Traceroute",
            Location  = new Point(308, 7),
            Width     = 120,
            Height    = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 100, 170),
            ForeColor = Color.White,
            Font      = new Font(Font.FontFamily, 8.5f, FontStyle.Bold)
        };

        _btnStop = new Button
        {
            Text      = "⏹  Stop",
            Location  = new Point(436, 7),
            Width     = 86,
            Height    = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(150, 40, 40),
            ForeColor = Color.White,
            Font      = new Font(Font.FontFamily, 8.5f),
            Enabled   = false
        };

        _chkPing = new CheckBox
        {
            Text       = "Ping continuo",
            AutoSize   = true,
            Location   = new Point(532, 11),
            Font       = new Font(Font.FontFamily, 8.5f),
            Appearance = Appearance.Button,
            FlatStyle  = FlatStyle.Flat
        };

        _lblStatus = new Label
        {
            Text      = "Inserisci un indirizzo IP / hostname e usa ▶ per il traceroute o attiva il Ping continuo.",
            AutoSize  = true,
            Location  = new Point(660, 13),
            ForeColor = Color.Gray,
            Font      = new Font(Font.FontFamily, 7.5f)
        };

        _btnTrace.Click             += OnStartTrace;
        _btnStop.Click              += OnStopAll;
        _chkPing.CheckedChanged     += OnPingToggle;
        _txtTarget.KeyDown          += (_, e) =>
            { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnStartTrace(null, EventArgs.Empty); } };

        var headerPanel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 42,
            BackColor = Color.FromArgb(242, 246, 255)
        };
        headerPanel.Controls.AddRange(new Control[]
            { lblIp, _txtTarget, _btnTrace, _btnStop, _chkPing, _lblStatus });

        // ===================================================================
        // SPLIT CONTAINER  (pannello sinistro = metriche, destra = traceroute)
        // ===================================================================
        var split = new SplitContainer
        {
            Dock        = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor   = Color.FromArgb(200, 210, 230)
        };
        // Tutte le proprietà che internamente toccano SplitterDistance vengono
        // impostate in modo differito: il Layout event può scattare più volte
        // durante la costruzione con Width=0, causando InvalidOperationException.
        // - Usiamo un delegate con nome per poterlo deregistrare correttamente
        //   (sc.Layout -= null è un no-op in C#).
        // - Saltiamo le iterazioni con Width troppo piccola.
        // - try/catch per sicurezza nel caso vengano raggiunti limit particolari.
        LayoutEventHandler? splitLayoutOnce = null;
        splitLayoutOnce = (s, _) =>
        {
            var sc = (SplitContainer)s!;
            if (sc.Width < 120) return;          // dimensioni non ancora reali
            sc.Layout -= splitLayoutOnce;
            try { sc.SplitterWidth = 5;   } catch { }
            try { sc.Panel1MinSize = 260; } catch { }
            try { sc.Panel2MinSize = 300; } catch { }
            try
            {
                sc.SplitterDistance = Math.Clamp(
                    (int)(sc.Width * 0.55), 260, sc.Width - 300 - sc.SplitterWidth);
            }
            catch { }
        };
        split.Layout += splitLayoutOnce;

        // ===================================================================
        // PANNELLO SINISTRO  (metriche + chart latenza + connessioni attive)
        // ===================================================================
        var tblLeft = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 5
        };
        tblLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tblLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // 0 – lblMetrics
        tblLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));  // 1 – gridMetrics
        tblLeft.RowStyles.Add(new RowStyle(SizeType.Percent,  100));  // 2 – chart (fill)
        tblLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 3 – lblConns
        tblLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));  // 4 – gridConns

        var lblMetrics = MakeHdrLabel("METRICHE IN TEMPO REALE", HdrMetrics);

        _gridMetrics = new FlowMetricsGrid
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
        StyleHdr(_gridMetrics, HdrMetrics);
        _gridMetrics.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Metric", HeaderText = "Metrica",        FillWeight = 42 },
            new DataGridViewTextBoxColumn { Name = "Value",  HeaderText = "Valore",         FillWeight = 28 },
            new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Stato",          FillWeight = 30 }
        });
        // Pre-popola le righe fisse
        _gridMetrics.Rows.Add("Latenza RTT",      "—", "—");
        _gridMetrics.Rows.Add("Jitter",           "—", "—");
        _gridMetrics.Rows.Add("Perdita pacchetti","—", "—");
        _gridMetrics.Rows.Add("Connessioni TCP",  "—", "—");

        _chart = new LatencyChart { Dock = DockStyle.Fill };

        var lblConns = MakeHdrLabel("CONNESSIONI ATTIVE VERSO IP TARGET", HdrConns);

        _gridConns = new FlowGrid
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
        StyleHdr(_gridConns, HdrConns);
        _gridConns.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Pid",    HeaderText = "PID",      FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "Proc",   HeaderText = "Processo", FillWeight = 28 },
            new DataGridViewTextBoxColumn { Name = "Local",  HeaderText = "Locale",   FillWeight = 32 },
            new DataGridViewTextBoxColumn { Name = "State",  HeaderText = "Stato",    FillWeight = 28 }
        });

        tblLeft.Controls.Add(lblMetrics,  0, 0);
        tblLeft.Controls.Add(_gridMetrics,0, 1);
        tblLeft.Controls.Add(_chart,      0, 2);
        tblLeft.Controls.Add(lblConns,    0, 3);
        tblLeft.Controls.Add(_gridConns,  0, 4);

        split.Panel1.Controls.Add(tblLeft);

        // ===================================================================
        // PANNELLO DESTRO  (traceroute)
        // ===================================================================
        _lblTraceHeader     = MakeHdrLabel("TRACEROUTE", HdrTrace);
        _lblTraceHeader.Dock = DockStyle.Top;

        _gridTrace = new FlowGrid
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
        StyleHdr(_gridTrace, HdrTrace);
        _gridTrace.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Hop",  HeaderText = "#",        FillWeight =  5, MinimumWidth = 30 },
            new DataGridViewTextBoxColumn { Name = "Ip",   HeaderText = "IP",       FillWeight = 22 },
            new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Hostname", FillWeight = 27 },
            new DataGridViewTextBoxColumn { Name = "Rtt1", HeaderText = "ms 1",     FillWeight =  9 },
            new DataGridViewTextBoxColumn { Name = "Rtt2", HeaderText = "ms 2",     FillWeight =  9 },
            new DataGridViewTextBoxColumn { Name = "Rtt3", HeaderText = "ms 3",     FillWeight =  9 },
            new DataGridViewTextBoxColumn { Name = "Avg",  HeaderText = "Media",    FillWeight = 11 },
            new DataGridViewTextBoxColumn { Name = "Loss", HeaderText = "Loss%",    FillWeight =  8 }
        });

        split.Panel2.Controls.Add(_gridTrace);
        split.Panel2.Controls.Add(_lblTraceHeader);

        // ===================================================================
        // SEZIONE NETFLOW / SFLOW / IPFIX
        // ===================================================================
        var pnlFlow = new Panel { Dock = DockStyle.Fill };

        var flowHdrBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Color.FromArgb(230, 245, 248)
        };

        var lblNfTitle = new Label
        {
            Text      = "NetFlow v5/v9  |  IPFIX  |  sFlow  — porta di ascolto:",
            AutoSize  = true,
            Location  = new Point(6, 10),
            Font      = new Font(Font.FontFamily, 8f)
        };

        _lblNfNetFlow = new Label
        {
            Text      = "NetFlow: ⬤ off",
            AutoSize  = true,
            Location  = new Point(330, 10),
            Font      = new Font(Font.FontFamily, 8f),
            ForeColor = Color.Gray
        };
        _lblNfSFlow = new Label
        {
            Text      = "sFlow: ⬤ off",
            AutoSize  = true,
            Location  = new Point(450, 10),
            Font      = new Font(Font.FontFamily, 8f),
            ForeColor = Color.Gray
        };
        _lblNfIpfix = new Label
        {
            Text      = "IPFIX: ⬤ off",
            AutoSize  = true,
            Location  = new Point(545, 10),
            Font      = new Font(Font.FontFamily, 8f),
            ForeColor = Color.Gray
        };

        _btnNfStart = new Button
        {
            Text      = "▶ Avvia",
            Location  = new Point(648, 6),
            Width     = 76,
            Height    = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(20, 100, 160),
            ForeColor = Color.White,
            Font      = new Font(Font.FontFamily, 8f, FontStyle.Bold)
        };
        _btnNfStop = new Button
        {
            Text      = "⏹ Stop",
            Location  = new Point(730, 6),
            Width     = 76,
            Height    = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(140, 40, 40),
            ForeColor = Color.White,
            Font      = new Font(Font.FontFamily, 8f),
            Enabled   = false
        };
        _btnNfStart.Click += OnNfStart;
        _btnNfStop.Click  += OnNfStop;

        flowHdrBar.Controls.AddRange(new Control[]
            { lblNfTitle, _lblNfNetFlow, _lblNfSFlow, _lblNfIpfix, _btnNfStart, _btnNfStop });

        var lblFlowHdr = MakeHdrLabel("FLUSSI RICEVUTI  (ultimi 500)", HdrFlow);
        lblFlowHdr.Dock = DockStyle.Top;

        _gridFlows = new FlowGrid
        {
            Dock                        = DockStyle.Fill,
            ReadOnly                    = true,
            AllowUserToAddRows          = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            RowHeadersVisible           = false,
            AllowUserToResizeRows       = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor             = Color.FromArgb(245, 252, 255),
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle                 = BorderStyle.None,
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles   = false
        };
        StyleHdr(_gridFlows, HdrFlow);
        _gridFlows.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Proto",    HeaderText = "Protocollo", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Exporter", HeaderText = "Exporter",   FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "Src",      HeaderText = "Src IP",      FillWeight = 16 },
            new DataGridViewTextBoxColumn { Name = "SrcPort",  HeaderText = "S.Port",      FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "Dst",      HeaderText = "Dst IP",      FillWeight = 16 },
            new DataGridViewTextBoxColumn { Name = "DstPort",  HeaderText = "D.Port",      FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "IpProto",  HeaderText = "L4",          FillWeight =  7 },
            new DataGridViewTextBoxColumn { Name = "Bytes",    HeaderText = "Byte",        FillWeight = 11 },
            new DataGridViewTextBoxColumn { Name = "Pkts",     HeaderText = "Pacchetti",   FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Time",     HeaderText = "Ora",         FillWeight = 10 }
        });

        pnlFlow.Controls.Add(_gridFlows);
        pnlFlow.Controls.Add(lblFlowHdr);
        pnlFlow.Controls.Add(flowHdrBar);

        // ===================================================================
        // SEZIONE DISPOSITIVI DI RETE (SWITCH / ROUTER)
        // ===================================================================
        var pnlDevices = new Panel { Dock = DockStyle.Fill };

        var devHdrBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Color.FromArgb(252, 248, 235)
        };

        _lblDevicesStatus = new Label
        {
            Text      = "Premi \"Scansiona\" per rilevare switch e router in rete.",
            AutoSize  = true,
            Location  = new Point(6, 10),
            Font      = new Font(Font.FontFamily, 8f),
            ForeColor = Color.FromArgb(80, 60, 10)
        };
        _btnScanDevices = new Button
        {
            Text      = "🔍 Scansiona",
            Location  = new Point(Width - 110, 5),
            Width     = 104,
            Height    = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(140, 100, 0),
            ForeColor = Color.White,
            Font      = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right
        };
        _chkSnmp = new CheckBox
        {
            Text      = "SNMP",
            AutoSize  = true,
            Checked   = false,
            Location  = new Point(Width - 215, 9),
            Font      = new Font(Font.FontFamily, 8.5f),
            ForeColor = Color.FromArgb(80, 55, 10),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnScanDevices.Click += OnScanDevices;
        devHdrBar.Controls.AddRange(new Control[] { _lblDevicesStatus, _chkSnmp, _btnScanDevices });

        var lblDevHdr = MakeHdrLabel("SWITCH / ROUTER RILEVATI IN RETE", HdrDevices);
        lblDevHdr.Dock = DockStyle.Top;

        _gridDevices = new FlowGrid
        {
            Dock                        = DockStyle.Fill,
            ReadOnly                    = true,
            AllowUserToAddRows          = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            RowHeadersVisible           = false,
            AllowUserToResizeRows       = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor             = Color.FromArgb(255, 252, 240),
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle                 = BorderStyle.None,
            CellBorderStyle             = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles   = false
        };
        StyleHdr(_gridDevices, HdrDevices);
        _gridDevices.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "Icon",       HeaderText = "",              FillWeight =  4 },
            new DataGridViewTextBoxColumn { Name = "Tipo",       HeaderText = "Tipo",          FillWeight =  8 },
            new DataGridViewTextBoxColumn { Name = "IP",         HeaderText = "IP",            FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "MAC",        HeaderText = "MAC",           FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "Host",       HeaderText = "Hostname / Nome", FillWeight = 18 },
            new DataGridViewTextBoxColumn { Name = "SysDescr",   HeaderText = "Descrizione SNMP", FillWeight = 24 },
            new DataGridViewTextBoxColumn { Name = "Uptime",     HeaderText = "Uptime",        FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "Ifaces",     HeaderText = "Interfacce",    FillWeight =  7 },
            new DataGridViewTextBoxColumn { Name = "Source",     HeaderText = "Fonte",         FillWeight =  8 }
        });

        pnlDevices.Controls.Add(_gridDevices);
        pnlDevices.Controls.Add(lblDevHdr);
        pnlDevices.Controls.Add(devHdrBar);

        // ===================================================================
        // SPLIT VERTICALE per le due nuove sezioni (flow | devices)
        // ===================================================================
        var splitBottom = new SplitContainer
        {
            Dock        = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor   = Color.FromArgb(200, 210, 230)
        };
        LayoutEventHandler? sbLayoutOnce = null;
        sbLayoutOnce = (s, _) =>
        {
            var sc = (SplitContainer)s!;
            if (sc.Width < 120) return;
            sc.Layout -= sbLayoutOnce;
            try { sc.SplitterWidth = 5;   } catch { }
            try { sc.Panel1MinSize = 300;  } catch { }
            try { sc.Panel2MinSize = 260;  } catch { }
            try
            {
                sc.SplitterDistance = Math.Clamp(
                    (int)(sc.Width * 0.60), 300, sc.Width - 260 - sc.SplitterWidth);
            }
            catch { }
        };
        splitBottom.Layout += sbLayoutOnce;
        splitBottom.Panel1.Controls.Add(pnlFlow);
        splitBottom.Panel2.Controls.Add(pnlDevices);

        // ===================================================================
        // SPLIT ORIZZONTALE principale (sopra=trace+metriche, sotto=flow+devices)
        // ===================================================================
        var splitMain = new SplitContainer
        {
            Dock        = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor   = Color.FromArgb(200, 210, 230)
        };
        LayoutEventHandler? smLayoutOnce = null;
        smLayoutOnce = (s, _) =>
        {
            var sc = (SplitContainer)s!;
            if (sc.Height < 100) return;
            sc.Layout -= smLayoutOnce;
            try { sc.SplitterWidth = 5;   } catch { }
            try { sc.Panel1MinSize = 260;  } catch { }
            try { sc.Panel2MinSize = 200;  } catch { }
            try
            {
                sc.SplitterDistance = Math.Clamp(
                    (int)(sc.Height * 0.55), 260, sc.Height - 200 - sc.SplitterWidth);
            }
            catch { }
        };
        splitMain.Layout += smLayoutOnce;
        splitMain.Panel1.Controls.Add(split);        // split traceroute/metriche già costruito
        splitMain.Panel2.Controls.Add(splitBottom);  // flow + devices

        // ===================================================================
        // LAYOUT FORM
        // ===================================================================
        Controls.Add(splitMain);
        Controls.Add(headerPanel);

        // ===================================================================
        // WIRING
        // ===================================================================
        _tracer.HopDiscovered  += OnHopDiscovered;
        _tracer.TraceCompleted += OnTraceCompleted;
        _tracer.TraceFailed    += OnTraceFailed;
        _scanService.ScanCompleted += OnScanCompleted;
        _devScanner.DeviceUpdated  += OnDeviceUpdated;
        _flowReceiver.FlowArrived  += OnFlowArrived;
    }

    // Usato nel costruttore — deve essere campo per accesso fuori lambda
    private readonly Label _lblTraceHeader;

    // -----------------------------------------------------------------------
    // TRACEROUTE
    // -----------------------------------------------------------------------

    private void OnStartTrace(object? sender, EventArgs e)
    {
        string ip = _txtTarget.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;

        _targetIp = ip;
        _gridTrace.Rows.Clear();
        _lblTraceHeader.Text = $"TRACEROUTE  →  {ip}";

        SetStatus($"Traceroute verso {ip}…", HdrTrace);
        _btnTrace.Enabled = false;
        _btnStop.Enabled  = true;

        _tracer.Start(ip);
    }

    private void OnStopAll(object? sender, EventArgs e)
    {
        _tracer.Stop();
        if (_chkPing.Checked) _chkPing.Checked = false;
        _btnTrace.Enabled = true;
        _btnStop.Enabled  = false;
        SetStatus("Fermato.", Color.Gray);
    }

    private void OnHopDiscovered(object? sender, HopResult hop)
    {
        if (InvokeRequired) { BeginInvoke(() => OnHopDiscovered(sender, hop)); return; }
        AddOrUpdateHop(hop);
    }

    private void OnTraceCompleted(object? sender, List<HopResult> hops)
    {
        if (InvokeRequired) { BeginInvoke(() => OnTraceCompleted(sender, hops)); return; }
        SetStatus($"Traceroute completato — {hops.Count} hop.", ColOk);
        _btnTrace.Enabled = true;
        _btnStop.Enabled  = false;
    }

    private void OnTraceFailed(object? sender, string error)
    {
        if (InvokeRequired) { BeginInvoke(() => OnTraceFailed(sender, error)); return; }
        SetStatus($"Errore: {error}", ColBad);
        _btnTrace.Enabled = true;
        _btnStop.Enabled  = false;
    }

    private void AddOrUpdateHop(HopResult hop)
    {
        string hopStr = hop.HopNumber.ToString();

        // Cerca riga esistente con stesso numero hop
        foreach (DataGridViewRow row in _gridTrace.Rows)
        {
            if (row.Cells["Hop"].Value?.ToString() == hopStr)
            {
                FillHopRow(row, hop);
                return;
            }
        }

        // Aggiungi nuova riga
        int idx = _gridTrace.Rows.Add(
            hop.HopNumber,
            hop.IpAddress,
            hop.Hostname,
            RttStr(hop.Rtt1),
            RttStr(hop.Rtt2),
            RttStr(hop.Rtt3),
            RttStr(hop.AvgRtt),
            hop.IsTimeout ? "***" : $"{hop.Loss}%");

        ApplyHopStyle(_gridTrace.Rows[idx], hop);
    }

    private void FillHopRow(DataGridViewRow row, HopResult hop)
    {
        row.Cells["Ip"].Value   = hop.IpAddress;
        row.Cells["Host"].Value = hop.Hostname;
        row.Cells["Rtt1"].Value = RttStr(hop.Rtt1);
        row.Cells["Rtt2"].Value = RttStr(hop.Rtt2);
        row.Cells["Rtt3"].Value = RttStr(hop.Rtt3);
        row.Cells["Avg"].Value  = RttStr(hop.AvgRtt);
        row.Cells["Loss"].Value = hop.IsTimeout ? "***" : $"{hop.Loss}%";
        ApplyHopStyle(row, hop);
    }

    private void ApplyHopStyle(DataGridViewRow row, HopResult hop)
    {
        double thr = _config.Config.AlertThresholds.LatencyMs;

        Color bg = hop.IsTimeout
            ? Color.FromArgb(252, 246, 210)
            : hop.AvgRtt > thr * 2.0 ? Color.FromArgb(255, 228, 228)
            : hop.AvgRtt > thr       ? Color.FromArgb(255, 248, 210)
            :                          Color.FromArgb(228, 255, 228);

        row.DefaultCellStyle.BackColor = bg;

        if (!hop.IsTimeout && hop.AvgRtt >= 0)
        {
            Color fg = hop.AvgRtt > thr * 2.0 ? ColBad
                     : hop.AvgRtt > thr        ? ColWarn
                     : ColOk;
            row.Cells["Avg"].Style.ForeColor = fg;
            row.Cells["Avg"].Style.Font      = new Font(_gridTrace.Font, FontStyle.Bold);
        }

        if (hop.IsTimeout)
        {
            row.DefaultCellStyle.ForeColor = Color.FromArgb(160, 140, 60);
        }
    }

    private static string RttStr(double ms) => ms < 0 ? "*" : $"{ms:F0}";

    // -----------------------------------------------------------------------
    // PING CONTINUO
    // -----------------------------------------------------------------------

    private void OnPingToggle(object? sender, EventArgs e)
    {
        if (_chkPing.Checked)
        {
            string ip = _txtTarget.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                _chkPing.Checked = false;
                return;
            }
            _targetIp    = ip;
            _pingSent    = 0;
            _pingLost    = 0;
            _prevRtt     = -1;
            _lastRtt     = -1;
            _jitter      = 0;
            lock (_histLock) _latHistory.Clear();
            _pingTimer.Start();
            SetStatus($"Ping continuo verso {ip}…  (ogni 1 s)", HdrMetrics);
        }
        else
        {
            _pingTimer.Stop();
            SetStatus("Ping fermato.", Color.Gray);
        }
    }

    private async void OnPingTick(object? sender, EventArgs e)
    {
        if (_pingBusy || string.IsNullOrEmpty(_targetIp)) return;
        _pingBusy = true;
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(_targetIp, 1500);

            _pingSent++;
            if (reply.Status == IPStatus.Success)
            {
                double rtt = reply.RoundtripTime;
                _lastRtt = rtt;
                if (_prevRtt >= 0)
                    _jitter = _jitter * 0.75 + Math.Abs(rtt - _prevRtt) * 0.25;
                _prevRtt = rtt;
                lock (_histLock)
                {
                    if (_latHistory.Count >= 120) _latHistory.Dequeue();
                    _latHistory.Enqueue(rtt);
                }
            }
            else
            {
                _pingLost++;
                lock (_histLock)
                {
                    if (_latHistory.Count >= 120) _latHistory.Dequeue();
                    _latHistory.Enqueue(-1);
                }
            }
        }
        catch
        {
            _pingLost++;
        }
        finally
        {
            _pingBusy = false;
        }

        RefreshMetrics();
    }

    private void RefreshMetrics()
    {
        double thr  = _config.Config.AlertThresholds.LatencyMs;
        double jThr = _config.Config.AlertThresholds.JitterMs;
        double lThr = _config.Config.AlertThresholds.LossPercent;
        int loss    = _pingSent > 0 ? _pingLost * 100 / _pingSent : 0;

        // Riga 0 – Latenza
        string latVal    = _lastRtt >= 0 ? $"{_lastRtt:F1} ms" : "—";
        string latStatus = _lastRtt < 0  ? "—"
                         : _lastRtt > thr ? "⚠ ALTA" : "✓ OK";
        Color  latColor  = _lastRtt < 0  ? ColNone
                         : _lastRtt > thr ? ColBad : ColOk;
        SetRow(0, latVal, latStatus, latColor);

        // Riga 1 – Jitter
        string jitVal    = _jitter > 0 ? $"{_jitter:F1} ms" : "—";
        string jitStatus = _jitter <= 0 ? "—"
                         : _jitter > jThr ? "⚠ ALTO" : "✓ OK";
        Color  jitColor  = _jitter <= 0 ? ColNone
                         : _jitter > jThr ? ColWarn : ColOk;
        SetRow(1, jitVal, jitStatus, jitColor);

        // Riga 2 – Perdita
        string losVal    = _pingSent > 0 ? $"{loss}%"   : "—";
        string losStatus = _pingSent == 0 ? "—"
                         : loss > (int)lThr ? "⚠ ALTA" : "✓ OK";
        Color  losColor  = _pingSent == 0 ? ColNone
                         : loss > (int)lThr ? ColBad : ColOk;
        SetRow(2, losVal, losStatus, losColor);

        // Aggiorna il grafico
        double[] snap;
        lock (_histLock) snap = _latHistory.ToArray();
        _chart.UpdateData(snap, thr);
    }

    private void SetRow(int idx, string val, string status, Color statusColor)
    {
        if (idx >= _gridMetrics.Rows.Count) return;
        _gridMetrics.Rows[idx].Cells["Value"].Value                = val;
        _gridMetrics.Rows[idx].Cells["Status"].Value               = status;
        _gridMetrics.Rows[idx].Cells["Status"].Style.ForeColor     = statusColor;
        _gridMetrics.Rows[idx].Cells["Status"].Style.Font          =
            statusColor == ColOk || statusColor == ColBad || statusColor == ColWarn
            ? new Font(_gridMetrics.Font, FontStyle.Bold)
            : _gridMetrics.Font;
    }

    // -----------------------------------------------------------------------
    // CONNESSIONI ATTIVE (da ScanService)
    // -----------------------------------------------------------------------

    private void OnScanCompleted(object? sender, List<TcpConnection> conns)
    {
        if (_disposed || string.IsNullOrEmpty(_targetIp)) return;

        var matching = conns
            .Where(c => c.RemoteIp.Equals(_targetIp, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Aggiorna metrica connessioni (riga 3)
        if (_gridMetrics.Rows.Count > 3)
        {
            _gridMetrics.Rows[3].Cells["Value"].Value  = matching.Count.ToString();
            _gridMetrics.Rows[3].Cells["Status"].Value =
                matching.Count > 0 ? "✓ attive" : "nessuna";
            _gridMetrics.Rows[3].Cells["Status"].Style.ForeColor =
                matching.Count > 0 ? ColOk : ColNone;
        }

        // Aggiorna griglia connessioni
        _gridConns.SuspendLayout();
        _gridConns.Rows.Clear();
        foreach (var c in matching)
        {
            string state = c.State switch
            {
                Core.TcpState.Established => "ESTABLISHED",
                Core.TcpState.TimeWait    => "TIME_WAIT",
                Core.TcpState.CloseWait   => "CLOSE_WAIT",
                _                         => c.State.ToString().ToUpperInvariant()
            };
            _gridConns.Rows.Add(c.Pid, c.ProcessName,
                                $"{c.LocalIp}:{c.LocalPort}", state);
        }
        _gridConns.ResumeLayout();
    }

    // -----------------------------------------------------------------------
    // NETFLOW / SFLOW / IPFIX
    // -----------------------------------------------------------------------

    private void OnNfStart(object? sender, EventArgs e)
    {
        _flowReceiver.StartAll();
        _flowTimer.Start();
        _btnNfStart.Enabled = false;
        _btnNfStop.Enabled  = true;
        UpdateFlowStatusLabels();
    }

    private void OnNfStop(object? sender, EventArgs e)
    {
        _flowReceiver.StopAll();
        _flowTimer.Stop();
        _btnNfStart.Enabled = true;
        _btnNfStop.Enabled  = false;
        UpdateFlowStatusLabels();
    }

    private void UpdateFlowStatusLabels()
    {
        _lblNfNetFlow.Text      = $"NetFlow: {(_flowReceiver.NetFlowListening ? "⬤ on" : "⬤ off")}  ({_flowReceiver.NetFlowPackets} pkt)";
        _lblNfNetFlow.ForeColor = _flowReceiver.NetFlowListening ? Color.FromArgb(20,130,20) : Color.Gray;
        _lblNfSFlow.Text        = $"sFlow: {(_flowReceiver.SFlowListening ? "⬤ on" : "⬤ off")}  ({_flowReceiver.SFlowPackets} pkt)";
        _lblNfSFlow.ForeColor   = _flowReceiver.SFlowListening ? Color.FromArgb(20,130,20) : Color.Gray;
        _lblNfIpfix.Text        = $"IPFIX: {(_flowReceiver.IpfixListening ? "⬤ on" : "⬤ off")}  ({_flowReceiver.IpfixPackets} pkt)";
        _lblNfIpfix.ForeColor   = _flowReceiver.IpfixListening ? Color.FromArgb(20,130,20) : Color.Gray;
    }

    private void OnFlowTimerTick(object? sender, EventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(OnFlowTimerTick, sender, e); return; }
        UpdateFlowStatusLabels();
        // Drain dei flussi in coda: aggiungi in cima alla griglia (max 500 righe)
        var recs = _flowReceiver.Drain();
        if (recs.Count == 0) return;

        _gridFlows.SuspendLayout();
        foreach (var r in recs)
        {
            _totalFlowBytes += r.Bytes;
            _totalFlowPkts  += r.Packets;
            _gridFlows.Rows.Insert(0,
                r.Protocol,
                r.Exporter,
                r.SrcStr,
                r.SrcPort > 0 ? r.SrcPort.ToString() : "—",
                r.DstStr,
                r.DstPort > 0 ? r.DstPort.ToString() : "—",
                r.ProtoStr,
                BytesHuman(r.Bytes),
                r.Packets.ToString(),
                r.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
        }
        // Tronca a 500 righe
        while (_gridFlows.Rows.Count > 500)
            _gridFlows.Rows.RemoveAt(_gridFlows.Rows.Count - 1);
        _gridFlows.ResumeLayout();
    }

    private void OnFlowArrived(object? sender, FlowRecord r)
    {
        // Evento già drenato da timer; non serve azione immediata
    }

    private static string BytesHuman(uint bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1048576.0:F1} MB"
        : bytes >= 1024    ? $"{bytes / 1024.0:F0} KB"
        :                    $"{bytes} B";

    // -----------------------------------------------------------------------
    // SWITCH / ROUTER SCANNER
    // -----------------------------------------------------------------------

    private void OnScanDevices(object? sender, EventArgs e)
    {
        bool withSnmp = _chkSnmp.Checked;
        _gridDevices.Rows.Clear();
        _lblDevicesStatus.Text      = withSnmp
            ? "Scansione in corso… (ARP + DNS + SNMP) potrebbe richiedere qualche secondo."
            : "Scansione in corso… (ARP + DNS) potrebbe richiedere qualche secondo.";
        _lblDevicesStatus.ForeColor = ColWarn;
        _btnScanDevices.Enabled     = false;
        _devScanner.ScanAsync(withSnmp);
        // Riabilita il pulsante dopo 15s per consentire nuova scansione
        var re = new System.Windows.Forms.Timer { Interval = 15000 };
        re.Tick += (_, _) => { re.Stop(); re.Dispose();
            if (!_disposed) { BeginInvoke(() => { _btnScanDevices.Enabled = true; }); } };
        re.Start();
    }

    private void OnDeviceUpdated(object? sender, NetworkDevice dev)
    {
        if (_disposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnDeviceUpdated(sender, dev)); return; }

        // Cerca riga esistente per questo IP
        foreach (DataGridViewRow row in _gridDevices.Rows)
        {
            if (row.Cells["IP"].Value?.ToString() == dev.IpAddress)
            {
                FillDeviceRow(row, dev);
                return;
            }
        }
        // Nuova riga
        int idx = _gridDevices.Rows.Add();
        FillDeviceRow(_gridDevices.Rows[idx], dev);
        int total = _gridDevices.Rows.Count;
        _lblDevicesStatus.Text      = $"{total} dispositivo/i rilevato/i.";
        _lblDevicesStatus.ForeColor = Color.FromArgb(80, 60, 10);
    }

    private static void FillDeviceRow(DataGridViewRow row, NetworkDevice dev)
    {
        row.Cells["Icon"].Value     = dev.TypeIcon;
        row.Cells["Tipo"].Value     = dev.DeviceType.ToString();
        row.Cells["IP"].Value       = dev.IpAddress;
        row.Cells["MAC"].Value      = string.IsNullOrEmpty(dev.MacAddress) ? "—" : dev.MacAddress;
        row.Cells["Host"].Value     = dev.DisplayName;
        row.Cells["SysDescr"].Value = string.IsNullOrEmpty(dev.SysDescr) ? "(SNMP non risponde)" : dev.SysDescr.Length > 80 ? dev.SysDescr[..80] + "…" : dev.SysDescr;
        row.Cells["Uptime"].Value   = string.IsNullOrEmpty(dev.UptimeRaw) ? "—" : dev.UptimeRaw;
        row.Cells["Ifaces"].Value   = dev.InterfaceCount > 0 ? dev.InterfaceCount.ToString() : "—";
        row.Cells["Source"].Value   = dev.Source;

        // Colore riga per tipo dispositivo
        row.DefaultCellStyle.BackColor = dev.DeviceType switch
        {
            DeviceType.Router => Color.FromArgb(255, 245, 225),
            DeviceType.Switch => Color.FromArgb(240, 255, 240),
            _                 => Color.White
        };
    }

    // -----------------------------------------------------------------------
    // DISPOSE
    // -----------------------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _pingTimer.Stop();
            _pingTimer.Dispose();
            _flowTimer.Stop();
            _flowTimer.Dispose();
            _tracer.Dispose();
            _scanService.ScanCompleted -= OnScanCompleted;
            _devScanner.DeviceUpdated  -= OnDeviceUpdated;
            _flowReceiver.FlowArrived  -= OnFlowArrived;
        }
        base.Dispose(disposing);
    }

    // -----------------------------------------------------------------------
    // HELPER UI
    // -----------------------------------------------------------------------

    private static Label MakeHdrLabel(string text, Color bg) => new()
    {
        Text      = text,
        Dock      = DockStyle.Fill,
        Height    = 24,
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

    private void SetStatus(string text, Color fg)
    {
        _lblStatus.Text      = text;
        _lblStatus.ForeColor = fg;
    }

    // -----------------------------------------------------------------------
    // DataGridView con double-buffering
    // -----------------------------------------------------------------------

    private sealed class FlowGrid : DataGridView
    {
        public FlowGrid() => DoubleBuffered = true;
    }

    private sealed class FlowMetricsGrid : DataGridView
    {
        public FlowMetricsGrid() => DoubleBuffered = true;
    }
}

// ===========================================================================
// GRAFICO LATENZA (ultimi 120 campioni)
// ===========================================================================

/// <summary>
/// Pannello che disegna un line-chart della latenza in tempo reale.
/// Aggiornato ad ogni ping tramite <see cref="UpdateData"/>.
/// </summary>
internal sealed class LatencyChart : Panel
{
    private double[] _data      = Array.Empty<double>();
    private double   _threshold = 100;
    private const int Pad = 14;

    public LatencyChart()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.DoubleBuffer, true);
    }

    public void UpdateData(double[] samples, double threshold)
    {
        _data      = samples;
        _threshold = threshold;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int w = Width  - Pad * 2;
        int h = Height - Pad * 2 - 4;

        // Etichetta titolo
        using var lblFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        g.DrawString("Latenza (ms)  —  ultimi 120 campioni (2 min)",
                     lblFont, new SolidBrush(Color.FromArgb(90, 90, 110)), Pad, 2);

        if (w <= 0 || h <= 20 || _data.Length < 2)
        {
            using var hintFont = new Font("Segoe UI", 8f);
            g.DrawString("Avvia il Ping continuo per visualizzare il grafico…",
                         hintFont, Brushes.Gray, Pad, Pad + 4);
            return;
        }

        double maxVal = _data.Where(d => d >= 0).DefaultIfEmpty(_threshold).Max();
        maxVal = Math.Max(maxVal * 1.15, _threshold * 1.2);

        // Griglia orizzontale
        using var gridPen = new Pen(Color.FromArgb(220, 220, 232));
        for (int i = 0; i <= 4; i++)
        {
            float y   = Pad + 4 + h - (float)(h * i / 4.0);
            double lv = maxVal * i / 4.0;
            g.DrawLine(gridPen, Pad, y, Pad + w, y);
            using var numFont = new Font("Segoe UI", 6.5f);
            g.DrawString($"{lv:F0}", numFont, Brushes.Gray, 0, y - 7);
        }

        // Linea soglia (tratteggiata rossa)
        float ty = Pad + 4 + h - (float)(h * _threshold / maxVal);
        using var thrPen = new Pen(Color.FromArgb(190, 180, 30, 30), 1)
            { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawLine(thrPen, Pad, ty, Pad + w, ty);

        // Costruisce punti validi
        var pts = new List<PointF>();
        for (int i = 0; i < _data.Length; i++)
        {
            if (_data[i] < 0) continue;
            float x = Pad + (float)(w * i / Math.Max(_data.Length - 1, 1));
            float y = Pad + 4 + h - (float)(h * _data[i] / maxVal);
            pts.Add(new PointF(x, y));
        }

        if (pts.Count < 2) return;

        // Riempimento sotto la curva
        var fillPts = new List<PointF>(pts)
        {
            new PointF(pts[^1].X, Pad + 4 + h),
            new PointF(pts[0].X,  Pad + 4 + h)
        };
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddLines(fillPts.ToArray());
        path.CloseFigure();
        using var fillBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(Pad, Pad, w, h),
            Color.FromArgb(70, 30, 130, 200),
            Color.FromArgb(6,  30, 130, 200),
            System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        g.FillPath(fillBrush, path);

        // Linea principale
        using var linePen = new Pen(Color.FromArgb(30, 130, 200), 2);
        g.DrawLines(linePen, pts.ToArray());
    }
}
