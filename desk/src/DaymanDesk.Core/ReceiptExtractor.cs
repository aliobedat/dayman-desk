using System.Globalization;
using System.Text.RegularExpressions;

namespace DaymanDesk.Core;

public sealed class ExtractResult
{
    public string? InvoiceNo { get; init; }
    public decimal? Total { get; init; }
    public double InvoiceConfidence { get; init; }
    public double TotalConfidence { get; init; }
    /// <summary>The weaker of the two: a receipt is only as usable as its worst field.</summary>
    public double Confidence => Math.Min(InvoiceConfidence, TotalConfidence);
    public string? InvoiceLine { get; init; }
    public string? TotalLine { get; init; }
}

/// <summary>
/// Finds the invoice number and the total on decoded receipt lines. Each line is tried as printed and
/// reversed, because many POS programs send Arabic in visual (left-to-right) order to the printer.
/// </summary>
public static class ReceiptExtractor
{
    sealed record Label(string Text, double Weight, bool AllowAl);

    // Labels are written normalized (see TextNormalizer): ة -> ه, أ/إ -> ا.
    static readonly Label[] InvoiceLabels =
    {
        new("رقم الفاتوره", 0.95, false), new("فاتوره رقم", 0.95, true), new("رقم فاتوره", 0.95, false),
        new("invoice no", 0.95, false), new("invoice number", 0.95, false), new("invoice #", 0.95, false),
        new("inv no", 0.9, false), new("inv #", 0.9, false), new("bill no", 0.9, false), new("bill #", 0.9, false),
        new("receipt no", 0.85, false), new("فاتوره", 0.8, true), new("invoice", 0.8, false),
        // No bare «رقم», «inv» or «bill»: they also label daily order numbers, which must never become the invoice.
    };

    static readonly Label[] TotalLabels =
    {
        new("grand total", 1.0, false), new("المطلوب", 0.95, false), new("مطلوب", 0.9, false),
        new("صافي", 0.95, true), new("net total", 0.95, false), new("total due", 0.95, false), new("amount due", 0.95, false),
        new("اجمالي", 0.9, true), new("مجموع", 0.85, true), new("total", 0.8, false), new("net", 0.8, false),
    };

    static readonly string[] InvoiceExclude =
    {
        "هاتف", "تلفون", "تليفون", "موبايل", "جوال", "phone", "tel", "mobile", "ضريب", "tax", "vat", "سجل",
        "cashier", "كاشير", "table", "طاوله", "terminal", "pos", "شارع", "مفوض",
    };

    static readonly string[] TotalExclude =
    {
        "subtotal", "sub total", "sub-total", "فرعي", "قبل", "before", "خصم", "discount", "عدد", "qty", "quantity",
        "كميه", "items", "اصناف", "الباقي", "باقي", "change", "المدفوع", "مدفوع", "paid", "cash", "نقدا", "نقدي",
        "مستلم", "received", "tendered", "visa", "فيزا", "card", "بطاقه",
    };

    static readonly string[] TaxWords = { "ضريب", "tax", "vat" };
    static readonly string[] InclusiveWords = { "شامل", "incl", "with tax", "مع الضريب" };
    static readonly string[] CurrencyWords = { "jod", "jd", "د.ا", "دينار", "دا" };

    static readonly Regex NumberRx = new(@"\d[\d.,]*\d|\d", RegexOptions.Compiled);
    static readonly Regex TokenRx = new(@"[A-Za-z0-9][A-Za-z0-9\-/_]*", RegexOptions.Compiled);
    static readonly Regex DateRx = new(@"^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}$", RegexOptions.Compiled);
    static readonly Regex TimeRx = new(@"^\d{1,2}:\d{2}", RegexOptions.Compiled);

    public static ExtractResult Extract(IReadOnlyList<string> rawLines)
    {
        var lines = rawLines.Select(l => new Line(l)).ToList();
        var inv = FindInvoice(lines);
        var tot = FindTotal(lines);
        return new ExtractResult
        {
            InvoiceNo = inv.value,
            InvoiceConfidence = inv.conf,
            InvoiceLine = inv.line,
            Total = tot.value,
            TotalConfidence = tot.conf,
            TotalLine = tot.line,
        };
    }

    sealed class Line
    {
        public readonly string Raw, Fwd, Rev;
        /// <summary>Fwd with the original letter case, for values (same length as Fwd).</summary>
        public readonly string Cased;
        public Line(string raw)
        {
            Raw = raw;
            Fwd = TextNormalizer.Normalize(raw);
            Cased = TextNormalizer.Normalize(raw, lower: false);
            if (Cased.Length != Fwd.Length) Cased = Fwd;
            Rev = TextNormalizer.Normalize(TextNormalizer.Reverse(raw));
        }
    }

    /// <summary>Finds a label as a word (an Arabic label may carry the article ال). Returns index and length in s.</summary>
    static (int idx, int len) Find(string s, Label label)
    {
        string pre = label.AllowAl ? "(?:ال)?" : "";
        var rx = new Regex(@"(?<![\p{L}])" + pre + Regex.Escape(label.Text) + @"(?![\p{L}])");
        var m = rx.Match(s);
        return m.Success ? (m.Index, m.Length) : (-1, 0);
    }

    static bool ContainsAny(string s, string[] words) => words.Any(w => s.Contains(w, StringComparison.Ordinal));

    static (string? value, double conf, string? line) FindInvoice(List<Line> lines)
    {
        foreach (var label in InvoiceLabels)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                if (ContainsAny(line.Fwd, InvoiceExclude) || ContainsAny(line.Rev, InvoiceExclude)) continue;

                var (fi, fl) = Find(line.Fwd, label);
                if (fi >= 0)
                {
                    // Logical order: the value follows the label; else anything on the line.
                    var v = FirstToken(line.Cased, fi + fl, line.Fwd.Length) ?? LastToken(line.Cased, 0, fi);
                    if (v != null) return (v, label.Weight, line.Raw);
                    if (li + 1 < lines.Count)
                    {
                        var next = FirstToken(lines[li + 1].Cased, 0, lines[li + 1].Cased.Length);
                        if (next != null) return (next, label.Weight * 0.8, line.Raw);
                    }
                    continue;
                }
                var (ri, rl) = Find(line.Rev, label);
                if (ri >= 0)
                {
                    // Visual order: the reversed label sits right of its value in the printed line.
                    int start = Math.Max(0, line.Fwd.Length - ri - rl);
                    var v = LastToken(line.Cased, 0, start) ?? FirstToken(line.Cased, Math.Min(line.Fwd.Length, line.Fwd.Length - ri), line.Fwd.Length);
                    if (v != null) return (v, label.Weight * 0.95, line.Raw);
                }
            }
        }
        return (null, 0, null);
    }

    static IEnumerable<string> Tokens(string s, int from, int to)
    {
        if (to <= from) yield break;
        foreach (Match m in TokenRx.Matches(s.Substring(from, to - from)))
        {
            string t = m.Value.Trim('-', '/', '_');
            if (t.Length == 0 || t.Length > 40) continue;
            if (!t.Any(char.IsDigit)) continue;
            if (DateRx.IsMatch(t) || TimeRx.IsMatch(t)) continue;
            yield return t;
        }
    }

    static string? FirstToken(string s, int from, int to)
    {
        var all = TokensWithDecimals(s, from, to);
        return all.FirstOrDefault();
    }

    static string? LastToken(string s, int from, int to) => TokensWithDecimals(s, from, to).LastOrDefault();

    static List<string> TokensWithDecimals(string s, int from, int to)
    {
        // A token directly followed by ".ddd" or ":dd" is an amount or a time, not an invoice number.
        var list = new List<string>();
        if (to <= from) return list;
        string sub = s.Substring(from, to - from);
        foreach (Match m in TokenRx.Matches(sub))
        {
            int end = m.Index + m.Length;
            if (end < sub.Length && (sub[end] == '.' || sub[end] == ',' || sub[end] == ':') && end + 1 < sub.Length && char.IsDigit(sub[end + 1]))
                continue;
            if (m.Index > 0 && (sub[m.Index - 1] == '.' || sub[m.Index - 1] == ':') && m.Index > 1 && char.IsDigit(sub[m.Index - 2]))
                continue;
            list.AddRange(Tokens(sub, m.Index, end));
        }
        return list;
    }

    static (decimal? value, double conf, string? line) FindTotal(List<Line> lines)
    {
        decimal? best = null;
        double bestConf = 0;
        string? bestLine = null;
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            string both = line.Fwd + " | " + line.Rev;
            bool inclusive = ContainsAny(both, InclusiveWords);
            if (ContainsAny(both, TotalExclude)) continue;
            if (!inclusive && ContainsAny(both, TaxWords)) continue;

            double w = 0;
            foreach (var label in TotalLabels)
            {
                if (Find(line.Fwd, label).idx >= 0 || Find(line.Rev, label).idx >= 0) { w = label.Weight; break; }
            }
            if (w == 0) continue;

            var (amount, hasDecimals) = PickAmount(line.Fwd);
            if (amount == null && li + 1 < lines.Count)
            {
                (amount, hasDecimals) = PickAmount(lines[li + 1].Fwd);
                w *= 0.85;
            }
            if (amount == null) continue;
            if (!hasDecimals) w *= 0.8;
            if (ContainsAny(both, CurrencyWords)) w = Math.Min(1.0, w + 0.05);

            // Higher weight wins; a tie goes to the lower line (the grand total is printed last).
            if (w >= bestConf)
            {
                best = amount;
                bestConf = w;
                bestLine = line.Raw;
            }
        }
        if (best != null) return (best, Math.Round(bestConf, 2), bestLine);

        // Fallback: the largest amount on a line that names the currency.
        foreach (var line in lines.AsEnumerable().Reverse())
        {
            if (!ContainsAny(line.Fwd + " " + line.Rev, CurrencyWords)) continue;
            var (amount, _) = PickAmount(line.Fwd);
            if (amount != null && (best == null || amount > best)) { best = amount; bestLine = line.Raw; }
        }
        return best != null ? (best, 0.4, bestLine) : (null, 0, null);
    }

    static (decimal? amount, bool hasDecimals) PickAmount(string s)
    {
        decimal? bestDec = null, bestInt = null;
        foreach (Match m in NumberRx.Matches(s))
        {
            // Skip dates and times: a number glued to '/', ':' or '-' with another number.
            int end = m.Index + m.Length;
            if (end < s.Length && (s[end] == '/' || s[end] == ':') ) continue;
            if (m.Index > 0 && (s[m.Index - 1] == '/' || s[m.Index - 1] == ':')) continue;
            var (val, dec) = ParseAmount(m.Value);
            if (val == null || val <= 0 || val >= 100000) continue;
            if (dec) { if (bestDec == null || val > bestDec) bestDec = val; }
            else { if (bestInt == null || val > bestInt) bestInt = val; }
        }
        if (bestDec != null) return (bestDec, true);
        return (bestInt, false);
    }

    /// <summary>
    /// Parses "12.500", "12,500", "1,250.750", "1.250,750", "12.5", "12". A single comma with 1 to 3
    /// digits after it is a decimal comma (JOD has 3 decimals and shop receipts are small).
    /// </summary>
    public static (decimal? value, bool hasDecimals) ParseAmount(string token)
    {
        string t = token.Trim().Trim('.', ',');
        if (t.Length == 0) return (null, false);
        int dots = t.Count(c => c == '.'), commas = t.Count(c => c == ',');
        string intPart, frac = "";
        if (dots > 0 && commas > 0)
        {
            char dec = t.LastIndexOf('.') > t.LastIndexOf(',') ? '.' : ',';
            char thou = dec == '.' ? ',' : '.';
            if (t.Count(c => c == dec) != 1) return (null, false);
            var parts = t.Replace(thou.ToString(), "").Split(dec);
            intPart = parts[0]; frac = parts[1];
        }
        else if (dots + commas == 1)
        {
            var parts = t.Split(dots == 1 ? '.' : ',');
            intPart = parts[0]; frac = parts[1];
            if (frac.Length > 3) return (null, false);
        }
        else if (dots + commas > 1)
        {
            // "1,250,000" or "1.250.000": thousands only
            char sep = dots > 0 ? '.' : ',';
            var groups = t.Split(sep);
            if (groups.Skip(1).Any(g => g.Length != 3)) return (null, false);
            intPart = string.Concat(groups);
        }
        else intPart = t;

        if (frac.Length > 3 || intPart.Length == 0 || !intPart.All(char.IsDigit) || !frac.All(char.IsDigit))
            return (null, false);
        var s = frac.Length > 0 ? intPart + "." + frac : intPart;
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? (v, frac.Length > 0)
            : (null, false);
    }
}
