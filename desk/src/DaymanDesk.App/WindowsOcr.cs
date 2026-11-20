using DaymanDesk.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace DaymanDesk.App;

/// <summary>
/// Reads image receipts with Windows.Media.Ocr, built into Windows 10/11 (WO-89). Arabic when the OCR language
/// Language.OCR~~~ar-SA is installed (the installer adds it), else English (the digits still read).
/// </summary>
sealed class WindowsOcr : IOcr
{
    static readonly string[] Preferred = { "ar-SA", "ar", "en-US", "en" };
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly RollingLog _log;
    OcrEngine? _engine;

    public WindowsOcr(RollingLog log) { _log = log; }

    /// <summary>The recognizer language in use, or null when none is installed.</summary>
    public string? LanguageTag { get; private set; }

    /// <summary>After the OCR languages were installed: pick the engine again (Arabic may be there now).</summary>
    public void Reset() => _engine = null;

    OcrEngine? Engine()
    {
        if (_engine != null) return _engine;
        foreach (var tag in Preferred)
        {
            try
            {
                var lang = new global::Windows.Globalization.Language(tag);
                if (!OcrEngine.IsLanguageSupported(lang)) continue;
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e == null) continue;
                _engine = e;
                LanguageTag = e.RecognizerLanguage.LanguageTag;
                _log.Info("ocr engine: " + LanguageTag);
                return e;
            }
            catch (Exception ex) { _log.Warn("ocr language " + tag + ": " + ex.Message); }
        }
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        LanguageTag = _engine?.RecognizerLanguage.LanguageTag;
        if (_engine == null) _log.Warn("no OCR language installed");
        return _engine;
    }

    public async Task<IReadOnlyList<string>?> ReadAsync(MonoBitmap image)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var engine = Engine();
            if (engine == null) return null;
            var lines = new List<string>();
            foreach (var part in image.SplitForOcr((int)OcrEngine.MaxImageDimension))
            {
                using var bmp = ToSoftwareBitmap(part);
                var result = await engine.RecognizeAsync(bmp);
                foreach (var line in result.Lines) lines.Add(line.Text);
            }
            return lines;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Black text on white, Bgra8 (the format every OCR recognizer takes).</summary>
    static SoftwareBitmap ToSoftwareBitmap(MonoBitmap m)
    {
        var px = new byte[m.Width * m.Height * 4];
        for (int i = 0; i < m.Pixels.Length; i++)
        {
            byte v = m.Pixels[i] != 0 ? (byte)0 : (byte)255;
            int o = i * 4;
            px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
        }
        return SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(px), BitmapPixelFormat.Bgra8,
            m.Width, m.Height, BitmapAlphaMode.Premultiplied);
    }
}
