using Microsoft.Win32;
using NetManCat.Core;
using NetManCat.Models;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello Configurazione.
/// Sincronizzazione bidirezionale tra UI e netmancat.json.
/// </summary>
public sealed class ConfigPanel : UserControl
{
    private readonly ConfigManager _config;
    private readonly SqliteLogger _logger;

    // Controlli UI
    private NumericUpDown _nudRefresh = null!;
    private NumericUpDown _nudLatAlert = null!;
    private NumericUpDown _nudLossAlert = null!;
    private NumericUpDown _nudJitterAlert = null!;
    private NumericUpDown _nudServerPort = null!;
    private RadioButton _rbMemory = null!;
    private RadioButton _rbSqlite = null!;
    private TextBox _txtSqlitePath = null!;
    private NumericUpDown _nudRetention = null!;
    private ComboBox _cmbCompression = null!;
    private Button? button2;   // generato dal designer, non utilizzato nell'UI costruita a codice
    private CheckBox _chkAutoStart = null!;

    // v1.1
    private CheckBox      _chkTcpProbe     = null!;
    private NumericUpDown _nudTcpProbePort  = null!;
    private CheckBox      _chkTls          = null!;
    private TextBox       _txtPfxPath      = null!;

    /// <summary>
    /// Scatenato quando l'utente cambia la modalità di log dopo aver salvato.
    /// Argomento: "memory" | "sqlite"
    /// </summary>
    public event EventHandler<string>? LogModeChanged;

    public ConfigPanel(ConfigManager config, SqliteLogger logger)
    {
        _config = config;
        _logger = logger;

        BuildLayout();
        LoadFromConfig();

        _config.ConfigChanged += (_, _) => LoadFromConfig();
    }

    // -------------------------------------------------------------------
    // Costruzione layout con GroupBox
    // -------------------------------------------------------------------
    private void BuildLayout()
    {
        // ----------------------------------------------------------------
        // HEADER con logo + screenshot applicazione
        // ----------------------------------------------------------------
        const int HdrH = 118;
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HdrH,
            BackColor = Color.FromArgb(22, 26, 48)
        };

        // Logo NetManCat (splash_logo.png) a sinistra
        var logoStream = typeof(ConfigPanel).Assembly
            .GetManifestResourceStream("NetManCat.splash_logo.png");
        if (logoStream != null)
        {
            header.Controls.Add(new PictureBox
            {
                Location = new Point(8, 6),
                Size = new Size(106, 106),
                Image = Image.FromStream(logoStream),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            });
        }

        // Screenshot applicazione a destra del logo
        var shotStream = typeof(ConfigPanel).Assembly
            .GetManifestResourceStream("NetManCat.config_screenshot.png");
        if (shotStream != null)
        {
            header.Controls.Add(new PictureBox
            {
                Location = new Point(420, 4),
                Size = new Size(HdrH * 2 - 10, HdrH - 8),
                Image = Image.FromStream(shotStream),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom
            });
        }

        // Etichetta versione/titolo nell'header
        header.Controls.Add(new Label
        {
            Text = "NetManCat — Configurazione",
            ForeColor = Color.FromArgb(0, 180, 220),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Location = new Point(120, 20),
            AutoSize = true
        });
        header.Controls.Add(new Label
        {
            Text = "Imposta soglie, log, compressione e avvio automatico.",
            ForeColor = Color.FromArgb(110, 120, 155),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8f),
            Location = new Point(120, 48),
            Size = new Size(290, 40)
        });

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        int y = 8;

        // ---- Monitoraggio ----
        var gbMonitor = MakeGroup("Monitoraggio", 8, y, 460, 60);
        y += 68;
        AddRow(gbMonitor, "Intervallo aggiornamento UI (ms):",
               _nudRefresh = MakeNum(100, 10_000, 500, 80), 20);

        // ---- Soglie di Alert ----
        var gbAlert = MakeGroup("Soglie di Alert", 8, y, 460, 100);
        y += 108;
        AddRow(gbAlert, "Latenza massima (ms):", _nudLatAlert = MakeNum(1, 10_000, 100, 80), 20);
        AddRow(gbAlert, "Packet loss massimo (%):", _nudLossAlert = MakeNum(0, 100, 5, 80), 46);
        AddRow(gbAlert, "Jitter massimo (ms):", _nudJitterAlert = MakeNum(0, 1_000, 20, 80), 72);

        // ---- Modalità Server ----
        var gbServer = MakeGroup("Modalità Server TCP", 8, y, 460, 60);
        y += 68;
        AddRow(gbServer, "Porta TCP in ascolto:", _nudServerPort = MakeNum(1, 65_535, 9_100, 80), 20);

        // ---- Log ----
        var gbLog = MakeGroup("Registrazione Log", 8, y, 460, 110);
        y += 118;
        _rbMemory = new RadioButton { Text = "In-memory (buffer circolare, default)", Location = new Point(12, 22), AutoSize = true, Checked = true };
        _rbSqlite = new RadioButton { Text = "SQLite — persistenza permanente", Location = new Point(12, 46), AutoSize = true };
        AddRow(gbLog, "Percorso file SQLite:", _txtSqlitePath = new TextBox { Width = 200, Text = "netmancat.db" }, 72);
        AddRow(gbLog, "Conserva dati (giorni):", _nudRetention = MakeNum(1, 365, 30, 80), 94);   // spostato sotto
        gbLog.Controls.AddRange(new Control[] { _rbMemory, _rbSqlite });

        // ---- Compressione ----
        var gbComp = MakeGroup("Compressione pacchetti TCP", 8, y, 460, 60);
        y += 68;
        _cmbCompression = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100
        };
        _cmbCompression.Items.AddRange(new object[] { "lz4", "brotli" });
        _cmbCompression.SelectedIndex = 0;
        AddRow(gbComp, "Algoritmo:", _cmbCompression, 22);

        // ---- v1.1: TCP Probe ----
        var gbTcpProbe = MakeGroup("Sonda TCP (v1.1) — RTT reale anche senza ICMP", 8, y, 460, 78);
        y += 86;
        _chkTcpProbe = new CheckBox { Text = "Abilita probe TCP in parallelo all'ICMP", Location = new Point(12, 22), AutoSize = true };
        gbTcpProbe.Controls.Add(_chkTcpProbe);
        AddRow(gbTcpProbe, "Porta probe TCP (es. 443, 80, 22):", _nudTcpProbePort = MakeNum(1, 65_535, 443, 80), 48);

        // ---- v1.1: TLS ----
        var gbTls = MakeGroup("Cifratura TLS (v1.1) — comunicazione server/client", 8, y, 460, 78);
        y += 86;
        _chkTls    = new CheckBox { Text = "Abilita TLS sulla connessione server/client",   Location = new Point(12, 22), AutoSize = true };
        gbTls.Controls.Add(_chkTls);
        AddRow(gbTls, "File certificato (.pfx):", _txtPfxPath = new TextBox { Width = 200, Text = "netmancat-sign.pfx" }, 50);

        // ---- Avvio automatico con Windows ----
        var gbAutoStart = MakeGroup("Avvio automatico con Windows", 8, y, 460, 52);
        y += 60;
        _chkAutoStart = new CheckBox
        {
            Text = "Avvia NetManCat all'accensione del PC (registro utente)",
            Location = new Point(12, 22),
            AutoSize = true
        };
        gbAutoStart.Controls.Add(_chkAutoStart);

        // ---- Salva ----
        var btnSave = new Button
        {
            Text = "  Salva Configurazione",
            Location = new Point(8, y + 8),
            Width = 220,
            Height = 36,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += OnSave;

        scroll.Controls.AddRange(new Control[]
        {
            gbMonitor, gbAlert, gbServer, gbLog, gbComp, gbTcpProbe, gbTls, gbAutoStart, btnSave
        });

        // Aggiunge prima lo scroll (Fill), poi l'header (Top):
        // WinForms applica il Dock nell'ordine di inserimento nella collection,
        // quindi l'header sovrapposto con Dock=Top verrà posizionato correttamente.
        Controls.Add(scroll);
        Controls.Add(header);
    }

    // -------------------------------------------------------------------
    // Helper: crea GroupBox posizionata
    // -------------------------------------------------------------------
    private static GroupBox MakeGroup(string title, int x, int y, int w, int h) =>
        new GroupBox
        {
            Text = title,
            Location = new Point(x, y),
            Size = new Size(w, h),
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 8.5f, FontStyle.Bold)
        };

    // -------------------------------------------------------------------
    // Helper: aggiunge label + controllo in una GroupBox
    // -------------------------------------------------------------------
    private static void AddRow(GroupBox gb, string labelText, Control ctrl, int yPos)
    {
        var lbl = new Label
        {
            Text = labelText,
            Location = new Point(12, yPos + 2),
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Regular)
        };
        ctrl.Location = new Point(260, yPos);
        gb.Controls.Add(lbl);
        gb.Controls.Add(ctrl);
    }

    // -------------------------------------------------------------------
    // Helper: crea NumericUpDown
    // -------------------------------------------------------------------
    private static NumericUpDown MakeNum(int min, int max, int val, int width) =>
        new NumericUpDown { Minimum = min, Maximum = max, Value = val, Width = width };

    // -------------------------------------------------------------------
    // Load / Save
    // -------------------------------------------------------------------
    private void LoadFromConfig()
    {
        if (InvokeRequired) { Invoke(LoadFromConfig); return; }

        var c = _config.Config;

        _nudRefresh.Value = Clamp((decimal)c.RefreshIntervalMs, 100, 10_000);
        _nudLatAlert.Value = Clamp((decimal)c.AlertThresholds.LatencyMs, 1, 10_000);
        _nudLossAlert.Value = Clamp((decimal)c.AlertThresholds.LossPercent, 0, 100);
        _nudJitterAlert.Value = Clamp((decimal)c.AlertThresholds.JitterMs, 0, 1_000);
        _nudServerPort.Value = Clamp(c.ServerMode.Port, 1, 65_535);
        _rbSqlite.Checked = c.Logging.Mode == "sqlite";
        _rbMemory.Checked = c.Logging.Mode != "sqlite";
        _txtSqlitePath.Text = c.Logging.SqlitePath;
        _nudRetention.Value = Clamp(c.Logging.RetentionDays, 1, 365);

        _cmbCompression.SelectedItem = c.Compression ?? "lz4";
        if (_cmbCompression.SelectedIndex < 0)
            _cmbCompression.SelectedIndex = 0;

        _chkAutoStart.Checked     = AutoStartHelper.IsEnabled();

        // v1.1
        _chkTcpProbe.Checked        = c.TcpProbe.Enabled;
        _nudTcpProbePort.Value      = Clamp(c.TcpProbe.Port, 1, 65_535);
        _chkTls.Checked             = c.Tls.Enabled;
        _txtPfxPath.Text            = c.Tls.PfxPath;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var c = _config.Config;
        string prevMode = c.Logging.Mode;

        c.RefreshIntervalMs = (int)_nudRefresh.Value;
        c.AlertThresholds.LatencyMs = (double)_nudLatAlert.Value;
        c.AlertThresholds.LossPercent = (double)_nudLossAlert.Value;
        c.AlertThresholds.JitterMs = (double)_nudJitterAlert.Value;
        c.ServerMode.Port = (int)_nudServerPort.Value;
        c.Logging.Mode = _rbSqlite.Checked ? "sqlite" : "memory";
        c.Logging.SqlitePath = _txtSqlitePath.Text.Trim();
        c.Logging.RetentionDays = (int)_nudRetention.Value;
        c.Compression = _cmbCompression.SelectedItem?.ToString() ?? "lz4";
        AutoStartHelper.Apply(_chkAutoStart.Checked);
        // v1.1
        c.TcpProbe.Enabled  = _chkTcpProbe.Checked;
        c.TcpProbe.Port     = (int)_nudTcpProbePort.Value;
        c.Tls.Enabled       = _chkTls.Checked;
        c.Tls.PfxPath       = _txtPfxPath.Text.Trim();
        _config.Apply(c);

        // Notifica il cambio di modalità log solo se è effettivamente cambiata
        if (prevMode != c.Logging.Mode)
            LogModeChanged?.Invoke(this, c.Logging.Mode);

        MessageBox.Show("Configurazione salvata correttamente.",
            "NetManCat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static decimal Clamp(decimal v, decimal min, decimal max) =>
        Math.Max(min, Math.Min(max, v));

    private void InitializeComponent()
    {
        button2 = new Button();
        SuspendLayout();
        // 
        // button2
        // 
        button2.Location = new Point(34, 42);
        button2.Name = "button2";
        button2.Size = new Size(75, 23);
        button2.TabIndex = 0;
        button2.Text = "button2";
        button2.UseVisualStyleBackColor = true;
        // 
        // ConfigPanel
        // 
        Controls.Add(button2);
        Name = "ConfigPanel";
        ResumeLayout(false);

    }

    private static decimal Clamp(int v, int min, int max) =>
        Math.Max(min, Math.Min(max, v));

    // Helper registro di Windows per l'avvio automatico
    private static class AutoStartHelper
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "NetManCat";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        public static void Apply(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, true);
                if (key == null) return;
                if (enable)
                {
                    // Percorso eseguibile tra virgolette per gestire spazi nel path
                    string exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                    key.SetValue(AppName, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }
            }
            catch { /* Registro non accessibile: ignora silenziosamente */ }
        }
    }
}
