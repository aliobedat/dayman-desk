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
    readonly int _timeoutMs;
    /// <param name="timeoutMs">90 s for printer changes; longer only for downloads (the OCR languages).</param>
    public PowerShellShell(RollingLog? log, int timeoutMs = 90_000) { _log = log; _timeoutMs = timeoutMs; }

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
            if (!p.WaitForExit(_timeoutMs))
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

/// <summary>A Windows printer queue. Host/PortNumber/Protocol: its port when it is a Standard TCP/IP port (1 = RAW).</summary>
public sealed record PrinterInfo(string Name, string PortName, string DriverName, string? Host = null, int PortNumber = 0, int Protocol = 0)
{
    public bool RawTcp => !string.IsNullOrWhiteSpace(Host) && PortNumber > 0 && Protocol == 1;

    /// <summary>
    /// WO-91: why this queue can never be a receipt printer (PDF/XPS writers, OneNote, fax, a shared printer of another
    /// PC, the nul port), or null. The list shows it with this note and «تشغيل» refuses it.
    /// </summary>
    public string? Unsupported
    {
        get
        {
            string port = PortName.Trim(), name = Name, driver = DriverName;
            if (name.StartsWith(@"\\", StringComparison.Ordinal) || port.StartsWith(@"\\", StringComparison.Ordinal))
                return "طابعة مشتركة من جهاز آخر: ثبّت البرنامج على الجهاز الموصولة به";
            if (port.StartsWith("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) || port.StartsWith("XPSPort:", StringComparison.OrdinalIgnoreCase)
                || driver.Contains("PDF", StringComparison.OrdinalIgnoreCase) || driver.Contains("XPS", StringComparison.OrdinalIgnoreCase))
                return "تحفظ ملفاً ولا تطبع ورقاً";
            if (name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || driver.Contains("OneNote", StringComparison.OrdinalIgnoreCase))
                return "OneNote ليست طابعة";
            if (port.StartsWith("SHRFAX:", StringComparison.OrdinalIgnoreCase) || driver.Contains("Fax", StringComparison.OrdinalIgnoreCase))
                return "فاكس ليس طابعة فواتير";
            if (port.Equals("nul:", StringComparison.OrdinalIgnoreCase) || port.Equals("nul", StringComparison.OrdinalIgnoreCase))
                return "لا منفذ لها";
            return null;
        }
    }

    /// <summary>The FILE: port prints to a file: allowed (for tests on an office PC), with a note.</summary>
    public bool FilePort => PortName.Trim().Equals("FILE:", StringComparison.OrdinalIgnoreCase);
}

public sealed class SetupException : Exception
{
    public SetupException(string arabic, string? detail = null) : base(arabic + (detail != null ? "\n" + detail : "")) { Arabic = arabic; }
    public string Arabic { get; }
}

/// <summary>
/// The port switch, per receipt printer (WO-89): each POS printer goes to DAYMAN_&lt;port&gt; (127.0.0.1:port), a raw
/// queue keeps its original port. Every change can be undone with Restore/Uninstall, and Install undoes itself on
/// any failure.
/// </summary>
public sealed class PrinterSetup
{
    public const string GenericDriver = "Generic / Text Only";
    public const string FirewallRule = "DaymanDeskNetwork";
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

    /// <summary>Every queue with its port; a Standard TCP/IP port adds its host, port number and protocol (1 = RAW).</summary>
    public const string ListScript =
        "ConvertTo-Json -Compress -InputObject @(Get-Printer | ForEach-Object { $pp = Get-PrinterPort -Name $_.PortName -ErrorAction SilentlyContinue; " +
        "[pscustomobject]@{ Name = $_.Name; PortName = $_.PortName; DriverName = $_.DriverName; Host = [string]$pp.PrinterHostAddress; " +
        "PortNumber = [int]$pp.PortNumber; Protocol = [int]$pp.Protocol } })";

    public List<PrinterInfo> ListPrinters()
    {
        var r = _sh.Run(ListScript);
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
            int N(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            if (S("Name").Length > 0)
                list.Add(new PrinterInfo(S("Name"), S("PortName"), S("DriverName"), S("Host").Length > 0 ? S("Host") : null, N("PortNumber"), N("Protocol")));
        }
        return list;
    }

    public string? GetPort(string printer)
    {
        var r = _sh.Run($"(Get-Printer -Name {Q(printer)}).PortName");
        return r.Ok ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// WO-91: true only when Windows answers that the printer does not exist (deleted after the install), as opposed
    /// to a port that could not be read (then the restore is not confirmed).
    /// </summary>
    public bool Missing(string printer)
    {
        // A stopped spooler lists no printer at all: that is «unreadable», never «missing».
        var r = _sh.Run(MissingScript(printer));
        bool missing = r.Ok && r.Stdout.Trim() == "missing";
        if (missing) SkippedMissing = true;
        return missing;
    }

    public static string MissingScript(string printer) =>
        "if ((Get-Service -Name Spooler).Status -ne 'Running') { 'spooler' } " +
        $"elseif (@(Get-Printer -ErrorAction Stop | ForEach-Object {{ $_.Name }}) -contains {Q(printer)}) {{ 'present' }} else {{ 'missing' }}";

    /// <summary>WO-91: a restore since the last reset skipped a printer deleted from Windows (the watchdog keeps watching).</summary>
    public bool SkippedMissing { get; set; }

    /// <summary>The smallest listen port from 9100 that is not taken.</summary>
    public static int FreePort(IEnumerable<int> taken)
    {
        var t = new HashSet<int>(taken);
        int p = DeskConfig.ListenPort;
        while (t.Contains(p)) p++;
        return p;
    }

    /// <summary>
    /// Installs the interception for every printer in <paramref name="printers"/>. <paramref name="startListener"/>
    /// must start the route's 127.0.0.1 listener and return true before its POS printer is moved to it.
    /// All or nothing: any failure restores every printer.
    /// </summary>
    public void Install(ConfigStore store, IReadOnlyList<string> printers, Func<PrinterRoute, bool> startListener)
    {
        var names = printers.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        if (names.Count == 0) throw new SetupException("اختر طابعة فواتير واحدة على الأقل.");
        var cfg = store.Current;
        var old = cfg.Printers.ToList();
        if (cfg.Active && !old.Select(r => r.PrinterName ?? "").OrderBy(x => x).SequenceEqual(names.OrderBy(x => x)))
            throw new SetupException("دايماً ديسك يعمل على طابعات أخرى. أوقفه أولاً من «إيقاف وإرجاع الطابعة».");
        // A printer of an earlier set that is still on a DAYMAN port would lose its original port if dropped.
        foreach (var r in old.Where(r => r.PrinterName != null && !names.Contains(r.PrinterName)))
            if (DeskConfig.IsDaymanPort(GetPort(r.PrinterName!)))
                throw new SetupException($"الطابعة «{r.PrinterName}» ما زالت موجّهة لدايماً ديسك. اضغط «إيقاف وإرجاع الطابعة» أولاً.");

        var list = ListPrinters();
        var taken = new List<int>();
        if (cfg.Network.Enabled) taken.Add(cfg.Network.ListenPort);
        var routes = new List<PrinterRoute>();
        foreach (var name in names)
        {
            var p = list.FirstOrDefault(x => x.Name == name) ?? throw new SetupException($"لم تُوجد الطابعة «{name}».");
            if (p.Name.StartsWith("Dayman Raw (", StringComparison.Ordinal))
                throw new SetupException("اختر طابعة الفواتير نفسها، لا طابعة دايماً.");
            if (p.Unsupported is { } why)
                throw new SetupException($"«{name}» لا تصلح طابعةَ فواتير ({why}). اختر طابعة الفواتير الحقيقية.");
            if (p.FilePort) _log?.Warn($"install: «{name}» prints to FILE: (a test setup, not a shop printer)");
            var known = old.FirstOrDefault(r => r.PrinterName == name);
            PrinterRoute route;
            if (DeskConfig.IsDaymanPort(p.PortName))
            {
                if (known == null || string.IsNullOrEmpty(known.OriginalPort))
                    throw new SetupException($"الطابعة «{name}» موجّهة لدايماً ديسك ولا يُعرف منفذها الأصلي. أعد منفذها يدوياً من إعدادات الطابعة.");
                route = known;
            }
            else
            {
                route = new PrinterRoute
                {
                    PrinterName = name, OriginalPort = p.PortName, OriginalDriver = p.DriverName, RawQueueName = DeskConfig.RawQueueFor(name),
                    DirectHost = p.RawTcp ? p.Host!.Trim() : null, DirectPort = p.RawTcp ? p.PortNumber : 0,
                    ListenPort = known?.ListenPort ?? 0,
                };
            }
            if (route.ListenPort == 0 || taken.Contains(route.ListenPort)) route.ListenPort = FreePort(taken);
            taken.Add(route.ListenPort);
            routes.Add(route);
        }
        // Saved before any change: Restore and the watchdog need every original port.
        store.Update(c => c.Printers = routes);
        _log?.Info("install: " + string.Join(", ", routes.Select(r => $"{r.OriginalPort} ({r.OriginalDriver}) -> {r.TcpPortName}{(r.DirectHost != null ? " direct" : "")}")));
        foreach (var r in old.Where(r => r.RawQueueName != null && !names.Contains(r.PrinterName ?? "")))
            _sh.Run(RemoveQueueScript(r.RawQueueName!)); // an earlier set's raw queue; its printer is already restored

        try
        {
            Must($"if (-not (Get-PrinterDriver -Name {Q(GenericDriver)} -ErrorAction SilentlyContinue)) {{ Add-PrinterDriver -Name {Q(GenericDriver)} }}",
                "تعذّر تثبيت تعريف Generic / Text Only.");
            foreach (var r in routes)
            {
                Must($"if (-not (Get-Printer -Name {Q(r.RawQueueName!)} -ErrorAction SilentlyContinue)) {{ Add-Printer -Name {Q(r.RawQueueName!)} -DriverName {Q(GenericDriver)} -PortName {Q(r.OriginalPort!)} }}",
                    $"تعذّر إنشاء طابعة دايماً على المنفذ الأصلي للطابعة «{r.PrinterName}».");
                Must($"if (-not (Get-PrinterPort -Name {Q(r.TcpPortName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name {Q(r.TcpPortName)} -PrinterHostAddress '127.0.0.1' -PortNumber {r.ListenPort} }}",
                    $"تعذّر إنشاء المنفذ 127.0.0.1:{r.ListenPort}.");
            }
            foreach (var r in routes)
                if (!startListener(r))
                    throw new SetupException($"المنفذ {r.ListenPort} على هذا الجهاز مستخدم من برنامج آخر.");
            // Moving the POS printers is the last step, once every listener runs.
            foreach (var r in routes)
                Must($"Set-Printer -Name {Q(r.PrinterName!)} -PortName {Q(r.TcpPortName)}",
                    $"تعذّر توجيه الطابعة «{r.PrinterName}» إلى دايماً ديسك.");
            store.Update(c => c.Active = true);
            _log?.Info("install: done");
        }
        catch (Exception ex)
        {
            _log?.Error("install failed, rolling back: " + ex.Message);
            try { Uninstall(store); } catch (Exception rex) { _log?.Error("rollback failed: " + rex.Message); }
            if (ex is SetupException) throw;
            throw new SetupException("حدث خطأ أثناء التشغيل وأُعيدت الطابعات كما كانت.", ex.Message);
        }
    }

    static string RemoveQueueScript(string raw) =>
        $"if (Get-Printer -Name {Q(raw)} -ErrorAction SilentlyContinue) {{ Remove-Printer -Name {Q(raw)} }}";

    void Must(string script, string arabic)
    {
        var r = _sh.Run(script);
        if (!r.Ok) throw new SetupException(arabic, r.Stderr);
    }

    /// <summary>
    /// Puts the route's POS printer back on its original port if it is on a DAYMAN port, and confirms it by reading
    /// the port again. Returns true if it changed it. Throws when the port cannot be read or is not the original after.
    /// </summary>
    public bool Restore(PrinterRoute route)
    {
        if (route.Lan || string.IsNullOrEmpty(route.PrinterName) || string.IsNullOrEmpty(route.OriginalPort)) return false;
        var port = GetPort(route.PrinterName);
        if (port == null && Missing(route.PrinterName)) { _log?.Warn($"printer «{route.PrinterName}» no longer exists: nothing to restore"); return false; }
        if (port == null) throw new SetupException($"تعذّرت قراءة منفذ الطابعة «{route.PrinterName}»، فلم نتأكد من إرجاعها.");
        if (!DeskConfig.IsDaymanPort(port)) return false;
        var r = _sh.Run($"Set-Printer -Name {Q(route.PrinterName)} -PortName {Q(route.OriginalPort)}");
        if (!r.Ok) throw new SetupException("تعذّر إرجاع الطابعة إلى منفذها الأصلي: " + route.OriginalPort, r.Stderr);
        var after = GetPort(route.PrinterName);
        if (after != route.OriginalPort)
            throw new SetupException("لم نتأكد من إرجاع الطابعة إلى منفذها الأصلي: " + route.OriginalPort, "port now: " + (after ?? "unreadable"));
        _log?.Info("restored original port " + route.OriginalPort);
        return true;
    }

    /// <summary>Restores every printer, trying all of them; throws the first failure after the last one was tried.</summary>
    public bool Restore(DeskConfig cfg)
    {
        bool any = false;
        SetupException? first = null;
        foreach (var r in cfg.Printers.ToList())
        {
            try { any |= Restore(r); }
            catch (SetupException ex) { first ??= ex; _log?.Error("restore " + r.PrinterName + ": " + ex.Message); }
        }
        if (first != null) throw first;
        return any;
    }

    /// <summary>Moves each POS printer whose listener runs to its DAYMAN port.</summary>
    public bool Assign(DeskConfig cfg, Func<PrinterRoute, bool> listening)
    {
        if (!cfg.Active) return false;
        bool any = false;
        foreach (var route in cfg.Printers.ToList())
        {
            if (string.IsNullOrEmpty(route.PrinterName) || !listening(route)) continue;
            var port = GetPort(route.PrinterName);
            if (port == null || port == route.TcpPortName) continue;
            var r = _sh.Run($"Set-Printer -Name {Q(route.PrinterName)} -PortName {Q(route.TcpPortName)}");
            if (!r.Ok) throw new SetupException($"تعذّر توجيه الطابعة «{route.PrinterName}» إلى دايماً ديسك.", r.Stderr);
            _log?.Info("assigned " + route.TcpPortName);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Restores every printer port and, only once all are confirmed, removes the raw queues and the DAYMAN ports.
    /// Printers only: «وضع الشبكة» (its listener and firewall rule) is left as it is, so an install rollback never
    /// touches it. Throws (and leaves everything in place) when a restore is not confirmed. Safe to run twice.
    /// </summary>
    public void Uninstall(ConfigStore store)
    {
        var cfg = store.Current;
        Restore(cfg);
        foreach (var r in cfg.Printers.Where(r => !string.IsNullOrEmpty(r.PrinterName) && !string.IsNullOrEmpty(r.OriginalPort)))
        {
            var port = GetPort(r.PrinterName!);
            if (port == null && Missing(r.PrinterName!)) continue; // deleted from Windows: nothing to restore
            if (port == null || DeskConfig.IsDaymanPort(port))
                throw new SetupException("لم نتأكد من إرجاع الطابعة إلى منفذها الأصلي: " + r.OriginalPort);
        }
        foreach (var r in cfg.Printers.Where(r => !string.IsNullOrEmpty(r.RawQueueName)))
            _sh.Run(RemoveQueueScript(r.RawQueueName!));
        _sh.Run("$used = @(Get-Printer | ForEach-Object { $_.PortName }); " +
                "Get-PrinterPort -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'DAYMAN_*' -and $used -notcontains $_.Name } | " +
                "ForEach-Object { Start-Sleep -Seconds 2; Remove-PrinterPort -Name $_.Name }");
        store.Update(c => c.Active = false);
    }
}

/// <summary>The inbound rule of «وضع الشبكة»: TCP on its port, from the local subnet only.</summary>
public static class Firewall
{
    public static readonly string RemoveScript =
        $"Get-NetFirewallRule -Name {PrinterSetup.Q(PrinterSetup.FirewallRule)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule";

    public static string AddScript(int port) =>
        RemoveScript + "\n" +
        $"New-NetFirewallRule -Name {PrinterSetup.Q(PrinterSetup.FirewallRule)} -DisplayName 'Dayman Desk network mode' -Direction Inbound " +
        $"-Action Allow -Protocol TCP -LocalPort {port} -RemoteAddress LocalSubnet -Profile Any | Out-Null";
}

/// <summary>The Windows OCR languages (WO-89): Arabic for the labels, English as the fallback (digits still read).</summary>
public static class OcrLanguages
{
    public static readonly string[] Capabilities = { "Language.OCR~~~ar-SA~0.0.1.0", "Language.OCR~~~en-US~0.0.1.0" };

    /// <summary>Adds each missing capability; prints one line per capability; exit 1 when an add failed.</summary>
    public static string InstallScript() =>
        "$failed = $false; $out = @()\n" +
        "foreach ($n in @(" + string.Join(", ", Capabilities.Select(PrinterSetup.Q)) + ")) {\n" +
        "  try {\n" +
        "    $c = Get-WindowsCapability -Online -Name $n\n" +
        "    if ($c.State -eq 'Installed') { $out += \"$n present\" } else { Add-WindowsCapability -Online -Name $n | Out-Null; $out += \"$n added\" }\n" +
        "  } catch { $failed = $true; $out += \"$n failed: $($_.Exception.Message)\" }\n" +
        "}\n" +
        "$out -join '; '\n" +
        "if ($failed) { exit 1 }";
}

/// <summary>The once-a-minute safety net: a POS printer left on its DAYMAN port with nothing listening goes back.</summary>
public static class Watchdog
{
    public static bool Run(DeskConfig cfg, PrinterSetup setup, bool listening, RollingLog log) => Run(cfg, setup, _ => listening, log);

    /// <summary>«وضع الشبكة» is on but nothing listens on its port (the program is not running).</summary>
    public static bool NetworkDown(DeskConfig cfg, Func<int, bool> listening) =>
        cfg.Network.Enabled && !string.IsNullOrEmpty(cfg.Network.PrinterHost) && !listening(cfg.Network.ListenPort);

    /// <summary>Restores each route whose port has no listener. Throws (after trying all) when a restore is not confirmed.</summary>
    public static bool Run(DeskConfig cfg, PrinterSetup setup, Func<int, bool> listening, RollingLog log)
    {
        bool any = false;
        SetupException? first = null;
        foreach (var r in cfg.Printers)
        {
            if (string.IsNullOrEmpty(r.PrinterName) || listening(r.ListenPort)) continue;
            try
            {
                if (setup.Restore(r))
                {
                    log.Warn($"watchdog: nothing listens on {r.ListenPort}, original port restored");
                    any = true;
                }
            }
            catch (SetupException ex) { first ??= ex; }
        }
        if (first != null) throw first;
        return any;
    }
}
