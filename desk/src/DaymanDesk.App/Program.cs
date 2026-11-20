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
                MessageBox.Show("دايماً ديسك يعمل الآن. افتحه من الأيقونة بجانب الساعة.", "دايماً ديسك",
                    MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var rt = new Runtime();
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
            var store = new ConfigStore(cfgPath); // falls back to config.json.bak
            var cfg = store.Current;
            if (string.IsNullOrEmpty(cfg.PrinterName)) return 0;
            bool listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(e => e.Port == DeskConfig.ListenPort && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any)));
            if (listening) return 0;
            var log = new RollingLog(Path.Combine(Runtime.DataDir, "logs"));
            if (store.LoadedFromBackup) log.Warn("watchdog: config.json unreadable, using config.json.bak");
            try { Watchdog.Run(cfg, new PrinterSetup(new PowerShellShell(log), log), listening, log); }
            catch (Exception ex) { log.Error("watchdog: restore not confirmed: " + ex.Message); return 1; }
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
