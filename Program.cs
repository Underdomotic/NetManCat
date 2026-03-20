using NetManCat.Core;
using NetManCat.UI;

namespace NetManCat;

internal static class Program
{
    private static SplashForm? _splash;
    private static string      _lastStep = "Avvio";

    [STAThread]
    static void Main()
    {
        // ── Gestori globali di eccezioni ────────────────────────────────
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException                    += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException     += OnUnhandledException;

        ApplicationConfiguration.Initialize();

        // ── Splash screen (thread STA separato → appare istantaneamente) 
        ShowSplash();

        try
        {
            ReportStep("Caricamento configurazione…", 10);
            var cfg = new ConfigManager();
            cfg.Load();

            ReportStep("Avvio interfaccia…", 40);
            var mainForm = new MainForm(cfg, ReportStep);

            ReportStep("Pronto ✓", 100);
            Thread.Sleep(280);

            CloseSplash();
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            CloseSplash();
            ShowCrash(ex, _lastStep);
        }
    }

    // ── Splash helpers ───────────────────────────────────────────────────

    private static void ShowSplash()
    {
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            _splash        = new SplashForm();
            _splash.Shown += (_, _) => ready.Set();
            Application.Run(_splash);
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(3000); // attende che la splash sia visibile (max 3 s)
    }

    private static void ReportStep(string msg, int percent)
    {
        _lastStep = msg;
        try { _splash?.ReportStep(msg, percent); }
        catch { /* splash potrebbe essere già chiusa */ }
    }

    private static void CloseSplash()
    {
        try { _splash?.CloseSplash(); }
        catch { }
    }

    // ── Crash handlers ───────────────────────────────────────────────────

    private static void OnThreadException(object sender,
        System.Threading.ThreadExceptionEventArgs e)
        => ShowCrash(e.Exception, _lastStep);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ShowCrash(e.ExceptionObject as Exception, _lastStep);
        Environment.Exit(1);
    }

    private static void ShowCrash(Exception? ex, string context = "")
    {
        try
        {
            // Salva su file come fallback (anche se la UI non parte)
            try
            {
                string log = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "crash.log");
                System.IO.File.WriteAllText(log,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n" +
                    $"{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}");
            }
            catch { }

            using var dlg = new CrashReporterForm(ex, context);
            dlg.ShowDialog();
        }
        catch { /* ultimo fallback: MessageBox */ 
            try { MessageBox.Show(ex?.Message, "Errore critico"); } catch { }
        }
    }
}
