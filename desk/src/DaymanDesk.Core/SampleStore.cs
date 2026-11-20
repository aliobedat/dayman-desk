using System.IO.Compression;

namespace DaymanDesk.Core;

/// <summary>Keeps the last N captured print jobs as samples/&lt;timestamp&gt;.bin.</summary>
public sealed class SampleStore
{
    readonly string _dir;
    readonly int _keep;
    readonly object _lock = new();

    public SampleStore(string dir, int keep = 200)
    {
        _dir = dir;
        _keep = keep;
        Directory.CreateDirectory(dir);
    }

    public string Dir => _dir;

    public string Save(byte[] data, DateTimeOffset at)
    {
        lock (_lock)
        {
            string name = at.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(_dir, name + ".bin");
            for (int n = 1; File.Exists(path); n++) path = Path.Combine(_dir, $"{name}-{n}.bin");
            File.WriteAllBytes(path, data);
            var all = Directory.GetFiles(_dir, "*.bin").OrderBy(f => f, StringComparer.Ordinal).ToList();
            for (int i = 0; i < all.Count - _keep; i++) File.Delete(all[i]);
            return path;
        }
    }

    public int Count => Directory.GetFiles(_dir, "*.bin").Length;

    /// <summary>Zips every sample into zipPath (overwritten).</summary>
    public void ExportZip(string zipPath)
    {
        lock (_lock)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var f in Directory.GetFiles(_dir, "*.bin"))
                zip.CreateEntryFromFile(f, "samples/" + Path.GetFileName(f));
        }
    }
}
