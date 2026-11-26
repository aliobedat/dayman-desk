using System.Text;
using System.Text.RegularExpressions;

namespace DaymanDesk.Core;

/// <summary>A 1-bit image, one byte per pixel (1 = black), row-major.</summary>
public sealed class MonoBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public MonoBitmap(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height];
    }

    public bool this[int x, int y] => Pixels[y * Width + x] != 0;

    public bool BlankRow(int y)
    {
        int o = y * Width;
        for (int x = 0; x < Width; x++) if (Pixels[o + x] != 0) return false;
        return true;
    }

    public int BlackPixels => Pixels.Count(p => p != 0);

    /// <summary>Each dot becomes 2x2 (the second, independent OCR read of an image receipt).</summary>
    public MonoBitmap Scale2x()
    {
        var o = new MonoBitmap(Width * 2, Height * 2);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (Pixels[y * Width + x] == 0) continue;
            int i = 2 * y * o.Width + 2 * x;
            o.Pixels[i] = o.Pixels[i + 1] = o.Pixels[i + o.Width] = o.Pixels[i + o.Width + 1] = 1;
        }
        return o;
    }

    /// <summary>WO-104: the rows of one OCR line (a little padding), with the columns of its letter words blanked.</summary>
    public MonoBitmap LineCrop(OcrLine line)
    {
        int pad = Math.Max(2, (line.Bottom - line.Top) / 6);
        int y0 = Math.Max(0, line.Top - pad), y1 = Math.Min(Height, line.Bottom + pad);
        var o = Rows(y0, Math.Max(1, y1 - y0));
        foreach (var (left, right) in line.WordsWithoutDigits)
            for (int y = 0; y < o.Height; y++)
                for (int x = Math.Max(0, left - pad); x <= Math.Min(Width - 1, right + pad); x++) o.Pixels[y * Width + x] = 0;
        return o;
    }

    public MonoBitmap Rows(int y0, int height)
    {
        var px = new byte[Width * height];
        Array.Copy(Pixels, y0 * Width, px, 0, px.Length);
        return new MonoBitmap(Width, height, px);
    }

    /// <summary>
    /// Slices of at most maxHeight rows for the OCR engine (Windows OCR refuses taller images). Each cut is on a blank
    /// row in the last quarter of the slice, so no text line is cut in two; without one it cuts at maxHeight.
    /// </summary>
    public IReadOnlyList<MonoBitmap> SplitForOcr(int maxHeight)
    {
        var parts = new List<MonoBitmap>();
        int y = 0;
        while (Height - y > maxHeight)
        {
            int cut = y + maxHeight;
            for (int c = y + maxHeight - 1; c >= y + maxHeight * 3 / 4; c--)
                if (BlankRow(c)) { cut = c + 1; break; }
            parts.Add(Rows(y, cut - y));
            y = cut;
        }
        if (Height - y > 0) parts.Add(Rows(y, Height - y));
        return parts;
    }
}

/// <summary>One OCR line: its text, its rows in the image, and the columns of its words that hold no digit (WO-104).</summary>
public sealed class OcrLine
{
    public OcrLine(string text, int top = 0, int bottom = 0, IReadOnlyList<(int Left, int Right)>? wordsWithoutDigits = null)
    {
        Text = text; Top = top; Bottom = bottom;
        WordsWithoutDigits = wordsWithoutDigits ?? Array.Empty<(int, int)>();
    }
    public string Text { get; }
    public int Top { get; }
    public int Bottom { get; }
    public IReadOnlyList<(int Left, int Right)> WordsWithoutDigits { get; }
}

/// <summary>Reads the text of an image receipt (Tesseract in the App; a fake in tests).</summary>
public interface IOcr
{
    /// <summary>The text lines top to bottom, or null when no OCR engine is available on this PC.</summary>
    Task<IReadOnlyList<OcrLine>?> ReadAsync(MonoBitmap image);
    /// <summary>WO-104: reads one line crop as a single line, English only, with only these characters. Null when unavailable.</summary>
    Task<string?> ReadLineAsync(MonoBitmap crop, string allowedChars);
    /// <summary>The recognizer language of the last read (WO-91: learned fields belong to it), or null.</summary>
    string? LanguageTag { get; }
}

/// <summary>
/// Rebuilds the printed picture of an image receipt from its ESC/POS raster commands (GS v 0, ESC *, GS ( L and
/// GS 8 L function 112), stacked top to bottom in print order and scaled to square dots.
/// </summary>
public static class RasterDecoder
{
    public const int MaxWidth = 1024, MaxHeight = 20000;

    public static MonoBitmap? Decode(byte[] job) => Decode(job, EscPosParser.Parse(job));

    public static MonoBitmap? Decode(byte[] job, ParsedJob parsed)
    {
        var parts = new List<MonoBitmap>();
        int width = 0, height = 0;
        foreach (var piece in parsed.RasterPieces)
        {
            var b = Piece(job, piece);
            if (b == null) continue;
            if (b.Width > MaxWidth || height + b.Height > MaxHeight) return null;
            parts.Add(b);
            width = Math.Max(width, b.Width);
            height += b.Height;
        }
        if (parts.Count == 0 || width == 0 || height == 0) return null;
        var o = new MonoBitmap(width, height);
        int y0 = 0;
        foreach (var b in parts)
        {
            for (int y = 0; y < b.Height; y++) Array.Copy(b.Pixels, y * b.Width, o.Pixels, (y0 + y) * width, b.Width);
            y0 += b.Height;
        }
        return o;
    }

    static MonoBitmap? Piece(byte[] d, RasterPiece p)
    {
        int w, h;
        switch (p.Format)
        {
            case RasterFormat.GsV0: w = p.Width * 8; h = p.Height; break;
            case RasterFormat.EscStar: w = p.Width; h = p.Height; break;
            default: w = p.Width; h = p.Height; break;
        }
        if (w <= 0 || h <= 0 || (long)w * p.ScaleX > MaxWidth || (long)h * p.ScaleY > MaxHeight) return null;
        var b = new MonoBitmap(w * p.ScaleX, h * p.ScaleY);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!Bit(d, p, x, y)) continue;
            for (int sy = 0; sy < p.ScaleY; sy++)
            for (int sx = 0; sx < p.ScaleX; sx++)
                b.Pixels[(y * p.ScaleY + sy) * b.Width + x * p.ScaleX + sx] = 1;
        }
        return b;
    }

    static bool Bit(byte[] d, RasterPiece p, int x, int y)
    {
        int i, bit;
        switch (p.Format)
        {
            case RasterFormat.EscStar:
            {
                // Column-major: each column is BandBytes bytes top to bottom, bit 7 on top.
                int k = p.Height / 8;
                i = p.Offset + x * k + y / 8;
                bit = 7 - y % 8;
                break;
            }
            case RasterFormat.GsV0:
                i = p.Offset + y * p.Width + x / 8;
                bit = 7 - x % 8;
                break;
            default:
                i = p.Offset + y * ((p.Width + 7) / 8) + x / 8;
                bit = 7 - x % 8;
                break;
        }
        return i < d.Length && (d[i] >> bit & 1) != 0;
    }
}

/// <summary>
/// Cleans OCR lines before the extractor: Latin digits, «٫» as a decimal point, and no spaces around a decimal
/// separator between digits («6 . 670» -> «6.670»). Spaces between plain digit groups are kept («2 5.000» is a
/// quantity and a price); the extractor's OCR rule refuses such an amount rather than guess.
/// </summary>
public static class OcrText
{
    static readonly Regex AroundSeparator = new(@"(\d)\s*([.,])\s*(?=\d)", RegexOptions.Compiled);

    public static IReadOnlyList<string> Clean(IEnumerable<string> lines)
    {
        var o = new List<string>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = CleanLine(raw);
            if (s.Length > 0) o.Add(s);
        }
        return o;
    }

    const string TotalChars = "0123456789.,-";
    const string InvoiceChars = "0123456789.,-/_#ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// WO-104: reads an image receipt: the page, then each line that holds the invoice number or the total (the
    /// owner's confirmed lines, else the labelled ones) again on its own crop, English only and as one line, with its
    /// letter words blanked. A large bold total («الإجمالي 1.50 د.ا») that the page read loses is read there
    /// (ReceiptExtractor.MergeLineRead: only a value the page lost is taken from it). Null when no OCR engine is available.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ReadReceiptAsync(IOcr ocr, MonoBitmap image, LearnedField? invoice, LearnedField? total)
    {
        var page = await ocr.ReadAsync(image).ConfigureAwait(false);
        if (page == null) return null;
        var kept = page.Where(l => !string.IsNullOrWhiteSpace(l.Text) && CleanLine(l.Text).Length > 0).ToList();
        var lines = kept.Select(l => CleanLine(l.Text)).ToList();
        var (invLines, totLines) = ReceiptExtractor.FieldLines(lines, invoice, total);
        foreach (int i in invLines.Union(totLines).OrderBy(i => i))
        {
            if (kept[i].Bottom <= kept[i].Top) continue;
            bool isTotal = totLines.Contains(i) && !invLines.Contains(i);
            var read = await ocr.ReadLineAsync(image.LineCrop(kept[i]), isTotal ? TotalChars : InvoiceChars).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(read)) lines[i] = ReceiptExtractor.MergeLineRead(lines[i], CleanLine(read!), isTotal);
        }
        return lines;
    }

    /// <summary>
    /// The CI self-test of an image receipt (WO-104/105), shared by the App (Tesseract) and tests: the owner's
    /// confirmation is played first (invoice = the first line holding invoiceHint; total = the first line holding
    /// totalHint, else the detected total line), then the two reads run as DeskAgent runs them. Ok: both reads give the
    /// same invoice number and total, or both say it is not a receipt.
    /// </summary>
    public static async Task<(string Report, bool Ok)> SelfTestAsync(IOcr ocr, MonoBitmap m, string? invoiceHint, string? totalHint)
    {
        var o = new StringBuilder();
        LearnedField? inv = null, tot = null;
        if (invoiceHint != null)
        {
            var seen = (await ReadReceiptAsync(ocr, m, null, null).ConfigureAwait(false) ?? Array.Empty<string>()).ToList();
            int li = seen.FindIndex(l => l.Contains(invoiceHint));
            int ti = totalHint != null ? seen.FindIndex(l => TextNormalizer.Normalize(l).Contains(TextNormalizer.Normalize(totalHint)))
                : seen.IndexOf(ReceiptExtractor.Extract(seen, ocr: true).TotalLine ?? "\0");
            inv = li >= 0 ? ReceiptExtractor.LearnInvoice(seen, li)?.field : null;
            tot = ti >= 0 ? ReceiptExtractor.LearnTotal(seen, ti)?.field : null;
            o.AppendLine($"confirmed: invoice line {li} {(inv != null ? "learned" : "not learned")}, total line {ti} {(tot != null ? "learned" : "not learned")}");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = await ReadReceiptAsync(ocr, m, inv, tot).ConfigureAwait(false);
        long t1 = sw.ElapsedMilliseconds;
        var second = await ReadReceiptAsync(ocr, m.Scale2x(), inv, tot).ConfigureAwait(false);
        long t2 = sw.ElapsedMilliseconds - t1;
        if (first == null || second == null) { o.AppendLine("ocr unavailable"); return (o.ToString(), false); }
        var a = ReceiptExtractor.Extract(first, inv, tot, ocr: true);
        var b = ReceiptExtractor.Extract(second, inv, tot, ocr: true);
        bool na = ReceiptExtractor.IsNonReceipt(first, a), nb = ReceiptExtractor.IsNonReceipt(second, b);
        o.AppendLine("read 1 (" + t1 + " ms): " + string.Join(" | ", first));
        o.AppendLine("read 2 (" + t2 + " ms): " + string.Join(" | ", second));
        o.AppendLine($"read 1: invoice {a.InvoiceNo} ({a.InvoiceConfidence:0.00}) total {a.Total} ({a.TotalConfidence:0.00})");
        o.AppendLine($"read 2: invoice {b.InvoiceNo} ({b.InvoiceConfidence:0.00}) total {b.Total} ({b.TotalConfidence:0.00})");
        bool agree = ReceiptExtractor.SameRead(a, b);
        o.AppendLine($"invoice {a.InvoiceNo}/{b.InvoiceNo} total {a.Total}/{b.Total} agree {(agree ? "yes" : "no")}");
        o.AppendLine($"receipt {(na ? "no" : "yes")}/{(nb ? "no" : "yes")}");
        // As DeskAgent registers: the first read confident, the second the same values.
        bool ok = (na && nb) || (!na && !nb && agree && a.InvoiceConfidence >= DeskAgent.MinConfidence && a.TotalConfidence >= DeskAgent.MinConfidence);
        return (o.ToString(), ok);
    }

    static string CleanLine(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            char c = ch;
            if (TextNormalizer.IsBidiMark(c)) continue;
            if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));
            else if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
            else if (c == '٫') c = '.';
            else if (c == '٬') c = ',';
            sb.Append(c);
        }
        var s = AroundSeparator.Replace(sb.ToString(), "$1$2");
        return string.Join(" ", s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
