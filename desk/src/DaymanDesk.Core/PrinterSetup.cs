using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DaymanDesk.Core;

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

public interface IShell
{
    ShellResult Run(string script);
}

/// <summary>Runs Windows PowerShell 5.1 with the script passed as -EncodedCommand (no quoting problems).</summary>
public sealed class PowerShellShell : IShell
{
    readonly RollingLog? _log;
    public PowerShellShell(RollingLog? log) { _log = log; }

    public const string Prelude =
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8;\n";

    public ShellResult Run(string script)
    {
        _log?.Info("ps> " + script.Replace("\n", " ; "));
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(90_000))
            {
                try { p.Kill(true); } catch { }
                _log?.Error("ps timeout");
                return new ShellResult(-1, "", "timeout");
            }
            var r = new ShellResult(p.ExitCode, outTask.Result.Trim(), errTask.Result.Trim());
            if (r.Ok) _log?.Info("ps ok" + (r.Stdout.Length > 0 ? ": " + Trunc(r.Stdout) : ""));
            else _log?.Error($"ps exit {r.ExitCode}: {Trunc(r.Stderr)}");
            return r;
        }
        catch (Exception ex)
        {
            _log?.Error("ps start failed: " + ex.Message);
            return new ShellResult(-1, "", ex.Message);
        }
    }

    static string Trunc(string s) => s.Length > 600 ? s[..600] + "..." : s;
}

public sealed record PrinterInfo(string Name, string PortName, string DriverName);

public sealed class SetupException : Exception
{
    public SetupException(string arabic, string? detail = null) : base(arabic + (detail != null ? "\n" + detail : "")) { Arabic = arabic; }
    public string Arabic { get; }
}

/// <summary>
/// The port switch: the POS printer goes to DAYMAN_9100 (127.0.0.1:9100), a raw queue keeps the original
/// port. Every change can be undone with Restore/Uninstall, and Install undoes itself on any failure.
/// </summary>
public sealed class PrinterSetup
{
    public const string GenericDriver = "Generic / Text Only";
    readonly IShell _sh;
    readonly RollingLog? _log;

    public PrinterSetup(IShell sh, RollingLog? log) { _sh = sh; _log = log; }

    /// <summary>A PowerShell single-quoted literal. PowerShell also treats curly quotes as quotes.</summary>
    public static string Q(string s)
    {
        var sb = new StringBuilder("'");
        foreach (char c in s)
        {
            if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    public List<PrinterInfo> ListPrinters()
    {
        var r = _sh.Run("ConvertTo-Json -Compress -InputObject @(Get-Printer | Select-Object Name,PortName,DriverName)");
        if (!r.Ok) throw new SetupException("تعذّرت قراءة قائمة الطابعات.", r.Stderr);
        return ParsePrinters(r.Stdout);
    }

    public static List<PrinterInfo> ParsePrinters(string json)
    {
        var list = new List<PrinterInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : new[] { doc.RootElement };
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (S("Name").Length > 0) list.Add(new PrinterInfo(S("Name"), S("PortName"), S("DriverName")));
        }
        return list;
    }

    public string? GetPort(string printer)
    {
        var r = _sh.Run($"(Get-Printer -Name {Q(printer)}).PortName");
        return r.Ok ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// Installs the interception for <paramref name="printer"/>. <paramref name="startListener"/> must start the
    /// 127.0.0.1:9100 listener and return true before the POS printer is moved to it.
    /// </summary>
    public void Install(ConfigStore store, string printer, Func<bool> startListener)
    {
        var cfg = store.Current;
        var list = ListPrinters();
        var p = list.FirstOrDefault(x => x.Name == printer) ?? throw new SetupException("لم تُوجد الطابعة المختارة.");
        if (p.Name.StartsWith("Dayman Raw (", StringComparison.Ordinal))
            throw new SetupException("اختر طابعة الفواتير نفسها، لا طابعة دايماً.");

        string original, driver;
        if (p.PortName == DeskConfig.TcpPortName)
        {
            if (cfg.PrinterName != printer || string.IsNullOrEmpty(cfg.OriginalPort))
                throw new SetupException("هذه الطابعة موجّهة لدايماً ديسك ولا يُعرف منفذها الأصلي. أعد منفذها يدوياً من إعدادات الطابعة.");
            original = cfg.OriginalPort!;
            driver = cfg.OriginalDriver ?? p.DriverName;
        }
        else
        {
            if (cfg.Active && cfg.PrinterName != null && cfg.PrinterName != printer)
                throw new SetupException("دايماً ديسك يعمل على طابعة أخرى. أوقفه أولاً.");
            original = p.PortName;
            driver = p.DriverName;
        }
        string raw = DeskConfig.RawQueueFor(printer);
        store.Update(c => { c.PrinterName = printer; c.OriginalPort = original; c.OriginalDriver = driver; c.RawQueueName = raw; });
        _log?.Info($"install: printer port {original}, driver {driver}");

        try
        {
            Must($"if (-not (Get-PrinterDriver -Name {Q(GenericDriver)} -ErrorAction SilentlyContinue)) {{ Add-PrinterDriver -Name {Q(GenericDriver)} }}",
                "تعذّر تثبيت تعريف Generic / Text Only.");
            Must($"if (-not (Get-Printer -Name {Q(raw)} -ErrorAction SilentlyContinue)) {{ Add-Printer -Name {Q(raw)} -DriverName {Q(GenericDriver)} -PortName {Q(original)} }}",
                "تعذّر إنشاء طابعة دايماً على منفذ الطابعة الأصلي.");
            Must($"if (-not (Get-PrinterPort -Name {Q(DeskConfig.TcpPortName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name {Q(DeskConfig.TcpPortName)} -PrinterHostAddress '127.0.0.1' -PortNumber {DeskConfig.ListenPort} }}",
                "تعذّر إنشاء المنفذ 127.0.0.1:9100.");
            if (!startListener())
                throw new SetupException("المنفذ 9100 على هذا الجهاز مستخدم من برنامج آخر.");
            Must($"Set-Printer -Name {Q(printer)} -PortName {Q(DeskConfig.TcpPortName)}",
                "تعذّر توجيه طابعة الفواتير إلى دايماً ديسك.");
            store.Update(c => c.Active = true);
            _log?.Info("install: done");
        }
        catch (Exception ex)
        {
            _log?.Error("install failed, rolling back: " + ex.Message);
            try { Uninstall(store); } catch (Exception rex) { _log?.Error("rollback failed: " + rex.Message); }
            if (ex is SetupException) throw;
            throw new SetupException("حدث خطأ أثناء التشغيل وأُعيدت الطابعة كما كانت.", ex.Message);
        }
    }

    void Must(string script, string arabic)
    {
        var r = _sh.Run(script);
        if (!r.Ok) throw new SetupException(arabic, r.Stderr);
    }

    /// <summary>
    /// Puts the POS printer back on its original port if it is on DAYMAN_9100, and confirms it by reading the
    /// port again. Returns true if it changed it. Throws when the port cannot be read or is not the original after.
    /// </summary>
    public bool Restore(DeskConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.PrinterName) || string.IsNullOrEmpty(cfg.OriginalPort)) return false;
        var port = GetPort(cfg.PrinterName)
            ?? throw new SetupException("تعذّرت قراءة منفذ طابعة الفواتير، فلم نتأكد من إرجاعها.");
        if (port != DeskConfig.TcpPortName) return false;
        var r = _sh.Run($"Set-Printer -Name {Q(cfg.PrinterName)} -PortName {Q(cfg.OriginalPort)}");
        if (!r.Ok) throw new SetupException("تعذّر إرجاع الطابعة إلى منفذها الأصلي: " + cfg.OriginalPort, r.Stderr);
        var after = GetPort(cfg.PrinterName);
        if (after != cfg.OriginalPort)
            throw new SetupException("لم نتأكد من إرجاع الطابعة إلى منفذها الأصلي: " + cfg.OriginalPort, "port now: " + (after ?? "unreadable"));
        _log?.Info("restored original port " + cfg.OriginalPort);
        return true;
    }

    /// <summary>Moves the POS printer to DAYMAN_9100 (the listener must already run).</summary>
    public bool Assign(DeskConfig cfg)
    {
        if (!cfg.Active || string.IsNullOrEmpty(cfg.PrinterName)) return false;
        var port = GetPort(cfg.PrinterName);
        if (port == null || port == DeskConfig.TcpPortName) return false;
        var r = _sh.Run($"Set-Printer -Name {Q(cfg.PrinterName)} -PortName {Q(DeskConfig.TcpPortName)}");
        if (!r.Ok) throw new SetupException("تعذّر توجيه طابعة الفواتير إلى دايماً ديسك.", r.Stderr);
        _log?.Info("assigned DAYMAN_9100");
        return true;
    }

    /// <summary>
    /// Restores the port and, only once that is confirmed, removes the raw queue and the TCP port.
    /// Throws (and leaves everything in place) when the restore is not confirmed. Safe to run twice.
    /// </summary>
    public void Uninstall(ConfigStore store)
    {
        var cfg = store.Current;
        Restore(cfg);
        if (!string.IsNullOrEmpty(cfg.PrinterName) && !string.IsNullOrEmpty(cfg.OriginalPort))
        {
            var port = GetPort(cfg.PrinterName);
            if (port == null || port == DeskConfig.TcpPortName)
                throw new SetupException("لم نتأكد من إرجاع الطابعة إلى منفذها الأصلي: " + cfg.OriginalPort);
        }
        if (!string.IsNullOrEmpty(cfg.RawQueueName))
            _sh.Run($"if (Get-Printer -Name {Q(cfg.RawQueueName)} -ErrorAction SilentlyContinue) {{ Remove-Printer -Name {Q(cfg.RawQueueName)} }}");
        _sh.Run($"if ((Get-PrinterPort -Name {Q(DeskConfig.TcpPortName)} -ErrorAction SilentlyContinue) -and -not (Get-Printer | Where-Object PortName -eq {Q(DeskConfig.TcpPortName)})) {{ Start-Sleep -Seconds 2; Remove-PrinterPort -Name {Q(DeskConfig.TcpPortName)} }}");
        store.Update(c => c.Active = false);
    }
}

/// <summary>The once-a-minute safety net: a POS printer left on DAYMAN_9100 with nothing listening goes back.</summary>
public static class Watchdog
{
    public static bool Run(DeskConfig cfg, PrinterSetup setup, bool listening, RollingLog log)
    {
        if (listening || string.IsNullOrEmpty(cfg.PrinterName)) return false;
        if (setup.Restore(cfg))
        {
            log.Warn("watchdog: nothing listens on 9100, original port restored");
            return true;
        }
        return false;
    }
}
