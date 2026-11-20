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

/// <summary>Reads the text of an image receipt (Windows.Media.Ocr in the App; a fake in tests).</summary>
public interface IOcr
{
    /// <summary>The text lines top to bottom, or null when no OCR engine is available on this PC.</summary>
    Task<IReadOnlyList<string>?> ReadAsync(MonoBitmap image);
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
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                char c = ch;
                if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));
                else if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
                else if (c == '٫') c = '.';
                else if (c == '٬') c = ',';
                sb.Append(c);
            }
            var s = AroundSeparator.Replace(sb.ToString(), "$1$2");
            s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (s.Length > 0) o.Add(s);
        }
        return o;
    }
}
