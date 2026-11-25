namespace DaymanDesk.Core;

/// <summary>A plain-text log: desk.log plus desk.1.log .. desk.(N-1).log, each at most MaxBytes.</summary>
public sealed class RollingLog
{
    readonly string _dir;
    readonly long _maxBytes;
    readonly int _files;
    readonly object _lock = new();

    public RollingLog(string dir, long maxBytes = 1024 * 1024, int files = 5)
    {
        _dir = dir;
        _maxBytes = maxBytes;
        _files = files;
        Directory.CreateDirectory(dir);
    }

    public string CurrentPath => System.IO.Path.Combine(_dir, "desk.log");
    public event Action<string>? Written;

    public void Info(string m) => Write("INFO", m);
    public void Warn(string m) => Write("WARN", m);
    public void Error(string m) => Write("ERROR", m);

    public void Write(string level, string message)
    {
        string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture) + $" {level} {message.Replace('\n', ' ').Replace('\r', ' ')}";
        lock (_lock)
        {
            try
            {
                var fi = new FileInfo(CurrentPath);
                if (fi.Exists && fi.Length + line.Length > _maxBytes) Roll();
                File.AppendAllText(CurrentPath, line + Environment.NewLine);
            }
            catch (IOException) { } // logging must never break printing
            catch (UnauthorizedAccessException) { }
        }
        Written?.Invoke(line);
    }

    void Roll()
    {
        string P(int n) => System.IO.Path.Combine(_dir, n == 0 ? "desk.log" : $"desk.{n}.log");
        if (File.Exists(P(_files - 1))) File.Delete(P(_files - 1));
        for (int n = _files - 2; n >= 0; n--)
            if (File.Exists(P(n))) File.Move(P(n), P(n + 1));
    }

    public IEnumerable<string> Files() =>
        Directory.EnumerateFiles(_dir, "desk*.log");
}
