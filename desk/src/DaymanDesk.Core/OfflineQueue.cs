using System.Text.Json;

namespace DaymanDesk.Core;

public sealed class QueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>"receipt" or "report".</summary>
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset EnqueuedAt { get; set; }
    public int Attempts { get; set; }
}

public enum SendOutcome { Sent, Transient, Rejected }

/// <summary>
/// A small persistent JSON queue for calls that could not reach the server. Flushed in order; the first
/// transient failure stops the flush so order is kept and the network is not hammered.
/// </summary>
public sealed class OfflineQueue
{
    public const int MaxItems = 500;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    readonly string _path;
    readonly object _lock = new();
    readonly Func<DateTimeOffset> _now;
    List<QueueItem> _items;

    public OfflineQueue(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => Amman.Now);
        _items = Load(path);
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    public IReadOnlyList<QueueItem> Snapshot() { lock (_lock) return _items.ToList(); }

    public void Enqueue<T>(string type, T payload)
    {
        lock (_lock)
        {
            _items.Add(new QueueItem
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload, DeskApiClient.Json),
                EnqueuedAt = _now(),
            });
            while (_items.Count > MaxItems) _items.RemoveAt(0);
            Save();
        }
    }

    /// <returns>Number of items sent.</returns>
    public async Task<int> FlushAsync(Func<QueueItem, Task<SendOutcome>> send)
    {
        int sent = 0;
        lock (_lock)
        {
            var cutoff = _now() - MaxAge;
            if (_items.RemoveAll(i => i.EnqueuedAt < cutoff) > 0) Save();
        }
        while (true)
        {
            QueueItem? head;
            lock (_lock) head = _items.FirstOrDefault();
            if (head == null) return sent;
            var outcome = await send(head).ConfigureAwait(false);
            lock (_lock)
            {
                if (outcome == SendOutcome.Transient)
                {
                    head.Attempts++;
                    Save();
                    return sent;
                }
                _items.RemoveAll(i => i.Id == head.Id);
                Save();
            }
            if (outcome == SendOutcome.Sent) sent++;
        }
    }

    static List<QueueItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(path), DeskApiClient.Json) ?? new();
        }
        catch (Exception) { return new(); } // a corrupt queue must never stop the program
    }

    void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, DeskApiClient.Json));
        FileUtil.MoveOverwrite(tmp, _path);
    }
}
