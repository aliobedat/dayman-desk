using System.Text;

namespace DaymanDesk.Core;

public enum ReceiptKind { Text, Image, Unknown }

public sealed class DecodingScore
{
    public int CodePage { get; init; }
    public string Name { get; init; } = "";
    public double Score { get; init; }
    public int ArabicLetters { get; init; }
    public int Replacements { get; init; }
    public int WordHits { get; init; }
}

public sealed class AnalysisResult
{
    public ReceiptKind Kind { get; init; }
    public bool HasRaster { get; init; }
    public bool HasCut { get; init; }
    /// <summary>The chosen code page (864, 720, 1256 or 65001); null when the text has no Arabic.</summary>
    public int? CodePage { get; init; }
    public string EncodingName { get; init; } = "";
    public int? CodeTable { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DecodingScore> Scores { get; init; } = Array.Empty<DecodingScore>();
    public string Reason { get; init; } = "";
    /// <summary>A coarse name of the print format (WO-89: unknown formats are reported once per format per day).</summary>
    public string Format { get; init; } = "";
    /// <summary>Printable text bytes in the job (WO-91: an unknown job with fewer than 20 is a printer command, not a receipt).</summary>
    public int TextChars { get; init; }
}

/// <summary>Classifies a captured print job and decodes its text with the most plausible Arabic code page.</summary>
public static class ReceiptAnalyzer
{
    public const int Cp864 = 864, Cp720 = 720, Cp1256 = 1256, Utf8 = 65001;
    public static readonly int[] Candidates = { Cp864, Cp720, Cp1256, Utf8 };

    static readonly object InitLock = new();
    static bool _registered;

    public static void EnsureCodePages()
    {
        lock (InitLock)
        {
            if (_registered) return;
            // WO-95: .NET Framework has every code page built in; on .NET (the Linux tests) register the provider,
            // found by name because netstandard2.0 has no reference to it.
            var provider = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages")
                ?.GetProperty("Instance")?.GetValue(null) as EncodingProvider;
            if (provider != null) Encoding.RegisterProvider(provider);
            _registered = true;
        }
    }

    public static Encoding GetDecoder(int cp)
    {
        EnsureCodePages();
        if (cp == Utf8) return new UTF8Encoding(false, false);
        return Encoding.GetEncoding(cp, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));
    }

    public static string Label(int cp) => cp switch
    {
        Cp864 => "CP864",
        Cp720 => "CP720",
        Cp1256 => "CP1256",
        Utf8 => "UTF-8",
        _ => "CP" + cp,
    };

    // Words that appear on almost every Arabic receipt (normalized: no hamza on alef, ة -> ه, ى -> ي).
    static readonly string[] CommonWords =
    {
        "رقم", "فاتوره", "الفاتوره", "المجموع", "مجموع", "الاجمالي", "اجمالي", "المطلوب", "الصافي", "صافي",
        "دينار", "شكرا", "نقدا", "نقدي", "الكميه", "السعر", "التاريخ", "المبلغ", "ضريبه", "المبيعات", "الوقت",
        "الكاشير", "العميل", "خصم", "الباقي", "المدفوع", "الصنف", "الاصناف", "زيارتكم", "اهلا", "طاوله", "سفري",
        "توصيل", "فرع", "مطعم", "عدد", "قيمه", "المجموع", "الكل", "الضريبه",
    };

    public static AnalysisResult Analyze(byte[] data)
    {
        var job = EscPosParser.Parse(data);
        var nonEmpty = job.Lines.Where(l => l.Any(b => b > 0x20)).ToList();
        int textChars = nonEmpty.Sum(l => l.Count(b => b > 0x20));

        if (job.ForeignFormat)
            return new AnalysisResult { Kind = ReceiptKind.Unknown, Reason = "foreign format (PCL/PS/PDF/XPS)", Format = ForeignName(data) };

        // Join all text lines once, decode each candidate, keep the best.
        var joined = new List<byte>();
        foreach (var l in nonEmpty) { joined.AddRange(l); joined.Add(0x0A); }
        var bytes = joined.ToArray();

        var scores = new List<DecodingScore>();
        var decoded = new Dictionary<int, string>();
        foreach (int cp in Candidates)
        {
            string s = GetDecoder(cp).GetString(bytes);
            decoded[cp] = s;
            scores.Add(Score(cp, s));
        }
        var best = scores.OrderByDescending(x => x.Score).ThenBy(x => Array.IndexOf(Candidates, x.CodePage)).First();
        string bestText = decoded[best.CodePage];
        bool hasArabic = best.ArabicLetters >= 3;
        if (!hasArabic)
        {
            // Plain Latin text decodes the same everywhere; CP1256 keeps ASCII as ASCII.
            best = scores.First(x => x.CodePage == Cp1256);
            bestText = decoded[Cp1256];
        }

        double unknownCtl = job.TotalBytes == 0 ? 1 : (double)job.UnknownControls / Math.Max(1, job.TotalBytes - job.RasterBytes);
        double good = GoodRatio(bestText);
        var lines = bestText.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).ToList();

        bool textOk = textChars >= 20 && unknownCtl < 0.02 && good >= 0.85 && best.Replacements <= Math.Max(2, textChars / 50);
        ReceiptKind kind;
        string reason;
        if (textOk)
        {
            kind = ReceiptKind.Text;
            reason = $"text {textChars} chars, {Label(best.CodePage)}";
        }
        else if (job.RasterCommands > 0 && job.RasterBytes >= 64 && job.RasterBytes >= job.TotalBytes * 0.3 && job.TruncatedRaster == 0)
        {
            kind = ReceiptKind.Image;
            reason = $"raster {job.RasterCommands} commands, {job.RasterBytes} bytes";
        }
        else
        {
            kind = ReceiptKind.Unknown;
            reason = $"text {textChars}, controls {unknownCtl:P1}, good {good:P0}";
        }

        return new AnalysisResult
        {
            Kind = kind,
            HasRaster = job.RasterCommands > 0,
            HasCut = job.HasCut,
            CodePage = kind == ReceiptKind.Text && hasArabic ? best.CodePage : null,
            EncodingName = kind == ReceiptKind.Text ? (hasArabic ? Label(best.CodePage) : "Latin") : "",
            CodeTable = job.CodeTable,
            Lines = kind == ReceiptKind.Text ? lines : Array.Empty<string>(),
            Scores = scores,
            Reason = reason,
            TextChars = textChars,
            Format = kind == ReceiptKind.Text ? "escpos-text" : job.RasterCommands > 0 || job.TruncatedRaster > 0 ? "escpos-raster" : job.TotalBytes == 0 ? "empty" : "escpos-other",
        };
    }

    static string ForeignName(byte[] d)
    {
        string head = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 12));
        if (head.StartsWith("%PDF", StringComparison.Ordinal)) return "pdf";
        if (head.StartsWith("%!PS", StringComparison.Ordinal)) return "postscript";
        if (head.StartsWith("PK", StringComparison.Ordinal)) return "xps-zip";
        return "pcl";
    }

    public static DecodingScore Score(int cp, string s)
    {
        int arabic = 0, repl = 0, odd = 0;
        foreach (char c in s)
        {
            if (TextNormalizer.IsArabicLetter(c)) arabic++;
            else if (c == '�') repl++;
            else if (IsOdd(c)) odd++;
        }
        string fwd = TextNormalizer.Normalize(s);
        string rev = TextNormalizer.Normalize(TextNormalizer.Reverse(s));
        int words = Math.Max(CountWords(fwd), CountWords(rev));
        int al = Math.Max(Count(fwd, "ال"), Count(rev, "ال"));
        double score = arabic + 15.0 * words + 2.0 * al - 8.0 * repl - 3.0 * odd;
        return new DecodingScore { CodePage = cp, Name = Label(cp), Score = score, ArabicLetters = arabic, Replacements = repl, WordHits = words };
    }

    static bool IsOdd(char c) =>
        (c >= '\u0080' && c <= '¿') ||           // C1 controls and Latin-1 symbols
        (c >= 'À' && c <= 'ÿ') ||           // accented Latin (CP1256/CP720 noise in Arabic text)
        (c >= '─' && c <= '◿') ||           // box drawing, blocks, shapes
        (c >= 'Ͱ' && c <= 'Ͽ') ||           // Greek
        (c >= '∀' && c <= '⋿') ||           // math operators
        (c >= '' && c <= '') ||           // private use
        (c >= 'ݐ' && c <= 'ݿ') ||
        (c >= 'پ' && c <= 'ۿ' && !(c >= '۰' && c <= '۹')); // Persian/Urdu letters: rare in Jordan

    static int CountWords(string norm)
    {
        int hits = 0;
        foreach (var w in CommonWords) if (norm.Contains(w, StringComparison.Ordinal)) hits++;
        return hits;
    }

    static int Count(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    static double GoodRatio(string s)
    {
        int total = 0, good = 0;
        foreach (char c in s)
        {
            if (c == '\n' || c == ' ') continue;
            total++;
            if (char.IsLetterOrDigit(c) && !IsOdd(c)) good++;
            else if ("-.,:;/#()*%+=_'\"!?@&<>[]|\\$~".IndexOf(c) >= 0) good++;
            else if (c >= '؀' && c <= 'ۿ') good++; // Arabic punctuation and digits
            else if (c >= 'ﹰ' && c <= '﻿') good++;
        }
        return total == 0 ? 0 : (double)good / total;
    }
}
