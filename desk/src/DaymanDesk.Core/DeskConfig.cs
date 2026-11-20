using System.Text.Json;

namespace DaymanDesk.Core;

public sealed class DeskConfig
{
    public const string TcpPortName = "DAYMAN_9100";
    public const int ListenPort = 9100;

    // Printer interception
    public string? PrinterName { get; set; }
    public string? OriginalPort { get; set; }
    public string? OriginalDriver { get; set; }
    public string? RawQueueName { get; set; }
    /// <summary>True while the program should hold the POS printer on DAYMAN_9100.</summary>
    public bool Active { get; set; }

    // Server
    public string ServerUrl { get; set; } = DeskApiClient.DefaultBaseUrl;
    public string? DeviceToken { get; set; }
    public string? DeviceName { get; set; }
    public string? ShopName { get; set; }
    public string? StampMode { get; set; }
    public bool QrEnabled { get; set; } = true;
    public int CodeValidityDays { get; set; } = 2;

    // Learned from the shop's receipts
    public int? CodePage { get; set; }
    public int? CodeTable { get; set; }
    public string LastPrintKind { get; set; } = "unknown";
    public int SamplesSent { get; set; }

    public bool Paired => !string.IsNullOrEmpty(DeviceToken);
    public static string RawQueueFor(string printer) => $"Dayman Raw ({printer})";
}

/// <summary>Loads and atomically saves config.json. Thread-safe.</summary>
public sealed class ConfigStore
{
    readonly string _path;
    readonly object _lock = new();
    public DeskConfig Current { get; private set; }

    public ConfigStore(string path)
    {
        _path = path;
        Current = Load();
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
                return JsonSerializer.Deserialize<DeskConfig>(File.ReadAllText(path), DeskApiClient.Json);
        }
        catch (Exception) { }
        return null;
    }

    public void Update(Action<DeskConfig> change)
    {
        lock (_lock)
        {
            change(Current);
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions(DeskApiClient.Json) { WriteIndented = true }));
            // Keep the last good file: the watchdog falls back to it if config.json is ever unreadable.
            if (TryRead(_path) != null) File.Copy(_path, BackupPath, true);
            File.Move(tmp, _path, true);
        }
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
