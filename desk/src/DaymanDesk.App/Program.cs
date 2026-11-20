using System.Net;
using System.Net.NetworkInformation;
using DaymanDesk.Core;

namespace DaymanDesk.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ReceiptAnalyzer.EnsureCodePages();
        if (args.Contains("--watchdog")) return RunWatchdog();

        using var single = new Mutex(true, @"Global\DaymanDesk.App", out bool first);
        if (!first)
        {
            if (!args.Contains("--tray"))
                MessageBox.Show("دايماً ديسك يعمل الآن. افتحه من الأيقونة بجانب الساعة.\n\nلتثبيت إصدار جديد: اختر «خروج (إرجاع الطابعة)» من الأيقونة، ثم شغّل هذا الملف مرة أخرى.", "دايماً ديسك",
                    MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var rt = new Runtime();
        // CI only: renders every tab and the «تأكيد الحقول» dialog to PNG files (no printer, no network, no Start).
        int shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0 && shots + 1 < args.Length) return Screenshots.Run(rt, args[shots + 1]);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            rt.Log.Error("CRASH " + e.ExceptionObject);
            try { rt.Agent.ReportAsync("error", "crash", e.ExceptionObject.ToString() ?? "crash").Wait(3000); } catch { }
            rt.ExitRestore();
        };
        Application.ThreadException += (_, e) =>
        {
            rt.Log.Error("UI error " + e.Exception);
            _ = rt.Agent.ReportAsync("error", "crash", "ui: " + e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { rt.Log.Error("task error " + e.Exception.Message); e.SetObserved(); };

        rt.Start();
        Application.Run(new MainForm(rt, startHidden: args.Contains("--tray")));
        return 0;
    }

    /// <summary>Run by the scheduled task every minute as SYSTEM. Cheap when all is well: one socket table read.</summary>
    static int RunWatchdog()
    {
        try
        {
            var cfgPath = Path.Combine(Runtime.DataDir, "config.json");
            if (!File.Exists(cfgPath) && !File.Exists(cfgPath + ".bak")) return 0;
            // No token protector and no Update: the watchdog never writes config.json, so it never writes the token.
            var store = new ConfigStore(cfgPath); // falls back to config.json.bak
            var cfg = store.Current;
            if (cfg.Printers.Count == 0 && !cfg.Network.Enabled) return 0;
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            bool Listening(int port) => listeners.Any(e => e.Port == port && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any)));
            bool networkDown = Watchdog.NetworkDown(cfg, Listening);
            if (cfg.Printers.All(r => Listening(r.ListenPort)) && !networkDown) return 0;
            var log = new RollingLog(Path.Combine(Runtime.DataDir, "logs"));
            if (store.LoadedFromBackup) log.Warn("watchdog: config.json unreadable, using config.json.bak");
            var shell = new PowerShellShell(log);
            if (networkDown)
            {
                // Network mode has no Windows port to restore (the address is set on the tablet): start the program again.
                log.Warn($"watchdog: nothing listens on network-mode port {cfg.Network.ListenPort}, starting the tray task");
                shell.Run(ScheduledTasks.StartTrayScript());
            }
            try { Watchdog.Run(cfg, new PrinterSetup(shell, log), Listening, log); }
            catch (Exception ex) { log.Error("watchdog: restore not confirmed: " + ex.Message); return 1; }
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
