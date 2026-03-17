using NetManCat.Core;
using NetManCat.UI.Panels;

namespace NetManCat.UI;

/// <summary>
/// Finestra principale di NetManCat.
/// Coordina i tre pannelli (Monitor, Server, Configurazione)
/// e i servizi di backend (TcpServerHost, SqliteLogger, MetricsBuffer).
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConfigManager    _config;
    private readonly MetricsBuffer    _buffer;
    private readonly SqliteLogger     _logger;
    private readonly TcpServerHost    _server;
    private readonly WatchlistManager _watchlist;
    private readonly LatencyProbe     _latencyProbe;
    private readonly ScanService      _scanService;
    private readonly BandwidthTracker _bwTracker;
    private readonly NetFlowReceiver      _flowReceiver;
    private readonly NetworkDeviceScanner _devScanner;

    private readonly TabControl     _tabs;
    private readonly MonitorPanel    _monitorPanel;
    private readonly AnalysisPanel   _analysisPanel;
    private readonly FlowAnalysisPanel _flowPanel;
    private readonly ServerPanel     _serverPanel;
    private readonly ConfigPanel     _configPanel;

    private readonly StatusStrip          _statusStrip;
    private readonly ToolStripStatusLabel _lblServerStatus;
    private readonly ToolStripStatusLabel _lblLogMode;
    private readonly ToolStripStatusLabel _lblClients;
    private readonly ToolStripStatusLabel _lblWatched;
    private readonly ToolStripStatusLabel _lblVersion;

    // Tray icon
    private readonly NotifyIcon        _trayIcon;
    private readonly ToolStripMenuItem _trayMenuServer;
    private bool                       _forceClose;

    public MainForm(ConfigManager config, Action<string, int>? onStep = null)
    {
        _config       = config;
        _buffer       = new MetricsBuffer();
        _logger       = new SqliteLogger();
        _server       = new TcpServerHost();
        _watchlist    = new WatchlistManager();
        _latencyProbe = new LatencyProbe();
        _bwTracker    = new BandwidthTracker();
        _flowReceiver = new NetFlowReceiver();
        _devScanner   = new NetworkDeviceScanner();

        onStep?.Invoke("Avvio scan service…", 50);
        _scanService  = new ScanService(_config.Config.RefreshIntervalMs);

        // -------------------------------------------------------------------
        // Pannelli (istanziati prima di InitializeComponent)
        // -------------------------------------------------------------------
        onStep?.Invoke("Monitor connessioni…", 58);
        _monitorPanel  = new MonitorPanel(_config, _buffer, _watchlist, _scanService);
        onStep?.Invoke("Pannello analisi…", 66);
        _analysisPanel = new AnalysisPanel(_config, _watchlist, _latencyProbe, _scanService, _bwTracker);
        onStep?.Invoke("Pannello flusso rete…", 74);
        _flowPanel     = new FlowAnalysisPanel(_config, _scanService, _flowReceiver, _devScanner);
        onStep?.Invoke("Pannello server/client…", 82);
        _serverPanel   = new ServerPanel(_config, _server, _watchlist, _scanService, _latencyProbe, _bwTracker);
        onStep?.Invoke("Configurazione UI…", 90);
        _configPanel   = new ConfigPanel(_config, _logger);

        // -------------------------------------------------------------------
        // Status strip
        // -------------------------------------------------------------------
        _lblServerStatus = new ToolStripStatusLabel("Server: inattivo")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Right,
            Width       = 220
        };
        _lblLogMode = new ToolStripStatusLabel("Log: memory")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Right,
            Width       = 140
        };
        _lblClients = new ToolStripStatusLabel("Client: 0")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Right,
            Width       = 100
        };
        _lblWatched = new ToolStripStatusLabel("In analisi: 0")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Right,
            Width       = 120,
            ForeColor   = Color.FromArgb(120, 90, 0)
        };
        _lblVersion = new ToolStripStatusLabel("NetManCat v1.0")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = Color.Gray
        };

        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange(new ToolStripItem[]
        {
            _lblServerStatus, _lblLogMode, _lblClients, _lblWatched, _lblVersion
        });

        // -------------------------------------------------------------------
        // Tab pages (ogni UserControl è impostato Dock=Fill nel tab page)
        // -------------------------------------------------------------------
        var tabMonitor = new TabPage("  Monitor  ")  { UseVisualStyleBackColor = true };
        tabMonitor.Controls.Add(_monitorPanel);
        _monitorPanel.Dock = DockStyle.Fill;

        var tabAnalisi = new TabPage("  ★ Analisi  ") { UseVisualStyleBackColor = true };
        tabAnalisi.Controls.Add(_analysisPanel);
        _analysisPanel.Dock = DockStyle.Fill;

        var tabFlow = new TabPage("  🔍 Flusso  ") { UseVisualStyleBackColor = true };
        tabFlow.Controls.Add(_flowPanel);
        _flowPanel.Dock = DockStyle.Fill;

        var tabServer = new TabPage("  Server / Client  ") { UseVisualStyleBackColor = true };
        tabServer.Controls.Add(_serverPanel);
        _serverPanel.Dock = DockStyle.Fill;

        var tabConfig = new TabPage("  Configurazione  ") { UseVisualStyleBackColor = true };
        tabConfig.Controls.Add(_configPanel);
        _configPanel.Dock = DockStyle.Fill;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.AddRange(new[] { tabMonitor, tabAnalisi, tabFlow, tabServer, tabConfig });

        // -------------------------------------------------------------------
        // Form
        // -------------------------------------------------------------------
        SuspendLayout();
        Text          = "NetManCat — Network Manager";
        Size          = new Size(1200, 720);
        MinimumSize   = new Size(900, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Icon? appIcon = null;
        var iconStream = typeof(MainForm).Assembly
            .GetManifestResourceStream("NetManCat.20netmoncat.ico");
        if (iconStream != null) { appIcon = new Icon(iconStream); Icon = appIcon; }

        Controls.Add(_tabs);
        Controls.Add(_statusStrip);
        ResumeLayout(false);

        // -------------------------------------------------------------------
        // Tray icon con menu contestuale
        // -------------------------------------------------------------------
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Apri", null, (_, _) => MostraFinestra());
        trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenuServer = new ToolStripMenuItem("Avvia server");
        _trayMenuServer.Click += (_, _) => { _serverPanel.ToggleServer(); UpdateTrayMenuServer(); };
        trayMenu.Items.Add(_trayMenuServer);
        trayMenu.Items.Add("Impostazioni", null, (_, _) => { MostraFinestra(); _tabs.SelectedIndex = 4; });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Chiudi", null, (_, _) => { _forceClose = true; Close(); });
        _trayIcon = new NotifyIcon
        {
            Icon             = appIcon ?? SystemIcons.Application,
            Text             = "NetManCat — Network Manager",
            ContextMenuStrip = trayMenu,
            Visible          = true
        };
        _trayIcon.DoubleClick += (_, _) => MostraFinestra();

        // -------------------------------------------------------------------
        // Wiring eventi
        // -------------------------------------------------------------------
        WireEvents();
        ApplyInitialConfig();
        _scanService.Start();
    }

    // -------------------------------------------------------------------
    // Wiring
    // -------------------------------------------------------------------
    private void WireEvents()
    {
        _config.ConfigChanged          += (_, _) => ApplyInitialConfig();
        _server.ClientConnected        += (_, _) => UpdateServerLabel();
        _server.ClientDisconnected     += (_, _) => UpdateServerLabel();
        _server.ErrorOccurred          += (_, msg) => ShowServerError(msg);
        _serverPanel.ServerToggled     += OnServerToggled;
        _configPanel.LogModeChanged    += OnLogModeChanged;
        _watchlist.WatchlistChanged    += (_, _) => UpdateWatchedLabel();
        FormClosing                    += OnFormClosing;
    }

    // -------------------------------------------------------------------
    // Applica la configurazione corrente all'avvio o dopo ogni modifica
    // -------------------------------------------------------------------
    private void ApplyInitialConfig()
    {
        if (InvokeRequired) { Invoke(ApplyInitialConfig); return; }

        var cfg = _config.Config;

        _scanService?.SetInterval(cfg.RefreshIntervalMs);

        // Attiva/disattiva SQLite logger in base alla modalità configurata
        if (cfg.Logging.Mode == "sqlite" && !_logger.IsActive)
            _logger.Start(cfg.Logging.SqlitePath);
        else if (cfg.Logging.Mode != "sqlite" && _logger.IsActive)
            _logger.Stop();

        UpdateLogLabel();
    }

    // -------------------------------------------------------------------
    // Handlers
    // -------------------------------------------------------------------
    private void OnServerToggled(object? sender, bool running)
    {
        UpdateServerLabel();
    }

    private void OnLogModeChanged(object? sender, string mode)
    {
        if (mode == "sqlite")
            _logger.Start(_config.Config.Logging.SqlitePath);
        else
            _logger.Stop();

        UpdateLogLabel();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Se l'utente preme la X mostra il dialogo di scelta (non per shutdown OS o _forceClose)
        if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            var scelta = ChiediChiudiOTray();
            if (scelta == null)  { e.Cancel = true; return; }   // Annulla
            if (scelta == false) { e.Cancel = true; Hide(); return; } // Riduci a tray
            // scelta == true → chiudi definitivamente (prosegui)
        }
        // Cleanup ordinato all'uscita
        _trayIcon.Visible = false;
        _server.Stop();
        _logger.Stop();
        _latencyProbe.Stop();
        _scanService.Stop();
        _bwTracker.Dispose();
        _flowReceiver.Dispose();
        _flowPanel.Dispose();
    }

    // -------------------------------------------------------------------
    // Aggiornamento status bar
    // -------------------------------------------------------------------
    private void UpdateServerLabel()
    {
        if (InvokeRequired) { Invoke(UpdateServerLabel); return; }

        if (_server.IsRunning)
        {
            _lblServerStatus.Text     = $"Server: attivo :{_config.Config.ServerMode.Port}";
            _lblServerStatus.ForeColor = Color.FromArgb(0, 128, 0);
            _lblClients.Text           = $"Client: {_server.ConnectedClients}";
        }
        else
        {
            _lblServerStatus.Text     = "Server: inattivo";
            _lblServerStatus.ForeColor = SystemColors.ControlText;
            _lblClients.Text           = "Client: 0";
        }
        UpdateTrayMenuServer();
    }

    private void UpdateLogLabel()
    {
        if (InvokeRequired) { Invoke(UpdateLogLabel); return; }
        _lblLogMode.Text = _logger.IsActive ? "Log: SQLite" : "Log: memory";
    }

    private void UpdateWatchedLabel()
    {
        if (InvokeRequired) { Invoke(UpdateWatchedLabel); return; }
        int n = _watchlist.Count;
        _lblWatched.Text      = $"In analisi: {n}";
        _lblWatched.ForeColor = n > 0 ? Color.FromArgb(140, 90, 0) : SystemColors.ControlText;
    }

    private void ShowServerError(string msg)
    {
        if (InvokeRequired) { Invoke(() => ShowServerError(msg)); return; }
        // Mostra errori TCP server nella status bar (non interrompe l'app)
        _lblServerStatus.Text     = $"Errore: {msg}";
        _lblServerStatus.ForeColor = Color.Red;
    }

    // Mostra la finestra principale dalla tray
    private void MostraFinestra()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // Sincronizza la voce Avvia/Arresta server nel menu tray
    private void UpdateTrayMenuServer()
    {
        if (InvokeRequired) { Invoke(UpdateTrayMenuServer); return; }
        _trayMenuServer.Text = _server.IsRunning ? "Arresta server" : "Avvia server";
    }

    // Dialogo di scelta alla pressione della X: true=chiudi, false=riduci a tray, null=annulla
    private static bool? ChiediChiudiOTray()
    {
        using var dlg = new Form
        {
            Text            = "NetManCat",
            Size            = new Size(370, 155),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterScreen,
            MinimizeBox     = false,
            MaximizeBox     = false
        };
        var lbl = new Label
        {
            Text     = "Cosa vuoi fare con NetManCat?",
            Location = new Point(16, 20),
            AutoSize = true
        };
        var btnClose  = new Button { Text = "Chiudi",            Location = new Point(14,  65), Width = 100, Height = 32, DialogResult = DialogResult.Yes    };
        var btnTray   = new Button { Text = "Riduci a notifica", Location = new Point(124, 65), Width = 120, Height = 32, DialogResult = DialogResult.No     };
        var btnCancel = new Button { Text = "Annulla",           Location = new Point(254, 65), Width = 88,  Height = 32, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { lbl, btnClose, btnTray, btnCancel });
        dlg.AcceptButton = btnClose;
        dlg.CancelButton = btnCancel;
        return dlg.ShowDialog() switch
        {
            DialogResult.Yes => true,
            DialogResult.No  => false,
            _                => null
        };
    }
}
