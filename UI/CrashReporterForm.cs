namespace NetManCat.UI;

/// <summary>
/// Finestra di segnalazione errore critico / crash.
/// Mostra tipo di eccezione, messaggio, stack trace e offre le azioni
/// "Copia negli appunti", "Riavvia" e "Chiudi".
/// </summary>
public sealed class CrashReporterForm : Form
{
    private readonly TextBox _txtDetails;

    public CrashReporterForm(Exception? ex, string context = "")
    {
        Text            = "NetManCat — Errore Critico";
        Size            = new Size(690, 560);
        MinimumSize     = new Size(500, 400);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(20, 22, 36);
        FormBorderStyle = FormBorderStyle.Sizable;
        TopMost         = true;

        // ────────────────────────────────────────────────────────────────
        // HEADER rosso
        // ────────────────────────────────────────────────────────────────
        var hdr = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 58,
            BackColor = Color.FromArgb(165, 28, 28)
        };
        var lblTitle = new Label
        {
            Text      = "⚠   NetManCat si è arrestato inaspettatamente",
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Padding   = new Padding(14, 0, 0, 0)
        };
        hdr.Controls.Add(lblTitle);

        // ────────────────────────────────────────────────────────────────
        // RIEPILOGO errore
        // ────────────────────────────────────────────────────────────────
        string typeName = ex?.GetType().FullName ?? "Eccezione sconosciuta";
        string msgText  = ex?.Message           ?? "(nessun messaggio)";
        string info     = $"Tipo:       {typeName}\nMessaggio: {msgText}";
        if (!string.IsNullOrWhiteSpace(context))
            info += $"\nContesto:  {context}";

        var lblInfo = new Label
        {
            Dock      = DockStyle.Top,
            Height    = string.IsNullOrWhiteSpace(context) ? 50 : 68,
            Text      = info,
            TextAlign = ContentAlignment.MiddleLeft,
            Font      = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(235, 210, 210),
            BackColor = Color.FromArgb(42, 22, 22),
            Padding   = new Padding(14, 8, 8, 8)
        };

        // ────────────────────────────────────────────────────────────────
        // TITOLO stack trace
        // ────────────────────────────────────────────────────────────────
        var lblStkHdr = new Label
        {
            Dock      = DockStyle.Top,
            Height    = 24,
            Text      = "  Stack trace:",
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 170, 200),
            BackColor = Color.FromArgb(30, 34, 55),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(10, 0, 0, 0)
        };

        // ────────────────────────────────────────────────────────────────
        // STACK TRACE
        // ────────────────────────────────────────────────────────────────
        _txtDetails = new TextBox
        {
            Dock        = DockStyle.Fill,
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Both,
            WordWrap    = false,
            BackColor   = Color.FromArgb(14, 16, 26),
            ForeColor   = Color.FromArgb(190, 200, 225),
            Font        = new Font("Consolas", 8.5f),
            BorderStyle = BorderStyle.None,
            Text        = BuildReport(ex, context)
        };

        // ────────────────────────────────────────────────────────────────
        // BARRA TASTI (basso)
        // ────────────────────────────────────────────────────────────────
        var btnRow = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 48,
            BackColor = Color.FromArgb(24, 28, 46),
            Padding   = new Padding(10, 8, 10, 8)
        };

        var btnCopy = new Button
        {
            Text      = "📋  Copia negli appunti",
            Width     = 185, Height = 30,
            Dock      = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 55, 90)
        };
        btnCopy.FlatAppearance.BorderColor = Color.FromArgb(65, 78, 125);

        var btnRestart = new Button
        {
            Text      = "🔄  Riavvia",
            Width     = 115, Height = 30,
            Dock      = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(32, 90, 145)
        };
        btnRestart.FlatAppearance.BorderColor = Color.FromArgb(50, 120, 185);

        var btnClose = new Button
        {
            Text      = "  Chiudi",
            Width     = 90, Height = 30,
            Dock      = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(90, 25, 25)
        };
        btnClose.FlatAppearance.BorderColor = Color.FromArgb(140, 45, 45);

        btnRow.Controls.AddRange(new Control[] { btnCopy, btnRestart, btnClose });

        // ────────────────────────────────────────────────────────────────
        // LAYOUT (ordine Dock: basso → top → fill)
        // ────────────────────────────────────────────────────────────────
        Controls.Add(_txtDetails);   // Fill
        Controls.Add(lblStkHdr);     // Top (aggiunto dopo fill: sopra)
        Controls.Add(lblInfo);       // Top
        Controls.Add(hdr);           // Top (primo — più in alto)
        Controls.Add(btnRow);        // Bottom

        // ────────────────────────────────────────────────────────────────
        // EVENTI
        // ────────────────────────────────────────────────────────────────
        btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_txtDetails.Text); }
            catch { /* ignore clipboard failures */ }
        };

        btnClose.Click += (_, _) => Close();

        btnRestart.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(Application.ExecutablePath); }
            catch { /* ignore */ }
            Close();
        };
    }

    // ────────────────────────────────────────────────────────────────────
    // Costruisce il testo completo del report
    // ────────────────────────────────────────────────────────────────────
    private static string BuildReport(Exception? ex, string context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== NetManCat Crash Report  —  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine($"Contesto: {context}");
            sb.AppendLine();
        }

        var e     = ex;
        int depth = 0;
        while (e != null)
        {
            if (depth > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"── Inner exception (depth {depth}) ──────────────────────");
            }
            sb.AppendLine($"Tipo:      {e.GetType().FullName}");
            sb.AppendLine($"Messaggio: {e.Message}");
            sb.AppendLine();
            sb.AppendLine("Stack trace:");
            sb.AppendLine(e.StackTrace ?? "  (nessuno)");
            e = e.InnerException;
            depth++;
        }

        if (ex == null)
            sb.AppendLine("(nessuna informazione sull'eccezione disponibile)");

        return sb.ToString();
    }
}
