using System.Globalization;
using System.Text.Json;

namespace DaymanDesk.Core;

public sealed class ReceiptRecord
{
    public DateTimeOffset At { get; init; }
    public ReceiptKind Kind { get; init; }
    public string? InvoiceNo { get; init; }
    public decimal? Total { get; init; }
    public double Confidence { get; init; }
    public string Status { get; set; } = "";
    public bool QrPrinted { get; set; }
    public bool Registered { get; set; }
    /// <summary>WO-89: a kitchen or order ticket, not a receipt (no QR, no report, not counted).</summary>
    public bool NonReceipt { get; set; }
    public string KindLabel => NonReceipt ? "ليست فاتورة" : Kind switch { ReceiptKind.Text => "نص", ReceiptKind.Image => "صورة", _ => "غير معروف" };
}

/// <summary>
/// The first text receipt after pairing, waiting for the owner to confirm (or correct) which line holds the
/// invoice number and which holds the total («تأكيد الحقول»). Indexes are into Lines; -1 = nothing detected.
/// </summary>
public sealed class FieldConfirmation
{
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public int InvoiceLine { get; init; } = -1;
    public int TotalLine { get; init; } = -1;
    public string? InvoiceNo { get; init; }
    public decimal? Total { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>The lines were read by OCR from an image receipt (the OCR rules of the extractor apply).</summary>
    public bool FromImage { get; init; }
}

/// <summary>Problems the status window shows, each with what the cashier or the owner should do.</summary>
public static class Problems
{
    public const string Offline = "offline", Revoked = "revoked", ParseFailed = "parse_failed", Rejected = "rejected",
        RateLimited = "rate_limited", QrPrintFailed = "qr_print_failed", ForwardFailed = "forward_failed",
        TokenUnreadable = "token_unreadable", ListenerBusy = "listener_busy", RestoreFailed = "restore_failed",
        OutOfRange = "out_of_range", UpdateInstallFailed = "update_install_failed", PrinterUnreachable = "printer_unreachable",
        OcrUnavailable = "ocr_unavailable", ImageUnreadable = "image_unreadable";

    /// <summary>Arabic text: the problem, then what to do. Printing is never stopped by any of these.</summary>
    public static string Describe(string? code, string? detail = null) => code switch
    {
        Offline => "لا يوجد اتصال بخادم دايماً. تُطبع الفواتير كالمعتاد بلا QR، وتُرسل إلى الخادم عند عودة الاتصال. تأكد من اتصال الإنترنت.",
        Revoked => "أُلغي ربط هذا الجهاز من لوحة التحكم. أنشئ رمز ربط جديداً من الإعدادات ← دايماً ديسك، واكتبه في تبويب «الربط».",
        ParseFailed => "لم يقرأ البرنامج رقم الفاتورة أو المجموع، فطُبعت بلا QR. إن تكرر ذلك فاضغط «إعادة تأكيد الحقول» في تبويب «الربط».",
        Rejected => "رفض الخادم الفاتورة" + (detail != null ? $" ({detail})" : "") + "، فطُبعت بلا QR. إن تكرر ذلك فتواصل مع دعم دايماً.",
        OutOfRange => "مبلغ الفاتورة خارج الحد المسموح للنقاط، فطُبعت بلا QR. تحقق من المبلغ المقروء في تبويب «الفواتير».",
        RateLimited => "وصل الجهاز إلى الحد المسموح من الطلبات في الساعة. تُطبع الفواتير كالمعتاد بلا QR، وتُرسل لاحقاً.",
        QrPrintFailed => "تعذّرت طباعة قسيمة QR. تأكد أن الطابعة متصلة وفيها ورق.",
        ForwardFailed => "تعذّر إرسال فاتورة إلى الطابعة عبر دايماً ديسك، فأُعيدت الطابعة إلى منفذها الأصلي وطُبعت مباشرة. افحص الطابعة ثم اضغط «تشغيل دايماً ديسك».",
        TokenUnreadable => "تعذّرت قراءة بيانات الربط المحفوظة على هذا الجهاز. اربط الجهاز من جديد برمز من لوحة التحكم.",
        ListenerBusy => $"المنفذ {detail ?? "9100"} مستخدم من برنامج آخر، فتطبع الطابعة مباشرة بلا دايماً ديسك. أغلق البرنامج الآخر ثم اضغط «تشغيل دايماً ديسك».",
        PrinterUnreachable => $"تعذّر الوصول إلى طابعة الشبكة{(detail != null ? " " + detail : "")}، فطُبعت الفاتورة بلا QR (أو تنتظر عودة الطابعة كما كانت قبل دايماً ديسك). تأكد أن الطابعة تعمل ومتصلة بالشبكة.",
        OcrUnavailable => "فواتير هذا المحل صور، وقراءة النص غير مثبّتة في Windows، فتُطبع بلا QR. اترك الجهاز متصلاً بالإنترنت وأعد تشغيله، فيثبّتها البرنامج تلقائياً.",
        ImageUnreadable => "لم يقرأ البرنامج رقم الفاتورة أو المجموع من صورة الفاتورة، فطُبعت بلا QR. إن تكرر ذلك فاضغط «إعادة تأكيد الحقول» في تبويب «الربط».",
        RestoreFailed => "لم نتأكد من إرجاع الطابعة إلى منفذها الأصلي. إن لم تطبع الفواتير فافتح خصائص الطابعة ← Ports واختر منفذها الأصلي.",
        UpdateInstallFailed => (detail ?? "تعذّر تثبيت الإصدار الجديد.") + " يعمل الإصدار المثبّت السابق كما كان.",
        null or "" => "",
        _ => "حدث خطأ غير متوقع. تُطبع الفواتير كالمعتاد. التفاصيل في سجل البرنامج.",
    };
}

/// <summary>Semantic version compare for the update check ("1.2.3"; a "-beta" suffix ranks below its release).</summary>
public static class SemVer
{
    public static bool TryParse(string? s, out (int major, int minor, int patch, bool pre) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s) || s.Length > 32) return false;
        var t = s.Trim().TrimStart('v', 'V');
        bool pre = false;
        int dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) { pre = t[dash] == '-'; t = t[..dash]; }
        var p = t.Split('.');
        if (p.Length < 2 || p.Length > 3) return false;
        var n = new int[3];
        for (int i = 0; i < p.Length; i++)
            if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]) || n[i] > 99999) return false;
        v = (n[0], n[1], n[2], pre);
        return true;
    }

    /// <summary>True only when both parse and latest is strictly newer than current.</summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (!TryParse(latest, out var l) || !TryParse(current, out var c)) return false;
        int cmp = (l.major, l.minor, l.patch).CompareTo((c.major, c.minor, c.patch));
        if (cmp != 0) return cmp > 0;
        return c.pre && !l.pre;
    }

    /// <summary>A download link the tray may open: an absolute https URL without user info.</summary>
    public static bool SafeUrl(string? url) =>
        url != null && url.Length <= 500 && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) && !string.IsNullOrEmpty(u.Host);
}

/// <summary>
/// Everything that happens to a print job after it was forwarded to the printer: analyse, extract,
/// register at /receipt, print the QR slip, report. It never touches the forwarded bytes.
/// </summary>
public sealed class DeskAgent
{
    /// <summary>WO-86: a receipt is registered only when both fields reach this confidence.</summary>
    public const double MinConfidence = 0.75;
    public const int FirstSamples = 10;
    public const int MaxSampleBytes = 48 * 1024; // base64 stays under 64 KB
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan QrDeadline = TimeSpan.FromSeconds(15);

    readonly ConfigStore _cfg;
    readonly DeskApiClient _api;
    readonly OfflineQueue _queue;
    readonly RollingLog _log;
    readonly Func<long, byte[], bool> _printIfCurrent;
    readonly Func<DateTimeOffset> _now;
    readonly IOcr? _ocr;
    readonly string _version;
    readonly Dictionary<string, DateTimeOffset> _lastReport = new();
    readonly object _lock = new();
    FieldConfirmation? _pending;

    public long LastForwardMs { get; set; }
    public event Action<ReceiptRecord>? ReceiptProcessed;
    /// <summary>Raised once, for the first text receipt after pairing: the UI opens «تأكيد الحقول».</summary>
    public event Action<FieldConfirmation>? ConfirmationNeeded;
    /// <summary>A newer version is available (version, https download link). Raised on each heartbeat that says so.</summary>
    public event Action<string, string>? UpdateAvailable;

    /// <param name="printIfCurrent">
    /// (sequence of the receipt, slip) -> prints the slip only if no other job was forwarded after that receipt,
    /// checking and printing under the same lock the forwarder holds. Returns false when it did not print.
    /// </param>
    /// <param name="ocr">Reads image receipts (WO-89); null keeps them unread (no QR, a report).</param>
    public DeskAgent(ConfigStore cfg, DeskApiClient api, OfflineQueue queue, RollingLog log,
        Func<long, byte[], bool> printIfCurrent, string appVersion, Func<DateTimeOffset>? now = null, IOcr? ocr = null)
    {
        _cfg = cfg; _api = api; _queue = queue; _log = log; _printIfCurrent = printIfCurrent;
        _version = appVersion; _now = now ?? (() => DateTimeOffset.Now); _ocr = ocr;
    }

    public static string KindWire(ReceiptKind k) => k switch { ReceiptKind.Text => "text", ReceiptKind.Image => "image", _ => "unknown" };

    /// <summary>The receipt waiting for «تأكيد الحقول», if any.</summary>
    public FieldConfirmation? PendingConfirmation { get { lock (_lock) return _pending; } }

    /// <summary>What happened to an image receipt before the extractor (WO-89).</summary>
    enum ImageRead { NotImage, NoOcr, Undecodable, NoEngine, Read }

    /// <param name="printerUnreachable">The TCP/IP printer did not answer and the job went to the spooler queue: no QR.</param>
    public async Task<ReceiptRecord> ProcessJobAsync(byte[] job, long forwardMs, long seqAfterForward, bool printerUnreachable = false)
    {
        var at = _now();
        LastForwardMs = forwardMs;
        ReceiptRecord rec;
        AnalysisResult analysis;
        ExtractResult? ext = null;
        IReadOnlyList<string> lines;
        var image = ImageRead.NotImage;
        bool nonReceipt = false, ocrAgree = true;
        IReadOnlyList<string> second = Array.Empty<string>();
        try
        {
            analysis = ReceiptAnalyzer.Analyze(job);
            lines = analysis.Lines;
            if (analysis.Kind == ReceiptKind.Image) image = await ReadImageAsync(job, (a, b) => { lines = a; second = b; }).ConfigureAwait(false);
            var c0 = _cfg.Current;
            if (analysis.Kind == ReceiptKind.Text || image == ImageRead.Read)
            {
                ext = ReceiptExtractor.Extract(lines, c0.FieldsConfirmed ? c0.LearnedInvoice : null, c0.FieldsConfirmed ? c0.LearnedTotal : null,
                    ocr: image == ImageRead.Read);
                nonReceipt = ReceiptExtractor.IsNonReceipt(lines, ext);
                if (image == ImageRead.Read)
                {
                    // OCR gives no per-digit confidence: the second read (2x upscale) must give the same two values.
                    var ext2 = ReceiptExtractor.Extract(second, c0.FieldsConfirmed ? c0.LearnedInvoice : null, c0.FieldsConfirmed ? c0.LearnedTotal : null, ocr: true);
                    ocrAgree = ext2.InvoiceNo == ext.InvoiceNo && ext2.Total == ext.Total;
                }
            }
        }
        catch (Exception ex)
        {
            // Bad bytes must never take the program down: the receipt already printed.
            _log.Error("analyse failed: " + ex.GetType().Name + ": " + ex.Message);
            rec = new ReceiptRecord { At = at, Kind = ReceiptKind.Unknown, Status = "تعذّر تحليل الفاتورة: بلا QR" };
            Count(c => { c.TodayReceipts++; if (c.Paired) c.TodayNotRegistered++; });
            await ReportAsync("error", "crash", "analyse: " + ex.GetType().Name + ": " + ex.Message, job).ConfigureAwait(false);
            ReceiptProcessed?.Invoke(rec);
            return rec;
        }
        rec = new ReceiptRecord
        {
            At = at, Kind = analysis.Kind, InvoiceNo = ext?.InvoiceNo, Total = ext?.Total, Confidence = ext?.Confidence ?? 0,
            NonReceipt = nonReceipt,
        };
        _log.Info($"job {job.Length} B, forward {forwardMs} ms, {analysis.Kind} ({analysis.Reason}){(image != ImageRead.NotImage ? ", image " + image : "")}{(nonReceipt ? ", not a receipt" : "")}, invoice {(ext?.InvoiceNo != null ? "found" : "none")} {ext?.InvoiceConfidence:0.00}, total {(ext?.Total != null ? "found" : "none")} {ext?.TotalConfidence:0.00}");

        try
        {
            _cfg.Update(c =>
            {
                c.LastPrintKind = KindWire(analysis.Kind);
                if (analysis.Kind == ReceiptKind.Text && analysis.CodePage != null)
                {
                    c.CodePage = analysis.CodePage;
                    if (analysis.CodeTable != null) c.CodeTable = analysis.CodeTable;
                }
            });
            await HandleAsync(job, forwardMs, seqAfterForward, analysis, lines, image, ext, rec, printerUnreachable, ocrAgree).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rec.Status = "خطأ داخلي: بلا QR";
            _log.Error("process failed: " + ex);
            await ReportAsync("error", "crash", "process: " + ex.GetType().Name + ": " + ex.Message).ConfigureAwait(false);
        }
        bool paired = _cfg.Current.Paired;
        // A kitchen or order ticket is not a receipt: not counted at all.
        if (!rec.NonReceipt) Count(c =>
        {
            c.TodayReceipts++;
            if (rec.Registered) c.TodayRegistered++;
            else if (paired && rec.Status != StatusCashier && rec.Status != StatusVoided) c.TodayNotRegistered++;
            if (rec.QrPrinted) c.TodayQr++;
        });
        ReceiptProcessed?.Invoke(rec);
        return rec;
    }

    const string StatusCashier = "سجّلها الكاشير يدوياً: بلا QR";
    const string StatusVoided = "ألغى صاحب المحل كودها: بلا QR";

    /// <summary>
    /// Decodes the raster to a bitmap and reads it twice with the OCR: as printed and upscaled 2x (two independent
    /// reads; a receipt registers only when both give the same invoice number and total). Never throws.
    /// </summary>
    async Task<ImageRead> ReadImageAsync(byte[] job, Action<IReadOnlyList<string>, IReadOnlyList<string>> setLines)
    {
        if (_ocr == null) return ImageRead.NoOcr;
        var bmp = RasterDecoder.Decode(job);
        if (bmp == null) return ImageRead.Undecodable;
        IReadOnlyList<string>? first, second;
        try
        {
            first = await _ocr.ReadAsync(bmp).ConfigureAwait(false);
            second = first == null ? null : await _ocr.ReadAsync(bmp.Scale2x()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("ocr failed: " + ex.GetType().Name + ": " + ex.Message);
            first = second = null;
        }
        if (first == null) return ImageRead.NoEngine;
        setLines(OcrText.Clean(first), OcrText.Clean(second ?? Array.Empty<string>()));
        return ImageRead.Read;
    }

    async Task HandleAsync(byte[] job, long forwardMs, long seq, AnalysisResult analysis, IReadOnlyList<string> lines, ImageRead image,
        ExtractResult? ext, ReceiptRecord rec, bool printerUnreachable, bool ocrAgree)
    {
        var cfg = _cfg.Current;
        if (forwardMs > 300)
        {
            _log.Warn($"slow forward: {forwardMs} ms");
            await ReportAsync("warn", "slow_forward", $"forward {forwardMs} ms for {job.Length} bytes").ConfigureAwait(false);
        }
        if (rec.NonReceipt)
        {
            // WO-89: forwarded and nothing else (no QR, no report, no sample, not a failure).
            rec.Status = "ليست فاتورة (طلب مطبخ أو تحضير): بلا QR";
            return;
        }
        if (printerUnreachable)
        {
            rec.Status = "الطابعة لم تُجب: الفاتورة في طابور Windows، بلا QR";
            SetProblem(Problems.PrinterUnreachable);
            await ReportAsync("warn", "other", "tcp/ip printer unreachable: job sent to the spooler queue, no qr").ConfigureAwait(false);
            return;
        }
        if (cfg.Paired && cfg.SamplesSent < FirstSamples)
        {
            _cfg.Update(c => c.SamplesSent++);
            await ReportAsync("info", "sample", Summary(analysis, ext, image), job, force: true).ConfigureAwait(false);
        }
        if (!cfg.Paired)
        {
            rec.Status = "الجهاز غير مربوط بالمحل";
            return;
        }
        if (analysis.Kind == ReceiptKind.Image && image != ImageRead.Read)
        {
            switch (image)
            {
                case ImageRead.Undecodable:
                    rec.Status = "صيغة صورة غير معروفة: بلا QR";
                    await ReportUnknownFormatAsync("escpos-raster-undecodable", analysis.Reason, job).ConfigureAwait(false);
                    break;
                case ImageRead.NoEngine:
                    rec.Status = "فاتورة صورة: قراءة النص غير مثبّتة، بلا QR";
                    SetProblem(Problems.OcrUnavailable);
                    await ReportAsync("warn", "other", "image receipt: no OCR engine (" + analysis.Reason + ")", job).ConfigureAwait(false);
                    break;
                default:
                    rec.Status = "فاتورة صورة: بلا QR";
                    await ReportAsync("info", "other", "image receipt: " + analysis.Reason, job).ConfigureAwait(false);
                    break;
            }
            return;
        }
        if (analysis.Kind == ReceiptKind.Unknown)
        {
            rec.Status = "صيغة غير معروفة: بلا QR";
            await ReportUnknownFormatAsync(analysis.Format, analysis.Reason, job).ConfigureAwait(false);
            return;
        }
        bool fromImage = image == ImageRead.Read;
        if (fromImage) ClearProblem(Problems.OcrUnavailable);
        if (!cfg.FieldsConfirmed)
        {
            // The first receipt after pairing: the owner confirms the two fields before anything registers.
            FieldConfirmation? raise = null;
            lock (_lock)
            {
                if (_pending == null) _pending = raise = BuildConfirmation(lines, ext, fromImage);
            }
            rec.Status = "بانتظار «تأكيد الحقول»: بلا QR";
            if (raise != null)
            {
                _log.Info("field confirmation requested");
                ConfirmationNeeded?.Invoke(raise);
            }
            return;
        }
        if (ext == null || ext.InvoiceNo == null || ext.Total == null
            || ext.InvoiceConfidence < MinConfidence || ext.TotalConfidence < MinConfidence || !ocrAgree)
        {
            if (!ocrAgree) _log.Warn("ocr: the two reads disagree on the invoice number or the total: no qr");
            rec.Status = fromImage ? "لم تُقرأ صورة الفاتورة: بلا QR" : "لم تُقرأ الفاتورة: بلا QR";
            SetProblem(fromImage ? Problems.ImageUnreadable : Problems.ParseFailed);
            await ReportAsync("warn", "parse_failed", Summary(analysis, ext, image), job).ConfigureAwait(false);
            return;
        }

        var req = new ReceiptRequest(
            Guid.NewGuid().ToString(), ext.InvoiceNo, ext.Total.Value.ToString("0.000", CultureInfo.InvariantCulture),
            _now().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), KindWire(analysis.Kind));
        var started = _now();
        var res = await _api.ReceiptAsync(cfg.DeviceToken!, req).ConfigureAwait(false);
        _log.Info($"receipt -> {res}");

        if (res.Ok && res.Value != null)
        {
            rec.Registered = true;
            ClearProblem(Problems.Offline, Problems.RateLimited, Problems.ParseFailed, Problems.Rejected, Problems.OutOfRange,
                Problems.ImageUnreadable, Problems.PrinterUnreachable);
            if (!_cfg.Current.QrEnabled) { rec.Status = "مسجّلة (أوقف صاحب المحل QR)"; return; }
            var c = _cfg.Current;
            byte[] slip;
            try { slip = QrReceipt.Build(res.Value.ClaimUrl, c.CodePage, c.CodeTable, req.InvoiceNo, req.Amount); }
            catch (ArgumentException)
            {
                rec.Status = "مسجّلة: رابط QR غير صالح";
                await ReportAsync("error", "other", "claimUrl not printable").ConfigureAwait(false);
                return;
            }
            bool late = _now() - started > QrDeadline;
            bool printed = !late && _printIfCurrent(seq, slip);
            if (!printed)
            {
                // Another receipt printed meanwhile (or the answer came late): a QR now would sit under the wrong receipt.
                rec.Status = "مسجّلة: QR لم يُطبع (طُبعت فاتورة بعدها أو تأخر الرد)";
                _log.Warn("qr skipped: another job was forwarded, the answer was late, or the slip did not print");
                await ReportAsync("warn", "other", "qr skipped: late answer, next job already printed, or print failed").ConfigureAwait(false);
                return;
            }
            rec.QrPrinted = true;
            ClearProblem(Problems.QrPrintFailed);
            rec.Status = res.Value.Already ? "QR مطبوع (فاتورة مكررة)" : "QR مطبوع";
            return;
        }
        if (res.Unauthorized)
        {
            Unpair(res.Error);
            rec.Status = "أُلغي ربط الجهاز: بلا QR";
            return;
        }
        if (res.Transient)
        {
            _queue.Enqueue("receipt", req);
            SetProblem(res.Status == 429 ? Problems.RateLimited : Problems.Offline);
            rec.Status = res.Status == 429 ? "حد الطلبات: بلا QR، تُرسل لاحقاً" : "لا اتصال: بلا QR، تُرسل لاحقاً";
            await ReportAsync("warn", "other", $"receipt queued: {res}").ConfigureAwait(false);
            return;
        }
        if (res.Status == 409)
        {
            // WO-88: INVOICE_VOIDED, the owner voided this invoice's code; otherwise the cashier entered it.
            rec.Status = res.Error == "INVOICE_VOIDED" ? StatusVoided : StatusCashier;
            return;
        }
        if (res.Status == 422)
        {
            rec.Status = "المبلغ خارج الحد: بلا QR";
            SetProblem(Problems.OutOfRange);
        }
        else
        {
            rec.Status = $"رفضها الخادم ({res.Error}): بلا QR";
            SetProblem(Problems.Rejected, res.Error);
        }
        await ReportAsync("error", "parse_failed", $"receipt rejected {res}: {Summary(analysis, ext, image)}").ConfigureAwait(false);
    }

    /// <summary>WO-89: an unknown print format is reported once per format per day, not per receipt.</summary>
    async Task ReportUnknownFormatAsync(string format, string reason, byte[] job)
    {
        string day = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool first = false;
        try
        {
            _cfg.Update(c =>
            {
                if (c.UnknownFormatsDay != day) { c.UnknownFormatsDay = day; c.UnknownFormatsReported.Clear(); }
                if (!c.UnknownFormatsReported.Contains(format)) { c.UnknownFormatsReported.Add(format); first = true; }
            });
        }
        catch (Exception ex) { _log.Warn("unknown format not saved: " + ex.GetType().Name); }
        if (!first) return;
        string head = Convert.ToHexString(job, 0, Math.Min(job.Length, 8));
        await ReportAsync("warn", "unknown_format", $"format {format}: {reason}, first bytes {head}", job, force: true).ConfigureAwait(false);
    }

    static FieldConfirmation BuildConfirmation(IReadOnlyList<string> lines, ExtractResult? ext, bool fromImage)
    {
        int inv = -1, tot = -1;
        if (ext?.InvoiceNo != null)
        {
            inv = IndexOfLine(lines, ext.InvoiceLine);
            // The heuristic may name the label line while the number sits on the next one.
            if (inv >= 0 && !ReceiptExtractor.InvoiceTokens(lines[inv]).Contains(ext.InvoiceNo) && inv + 1 < lines.Count
                && ReceiptExtractor.InvoiceTokens(lines[inv + 1]).Contains(ext.InvoiceNo)) inv++;
            if (inv >= 0 && !ReceiptExtractor.InvoiceTokens(lines[inv]).Contains(ext.InvoiceNo)) inv = -1;
        }
        if (ext?.Total != null)
        {
            tot = IndexOfLine(lines, ext.TotalLine, last: true);
            if (tot >= 0 && ReceiptExtractor.AmountOf(lines[tot]) != ext.Total && tot + 1 < lines.Count
                && ReceiptExtractor.AmountOf(lines[tot + 1]) == ext.Total) tot++;
            if (tot >= 0 && ReceiptExtractor.AmountOf(lines[tot]) != ext.Total) tot = -1;
        }
        return new FieldConfirmation
        {
            Lines = lines.ToList(),
            InvoiceLine = inv,
            TotalLine = tot,
            InvoiceNo = inv >= 0 ? ext!.InvoiceNo : null,
            Total = tot >= 0 ? ext!.Total : null,
            At = DateTimeOffset.Now,
            FromImage = fromImage,
        };
    }

    static int IndexOfLine(IReadOnlyList<string> lines, string? line, bool last = false)
    {
        if (line == null) return -1;
        if (last) { for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] == line) return i; return -1; }
        for (int i = 0; i < lines.Count; i++) if (lines[i] == line) return i;
        return -1;
    }

    /// <summary>
    /// The owner's choice in «تأكيد الحقول». Learns both fields, stores them, and from now on receipts register.
    /// Returns null on success, else the Arabic reason (nothing is stored then).
    /// </summary>
    public string? ConfirmFields(FieldConfirmation p, int invoiceLine, int totalLine)
    {
        if (invoiceLine == totalLine && invoiceLine >= 0) return "اختر سطرين مختلفين لرقم الفاتورة والمجموع.";
        var inv = ReceiptExtractor.LearnInvoice(p.Lines, invoiceLine, invoiceLine == p.InvoiceLine ? p.InvoiceNo : null);
        if (inv == null) return "السطر المختار لرقم الفاتورة لا يحوي رقماً. اختر السطر الذي يظهر فيه رقم الفاتورة.";
        var tot = ReceiptExtractor.LearnTotal(p.Lines, totalLine);
        if (tot == null) return "السطر المختار للمجموع لا يحوي مبلغاً. اختر السطر الذي يظهر فيه المجموع النهائي.";
        // The learned labels must find the same values on this very receipt, or later receipts would go wrong.
        var check = ReceiptExtractor.Extract(p.Lines, inv.Value.field, tot.Value.field, ocr: p.FromImage);
        if (check.InvoiceNo != inv.Value.value || check.Total != tot.Value.value)
            return "يتكرر عنوان السطر المختار في الفاتورة. اختر السطر الأقرب إلى العنوان الصحيح، أو تواصل مع دعم دايماً.";
        _cfg.Update(c =>
        {
            c.LearnedInvoice = inv.Value.field;
            c.LearnedTotal = tot.Value.field;
            c.FieldsConfirmed = true;
        });
        lock (_lock) _pending = null;
        _log.Info($"fields confirmed (invoice line {invoiceLine}, total line {totalLine}, of {p.Lines.Count})");
        _ = ReportAsync("info", "other", $"fields confirmed by owner: invoice {(invoiceLine == p.InvoiceLine ? "as detected" : "corrected")}, total {(totalLine == p.TotalLine ? "as detected" : "corrected")}");
        return null;
    }

    /// <summary>«إعادة تأكيد الحقول»: forget the learned fields; the next text receipt asks again.</summary>
    public void RequestReconfirm()
    {
        _cfg.Update(c => { c.FieldsConfirmed = false; c.LearnedInvoice = null; c.LearnedTotal = null; });
        lock (_lock) _pending = null;
        _log.Info("fields confirmation reset by owner");
    }

    static string Summary(AnalysisResult a, ExtractResult? e, ImageRead image) =>
        $"kind {KindWire(a.Kind)}, enc {a.EncodingName}, {a.Reason}, lines {a.Lines.Count}{(image != ImageRead.NotImage ? ", image " + image : "")}, " +
        $"invoice {(e?.InvoiceNo != null ? "found" : "none")} {e?.InvoiceConfidence:0.00}, " +
        $"total {(e?.Total != null ? "found" : "none")} {e?.TotalConfidence:0.00}";

    void Unpair(string? why)
    {
        _log.Error("device token refused (" + why + "): unpaired");
        _cfg.Update(c => c.DeviceToken = null);
        SetProblem(Problems.Revoked);
    }

    /// <summary>Today's counters roll over at local midnight. A disk error here is logged, never thrown.</summary>
    void Count(Action<DeskConfig> change)
    {
        try
        {
            _cfg.Update(c =>
            {
                string day = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (c.StatsDay != day)
                {
                    c.StatsDay = day;
                    c.TodayReceipts = c.TodayRegistered = c.TodayQr = c.TodayNotRegistered = 0;
                }
                change(c);
            });
        }
        catch (Exception ex) { _log.Warn("counters not saved: " + ex.GetType().Name); }
    }

    public void SetProblem(string code, string? detail = null)
    {
        var c = _cfg.Current;
        if (c.LastProblem == code && c.LastProblemDetail == detail && c.LastProblemAt != null && _now() - c.LastProblemAt < TimeSpan.FromMinutes(1)) return;
        try { _cfg.Update(x => { x.LastProblem = code; x.LastProblemDetail = detail; x.LastProblemAt = _now(); }); }
        catch (Exception ex) { _log.Warn("problem not saved: " + ex.GetType().Name); }
    }

    public void ClearProblem(params string[] codes)
    {
        var p = _cfg.Current.LastProblem;
        if (p == null || !codes.Contains(p)) return;
        try { _cfg.Update(x => { x.LastProblem = null; x.LastProblemDetail = null; x.LastProblemAt = null; }); }
        catch (Exception ex) { _log.Warn("problem not cleared: " + ex.GetType().Name); }
    }

    /// <summary>Sends a report. Rate-limited per kind (samples only by FirstSamples); queued when offline.</summary>
    public async Task ReportAsync(string level, string kind, string message, byte[]? sample = null, bool force = false)
    {
        var cfg = _cfg.Current;
        if (!cfg.Paired) return;
        lock (_lock)
        {
            var now = _now();
            if (!force && _lastReport.TryGetValue(kind, out var last) && now - last < ReportInterval) return;
            _lastReport[kind] = now;
        }
        if (message.Length > 2000) message = message[..2000];
        string? b64 = null;
        if (sample != null) b64 = Convert.ToBase64String(sample, 0, Math.Min(sample.Length, MaxSampleBytes));
        var req = new ReportRequest(level, kind, message, _version, b64);
        var res = await _api.ReportAsync(cfg.DeviceToken!, req).ConfigureAwait(false);
        if (res.Ok) return;
        if (res.Unauthorized) { Unpair(res.Error); return; }
        if (res.Transient) _queue.Enqueue("report", req with { Sample = null });
        else _log.Warn($"report {kind} refused: {res}");
    }

    public async Task<ApiResult<HeartbeatResponse>?> HeartbeatAsync()
    {
        var cfg = _cfg.Current;
        if (!cfg.Paired) return null;
        var res = await _api.HeartbeatAsync(cfg.DeviceToken!, new HeartbeatRequest(_version, cfg.LastPrintKind, _queue.Count, LastForwardMs)).ConfigureAwait(false);
        if (res.Ok && res.Value != null)
        {
            var v = res.Value;
            bool newer = SemVer.IsNewer(v.LatestVersion, _version) && SemVer.SafeUrl(v.DownloadUrl);
            _cfg.Update(c =>
            {
                c.QrEnabled = v.QrEnabled; c.CodeValidityDays = v.CodeValidityDays;
                c.UpdateVersion = newer ? v.LatestVersion!.Trim() : null;
                c.UpdateUrl = newer ? v.DownloadUrl : null;
            });
            ClearProblem(Problems.Offline, Problems.RateLimited);
            if (newer) UpdateAvailable?.Invoke(v.LatestVersion!.Trim(), v.DownloadUrl!);
            await FlushQueueAsync().ConfigureAwait(false);
        }
        else if (res.Unauthorized) Unpair(res.Error);
        else
        {
            _log.Warn($"heartbeat: {res}");
            if (res.Status == 0) SetProblem(Problems.Offline);
        }
        return res;
    }

    public async Task<int> FlushQueueAsync()
    {
        if (!_cfg.Current.Paired || _queue.Count == 0) return 0;
        int n = await _queue.FlushAsync(async item =>
        {
            var token = _cfg.Current.DeviceToken;
            if (token == null) return SendOutcome.Transient;
            int status; bool ok, transient, unauthorized;
            try
            {
                if (item.Type == "receipt")
                {
                    var req = JsonSerializer.Deserialize<ReceiptRequest>(item.Payload, DeskApiClient.Json);
                    if (req == null) return SendOutcome.Rejected;
                    var r = await _api.ReceiptAsync(token, req).ConfigureAwait(false);
                    (status, ok, transient, unauthorized) = (r.Status, r.Ok, r.Transient, r.Unauthorized);
                }
                else
                {
                    var req = JsonSerializer.Deserialize<ReportRequest>(item.Payload, DeskApiClient.Json);
                    if (req == null) return SendOutcome.Rejected;
                    var r = await _api.ReportAsync(token, req).ConfigureAwait(false);
                    (status, ok, transient, unauthorized) = (r.Status, r.Ok, r.Transient, r.Unauthorized);
                }
            }
            catch (JsonException) { return SendOutcome.Rejected; } // a damaged item must not block the queue
            if (ok) return SendOutcome.Sent;
            if (unauthorized) { Unpair("queue"); return SendOutcome.Transient; }
            if (transient) return SendOutcome.Transient;
            _log.Warn($"queued {item.Type} refused: {status}");
            return SendOutcome.Rejected;
        }).ConfigureAwait(false);
        if (n > 0) _log.Info($"queue: sent {n}, left {_queue.Count}");
        return n;
    }

    public async Task<ApiResult<PairResponse>> PairAsync(string code, string deviceName)
    {
        var res = await _api.PairAsync(new PairRequest(code.Trim().ToUpperInvariant(), deviceName, _version)).ConfigureAwait(false);
        if (res.Ok && res.Value != null && !string.IsNullOrEmpty(res.Value.Token))
        {
            var v = res.Value;
            _cfg.Update(c =>
            {
                c.DeviceToken = v.Token; c.ShopName = v.ShopName; c.StampMode = v.StampMode;
                c.CodeValidityDays = v.CodeValidityDays; c.QrEnabled = v.QrEnabled; c.DeviceName = deviceName;
                c.SamplesSent = 0;
                // A new pairing asks again on the first receipt (it may be another shop or another POS).
                c.FieldsConfirmed = false; c.LearnedInvoice = null; c.LearnedTotal = null;
                if (c.LastProblem is Problems.Revoked or Problems.TokenUnreadable) { c.LastProblem = null; c.LastProblemDetail = null; c.LastProblemAt = null; }
            });
            lock (_lock) _pending = null;
            _log.Info("paired with shop");
        }
        else _log.Warn($"pair failed: {res}");
        return res;
    }

    /// <summary>Arabic text for a pairing error.</summary>
    public static string PairError(ApiResult<PairResponse> r) => r.Error switch
    {
        "CODE_NOT_FOUND" => "رمز الربط غير صحيح. تأكد من الرمز في لوحة التحكم ثم أعد المحاولة.",
        "CODE_EXPIRED" => "انتهت صلاحية رمز الربط (10 دقائق). أنشئ رمزاً جديداً من الإعدادات ← دايماً ديسك.",
        "RATE_LIMITED" => "محاولات كثيرة. انتظر قليلاً ثم أعد المحاولة.",
        "NETWORK" or "TIMEOUT" => "لا يوجد اتصال بالخادم. تأكد من اتصال الإنترنت ثم أعد المحاولة.",
        _ => r.Message ?? $"تعذّر الربط (الرمز {r.Status}). أعد المحاولة، وإن تكرر ذلك فتواصل مع دعم دايماً.",
    };
}
