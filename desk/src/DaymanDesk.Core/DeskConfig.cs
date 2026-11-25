using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaymanDesk.Core;

/// <summary>
/// One intercepted printer (WO-89). A Windows route: the POS printer queue is moved to DAYMAN_&lt;ListenPort&gt;
/// (127.0.0.1:ListenPort) and a hidden raw queue keeps its original port. The network-mode route (Lan) has no
/// Windows printer: it listens on the LAN and proxies to DirectHost:DirectPort.
/// </summary>
public sealed class PrinterRoute
{
    public string? PrinterName { get; set; }
    public string? OriginalPort { get; set; }
    public string? OriginalDriver { get; set; }
    public string? RawQueueName { get; set; }
    public int ListenPort { get; set; } = DeskConfig.ListenPort;
    /// <summary>The original port is a Standard TCP/IP (RAW) port: the forwarder sends straight to this printer.</summary>
    public string? DirectHost { get; set; }
    public int DirectPort { get; set; }
    /// <summary>The network-mode route (built from DeskConfig.Network, never stored in Printers).</summary>
    [JsonIgnore] public bool Lan { get; set; }

    [JsonIgnore] public string TcpPortName => DeskConfig.PortNameFor(ListenPort);
    /// <summary>The listener key: the printer name, or "\\network" (a backslash is never in a Windows printer name).</summary>
    [JsonIgnore] public string Id => Lan ? "\\network" : PrinterName ?? "";
}

/// <summary>«وضع الشبكة» (WO-89): POS devices without Windows print to this PC, which forwards to the real printer.</summary>
public sealed class NetworkSettings
{
    public bool Enabled { get; set; }
    public int ListenPort { get; set; } = DeskConfig.ListenPort;
    public string? PrinterHost { get; set; }
    public int PrinterPort { get; set; } = DeskConfig.ListenPort;

    public PrinterRoute? Route() => Enabled && !string.IsNullOrEmpty(PrinterHost)
        ? new PrinterRoute { Lan = true, ListenPort = ListenPort, DirectHost = PrinterHost, DirectPort = PrinterPort }
        : null;
}

/// <summary>WO-90: one pre-issued code and when this program stops printing it (before the server's 30 days run out).</summary>
public sealed class PoolCode
{
    public string Code { get; set; } = "";
    public DateTimeOffset Until { get; set; }
}

public sealed class DeskConfig
{
    public const string TcpPortName = "DAYMAN_9100";
    public const int ListenPort = 9100;
    public static string PortNameFor(int port) => "DAYMAN_" + port;
    public static bool IsDaymanPort(string? port) => port != null && port.StartsWith("DAYMAN_", StringComparison.Ordinal);

    // Printer interception (WO-89: one route per receipt printer)
    public List<PrinterRoute> Printers { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    // v1.0 single-printer fields: read once into Printers, then written as a mirror of Printers[0] so that a v1.0
    // watchdog left installed by a failed update still restores the first printer.
    public string? PrinterName { get; set; }
    public string? OriginalPort { get; set; }
    public string? OriginalDriver { get; set; }
    public string? RawQueueName { get; set; }
    /// <summary>True while the program should hold the POS printers on their DAYMAN_ ports.</summary>
    public bool Active { get; set; }

    // Server
    public string ServerUrl { get; set; } = DeskApiClient.DefaultBaseUrl;
    /// <summary>The device token in memory. Never written as is when a protector is set (see ConfigStore).</summary>
    [JsonIgnore] public string? DeviceToken { get; set; }
    /// <summary>On disk: the plain token (v0 files, or no protector). Migrated to TokenProtected on load.</summary>
    [JsonPropertyName("deviceToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenPlain { get; set; }
    /// <summary>On disk: the token sealed with DPAPI (LocalMachine), base64.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenProtected { get; set; }
    public string? DeviceName { get; set; }
    public string? ShopName { get; set; }
    public string? StampMode { get; set; }
    public bool QrEnabled { get; set; } = true;
    public int CodeValidityDays { get; set; } = 2;

    // WO-90 pool (contract v1.2): codes the server reserved for this device (POST /codes), printed at once on the
    // next receipts and bound by /receipt afterwards; the shop's claim URL prefix; and the code printed per
    // invoice per local day ("yyyy-MM-dd|invoice" -> code, "" = refused by the server: no QR on a reprint).
    public List<PoolCode> CodePool { get; set; } = new();
    public string? ClaimBase { get; set; }
    public Dictionary<string, string> PrintedCodes { get; set; } = new();

    // WO-95 (contract v1.3): the signing key (base64 X|Y|D, sealed with DPAPI like the token), this device's number in
    // the shop, whether the server holds the key, and the next seq of a signed QR (never reused, kept across pairings).
    // PrintedCodes holds "r:" + token for an invoice that got a signed QR (a reprint prints the same QR).
    public string? SignKeyProtected { get; set; }
    public int? DeviceNumber { get; set; }
    public bool KeyRegistered { get; set; }
    public long NextSeq { get; set; } = 1;

    // Learned from the shop's receipts
    public int? CodePage { get; set; }
    public int? CodeTable { get; set; }
    public string LastPrintKind { get; set; } = "unknown";
    public int SamplesSent { get; set; }

    // First-receipt confirmation (WO-86 item 1): the owner confirmed which lines hold the two fields.
    public bool FieldsConfirmed { get; set; }
    public LearnedField? LearnedInvoice { get; set; }
    public LearnedField? LearnedTotal { get; set; }
    /// <summary>WO-91: the OCR language of an image receipt the fields were confirmed on; another language asks again.</summary>
    public string? FieldsOcrLanguage { get; set; }

    /// <summary>
    /// WO-91: «خروج» put every printer back on its own port (confirmed). The watchdog then has nothing to restore and
    /// starts no PowerShell; cleared before any printer is pointed at the program again.
    /// </summary>
    public bool RestoredAtExit { get; set; }

    // Update check (heartbeat latestVersion / downloadUrl); null when this build is current.
    public string? UpdateVersion { get; set; }
    public string? UpdateUrl { get; set; }

    // Today's counters (local day) and the last problem, for the status window.
    public string? StatsDay { get; set; }
    public int TodayReceipts { get; set; }
    public int TodayRegistered { get; set; }
    public int TodayQr { get; set; }
    public int TodayNotRegistered { get; set; }
    public string? LastProblem { get; set; }
    public string? LastProblemDetail { get; set; }
    public DateTimeOffset? LastProblemAt { get; set; }

    // Unknown print formats already reported today (WO-89: one report per format per day).
    public string? UnknownFormatsDay { get; set; }
    public List<string> UnknownFormatsReported { get; set; } = new();

    public bool Paired => !string.IsNullOrEmpty(DeviceToken);
    /// <summary>The raw queue on the real port; WO-91: its name tells the POS user not to pick it.</summary>
    public static string RawQueueFor(string printer) => $"Dayman Raw ({printer}) - لا تختر هذه الطابعة";

    /// <summary>A v1.0 file: its single printer becomes the first route.</summary>
    internal void MigrateLegacy()
    {
        Printers ??= new();
        Network ??= new();
        UnknownFormatsReported ??= new();
        CodePool ??= new();
        PrintedCodes ??= new();
        if (Printers.Count == 0 && !string.IsNullOrEmpty(PrinterName))
            Printers.Add(new PrinterRoute
            {
                PrinterName = PrinterName, OriginalPort = OriginalPort, OriginalDriver = OriginalDriver,
                RawQueueName = RawQueueName ?? $"Dayman Raw ({PrinterName})", ListenPort = ListenPort, // the 1.0 name
            });
    }

    internal void MirrorLegacy()
    {
        var first = Printers.FirstOrDefault(r => r.ListenPort == ListenPort);
        PrinterName = first?.PrinterName; OriginalPort = first?.OriginalPort;
        OriginalDriver = first?.OriginalDriver; RawQueueName = first?.RawQueueName;
    }
}

/// <summary>Seals the device token at rest (DPAPI on Windows). Unprotect returns null when it cannot.</summary>
public interface ITokenProtector
{
    string Protect(string plain);
    string? Unprotect(string sealedBase64);
}

/// <summary>
/// Loads and atomically saves config.json. Thread-safe. With a protector the token is only ever written
/// sealed; a plain token from a v0 file is sealed by MigrateToken, in config.json and config.json.bak. Until a
/// seal succeeds the v0 plain token stays on disk as it was (never dropped, never replaced by nothing).
/// </summary>
public sealed class ConfigStore
{
    readonly string _path;
    readonly object _lock = new();
    readonly ITokenProtector? _protector;
    public DeskConfig Current { get; private set; }
    /// <summary>WO-95: seals the signing key as it seals the token (null: stored as is, tests only).</summary>
    public ITokenProtector? Protector => _protector;

    /// <param name="migrateNow">False defers sealing a plain v0 token until MigrateToken is called.</param>
    public ConfigStore(string path, ITokenProtector? protector = null, bool migrateNow = true)
    {
        _path = path;
        _protector = protector;
        Current = Load();
        OpenToken();
        if (migrateNow) MigrateToken();
    }

    /// <summary>True when a sealed token was found but could not be opened (config copied from another PC).</summary>
    public bool TokenUnreadable { get; private set; }
    /// <summary>True when a plain v0 token was sealed by this store.</summary>
    public bool TokenMigrated { get; private set; }

    /// <summary>The plain v0 token still on disk, written back as is until sealing it succeeds.</summary>
    string? _plainPending;
    bool _migrationAllowed;

    void OpenToken()
    {
        var c = Current;
        if (_protector == null)
        {
            c.DeviceToken = c.TokenPlain;
            return;
        }
        if (!string.IsNullOrEmpty(c.TokenProtected))
        {
            try { c.DeviceToken = _protector.Unprotect(c.TokenProtected); }
            catch (Exception) { c.DeviceToken = null; }
            if (c.DeviceToken == null) TokenUnreadable = true;
            else { _sealedFor = c.DeviceToken; _sealed = c.TokenProtected; }
            // A dead sealed token, or a plain leftover beside a good sealed one: drop it now.
            // Twice: the second save replaces config.json.bak, which still held it.
            if (TokenUnreadable || !string.IsNullOrEmpty(c.TokenPlain))
            {
                try { Update(_ => { }); Update(_ => { }); }
                catch (Exception) { } // a read-only disk must not stop the program; it retries on the next save
            }
        }
        else
        {
            c.DeviceToken = c.TokenPlain;
            if (!string.IsNullOrEmpty(c.TokenPlain)) _plainPending = c.TokenPlain;
        }
    }

    /// <summary>
    /// Seals a plain v0 token, in config.json and config.json.bak. When DPAPI fails nothing is saved: the v0
    /// file stays untouched, and later saves keep its plain token until a seal succeeds. True when no plain
    /// token is left pending.
    /// </summary>
    public bool MigrateToken()
    {
        lock (_lock)
        {
            _migrationAllowed = true;
            if (_plainPending == null) return true;
            if (Current.DeviceToken == _plainPending && Seal(_plainPending) == null) return false;
            try { Update(_ => { }); }
            catch (Exception) { } // a read-only disk must not stop the program; it retries on the next save
            return _plainPending == null;
        }
    }

    public string Path => _path;

    public string BackupPath => _path + ".bak";
    /// <summary>True when config.json was missing or unreadable and config.json.bak was used.</summary>
    public bool LoadedFromBackup { get; private set; }

    DeskConfig Load()
    {
        var main = TryRead(_path);
        if (main != null) return main;
        var bak = TryRead(BackupPath);
        if (bak != null) { LoadedFromBackup = true; return bak; }
        return new DeskConfig();
    }

    static DeskConfig? TryRead(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var c = JsonSerializer.Deserialize<DeskConfig>(File.ReadAllText(path), DeskApiClient.Json);
                c?.MigrateLegacy();
                return c;
            }
        }
        catch (Exception) { }
        return null;
    }

    string? _sealedFor, _sealed;

    /// <summary>True when sealing failed: the token then lives in memory only (never written in plain text).</summary>
    public bool SealFailed { get; private set; }

    /// <summary>Seals once per token value (DPAPI is not called on every save). A failure writes no token at all.</summary>
    string? Seal(string? token)
    {
        if (string.IsNullOrEmpty(token)) { _sealedFor = _sealed = null; return null; }
        if (token == _sealedFor && _sealed != null) return _sealed;
        try
        {
            _sealed = _protector!.Protect(token);
            _sealedFor = token;
            SealFailed = false;
            return _sealed;
        }
        catch (Exception)
        {
            SealFailed = true;
            _sealedFor = _sealed = null;
            return null;
        }
    }

    public void Update(Action<DeskConfig> change)
    {
        lock (_lock)
        {
            change(Current);
            var c = Current;
            bool migrated = false;
            if (_protector != null)
            {
                if (_plainPending != null && c.DeviceToken == _plainPending)
                {
                    var s = _migrationAllowed ? Seal(c.DeviceToken) : null;
                    if (s == null) { c.TokenPlain = _plainPending; c.TokenProtected = null; } // the v0 token stays as it was
                    else { c.TokenProtected = s; c.TokenPlain = null; _plainPending = null; migrated = TokenMigrated = true; }
                }
                else
                {
                    _plainPending = null; // unpaired or paired again: the old plain token goes
                    c.TokenProtected = Seal(c.DeviceToken);
                    c.TokenPlain = null;
                }
            }
            else c.TokenPlain = string.IsNullOrEmpty(c.DeviceToken) ? null : c.DeviceToken;
            c.MirrorLegacy();
            Write();
            // Again: the first write moved the plain-token file to config.json.bak.
            if (migrated) Write();
        }
    }

    void Write()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions(DeskApiClient.Json) { WriteIndented = true }));
        // Keep the last good file: the watchdog falls back to it if config.json is ever unreadable.
        if (TryRead(_path) != null) File.Copy(_path, BackupPath, true);
        FileUtil.MoveOverwrite(tmp, _path);
    }

    /// <summary>
    /// Sets the server URL. Only an absolute https URL is accepted. A different host clears the device token
    /// (the program must pair again with the new server). Returns false when the URL is refused.
    /// </summary>
    public bool SetServerUrl(string? input)
    {
        string url = string.IsNullOrWhiteSpace(input) ? DeskApiClient.DefaultBaseUrl : input.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        Update(c =>
        {
            Uri.TryCreate(c.ServerUrl, UriKind.Absolute, out var old);
            if (old == null || !string.Equals(old.Host, uri.Host, StringComparison.OrdinalIgnoreCase) || old.Port != uri.Port)
            {
                c.DeviceToken = null;
                c.ShopName = null;
            }
            c.ServerUrl = url;
        });
        return true;
    }
}
