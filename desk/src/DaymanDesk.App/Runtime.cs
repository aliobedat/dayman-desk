using System.Globalization;
using System.Reflection;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>Owns the long-lived parts: config, log, listeners, agent, heartbeat.</summary>
sealed class Runtime : IDisposable
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DaymanDesk");
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(5);

    public readonly RollingLog Log;
    public readonly ConfigStore Cfg;
    public readonly SampleStore Samples;
    public readonly OfflineQueue Queue;
    public readonly IShell Shell;
    public readonly PrinterSetup Setup;
    public readonly PassThroughServer Server;
    public readonly WindowsOcr Ocr;
    readonly HttpClient _http;
    System.Threading.Timer? _heartbeat;
    int _ocrInstallStarted;
    public DeskAgent Agent { get; private set; }

    public event Action<ReceiptRecord>? ReceiptProcessed;
    public event Action? StateChanged;
    public event Action<FieldConfirmation>? ConfirmationNeeded;
    public event Action<string, string>? UpdateAvailable;

    public Runtime()
    {
        Directory.CreateDirectory(DataDir);
        Log = new RollingLog(Path.Combine(DataDir, "logs"));
        Installer.LockDataDir(Log); // before the token is ever written
        Cfg = OpenConfig(Log);
        Samples = new SampleStore(Path.Combine(DataDir, "samples"));
        Queue = new OfflineQueue(Path.Combine(DataDir, "queue.json"));
        Shell = new PowerShellShell(Log);
        Setup = new PrinterSetup(Shell, Log);
        Ocr = new WindowsOcr(Log);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaymanDesk/" + Version);
        Server = new PassThroughServer(new WinspoolQueue(), Log, OnForwardFailed);
        Server.JobForwarded += OnJob;
        Server.PrinterUnreachable += OnNetworkPrinterUnreachable;
        Agent = BuildAgent();
        if (Cfg.TokenUnreadable) Agent.SetProblem(Problems.TokenUnreadable);
    }

    /// <summary>
    /// config.json with the token sealed by DPAPI. A plain v0 token is sealed only by MigrateToken, once this
    /// version runs from Program Files: if the self-install fails, the old installed copy still reads it.
    /// </summary>
    public static ConfigStore OpenConfig(RollingLog log)
    {
        var store = new ConfigStore(Path.Combine(DataDir, "config.json"), new DpapiProtector(), migrateNow: false);
        if (store.LoadedFromBackup) log.Warn("config.json unreadable: loaded config.json.bak");
        if (store.TokenUnreadable) log.Error("device token could not be opened (DPAPI): pair again");
        return store;
    }

    /// <summary>Seals a plain v0 token. Call only when this version is the installed one.</summary>
    void MigrateToken()
    {
        bool wasMigrated = Cfg.TokenMigrated;
        if (!Cfg.MigrateToken()) Log.Warn("device token not sealed yet (DPAPI failed): plain v0 token kept, retried on the next save");
        else if (Cfg.TokenMigrated && !wasMigrated) Log.Info("device token sealed with DPAPI (migrated from plain)");
    }

    DeskAgent BuildAgent()
    {
        var a = new DeskAgent(Cfg, new DeskApiClient(_http, Cfg.Current.ServerUrl), Queue, Log, PrintSlipIfCurrent, Version, ocr: Ocr);
        a.ReceiptProcessed += r => { ReceiptProcessed?.Invoke(r); StateChanged?.Invoke(); };
        a.ConfirmationNeeded += p => ConfirmationNeeded?.Invoke(p);
        a.UpdateAvailable += (v, u) => UpdateAvailable?.Invoke(v, u);
        return a;
    }

    /// <summary>Only an absolute https URL; a new host means pairing again. Returns false when refused.</summary>
    public bool SetServerUrl(string url)
    {
        if (!Cfg.SetServerUrl(url)) return false;
        Agent = BuildAgent();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>True while at least one Windows printer or «وضع الشبكة» runs through the program.</summary>
    public bool On => (Cfg.Current.Active && Cfg.Current.Printers.Any(r => Server.IsRunning(r.Id)))
        || (Cfg.Current.Network.Route() is { } n && Server.IsRunning(n.Id));

    /// <summary>At program start: listen first, then (if the owner left it on) point the POS printers at us.</summary>
    public void Start()
    {
        var cfg = Cfg.Current;
        Log.Info($"start v{Version}, active {cfg.Active}, printers {cfg.Printers.Count}, network {cfg.Network.Enabled}, paired {cfg.Paired}");
        if (Installer.RunningInstalled) MigrateToken();
        if (cfg.Active || cfg.Network.Enabled)
        {
            // An update: the owner closed the old version and ran the new file from Downloads. Install it over the
            // old copy so the tasks start this version. A failure keeps the old copy and its tasks (still safe).
            if (!Installer.RunningInstalled)
            {
                // The watchdog runs the installed exe for under a second each minute: retry a locked file.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        Installer.EnsureInstalled(Shell, Log);
                        Log.Info("update: installed this version to Program Files");
                        Agent.ClearProblem(Problems.UpdateInstallFailed);
                        MigrateToken();
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"update install failed (attempt {attempt}): " + ex.Message);
                        if (attempt < 3) Thread.Sleep(3000);
                        // The owner sees why in the status window (he just ran the new file, so it is open).
                        else Agent.SetProblem(Problems.UpdateInstallFailed, ex is SetupException se ? se.Arabic : null);
                    }
                }
            }
            if (cfg.Active) StartPrinters();
            StartNetwork();
            EnsureOcrLanguages();
        }
        _heartbeat = new System.Threading.Timer(_ => _ = SafeHeartbeat(), null, TimeSpan.FromSeconds(15), HeartbeatEvery);
        StateChanged?.Invoke();
    }

    /// <summary>Each Windows route: its listener first, then its POS printer. A busy port: that printer prints directly.</summary>
    void StartPrinters()
    {
        var cfg = Cfg.Current;
        foreach (var r in cfg.Printers.ToList())
        {
            if (Server.Start(r)) continue;
            Agent.SetProblem(Problems.ListenerBusy, r.ListenPort.ToString(CultureInfo.InvariantCulture));
            try { Setup.Restore(r); }
            catch (Exception ex) { Log.Error("restore: " + ex.Message); Agent.SetProblem(Problems.RestoreFailed); }
        }
        try { Setup.Assign(cfg, r => Server.IsRunning(r.Id)); }
        catch (Exception ex) { Log.Error("assign at start: " + ex.Message); }
    }

    void StartNetwork()
    {
        var route = Cfg.Current.Network.Route();
        if (route == null || Server.Start(route)) return;
        Agent.SetProblem(Problems.ListenerBusy, route.ListenPort.ToString(CultureInfo.InvariantCulture));
    }

    async Task SafeHeartbeat()
    {
        try { await Agent.HeartbeatAsync(); StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("heartbeat: " + ex.Message); }
    }

    public void Install(IReadOnlyList<string> printers)
    {
        Installer.EnsureInstalled(Shell, Log);
        MigrateToken();
        // A stopped earlier set may still listen on old ports: start clean (a running set keeps its ports).
        if (!Cfg.Current.Active) foreach (var r in Cfg.Current.Printers) Server.Stop(r.Id);
        Setup.Install(Cfg, printers, r => Server.Start(r));
        Agent.ClearProblem(Problems.ForwardFailed, Problems.ListenerBusy, Problems.RestoreFailed, Problems.UpdateInstallFailed);
        EnsureOcrLanguages();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// «وضع الشبكة» on or off. On: validate, install the program (it must start at logon), open the firewall for the
    /// local subnet, then listen. Throws SetupException with the Arabic reason.
    /// </summary>
    public void SetNetworkMode(bool enabled, string? host, int listenPort, int printerPort)
    {
        try
        {
            Server.Stop(new PrinterRoute { Lan = true }.Id);
            if (!enabled)
            {
                Shell.Run(Firewall.RemoveScript);
                Cfg.Update(c => c.Network.Enabled = false);
                Agent.ClearProblem(Problems.PrinterUnreachable, Problems.ListenerBusy);
                return;
            }
            var routes = Cfg.Current.Active ? Cfg.Current.Printers : new List<PrinterRoute>();
            var err = PassThroughServer.ValidateNetwork(host, listenPort, printerPort, routes, PassThroughServer.LocalIPv4());
            if (err != null) throw new SetupException(err);
            Installer.EnsureInstalled(Shell, Log);
            MigrateToken();
            var fw = Shell.Run(Firewall.AddScript(listenPort));
            if (!fw.Ok) throw new SetupException("تعذّر فتح المنفذ في جدار حماية Windows.", fw.Stderr);
            Cfg.Update(c =>
            {
                c.Network.Enabled = true; c.Network.PrinterHost = host!.Trim();
                c.Network.ListenPort = listenPort; c.Network.PrinterPort = printerPort;
            });
            if (!Server.Start(Cfg.Current.Network.Route()!))
            {
                Cfg.Update(c => c.Network.Enabled = false);
                Shell.Run(Firewall.RemoveScript);
                throw new SetupException($"المنفذ {listenPort} على هذا الجهاز مستخدم من برنامج آخر. اختر منفذاً آخر.");
            }
            Agent.ClearProblem(Problems.ListenerBusy);
            EnsureOcrLanguages();
        }
        finally { StateChanged?.Invoke(); }
    }

    /// <summary>
    /// Adds the Arabic and English OCR languages if missing (Windows Update download, can take minutes), once per
    /// run, in the background. Best effort: a failure is logged and reported; English digits still read.
    /// </summary>
    void EnsureOcrLanguages()
    {
        if (Interlocked.Exchange(ref _ocrInstallStarted, 1) == 1) return;
        Task.Run(() =>
        {
            var r = new PowerShellShell(Log, timeoutMs: 20 * 60_000).Run(OcrLanguages.InstallScript());
            Ocr.Reset();
            if (r.Ok) Log.Info("ocr languages: " + r.Stdout);
            else
            {
                var why = (r.Stdout + " " + r.Stderr).Trim();
                _ = Agent.ReportAsync("warn", "other", "ocr language install failed: " + (why.Length > 600 ? why[..600] : why));
            }
        });
    }

    /// <summary>«تأكيد الحقول» from the window. Null on success, else the Arabic reason.</summary>
    public string? ConfirmFields(FieldConfirmation p, int invoiceLine, int totalLine)
    {
        var r = Agent.ConfirmFields(p, invoiceLine, totalLine);
        StateChanged?.Invoke();
        return r;
    }

    public void RequestReconfirm()
    {
        Agent.RequestReconfirm();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// «إيقاف وإرجاع الطابعة». Uninstall throws unless every original port is confirmed; only then the listeners
    /// stop and the tasks go. On a throw everything keeps running and the UI shows the error.
    /// </summary>
    public void StopAndRestore()
    {
        try
        {
            Setup.Uninstall(Cfg);
            // «وضع الشبكة» goes too: listener, firewall rule, setting.
            Server.StopAll();
            Shell.Run(Firewall.RemoveScript);
            Cfg.Update(c => c.Network.Enabled = false);
            Installer.RemoveTasks(Shell);
        }
        finally { StateChanged?.Invoke(); }
    }

    /// <summary>Normal exit: the printers go back to their own ports; the next start re-assigns them.</summary>
    public void ExitRestore()
    {
        _heartbeat?.Dispose();
        try { Setup.Restore(Cfg.Current); }
        catch (Exception ex) { Log.Error("restore at exit not confirmed (the watchdog will retry): " + ex.Message); }
        Server.StopAll();
        Log.Info("exit");
    }

    void OnJob(ForwardedJob j)
    {
        try { Samples.Save(j.Data, DateTimeOffset.Now); }
        catch (Exception ex) { Log.Warn("sample save: " + ex.Message); }
        _ = Agent.ProcessJobAsync(j.Data, j.ForwardMs, j.Seq, j.PrinterUnreachable);
    }

    /// <summary>Prints the QR slip on the receipt's own printer, only if no job reached that printer after it.</summary>
    bool PrintSlipIfCurrent(long seq, byte[] slip)
    {
        try { return Server.PrintIfCurrent(seq, slip); }
        catch (Exception ex)
        {
            Log.Error("qr print failed: " + ex.Message);
            Agent.SetProblem(Problems.QrPrintFailed);
            return false;
        }
    }

    void OnNetworkPrinterUnreachable(PrinterRoute route, string why)
    {
        Agent.SetProblem(Problems.PrinterUnreachable, route.DirectHost);
        _ = Agent.ReportAsync("warn", "other", $"network-mode printer unreachable ({why}): the POS connection was refused");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A raw queue refused a job. Every printer goes back to its own port; the failed job prints directly on its
    /// printer if that restore is confirmed. A route whose restore is not confirmed stops listening, so the watchdog
    /// restores it within a minute. Windows interception stays off until the owner presses «تشغيل» again.
    /// </summary>
    void OnForwardFailed(PrinterRoute failed, byte[] data)
    {
        var cfg = Cfg.Current;
        bool restored = true;
        foreach (var r in cfg.Printers.ToList())
        {
            try { Setup.Restore(r); }
            catch (Exception ex)
            {
                if (r.Id == failed.Id) restored = false;
                Log.Error($"fallback restore of {r.PrinterName} not confirmed, stopping its listener for the watchdog: " + ex.Message);
                Server.Stop(r.Id);
            }
        }
        if (restored && failed.PrinterName != null)
        {
            try
            {
                RawPrinter.Write(failed.PrinterName, data, "Dayman Desk (fallback)");
                Log.Warn("fallback: printer restored and job printed directly");
            }
            catch (Exception ex) { Log.Error("fallback print failed: " + ex.Message); }
        }
        Cfg.Update(c => c.Active = false);
        Agent.SetProblem(restored ? Problems.ForwardFailed : Problems.RestoreFailed);
        _ = Agent.ReportAsync("error", "other", restored ? "forward failed: printers restored, job printed directly" : "forward failed: restore not confirmed, listener stopped");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _http.Dispose();
    }
}
