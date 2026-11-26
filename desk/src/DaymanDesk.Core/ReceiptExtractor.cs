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
/// One field the owner confirmed on the first receipt: the line's label (the line without its numbers) and where
/// the line sits. Applied first on later receipts; the heuristics only run when it does not match.
/// </summary>
public sealed class LearnedField
{
    /// <summary>ReceiptExtractor.LabelKey of the label line ("" when the field was learned by its shape).</summary>
    public string Label { get; set; } = "";
    /// <summary>
    /// WO-91: ReceiptExtractor.ShapeKey of the label line, set only when it had no letters (a driver or code page
    /// that turns every Arabic letter into '.'). Null in configs learned before 1.2.2 and for letter labels.
    /// </summary>
    public string? Shape { get; set; }
    /// <summary>
    /// WO-91, shape fields only: the shape of the line next to the field at confirmation (invoice: the line above its
    /// label; total: the line after its value; "" when there is none). A look-alike line elsewhere (a Z report, a
    /// refund) has other neighbours.
    /// </summary>
    public string? Neighbor { get; set; }
    /// <summary>The value is on the line after the label line (the label line holds no number).</summary>
    public bool ValueOnNextLine { get; set; }
    /// <summary>Invoice: line index from the top. Total: line index from the bottom.</summary>
    public int Position { get; set; }
    /// <summary>Invoice only: which number-like token of the value line (0 = first).</summary>
    public int TokenIndex { get; set; }
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

    /// <summary>Confidence of a field found through the owner's confirmed label.</summary>
    public const double LearnedConfidence = 0.95;

    /// <summary>
    /// The heuristics' ceiling when the owner's confirmed label is missing on a receipt: below MinConfidence,
    /// so a line the owner corrected is never registered again by a heuristic guess.
    /// </summary>
    public const double UnmatchedLearnedCap = DeskAgent.MinConfidence - 0.05;

    /// <param name="ocr">
    /// The lines come from OCR of an image receipt (WO-89): a total must have decimals (JOD prints 3; an OCR that
    /// dropped the point would read 6.670 as 6670), and an amount right after another digit group («1 250.000»:
    /// 1250 or 250?) is refused rather than guessed.
    /// </param>
    public static ExtractResult Extract(IReadOnlyList<string> rawLines, LearnedField? learnedInvoice = null, LearnedField? learnedTotal = null, bool ocr = false)
    {
        var lines = rawLines.Select(l => new Line(l)).ToList();
        var inv = learnedInvoice != null ? ApplyInvoice(lines, learnedInvoice, ocr) : (null, 0, null);
        if (inv.value == null)
        {
            inv = FindInvoice(lines);
            if (learnedInvoice != null) inv.conf = Math.Min(inv.conf, UnmatchedLearnedCap);
        }
        var tot = learnedTotal != null ? ApplyTotal(lines, learnedTotal, ocr) : (null, 0, null);
        if (tot.value == null)
        {
            tot = FindTotal(lines, ocr);
            if (learnedTotal != null) tot.conf = Math.Min(tot.conf, UnmatchedLearnedCap);
        }
        // WO-91: shapes only count together. Both fields learned by shape and both matched, or neither counts.
        bool anyShape = !string.IsNullOrEmpty(learnedInvoice?.Shape) || !string.IsNullOrEmpty(learnedTotal?.Shape);
        bool bothShape = !string.IsNullOrEmpty(learnedInvoice?.Shape) && !string.IsNullOrEmpty(learnedTotal?.Shape);
        if (anyShape && !(bothShape && inv.conf >= LearnedConfidence && tot.conf >= LearnedConfidence))
        {
            inv.conf = Math.Min(inv.conf, UnmatchedLearnedCap);
            tot.conf = Math.Min(tot.conf, UnmatchedLearnedCap);
        }
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

    /// <summary>
    /// A line reduced to its label: normalized, without digits, number punctuation or extra spaces. Two
    /// receipts of the same POS give the same key for the same label whatever the numbers on it. A printed
    /// time and its AM/PM (ص/م) are dropped too, so a label learned in the morning still matches at night.
    /// </summary>
    public static string LabelKey(string raw)
    {
        // Times and their AM/PM first, then whole Latin value tokens ("INV-2291", "A12"), so a letter
        // prefix of a number is not label.
        var n = AmPmRx.Replace(ClockRx.Replace(TextNormalizer.Normalize(raw), " "), " ");
        n = ValueTokenRx.Replace(n, " ");
        var sb = new System.Text.StringBuilder(n.Length);
        foreach (char c in n)
        {
            if (char.IsDigit(c) || LabelPunctuation.IndexOf(c) >= 0) sb.Append(' ');
            else sb.Append(c);
        }
        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    const string LabelPunctuation = ".,:;#/\\-_*=|()[]%+";

    static readonly Regex ClockRx = new(@"\d{1,2}:\d{2}(?::\d{2})?", RegexOptions.Compiled);
    static readonly Regex AmPmRx = new(@"(?<![\p{L}])(?:a\.?m\.?|p\.?m\.?|ص|م)(?![\p{L}])", RegexOptions.Compiled);

    /// <summary>
    /// WO-91: a line without letters reduced to its shape: normalized, every number (and time) replaced by one '9',
    /// whitespace collapsed; punctuation, dots and garbled characters kept. «... ........: 1001» gives
    /// «... ........: 9» whatever the invoice number, so the same label of the same POS gives the same shape.
    /// </summary>
    public static string ShapeKey(string raw)
    {
        var n = NumberRx.Replace(ClockRx.Replace(TextNormalizer.Normalize(raw), "9"), "9");
        return string.Join(" ", n.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A shape worth matching: at least two characters besides the numbers (never a bare number line).</summary>
    static bool UsableShape(string shape) => shape.Count(c => c != ' ' && c != '9') >= 2;

    static readonly Regex ValueTokenRx = new(@"[a-z0-9\-/_.,#]*[0-9][a-z0-9\-/_.,#]*", RegexOptions.Compiled);

    /// <summary>A usable label: at least two letters, and more than a currency name («2.000 JOD» has no label).</summary>
    static bool HasLetters(string key) =>
        key.Count(char.IsLetter) >= 2 && !key.Split(' ').All(w => CurrencyWords.Contains(w) || w == "د" || w == "ا");

    /// <summary>The invoice-number candidates on a line, in line order.</summary>
    public static IReadOnlyList<string> InvoiceTokens(string raw)
    {
        var l = new Line(raw);
        return TokensWithDecimals(l.Cased, 0, l.Cased.Length);
    }

    /// <summary>The amount a line would give as the total (the largest amount on it), or null.</summary>
    public static decimal? AmountOf(string raw) => PickAmount(new Line(raw).Fwd).amount;

    /// <summary>
    /// Learns the invoice field from the line the owner chose. A line without letters (only the number) takes
    /// its label from the line above. Returns null when the chosen line holds no invoice-like token.
    /// </summary>
    /// <param name="forceShape">WO-91: learn the shape even when the line has letters (the other field is a shape).</param>
    public static (LearnedField field, string value)? LearnInvoice(IReadOnlyList<string> lines, int index, string? preferredValue = null, bool forceShape = false)
    {
        if (index < 0 || index >= lines.Count) return null;
        var tokens = InvoiceTokens(lines[index]);
        if (tokens.Count == 0) return null;
        int ti = 0;
        if (preferredValue != null) { int k = tokens.ToList().IndexOf(preferredValue); if (k >= 0) ti = k; }
        var (label, shape, next, labelIndex) = LabelFor(lines, index, forceShape);
        if (label == null && shape == null) return null;
        return (new LearnedField
        {
            Label = label ?? "", Shape = shape, ValueOnNextLine = next, Position = labelIndex, TokenIndex = ti,
            Neighbor = shape != null ? InvoiceNeighbor(lines.Select(ShapeKey).ToList(), labelIndex) : null,
        }, tokens[ti]);
    }

    /// <summary>Learns the total field from the line the owner chose; null when it holds no amount.</summary>
    public static (LearnedField field, decimal value)? LearnTotal(IReadOnlyList<string> lines, int index, bool forceShape = false)
    {
        if (index < 0 || index >= lines.Count) return null;
        var amount = AmountOf(lines[index]);
        if (amount == null) return null;
        var (label, shape, next, labelIndex) = LabelFor(lines, index, forceShape);
        if (label == null && shape == null) return null;
        return (new LearnedField
        {
            Label = label ?? "", Shape = shape, ValueOnNextLine = next, Position = lines.Count - 1 - labelIndex,
            Neighbor = shape != null ? TotalNeighbor(lines.Select(ShapeKey).ToList(), labelIndex, next) : null,
        }, amount.Value);
    }

    /// <summary>The shape of the line above the invoice label, or "".</summary>
    static string InvoiceNeighbor(IReadOnlyList<string> shapes, int labelIndex) => labelIndex > 0 ? shapes[labelIndex - 1] : "";

    /// <summary>The shape of the line after the total's value, or "".</summary>
    static string TotalNeighbor(IReadOnlyList<string> shapes, int labelIndex, bool valueOnNext)
    {
        int n = labelIndex + (valueOnNext ? 2 : 1);
        return n < shapes.Count ? shapes[n] : "";
    }

    /// <summary>WO-91: a shape field may move this many lines (an extra item line, a longer header) and no more.</summary>
    public const int ShapeDrift = 2;

    /// <summary>
    /// The reason «تأكيد الحقول» refuses a line for the invoice number, or null when it can be learned. The window and
    /// DeskAgent.ConfirmFields both use it (WO-91: the window never shows a value that saving then refuses).
    /// </summary>
    public static string? InvoiceLineProblem(IReadOnlyList<string> lines, int index)
    {
        if (index < 0 || index >= lines.Count) return "اضغط أولاً على سطر رقم الفاتورة في الفاتورة.";
        if (InvoiceTokens(lines[index]).Count == 0) return "السطر المختار لرقم الفاتورة لا يحوي رقماً. اختر السطر الذي يظهر فيه رقم الفاتورة.";
        if (LearnInvoice(lines, index) == null) return "لا يستطيع البرنامج تمييز سطر رقم الفاتورة هذا في الفواتير التالية: لا عنوان فيه ولا في السطر الذي فوقه. اختر السطر الذي يظهر فيه رقم الفاتورة مع عنوانه.";
        return null;
    }

    /// <summary>The reason «تأكيد الحقول» refuses a line for the total, or null when it can be learned.</summary>
    public static string? TotalLineProblem(IReadOnlyList<string> lines, int index)
    {
        if (index < 0 || index >= lines.Count) return "اضغط أولاً على سطر المجموع في الفاتورة.";
        if (AmountOf(lines[index]) == null) return "السطر المختار للمجموع لا يحوي مبلغاً. اختر السطر الذي يظهر فيه المجموع النهائي.";
        if (LearnTotal(lines, index) == null) return "لا يستطيع البرنامج تمييز سطر المجموع هذا في الفواتير التالية: لا عنوان فيه ولا في السطر الذي فوقه. اختر السطر الذي يظهر فيه المجموع مع عنوانه.";
        return null;
    }

    /// <summary>
    /// The label of the chosen line: its letter label, else the letter label of the line above (value on the next
    /// line). WO-91: without letters on either, the chosen line's shape, else the shape of the line above.
    /// </summary>
    static (string? label, string? shape, bool next, int labelIndex) LabelFor(IReadOnlyList<string> lines, int index, bool forceShape = false)
    {
        var key = LabelKey(lines[index]);
        if (!forceShape && HasLetters(key)) return (key, null, false, index);
        if (!forceShape && index > 0)
        {
            var above = LabelKey(lines[index - 1]);
            if (HasLetters(above)) return (above, null, true, index - 1);
        }
        var shape = ShapeKey(lines[index]);
        if (UsableShape(shape)) return (null, shape, false, index);
        if (index > 0)
        {
            var aboveShape = ShapeKey(lines[index - 1]);
            if (UsableShape(aboveShape)) return (null, aboveShape, true, index - 1);
        }
        return (null, null, false, index);
    }

    /// <summary>
    /// WO-91: the lines with the learned shape, closest to the learned position first, and whether the closest one
    /// counts as a learned match: the only line of the receipt with that shape, within ShapeDrift lines of the learned
    /// position (invoice from the top, total from the bottom), with the same neighbouring line as at confirmation.
    /// Otherwise the closest only tells what the receipt probably says (logged and shown), below MinConfidence: no QR.
    /// </summary>
    static (List<int> hits, bool counts) ShapeMatches(List<Line> lines, LearnedField f, bool fromBottom)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++) if (lines[i].Shape == f.Shape) hits.Add(i);
        int Pos(int i) => fromBottom ? lines.Count - 1 - i : i;
        var ordered = hits.OrderBy(i => Math.Abs(Pos(i) - f.Position)).ToList();
        if (hits.Count != 1 || f.Neighbor == null) return (ordered, false);
        int hit = hits[0];
        var shapes = lines.Select(l => l.Shape).ToList();
        string neighbor = fromBottom ? TotalNeighbor(shapes, hit, f.ValueOnNextLine) : InvoiceNeighbor(shapes, hit);
        return (ordered, Math.Abs(Pos(hit) - f.Position) <= ShapeDrift && neighbor == f.Neighbor);
    }

    /// <summary>
    /// The lines whose label is the learned one, closest to the learned position first. WO-105, OCR only: without an
    /// exact match, a label one OCR letter off («الفاثوره», «المجموعح الفرعي»; 1 edit per 7 letters) matches too.
    /// </summary>
    static IEnumerable<int> Matches(List<Line> lines, LearnedField f, bool fromBottom, bool ocr = false)
    {
        var hits = new List<int>();
        // LabelKey again: a label saved before a LabelKey change (e.g. with "pm" in it) still matches.
        var label = LabelKey(f.Label);
        for (int i = 0; i < lines.Count; i++) if (lines[i].Key == label) hits.Add(i);
        if (hits.Count == 0 && ocr && label.Length >= 6)
        {
            int max = Math.Max(1, label.Length / 7);
            for (int i = 0; i < lines.Count; i++) if (Edits(lines[i].Key, label, max) <= max) hits.Add(i);
        }
        return hits.OrderBy(i => Math.Abs((fromBottom ? lines.Count - 1 - i : i) - f.Position));
    }

    /// <summary>Levenshtein distance, or more than max as soon as it is certain.</summary>
    static int Edits(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// WO-104: the lines of an OCR page worth reading again on their own: the value lines of the owner's confirmed
    /// fields, and every line labelled as an invoice number or a total (not excluded), with the next line when the
    /// label line holds no number.
    /// </summary>
    public static (ISet<int> invoice, ISet<int> total) FieldLines(IReadOnlyList<string> rawLines, LearnedField? learnedInvoice, LearnedField? learnedTotal)
    {
        var lines = rawLines.Select(l => new Line(l)).ToList();
        var inv = new HashSet<int>();
        var tot = new HashSet<int>();
        void Learned(HashSet<int> set, LearnedField? f, bool fromBottom)
        {
            if (f == null) return;
            var hits = !string.IsNullOrEmpty(f.Shape) ? ShapeMatches(lines, f, fromBottom).hits
                : !string.IsNullOrEmpty(f.Label) ? Matches(lines, f, fromBottom, ocr: true) : Enumerable.Empty<int>();
            foreach (int i in hits) if ((f.ValueOnNextLine ? i + 1 : i) < lines.Count) set.Add(f.ValueOnNextLine ? i + 1 : i);
        }
        Learned(inv, learnedInvoice, false);
        Learned(tot, learnedTotal, true);
        void Labelled(HashSet<int> set, int i)
        {
            set.Add(i);
            if (!lines[i].Fwd.Any(char.IsDigit) && i + 1 < lines.Count) set.Add(i + 1);
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string both = line.Fwd + " | " + line.Rev;
            if (!ContainsAny(line.Fwd, InvoiceExclude) && !ContainsAny(line.Rev, InvoiceExclude)
                && InvoiceLabels.Any(l => Find(line.Fwd, l).idx >= 0 || Find(line.Rev, l).idx >= 0)) Labelled(inv, i);
            if (!ContainsAny(both, TotalExclude) && (ContainsAny(both, InclusiveWords) || !ContainsAny(both, TaxWords))
                && TotalLabels.Any(l => Find(line.Fwd, l).idx >= 0 || Find(line.Rev, l).idx >= 0)) Labelled(tot, i);
        }
        return (inv, tot);
    }

    /// <summary>
    /// WO-105: the two OCR reads of an image receipt agree: both found an invoice number and a total, the invoice numbers
    /// equal once trimmed of edge punctuation and direction marks, the totals equal to 3 decimals.
    /// </summary>
    public static bool SameRead(ExtractResult a, ExtractResult b) =>
        a.InvoiceNo != null && a.Total != null && b.InvoiceNo != null && b.Total != null
        && string.Equals(CleanInvoice(a.InvoiceNo), CleanInvoice(b.InvoiceNo), StringComparison.Ordinal) && CleanInvoice(a.InvoiceNo).Length > 0
        && Math.Round(a.Total.Value, 3) == Math.Round(b.Total.Value, 3);

    /// <summary>The invoice number as sent: edge punctuation and direction marks trimmed.</summary>
    public static string CleanInvoice(string s) =>
        new string(s.Where(c => !TextNormalizer.IsBidiMark(c)).ToArray()).Trim().Trim('.', ',', ':', ';', '-', '_', '/', '#');

    static readonly Regex DecimalRx =new(@"\d+[.,]\d{1,3}(?![\d.,])", RegexOptions.Compiled);

    /// <summary>
    /// WO-104: a page line completed by its own line read, only where the page lost the value (a read that was fine is
    /// never replaced: the line read may pick up a stray mark). Total: a page line without a decimal amount takes the
    /// line read's decimal amounts in place of its numbers. Invoice: a page line without an invoice-like token takes the
    /// line read's longest one when it has at least 3 digits.
    /// </summary>
    public static string MergeLineRead(string page, string read, bool total)
    {
        if (total)
        {
            if (DecimalRx.IsMatch(page)) return page;
            var amounts = DecimalRx.Matches(read).Cast<Match>().Select(m => m.Value).ToList();
            if (amounts.Count == 0) return page;
            var rest = NumberRx.Replace(page, " ");
            return string.Join(" ", (rest + " " + string.Join(" ", amounts)).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
        if (InvoiceTokens(page).Any(t => t.Any(char.IsDigit))) return page;
        var best = InvoiceTokens(read).Where(t => t.Count(char.IsDigit) >= 3).OrderByDescending(t => t.Length).FirstOrDefault();
        return best == null ? page : page + " " + best;
    }

    static (string? value, double conf, string? line) ApplyInvoice(List<Line> lines, LearnedField f, bool ocr)
    {
        if (!string.IsNullOrEmpty(f.Shape))
        {
            var (hits, counts) = ShapeMatches(lines, f, fromBottom: false);
            foreach (int i in hits)
            {
                int vi = f.ValueOnNextLine ? i + 1 : i;
                if (vi >= lines.Count) continue;
                var tokens = TokensWithDecimals(lines[vi].Cased, 0, lines[vi].Cased.Length);
                // The same value position, never another number of the line.
                if (f.TokenIndex >= tokens.Count) continue;
                return (tokens[f.TokenIndex], counts ? LearnedConfidence : UnmatchedLearnedCap, lines[i].Raw);
            }
            return (null, 0, null);
        }
        if (string.IsNullOrEmpty(f.Label)) return (null, 0, null);
        foreach (int i in Matches(lines, f, fromBottom: false, ocr))
        {
            int vi = f.ValueOnNextLine ? i + 1 : i;
            if (vi >= lines.Count) continue;
            var tokens = TokensWithDecimals(lines[vi].Cased, 0, lines[vi].Cased.Length);
            if (tokens.Count == 0) continue;
            var v = f.TokenIndex < tokens.Count ? tokens[f.TokenIndex] : tokens[^1];
            return (v, LearnedConfidence, lines[i].Raw);
        }
        return (null, 0, null);
    }

    static (decimal? value, double conf, string? line) ApplyTotal(List<Line> lines, LearnedField f, bool ocr)
    {
        if (!string.IsNullOrEmpty(f.Shape))
        {
            var (hits, counts) = ShapeMatches(lines, f, fromBottom: true);
            foreach (int i in hits)
            {
                int vi = f.ValueOnNextLine ? i + 1 : i;
                if (vi >= lines.Count) continue;
                var (amount, _) = PickAmount(lines[vi].Fwd, ocr);
                if (amount != null) return (amount, counts ? LearnedConfidence : UnmatchedLearnedCap, lines[i].Raw);
            }
            return (null, 0, null);
        }
        if (string.IsNullOrEmpty(f.Label)) return (null, 0, null);
        foreach (int i in Matches(lines, f, fromBottom: true, ocr))
        {
            int vi = f.ValueOnNextLine ? i + 1 : i;
            if (vi >= lines.Count) continue;
            var (amount, _) = PickAmount(lines[vi].Fwd, ocr);
            if (amount != null) return (amount, LearnedConfidence, lines[i].Raw);
        }
        return (null, 0, null);
    }

    sealed class Line
    {
        public readonly string Raw, Fwd, Rev;
        /// <summary>Fwd with the original letter case, for values (same length as Fwd).</summary>
        public readonly string Cased;
        public readonly string Key, Shape;
        public Line(string raw)
        {
            Raw = raw;
            Key = LabelKey(raw);
            Shape = ShapeKey(raw);
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

    static (decimal? value, double conf, string? line) FindTotal(List<Line> lines, bool ocr)
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

            var (amount, hasDecimals) = PickAmount(line.Fwd, ocr);
            if (amount == null && li + 1 < lines.Count)
            {
                (amount, hasDecimals) = PickAmount(lines[li + 1].Fwd, ocr);
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
            var (amount, _) = PickAmount(line.Fwd, ocr);
            if (amount != null && (best == null || amount > best)) { best = amount; bestLine = line.Raw; }
        }
        return best != null ? (best, 0.4, bestLine) : (null, 0, null);
    }

    static readonly Regex DigitGroupBefore = new(@"\d\s+$", RegexOptions.Compiled);

    static (decimal? amount, bool hasDecimals) PickAmount(string s, bool ocr = false)
    {
        decimal? bestDec = null, bestInt = null;
        foreach (Match m in NumberRx.Matches(s))
        {
            // OCR: «1 250.000» may be 1250 or a quantity and 250: on doubt the line gives no amount.
            if (ocr && DigitGroupBefore.IsMatch(s[..m.Index])) return (null, false);
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
        return ocr ? (null, false) : (bestInt, false);
    }

    // Order tickets for the kitchen or the bar printed on the receipt printer (normalized forms).
    static readonly Label[] KitchenMarkers =
    {
        new("مطبخ", 1, true), new("تحضير", 1, true), new("طلب تحضير", 1, false), new("kitchen", 1, false),
        new("kot", 1, false), new("order ticket", 1, false),
    };

    public static bool HasKitchenMarker(IReadOnlyList<string> rawLines) =>
        rawLines.Select(l => new Line(l)).Any(l => KitchenMarkers.Any(k => Find(l.Fwd, k).idx >= 0 || Find(l.Rev, k).idx >= 0));

    /// <summary>
    /// WO-89: a print that is not a receipt (a kitchen or order ticket on the same printer), forwarded and nothing
    /// else: no QR, no report, not a failure. Only without a confidently read total, and only with a kitchen marker
    /// or with no decimal amount at all. A print with prices, or with a confidently read invoice number, is a receipt
    /// (if it is not read, a reading failure, reported).
    /// </summary>
    public static bool IsNonReceipt(IReadOnlyList<string> lines, ExtractResult? e)
    {
        if (e == null) return true;
        if (e.Total != null && e.TotalConfidence >= DeskAgent.MinConfidence) return false;
        if (HasKitchenMarker(lines)) return true;
        if (e.InvoiceNo != null && e.InvoiceConfidence >= DeskAgent.MinConfidence) return false;
        return !lines.Any(l => PickAmount(TextNormalizer.Normalize(l)).hasDecimals);
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
