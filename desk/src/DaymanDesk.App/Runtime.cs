using System.Reflection;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>Owns the long-lived parts: config, log, listener, agent, heartbeat.</summary>
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
    readonly HttpClient _http;
    System.Threading.Timer? _heartbeat;
    public DeskAgent Agent { get; private set; }

    public event Action<ReceiptRecord>? ReceiptProcessed;
    public event Action? StateChanged;

    public Runtime()
    {
        Directory.CreateDirectory(DataDir);
        Log = new RollingLog(Path.Combine(DataDir, "logs"));
        Installer.LockDataDir(Log); // before the token is ever written
        Cfg = new ConfigStore(Path.Combine(DataDir, "config.json"));
        if (Cfg.LoadedFromBackup) Log.Warn("config.json unreadable: loaded config.json.bak");
        Samples = new SampleStore(Path.Combine(DataDir, "samples"));
        Queue = new OfflineQueue(Path.Combine(DataDir, "queue.json"));
        Shell = new PowerShellShell(Log);
        Setup = new PrinterSetup(Shell, Log);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaymanDesk/" + Version);
        Server = new PassThroughServer(() => Cfg.Current.RawQueueName, Log, OnForwardFailed);
        Server.JobForwarded += OnJob;
        Agent = BuildAgent();
    }

    DeskAgent BuildAgent()
    {
        var a = new DeskAgent(Cfg, new DeskApiClient(_http, Cfg.Current.ServerUrl), Queue, Log, PrintSlipIfCurrent, Version);
        a.ReceiptProcessed += r => { ReceiptProcessed?.Invoke(r); StateChanged?.Invoke(); };
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

    /// <summary>At program start: listen first, then (if the owner left it on) point the POS printer at us.</summary>
    public void Start()
    {
        Log.Info($"start v{Version}, active {Cfg.Current.Active}, paired {Cfg.Current.Paired}");
        if (Cfg.Current.Active)
        {
            if (Server.Start())
            {
                try { Setup.Assign(Cfg.Current); }
                catch (Exception ex) { Log.Error("assign at start: " + ex.Message); }
            }
            else
            {
                // Someone else holds 9100: make sure the POS printer prints directly.
                try { Setup.Restore(Cfg.Current); } catch (Exception ex) { Log.Error("restore: " + ex.Message); }
            }
        }
        _heartbeat = new System.Threading.Timer(_ => _ = SafeHeartbeat(), null, TimeSpan.FromSeconds(15), HeartbeatEvery);
        StateChanged?.Invoke();
    }

    async Task SafeHeartbeat()
    {
        try { await Agent.HeartbeatAsync(); StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("heartbeat: " + ex.Message); }
    }

    public void Install(string printer)
    {
        Installer.EnsureInstalled(Shell, Log);
        Setup.Install(Cfg, printer, () => Server.Start());
        StateChanged?.Invoke();
    }

    /// <summary>
    /// «إيقاف وإرجاع الطابعة». Uninstall throws unless the original port is confirmed; only then the listener
    /// stops and the tasks go. On a throw everything keeps running and the UI shows the error.
    /// </summary>
    public void StopAndRestore()
    {
        try
        {
            Setup.Uninstall(Cfg);
            Server.Stop();
            Installer.RemoveTasks(Shell);
        }
        finally { StateChanged?.Invoke(); }
    }

    /// <summary>Normal exit: the printer goes back to its own port; the next start re-assigns it.</summary>
    public void ExitRestore()
    {
        try { Setup.Restore(Cfg.Current); }
        catch (Exception ex) { Log.Error("restore at exit not confirmed (the watchdog will retry): " + ex.Message); }
        Server.Stop();
        Log.Info("exit");
    }

    void OnJob(byte[] data, long ms, long seq)
    {
        try { Samples.Save(data, DateTimeOffset.Now); }
        catch (Exception ex) { Log.Warn("sample save: " + ex.Message); }
        _ = Agent.ProcessJobAsync(data, ms, seq);
    }

    /// <summary>Prints the QR slip only if no job was forwarded after the receipt; check and print under PrintLock.</summary>
    bool PrintSlipIfCurrent(long seq, byte[] slip)
    {
        var q = Cfg.Current.RawQueueName;
        if (q == null) return false;
        try
        {
            lock (PassThroughServer.PrintLock)
            {
                if (Server.Sequence != seq) return false;
                RawPrinter.Write(q, slip, "Dayman QR");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("qr print failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The raw queue refused a job. If the original port is confirmed back, print the job there directly.
    /// If not, stop listening so the watchdog restores the port within a minute. Either way stay off.
    /// </summary>
    void OnForwardFailed(byte[] data)
    {
        var cfg = Cfg.Current;
        bool restored;
        try
        {
            Setup.Restore(cfg);
            restored = true;
        }
        catch (Exception ex)
        {
            restored = false;
            Log.Error("fallback restore not confirmed, stopping the listener for the watchdog: " + ex.Message);
            Server.Stop();
        }
        if (restored && cfg.PrinterName != null)
        {
            try
            {
                lock (PassThroughServer.PrintLock) RawPrinter.Write(cfg.PrinterName, data, "Dayman Desk (fallback)");
                Log.Warn("fallback: printer restored and job printed directly");
            }
            catch (Exception ex) { Log.Error("fallback print failed: " + ex.Message); }
        }
        Cfg.Update(c => c.Active = false); // stay off until the owner presses «تشغيل» again
        _ = Agent.ReportAsync("error", "other", restored ? "forward failed: printer restored, job printed directly" : "forward failed: restore not confirmed, listener stopped");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _http.Dispose();
    }
}
