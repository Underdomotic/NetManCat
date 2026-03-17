namespace NetManCat.UI;

/// <summary>
/// Splash screen che si apre istantaneamente su un thread STA separato
/// lasciando il thread principale libero di inizializzare i servizi.
/// </summary>
public sealed class SplashForm : Form
{
    // --- Palette ---
    private static readonly Color BgColor      = Color.FromArgb(18,  22,  42);
    private static readonly Color AccentCyan   = Color.FromArgb(0,  180, 220);
    private static readonly Color AccentGreen  = Color.FromArgb(0,  210, 130);
    private static readonly Color TextLight    = Color.FromArgb(220, 228, 245);
    private static readonly Color TextGray     = Color.FromArgb(110, 120, 155);
    private static readonly Color PanelDark    = Color.FromArgb(28,  33,  58);

    // --- Controls ---
    private readonly Panel  _progressFill;
    private readonly Panel  _progressTrack;
    private readonly Label  _lblCurrentStep;
    private readonly Label[] _historyLabels = new Label[4];

    // --- State ---
    private string _prevStep  = "";
    private readonly List<string> _history = new();

    // --- Constants ---
    private const int FormW = 480;
    private const int FormH = 290;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.CenterScreen;
        Size            = new Size(FormW, FormH);
        BackColor       = BgColor;
        TopMost         = true;

        // ────────────────────────────────────────────────────────────────
        // LOGO / INTESTAZIONE (0-86)
        // ────────────────────────────────────────────────────────────────
        // Carica il logo PNG dall'assembly embedded; fallback badge testuale
        Control badge;
        var logoStream = typeof(SplashForm).Assembly
            .GetManifestResourceStream("NetManCat.splash_logo.png");
        if (logoStream != null)
        {
            badge = new PictureBox
            {
                Location  = new Point(18, 8),
                Size      = new Size(70, 70),
                Image     = Image.FromStream(logoStream),
                SizeMode  = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
        }
        else
        {
            var p = new Panel { Location = new Point(26, 18), Size = new Size(54, 54), BackColor = AccentCyan };
            p.Controls.Add(new Label
            {
                Text = "NC", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
            });
            badge = p;
        }

        var lblAppName = new Label
        {
            Text = "NetManCat", Location = new Point(92, 17), Size = new Size(240, 32),
            Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = TextLight, BackColor = Color.Transparent
        };
        var lblTagline = new Label
        {
            Text = "Network Connection Monitor", Location = new Point(94, 50), Size = new Size(240, 18),
            Font = new Font("Segoe UI", 8.5f), ForeColor = TextGray, BackColor = Color.Transparent
        };
        string ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        var lblVersion = new Label
        {
            Text = $"v{ver}", Location = new Point(FormW - 70, 64), AutoSize = true,
            Font = new Font("Segoe UI", 8f), ForeColor = Color.FromArgb(70, 82, 115), BackColor = Color.Transparent
        };

        // Linea separatrice
        var sep = new Panel { Location = new Point(0, 84), Size = new Size(FormW, 1), BackColor = PanelDark };

        // ────────────────────────────────────────────────────────────────
        // PROGRESS BAR (94-104)
        // ────────────────────────────────────────────────────────────────
        _progressTrack = new Panel
        {
            Location  = new Point(26, 96),
            Size      = new Size(FormW - 52, 8),
            BackColor = PanelDark
        };
        _progressFill = new Panel
        {
            Location  = new Point(0, 0),
            Size      = new Size(0, 8),
            BackColor = AccentCyan
        };
        _progressTrack.Controls.Add(_progressFill);

        // Label percentuale
        var lblPct = new Label
        {
            Name      = "lblPct",
            Text      = "0%",
            Location  = new Point(FormW - 52, 93),
            Size      = new Size(26, 14),
            TextAlign = ContentAlignment.MiddleRight,
            Font      = new Font("Segoe UI", 7f),
            ForeColor = TextGray,
            BackColor = Color.Transparent
        };

        // ────────────────────────────────────────────────────────────────
        // STEP CORRENTE (112-132)
        // ────────────────────────────────────────────────────────────────
        _lblCurrentStep = new Label
        {
            Text      = "Inizializzazione…",
            Location  = new Point(26, 113),
            Size      = new Size(FormW - 52, 20),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = AccentCyan,
            BackColor = Color.Transparent
        };

        // ────────────────────────────────────────────────────────────────
        // HISTORY (step già eseguiti)  140, 163, 186, 209
        // ────────────────────────────────────────────────────────────────
        for (int i = 0; i < _historyLabels.Length; i++)
        {
            _historyLabels[i] = new Label
            {
                Location  = new Point(34, 140 + i * 23),
                Size      = new Size(FormW - 60, 18),
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(55 + i * 4, 65 + i * 4, 95 + i * 4),
                BackColor = Color.Transparent,
                Text      = "",
                Visible   = false
            };
        }

        // ────────────────────────────────────────────────────────────────
        // FOOTER bar (fondo)
        // ────────────────────────────────────────────────────────────────
        var footer = new Panel
        {
            Location  = new Point(0, FormH - 26),
            Size      = new Size(FormW, 26),
            BackColor = PanelDark
        };
        var lblCopy = new Label
        {
            Text      = $"© {DateTime.Now.Year}  NetManCat — Network Connection Analyzer",
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(68, 78, 105),
            BackColor = Color.Transparent,
            Padding   = new Padding(10, 0, 0, 0)
        };
        footer.Controls.Add(lblCopy);

        // ────────────────────────────────────────────────────────────────
        // ASSEMBLE
        // ────────────────────────────────────────────────────────────────
        Controls.Add(badge);
        Controls.Add(lblAppName);
        Controls.Add(lblTagline);
        Controls.Add(lblVersion);
        Controls.Add(sep);
        Controls.Add(_progressTrack);
        Controls.Add(lblPct);
        Controls.Add(_lblCurrentStep);
        for (int i = _historyLabels.Length - 1; i >= 0; i--)
            Controls.Add(_historyLabels[i]);
        Controls.Add(footer);
    }

    // ────────────────────────────────────────────────────────────────────
    // API chiamata dal thread principale
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggiorna la barra di progresso e il messaggio di stato.
    /// Thread-safe: può essere chiamato da qualsiasi thread.
    /// </summary>
    public void ReportStep(string message, int percent)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { Invoke(() => ReportStep(message, percent)); }
            catch { }
            return;
        }

        // Sposta lo step precedente nella history
        if (!string.IsNullOrEmpty(_prevStep))
        {
            _history.Insert(0, _prevStep);
            if (_history.Count > _historyLabels.Length)
                _history.RemoveAt(_history.Count - 1);
        }
        _prevStep = message;

        // Progress bar
        int pct = Math.Clamp(percent, 0, 100);
        _progressFill.Width      = (int)(_progressTrack.Width * pct / 100.0);
        _progressFill.BackColor  = pct >= 100 ? AccentGreen : AccentCyan;

        // Label percentuale
        if (Controls.Find("lblPct", false).FirstOrDefault() is Label lp)
            lp.Text = $"{pct}%";

        // Step corrente
        _lblCurrentStep.Text      = message;
        _lblCurrentStep.ForeColor = pct >= 100 ? AccentGreen : AccentCyan;

        // History labels
        for (int i = 0; i < _historyLabels.Length; i++)
        {
            if (i < _history.Count)
            {
                _historyLabels[i].Text    = $"✓  {_history[i]}";
                _historyLabels[i].Visible = true;
                // Dissolvenza progressiva: il più recente è più luminoso
                int c = 90 - i * 18;
                _historyLabels[i].ForeColor = Color.FromArgb(c, c + 8, c + 30);
            }
            else
            {
                _historyLabels[i].Visible = false;
            }
        }

        Refresh();
    }

    /// <summary>Chiude la splash in modo sicuro (thread-safe).</summary>
    public void CloseSplash()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { Invoke(CloseSplash); }
            catch { }
            return;
        }
        Close();
    }

    // Drop shadow su form borderless
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    // Bordo sottile attorno al form
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(38, 46, 80), 1);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}
