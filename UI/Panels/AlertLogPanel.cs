using NetManCat.Core;
using NetManCat.Models;
using System.Text;

namespace NetManCat.UI.Panels;

/// <summary>
/// Pannello di log alert — mostra tutti gli alert scattati da AlertEngine.
///
/// Layout:
///   ┌──────────────────────────────────────────────────┐
///   │  toolbar: [🗑 Cancella]  [💾 Esporta CSV]        │
///   ├──────────────────────────────────────────────────┤
///   │  DataGridView: Timestamp | Processo | IP remoto  │
///   │                Metrica   | Valore   | Soglia      │
///   └──────────────────────────────────────────────────┘
///
/// Ogni riga è colorata: rosso se latenza, arancio se loss, giallo se jitter.
/// Gli alert nuovi arrivano in cima (InsertAt 0) per visibilità immediata.
/// </summary>
public sealed class AlertLogPanel : UserControl
{
    // -----------------------------------------------------------------------
    // Controlli UI
    // -----------------------------------------------------------------------
    private readonly DataGridView        _grid;
    private readonly DataGridViewColumn  _colTimestamp;
    private readonly DataGridViewColumn  _colProcess;
    private readonly DataGridViewColumn  _colIp;
    private readonly DataGridViewColumn  _colMetric;
    private readonly DataGridViewColumn  _colValue;
    private readonly DataGridViewColumn  _colThreshold;
    private readonly ToolStripStatusLabel _lblCount;

    // Numero massimo di righe conservate in memoria
    private const int MaxRows = 500;

    // Colori per tipo di metrica
    private static readonly Color ColLatenza = Color.FromArgb(255, 210, 210); // rosso pastello
    private static readonly Color ColLoss    = Color.FromArgb(255, 220, 180); // arancio pastello
    private static readonly Color ColJitter  = Color.FromArgb(255, 250, 190); // giallo pastello

    public AlertLogPanel()
    {
        // ---- Toolbar ----
        var toolbar  = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var btnClear = new ToolStripButton("🗑  Cancella log")
        {
            ToolTipText    = "Rimuove tutti gli alert dalla lista",
            DisplayStyle   = ToolStripItemDisplayStyle.Text
        };
        var btnExport = new ToolStripButton("💾  Esporta CSV")
        {
            ToolTipText    = "Salva il log degli alert in un file CSV",
            DisplayStyle   = ToolStripItemDisplayStyle.Text
        };
        _lblCount = new ToolStripStatusLabel("Nessun alert")
        {
            Alignment  = ToolStripItemAlignment.Right,
            ForeColor  = Color.Gray,
            Spring     = true   // occupa lo spazio rimanente nella status bar
        };
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            btnClear,
            new ToolStripSeparator(),
            btnExport
            // _lblCount rimosso dalla toolbar: appartiene solo alla statusBar
        });

        // ---- DataGridView ----
        _colTimestamp = new DataGridViewTextBoxColumn
        {
            Name = "Timestamp", HeaderText = "Timestamp", Width = 155, ReadOnly = true
        };
        _colProcess = new DataGridViewTextBoxColumn
        {
            Name = "Processo", HeaderText = "Processo", Width = 140, ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        };
        _colIp = new DataGridViewTextBoxColumn
        {
            Name = "IP remoto", HeaderText = "IP remoto", Width = 130, ReadOnly = true
        };
        _colMetric = new DataGridViewTextBoxColumn
        {
            Name = "Metrica", HeaderText = "Metrica", Width = 90, ReadOnly = true
        };
        _colValue = new DataGridViewTextBoxColumn
        {
            Name = "Valore", HeaderText = "Valore", Width = 90, ReadOnly = true
        };
        _colThreshold = new DataGridViewTextBoxColumn
        {
            Name = "Soglia", HeaderText = "Soglia", Width = 80, ReadOnly = true
        };

        _grid = new DataGridView
        {
            Dock                  = DockStyle.Fill,
            ReadOnly              = true,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible     = false,
            SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.None,
            BackgroundColor       = SystemColors.Window,
            BorderStyle           = BorderStyle.None,
            ColumnHeadersHeight   = 26,
            RowTemplate           = { Height = 22 }
        };
        _grid.Columns.AddRange(
            _colTimestamp, _colProcess, _colIp, _colMetric, _colValue, _colThreshold
        );
        // Colorazione per riga in base alla metrica
        _grid.RowPrePaint += OnRowPrePaint;

        // ---- Layout — ORDINE CRITICO: Bottom/Top prima di Fill ----
        // WinForms elabora il docking nell'ordine inverso di Controls.Add:
        // statusBar (Bottom) e toolbar (Top) devono essere aggiunti PRIMA di
        // _grid (Fill), altrimenti Fill occupa tutto e li sovrappone.
        var statusBar  = new StatusStrip();
        statusBar.Items.Add(_lblCount);

        Controls.Add(statusBar);  // 1° Bottom
        Controls.Add(toolbar);    // 2° Top
        Controls.Add(_grid);      // 3° Fill — DEVE essere aggiunto per ultimo

        // ---- Handler ----
        btnClear.Click  += (_, _) => ClearLog();
        btnExport.Click += (_, _) => EsportaCsv();
    }

    // -----------------------------------------------------------------------
    // API pubblica
    // -----------------------------------------------------------------------

    /// <summary>
    /// Aggiunge un alert alla cima della lista.
    /// Deve essere chiamato sul thread UI (AlertEngine → MainForm → Invoke → qui).
    /// </summary>
    public void AddAlert(NetworkAlert alert)
    {
        // Inserisce in testa per visibilità immediata
        _grid.Rows.Insert(0,
            alert.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            alert.Process,
            alert.RemoteIp,
            alert.MetricLabel,
            $"{alert.Value:F1} {alert.Unit}",
            $"{alert.Threshold:F0} {alert.Unit}"
        );
        // Associa il tipo metrica al tag della riga per la colorazione
        if (_grid.Rows.Count > 0)
            _grid.Rows[0].Tag = alert.Metric;

        // Limita il numero di righe per evitare crescita illimitata in memoria
        while (_grid.Rows.Count > MaxRows)
            _grid.Rows.RemoveAt(_grid.Rows.Count - 1);

        AggiornaConto();
    }

    /// <summary>Svuota il log.</summary>
    public void ClearLog()
    {
        _grid.Rows.Clear();
        AggiornaConto();
    }

    // -----------------------------------------------------------------------
    // Colorazione righe
    // -----------------------------------------------------------------------

    private void OnRowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is AlertMetric metric)
        {
            var color = metric switch
            {
                AlertMetric.Latenza => ColLatenza,
                AlertMetric.Perdita => ColLoss,
                AlertMetric.Jitter  => ColJitter,
                _                   => SystemColors.Window
            };
            // Applica solo se la riga non è selezionata (evita sovrascrittura colore selezione)
            if (!row.Selected)
            {
                foreach (DataGridViewCell cell in row.Cells)
                    cell.Style.BackColor = color;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Export CSV
    // -----------------------------------------------------------------------

    private void EsportaCsv()
    {
        if (_grid.Rows.Count == 0)
        {
            MessageBox.Show("Nessun alert da esportare.", "NetManCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title            = "Salva log alert",
            Filter           = "CSV (*.csv)|*.csv",
            FileName         = $"NetManCat_alert_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt       = "csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp;Processo;IP_remoto;Metrica;Valore;Soglia");
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var cells = row.Cells;
                sb.AppendLine(string.Join(";",
                    cells[0].Value, cells[1].Value, cells[2].Value,
                    cells[3].Value, cells[4].Value, cells[5].Value));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"Log esportato:\n{dlg.FileName}", "NetManCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore export: {ex.Message}", "NetManCat",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private void AggiornaConto()
    {
        int n = _grid.Rows.Count;
        _lblCount.Text = n == 0 ? "Nessun alert" : $"{n} alert";
    }
}
