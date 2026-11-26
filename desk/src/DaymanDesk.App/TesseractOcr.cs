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
            var native = Path.Combine(Dir, Environment.Is64BitProcess ? "x64" : "x86");
            SetDllDirectory(native);
            if (ForcePreload || !Win10OrLater()) Preload(native);
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

    /// <summary>CI only (--ocr-selftest): runs the Windows 7 preload on a newer Windows too.</summary>
    public static bool ForcePreload;

    /// <summary>Windows 10+ resolves api-ms-win-crt to its own system UCRT; the app-local one is for 7 and 8.x.</summary>
    static bool Win10OrLater() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentMajorVersionNumber", null) is int v && v >= 10;

    /// <summary>
    /// WO-108: loads the app-local UCRT and VC++ runtime, then the OCR natives, each by full path from the verified
    /// folder, so the api-ms-win-crt forwarders bind to this ucrtbase.dll and never to an old one in System32 (Windows 7
    /// with an early KB2999226: «Entry Point Not Found»). A ucrtbase already loaded from elsewhere, or any failed load,
    /// makes OCR unavailable (thrown, logged by the caller).
    /// </summary>
    void Preload(string native)
    {
        if (!ForcePreload)
        {
            var loaded = GetModuleHandle("ucrtbase.dll");
            if (loaded != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(1024);
                GetModuleFileName(loaded, sb, sb.Capacity);
                if (!string.Equals(Path.GetDirectoryName(sb.ToString()), native, StringComparison.OrdinalIgnoreCase))
                    throw new DllNotFoundException("ucrtbase.dll already loaded from " + sb);
            }
        }
        var files = Directory.GetFiles(native, "*.dll").Select(Path.GetFileName).ToList();
        string[] order = { "ucrtbase.dll", "api-ms-win-core-*", "api-ms-win-crt-*", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "leptonica*", "tesseract*" };
        int n = 0;
        foreach (var pat in order)
            foreach (var f in files.Where(f => Matches(f!, pat)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (LoadLibraryW(Path.Combine(native, f!)) == IntPtr.Zero)
                    throw new DllNotFoundException(f + ": " + new System.ComponentModel.Win32Exception().Message);
                n++;
            }
        _log.Info("ocr preload: ok, " + n + " dlls by full path");
    }

    static bool Matches(string file, string pat) => pat.EndsWith("*", StringComparison.Ordinal)
        ? file.StartsWith(pat.Substring(0, pat.Length - 1), StringComparison.OrdinalIgnoreCase)
        : string.Equals(file, pat, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OcrLine>?> ReadAsync(MonoBitmap image)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var engine = Engine();
                if (engine == null) return (IReadOnlyList<OcrLine>?)null;
                var lines = new List<OcrLine>();
                int y0 = 0;
                foreach (var part in image.SplitForOcr(4000))
                {
                    using (var pix = Pix.LoadFromMemory(ToPgm(part)))
                    using (var page = engine.Process(pix, PageSegMode.SingleColumn))
                    using (var it = page.GetIterator())
                    {
                        // WO-104: each line with its rows and the columns of its words without a digit (blanked on a re-read).
                        it.Begin();
                        do
                        {
                            var text = (it.GetText(PageIteratorLevel.TextLine) ?? "").Trim();
                            if (text.Length == 0 || !it.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
                            var letters = new List<(int, int)>();
                            do
                            {
                                var w = it.GetText(PageIteratorLevel.Word) ?? "";
                                if (!w.Any(char.IsDigit) && it.TryGetBoundingBox(PageIteratorLevel.Word, out var wb)) letters.Add((wb.X1, wb.X2));
                            } while (it.Next(PageIteratorLevel.TextLine, PageIteratorLevel.Word));
                            lines.Add(new OcrLine(text, box.Y1 + y0, box.Y2 + y0, letters));
                        } while (it.Next(PageIteratorLevel.TextLine));
                    }
                    y0 += part.Height;
                }
                return lines;
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    TesseractEngine? _lineEngine;

    /// <summary>WO-104: one line crop, English only, as a single line, with only the allowed characters.</summary>
    public async Task<string?> ReadLineAsync(MonoBitmap crop, string allowedChars)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (Engine() == null) return null;
                try
                {
                    _lineEngine ??= new TesseractEngine(Path.Combine(Dir, "tessdata"), "eng", EngineMode.LstmOnly);
                    _lineEngine.SetVariable("tessedit_char_whitelist", allowedChars);
                    using var pix = Pix.LoadFromMemory(ToPgm(crop));
                    using var page = _lineEngine.Process(pix, PageSegMode.SingleLine);
                    return (page.GetText() ?? "").Trim();
                }
                catch (Exception ex)
                {
                    _log.Warn("ocr line read failed: " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
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

    public void Dispose() { _lineEngine?.Dispose(); _engine?.Dispose(); }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetModuleFileName(IntPtr module, System.Text.StringBuilder path, int size);
}
