using System.Reflection;
using DaymanDesk.Core;
using Tesseract;

namespace DaymanDesk.App;

/// <summary>
/// WO-95: reads image receipts with Tesseract (tessdata_fast Arabic + English), bundled in the exe, so every Windows
/// from 7 SP1 reads them with no language download. The native libraries and tessdata are extracted once per version
/// to C:\ProgramData\DaymanDesk\ocr\&lt;version&gt;, with the VC++ 2015-2022 runtime and the UCRT (WO-95b: a bare Windows 7). DeskAgent still reads twice (as printed and upscaled) and both must agree.
/// </summary>
sealed class TesseractOcr : IOcr, IDisposable
{
    const string Languages = "ara+eng";
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly RollingLog _log;
    TesseractEngine? _engine;
    bool _failed;

    readonly bool _dirLocked;

    /// <param name="dirLocked">The data folder's ACL was set (only SYSTEM and Administrators write it). Otherwise no
    /// native code is ever loaded from it: OCR is unavailable.</param>
    public TesseractOcr(RollingLog log, bool dirLocked) { _log = log; _dirLocked = dirLocked; }

    /// <summary>The fields learned on an image receipt belong to this reader (WO-91).</summary>
    public string? LanguageTag { get; private set; }

    static string Dir => Path.Combine(Runtime.DataDir, "ocr", Runtime.Version);

    /// <summary>
    /// Writes every embedded ocr/ resource to its file. An existing file is kept only when its length and SHA-256 match
    /// the embedded copy (a planted or damaged DLL is replaced before anything loads it).
    /// </summary>
    static void Extract()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ocr/", StringComparison.Ordinal)))
        {
            var path = Path.Combine(Dir, name.Substring(4).Replace('/', Path.DirectorySeparatorChar));
            byte[] want;
            using (var src = asm.GetManifestResourceStream(name)!)
            using (var ms = new MemoryStream()) { src.CopyTo(ms); want = ms.ToArray(); }
            if (File.Exists(path) && Same(path, want)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", want);
            FileUtil.MoveOverwrite(path + ".tmp", path);
            if (!Same(path, want)) throw new IOException("ocr file not written: " + name);
        }
    }

    static bool Same(string path, byte[] want)
    {
        var have = File.ReadAllBytes(path);
        if (have.Length != want.Length) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(have).SequenceEqual(sha.ComputeHash(want));
    }

    TesseractEngine? Engine()
    {
        if (_engine != null || _failed) return _engine;
        if (!_dirLocked)
        {
            _failed = true;
            _log.Error("ocr unavailable: the data folder could not be locked, no native code is loaded from it");
            return null;
        }
        try
        {
            Extract();
            TesseractEnviornment.CustomSearchPath = Dir;
            // The VC++ runtime (and the UCRT for Windows 7) sit app-local next to the OCR natives: their loads search here.
            SetDllDirectory(Path.Combine(Dir, Environment.Is64BitProcess ? "x64" : "x86"));
            _engine = new TesseractEngine(Path.Combine(Dir, "tessdata"), Languages, EngineMode.LstmOnly);
            LanguageTag = "tesseract-" + Languages;
            _log.Info("ocr engine: tesseract " + Languages);
        }
        catch (Exception ex)
        {
            _failed = true; // once per run: a missing VC++ runtime or a broken extract does not retry on every receipt
            _log.Error("ocr unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
        return _engine;
    }

    public async Task<IReadOnlyList<string>?> ReadAsync(MonoBitmap image)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var engine = Engine();
                if (engine == null) return (IReadOnlyList<string>?)null;
                var lines = new List<string>();
                foreach (var part in image.SplitForOcr(4000))
                {
                    using var pix = Pix.LoadFromMemory(ToPgm(part));
                    using var page = engine.Process(pix, PageSegMode.SingleColumn);
                    foreach (var l in (page.GetText() ?? "").Split('\n'))
                        if (l.Trim().Length > 0) lines.Add(l.Trim());
                }
                return lines;
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Black text on white as an 8-bit PGM (P5), which Leptonica reads from memory.</summary>
    static byte[] ToPgm(MonoBitmap m)
    {
        var head = System.Text.Encoding.ASCII.GetBytes($"P5\n{m.Width} {m.Height}\n255\n");
        var o = new byte[head.Length + m.Pixels.Length];
        Buffer.BlockCopy(head, 0, o, 0, head.Length);
        for (int i = 0; i < m.Pixels.Length; i++) o[head.Length + i] = m.Pixels[i] != 0 ? (byte)0 : (byte)255;
        return o;
    }

    public void Dispose() => _engine?.Dispose();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);
}
