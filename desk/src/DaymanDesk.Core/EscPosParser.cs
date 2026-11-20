namespace DaymanDesk.Core;

public enum RasterFormat { GsV0, EscStar, Graphics }

/// <summary>
/// One complete image command of the job (WO-89), for RasterDecoder. Offset/Length: the image bits in the job.
/// GsV0: WidthBytes x Height rows. EscStar: Width columns of BandBytes bytes (8 or 24 dots). Graphics (GS ( L /
/// GS 8 L function 112): Width x Height dots, rows of (Width + 7) / 8 bytes. Mode: GS v 0 m, ESC * m, or the
/// GS ( L tone byte a.
/// </summary>
public sealed record RasterPiece(RasterFormat Format, int Offset, int Length, int Width, int Height, int Mode, int ScaleX, int ScaleY);

/// <summary>The raw result of walking an ESC/POS byte stream: text bytes per line, with every command removed.</summary>
public sealed class ParsedJob
{
    public List<byte[]> Lines { get; } = new();
    /// <summary>Printable bytes (>= 0x20) that were not part of a command.</summary>
    public int TextBytes { get; set; }
    /// <summary>Control bytes (< 0x20) that are neither a known command prefix nor a line control.</summary>
    public int UnknownControls { get; set; }
    public int RasterCommands { get; set; }
    public int RasterBytes { get; set; }
    /// <summary>The last ESC t n seen (character code table), if any.</summary>
    public int? CodeTable { get; set; }
    public bool HasCut { get; set; }
    /// <summary>The stream starts like PCL, PostScript, PDF or a ZIP/XPS package, not ESC/POS.</summary>
    public bool ForeignFormat { get; set; }
    public int TotalBytes { get; set; }
    /// <summary>Image commands whose declared size runs past the end of the job (noise, not a real image).</summary>
    public int TruncatedRaster { get; set; }
    /// <summary>The complete image commands, in print order.</summary>
    public List<RasterPiece> RasterPieces { get; } = new();

    /// <returns>True when the declared payload is complete in the job.</returns>
    internal bool Raster(long declared, long available)
    {
        if (declared <= 0) return false;
        if (declared > available) { TruncatedRaster++; return false; }
        RasterCommands++;
        RasterBytes += (int)declared;
        return true;
    }
}

/// <summary>
/// Walks an ESC/POS job. It knows the length of the common Epson-compatible commands so that their
/// parameters and image payloads never leak into the text. Unknown commands are assumed to take one
/// parameter byte.
/// </summary>
public static class EscPosParser
{
    const byte ESC = 0x1B, GS = 0x1D, FS = 0x1C, DLE = 0x10;

    public static ParsedJob Parse(ReadOnlySpan<byte> d)
    {
        var job = new ParsedJob { TotalBytes = d.Length };
        job.ForeignFormat = IsForeign(d);
        var line = new List<byte>(64);
        int i = 0;

        void EndLine()
        {
            job.Lines.Add(line.ToArray());
            line.Clear();
        }

        while (i < d.Length)
        {
            byte b = d[i];
            if (b >= 0x20)
            {
                line.Add(b);
                job.TextBytes++;
                i++;
                continue;
            }
            switch (b)
            {
                case 0x0A: // LF
                case 0x0C: // FF
                    EndLine();
                    i++;
                    break;
                case 0x0D: // CR: a CR LF pair is one line end
                    if (i + 1 < d.Length && d[i + 1] == 0x0A) i++;
                    EndLine();
                    i++;
                    break;
                case 0x09: // HT
                    line.Add(0x20);
                    i++;
                    break;
                case ESC:
                    i = Esc(d, i, job, line, EndLine);
                    break;
                case GS:
                    i = Gs(d, i, job);
                    break;
                case FS:
                    i = Fs(d, i);
                    break;
                case DLE:
                    i = Dle(d, i);
                    break;
                case 0x00:
                    i++; // NUL padding is common and harmless
                    break;
                default:
                    job.UnknownControls++;
                    i++;
                    break;
            }
        }
        if (line.Count > 0) EndLine();
        return job;
    }

    static bool IsForeign(ReadOnlySpan<byte> d)
    {
        static bool Starts(ReadOnlySpan<byte> s, string p)
        {
            if (s.Length < p.Length) return false;
            for (int k = 0; k < p.Length; k++) if (s[k] != (byte)p[k]) return false;
            return true;
        }
        return Starts(d, "%!PS") || Starts(d, "%PDF") || Starts(d, "PK\u0003\u0004")
            || Starts(d, "\u001B%-12345X") || Starts(d, "\u001BE\u001B");
    }

    static int Skip(ReadOnlySpan<byte> d, int i, int n) => Math.Min(d.Length, i + n);

    static int Esc(ReadOnlySpan<byte> d, int i, ParsedJob job, List<byte> line, Action endLine)
    {
        if (i + 1 >= d.Length) return d.Length;
        byte c = d[i + 1];
        int p = i + 2; // first parameter
        switch ((char)c)
        {
            case '@': case '2': case 'i': case 'm': case 'L': case 'S': case '<':
                return p;
            case 'd': // print and feed n lines
            case 'J': // print and feed n dots
                endLine();
                return Skip(d, p, 1);
            case 't':
                if (p < d.Length) job.CodeTable = d[p];
                return Skip(d, p, 1);
            case '$': case '\\':
                return Skip(d, p, 2);
            case 'c': // ESC c 3 n / ESC c 4 n / ESC c 5 n
                return Skip(d, p, 2);
            case 'p': // pulse: m t1 t2
                return Skip(d, p, 3);
            case 'B': // beeper on many clones: n t
                return Skip(d, p, 2);
            case 'W':
                return Skip(d, p, 8);
            case 'D': // tab stops, NUL-terminated
                while (p < d.Length && d[p] != 0) p++;
                return Math.Min(d.Length, p + 1);
            case '*': // bit image: m nL nH data
            {
                if (p + 2 >= d.Length) return d.Length;
                int m = d[p];
                int n = d[p + 1] | (d[p + 2] << 8);
                int k = m == 32 || m == 33 ? 3 : 1;
                int bytes = n * k;
                if (job.Raster(bytes, d.Length - (p + 3)))
                    job.RasterPieces.Add(new RasterPiece(RasterFormat.EscStar, p + 3, bytes, n, 8 * k, m,
                        m == 0 || m == 32 ? 2 : 1, k == 1 ? 3 : 1)); // single density is half width; 8-dot bands are 1/3 height
                return Skip(d, p + 3, bytes);
            }
            case '&': // user-defined characters: y c1 c2 [x d1..d(y*x)]...
            {
                if (p + 2 >= d.Length) return d.Length;
                int y = d[p], c1 = d[p + 1], c2 = d[p + 2];
                int q = p + 3;
                for (int k = c1; k <= c2 && q < d.Length; k++)
                {
                    int x = d[q];
                    q += 1 + y * x;
                }
                return Math.Min(d.Length, q);
            }
            case 'V':
                // Some printers use ESC V n for rotation, a few use it to cut; treat as 1 param.
                return Skip(d, p, 1);
            default:
                // '!', '-', 'E', 'G', 'M', 'a', 'R', '{', '3', ' ', 'r', 'U', 'T', ... : one parameter
                return Skip(d, p, 1);
        }
    }

    static int Gs(ReadOnlySpan<byte> d, int i, ParsedJob job)
    {
        if (i + 1 >= d.Length) return d.Length;
        byte c = d[i + 1];
        int p = i + 2;
        switch ((char)c)
        {
            case 'V': // cut: m [n]
            {
                job.HasCut = true;
                if (p >= d.Length) return d.Length;
                int m = d[p];
                return Skip(d, p, (m == 0 || m == 1 || m == 48 || m == 49) ? 1 : 2);
            }
            case 'v': // GS v 0 m xL xH yL yH data
            {
                if (p + 5 >= d.Length) return d.Length;
                int x = d[p + 2] | (d[p + 3] << 8);
                int y = d[p + 4] | (d[p + 5] << 8);
                int bytes = x * y;
                int m = d[p + 1];
                if (job.Raster(bytes, d.Length - (p + 6)))
                    job.RasterPieces.Add(new RasterPiece(RasterFormat.GsV0, p + 6, bytes, x, y, m, (m & 1) != 0 ? 2 : 1, (m & 2) != 0 ? 2 : 1));
                return Skip(d, p + 6, bytes);
            }
            case '(': // GS ( fn pL pH data
            {
                if (p + 2 >= d.Length) return d.Length;
                byte fn = d[p];
                int len = d[p + 1] | (d[p + 2] << 8);
                if (fn == (byte)'L' && job.Raster(len, d.Length - (p + 3))) Graphics(d, p + 3, len, job);
                return Skip(d, p + 3, len);
            }
            case '8': // GS 8 L p1 p2 p3 p4 data
            {
                if (p + 4 >= d.Length) return d.Length;
                long len = (long)d[p + 1] + ((long)d[p + 2] << 8) + ((long)d[p + 3] << 16) + ((long)d[p + 4] << 24);
                if (job.Raster(len, d.Length - (p + 5))) Graphics(d, p + 5, (int)len, job);
                return (int)Math.Min(d.Length, p + 5 + len);
            }
            case '*': // define downloaded bit image: x y data(x*y*8)
            {
                if (p + 1 >= d.Length) return d.Length;
                int bytes = d[p] * d[p + 1] * 8;
                job.Raster(bytes, d.Length - (p + 2));
                return Skip(d, p + 2, bytes);
            }
            case 'k': // barcode
            {
                if (p >= d.Length) return d.Length;
                int m = d[p];
                if (m <= 6)
                {
                    int q = p + 1;
                    while (q < d.Length && d[q] != 0) q++;
                    return Math.Min(d.Length, q + 1);
                }
                if (p + 1 >= d.Length) return d.Length;
                return Skip(d, p + 2, d[p + 1]);
            }
            case 'L': case 'W': case 'P': case '$': case '\\':
                return Skip(d, p, 2);
            default:
                // '!', 'B', 'H', 'f', 'h', 'w', 'a', 'b', 'r', 'I', 'T', '/', ... : one parameter
                return Skip(d, p, 1);
        }
    }

    /// <summary>
    /// GS ( L / GS 8 L body: m fn [fn 112: a bx by c xL xH yL yH data]. Only function 112 (store raster graphics in
    /// the print buffer), monochrome (a = 48), is an image of the receipt; logos in NV memory (fn 67) are not.
    /// </summary>
    static void Graphics(ReadOnlySpan<byte> d, int start, int len, ParsedJob job)
    {
        if (len < 10 || d[start + 1] != 112 || d[start + 2] != 48) return;
        int bx = d[start + 3], by = d[start + 4];
        int x = d[start + 6] | (d[start + 7] << 8), y = d[start + 8] | (d[start + 9] << 8);
        int bytes = (x + 7) / 8 * y;
        if (x <= 0 || y <= 0 || bytes > len - 10) return;
        job.RasterPieces.Add(new RasterPiece(RasterFormat.Graphics, start + 10, bytes, x, y, 48, bx == 2 ? 2 : 1, by == 2 ? 2 : 1));
    }

    static int Fs(ReadOnlySpan<byte> d, int i)
    {
        if (i + 1 >= d.Length) return d.Length;
        char c = (char)d[i + 1];
        int p = i + 2;
        return c switch
        {
            '&' or '.' => p,
            'p' or 'S' => Skip(d, p, 2),
            _ => Skip(d, p, 1),
        };
    }

    static int Dle(ReadOnlySpan<byte> d, int i)
    {
        if (i + 1 >= d.Length) return d.Length;
        byte c = d[i + 1];
        int p = i + 2;
        if (c == 0x14) return Skip(d, p, 3); // DLE DC4 fn m t
        if (c == 0x04 || c == 0x05) return Skip(d, p, 1);
        return p;
    }
}
