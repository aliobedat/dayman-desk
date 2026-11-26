using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// <summary>The OCR language that read it (FromImage only).</summary>
    public string? OcrLanguage { get; init; }
    /// <summary>WO-97: names this receipt on the server, so the owner's choice applies only to the lines he saw.</summary>
    public string SampleId { get; init; } = Guid.NewGuid().ToString();
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
        Offline => "لا يوجد اتصال بخادم دايماً. تُطبع الفواتير كالمعتاد، ومعها QR ما دامت على الجهاز رموز جاهزة، وتُرسل إلى الخادم عند عودة الاتصال. تأكد من اتصال الإنترنت.",
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
        OcrUnavailable => "فواتير هذا المحل صور، وقراءة النص من الصورة لا تعمل على هذا الجهاز، فتُطبع بلا QR. أعد تشغيل الجهاز، وإن تكرر ذلك فتواصل مع دعم دايماً.",
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
    // WO-97: the receipt sent to the owner's dashboard («تأكيد الحقول» there), whether the server has it, and
    // why the owner's last choice was refused (shown to him with the re-sent receipt).
    FieldConfirmation? _remote;
    bool _remoteSent;
    string? _remoteProblem;
    // WO-97: the last owner choice handled (applied, refused or stale); acknowledged on every heartbeat.
    string? _appliedSampleId;

    string? AppliedSampleId { get { lock (_lock) return _appliedSampleId; } }

    /// <summary>WO-97: the program's status while the owner has not confirmed the fields on his dashboard.</summary>
    public const string AwaitingOwner = "بانتظار تأكيد صاحب المحل من لوحة التحكم";
    public const int MaxSampleLines = 60, MaxSampleChars = 80;

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
    /// <param name="printedNothing">WO-91: called when the job is a printer command, not paper (it releases its sequence).</param>
    public async Task<ReceiptRecord> ProcessJobAsync(byte[] job, long forwardMs, long seqAfterForward, bool printerUnreachable = false, Action? printedNothing = null)
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
            if (image == ImageRead.Read) CheckOcrLanguage();
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
            // WO-91: a drawer kick, an init or a status query sent as a print job: a few control bytes, no picture.
            else if (analysis.Kind == ReceiptKind.Unknown && analysis.Format is "escpos-other" or "empty" && analysis.TextChars < 20)
            {
                nonReceipt = true;
                // It printed no receipt: the previous receipt's pending QR may still print (PassThroughServer.Release).
                printedNothing?.Invoke();
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
        var after = new List<Func<Task>>();
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
            await HandleAsync(job, forwardMs, seqAfterForward, analysis, lines, image, ext, rec, printerUnreachable, ocrAgree, after).ConfigureAwait(false);
            // WO-90: after a registered receipt (never before its QR) the pool is topped up; offline the heartbeat does it.
            if (rec.Registered) await RefillPoolAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rec.Status = "خطأ داخلي: بلا QR";
            _log.Error("process failed: " + ex);
            await ReportAsync("error", "crash", "process: " + ex.GetType().Name + ": " + ex.Message).ConfigureAwait(false);
        }
        // WO-91: the sample and slow-forward reports go after the slip, never before it.
        foreach (var report in after)
        {
            try { await report().ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("deferred report: " + ex.Message); }
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
        ExtractResult? ext, ReceiptRecord rec, bool printerUnreachable, bool ocrAgree, List<Func<Task>> after)
    {
        var cfg = _cfg.Current;
        if (forwardMs > 300)
        {
            _log.Warn($"slow forward: {forwardMs} ms");
            after.Add(() => ReportAsync("warn", "slow_forward", $"forward {forwardMs} ms for {job.Length} bytes"));
        }
        if (rec.NonReceipt)
        {
            // WO-89: forwarded and nothing else (no QR, no report, no sample, not a failure).
            rec.Status = analysis.Kind == ReceiptKind.Unknown ? "ليست فاتورة (أمر قصير للطابعة): بلا QR" : "ليست فاتورة (طلب مطبخ أو تحضير): بلا QR";
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
            string summary = Summary(analysis, ext, image);
            after.Add(() => ReportAsync("info", "sample", summary, job, force: true));
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
            // WO-91: the newest receipt replaces the pending one (a better print after a garbled one is the one
            // «تأكيد الحقول» shows); only a receipt with nothing pending (the first, or after «لاحقاً») opens the window.
            // WO-97: the owner confirms on his dashboard; the first such receipt is the one sent there (his choice
            // applies to the lines he saw). The window stays for an installer, on a button.
            FieldConfirmation? raise = null;
            bool send = false;
            var fresh = BuildConfirmation(lines, ext, fromImage, fromImage ? _ocr?.LanguageTag : null);
            lock (_lock)
            {
                if (_pending == null) raise = fresh;
                _pending = fresh;
                if (_remote == null) { _remote = fresh; _remoteSent = false; _remoteProblem = null; send = true; }
            }
            rec.Status = AwaitingOwner + ": بلا QR";
            if (raise != null)
            {
                _log.Info("field confirmation requested");
                ConfirmationNeeded?.Invoke(raise);
            }
            if (send) await SendFieldsAsync().ConfigureAwait(false);
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

        // WO-90: with a pool code the slip prints now and the receipt is bound afterwards (also offline). A reprint
        // of the same invoice the same day prints the same code. No pool code: wait for /receipt as before.
        string invoiceKey = _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + ext.InvoiceNo;
        // The server's amount bounds (lib/desk.ts: DELIVERY_MIN_FILS to PURCHASE_MAX_FILS): outside them no pool code
        // is printed; /receipt answers 422 and the receipt gets no QR, as before.
        bool inRange = ext.Total.Value >= MinAmount && ext.Total.Value <= MaxAmount;
        // WO-95: a device whose key the server holds signs the QR itself (contract v1.3); only without one (no key yet,
        // an older server) does it take a pool code as in 1.2.
        string? signedToken = cfg.QrEnabled && inRange ? TakeSigned(invoiceKey, ext.InvoiceNo, ext.Total.Value) : null;
        string? poolCode = signedToken == null && cfg.QrEnabled && inRange ? TakeCode(invoiceKey) : null;
        var req = new ReceiptRequest(
            Guid.NewGuid().ToString(), ext.InvoiceNo, ext.Total.Value.ToString("0.000", CultureInfo.InvariantCulture),
            _now().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), KindWire(analysis.Kind), poolCode,
            signedToken == null ? null : DeskSigner.SeqOf(signedToken));
        if (signedToken != null || poolCode != null)
        {
            await PrePrintedAsync(req, seq, rec, invoiceKey, analysis, ext, image, signedToken).ConfigureAwait(false);
            return;
        }
        var started = _now();
        var res = await _api.ReceiptAsync(cfg.DeviceToken!, req).ConfigureAwait(false);
        _log.Info($"receipt -> {res}");

        if (res.Ok && res.Value != null)
        {
            rec.Registered = true;
            ClearProblem(Problems.Offline, Problems.RateLimited, Problems.ParseFailed, Problems.Rejected, Problems.OutOfRange,
                Problems.ImageUnreadable, Problems.PrinterUnreachable);
            if (ValidCode(res.Value.Code)) Remember(invoiceKey, res.Value.Code); // a reprint prints this code at once
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
        if (res.Status == 409 || res.Status == 422) Remember(invoiceKey, ""); // a reprint prints no QR either
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

    // ---------- WO-90: the pool of pre-issued codes ----------

    /// <summary>Refill when fewer codes than this are left; ask for this many.</summary>
    public const int PoolRefillAt = 20;
    public const int PoolRefillCount = 50;
    /// <summary>JOD; the server's DELIVERY_MIN_FILS (1000) and PURCHASE_MAX_FILS (10,000,000) bounds.</summary>
    public const decimal MinAmount = 1.000m, MaxAmount = 10000.000m;
    /// <summary>The server expires an unbound pool code 30 days after the refill; the program stops printing it at 25.</summary>
    public static readonly TimeSpan PoolLifetime = TimeSpan.FromDays(25);
    static readonly TimeSpan PoolRetry = TimeSpan.FromMinutes(1);
    const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    int _refilling;
    DateTimeOffset _poolFailedAt = DateTimeOffset.MinValue;

    /// <summary>A stamp code as the server sends it: 10 characters of Crockford base 32.</summary>
    public static bool ValidCode(string? code) => code != null && code.Length == 10 && code.All(ch => CodeAlphabet.Contains(ch));

    /// <summary>The claim URL prefix: absolute https, printable ASCII, ending in /s/ (the slip's QR is it plus the code).</summary>
    public static bool ValidClaimBase(string? url) =>
        url != null && url.Length <= 600 && url.EndsWith("/s/", StringComparison.Ordinal) && url.All(ch => ch > 0x20 && ch < 0x7F)
        && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>
    /// The code to print for this invoice today: the one already printed for it, else the next pool code (recorded
    /// for a reprint). Null: refused before, no pool, or no claim base. Also forgets invoices older than 8 days.
    /// </summary>
    string? TakeCode(string key)
    {
        string? code = null;
        string cutoff = _now().AddDays(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _cfg.Update(c =>
        {
            foreach (var old in c.PrintedCodes.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) c.PrintedCodes.Remove(old);
            if (c.PrintedCodes.TryGetValue(key, out var had)) { code = had.Length == 0 ? null : had; return; }
            var now = _now();
            c.CodePool.RemoveAll(p => p.Until <= now); // never bound in time: the server has expired it
            if (!ValidClaimBase(c.ClaimBase) || c.CodePool.Count == 0) return;
            code = c.CodePool[0].Code;
            c.CodePool.RemoveAt(0);
            c.PrintedCodes[key] = code;
        });
        return code;
    }

    /// <summary>
    /// WO-95: the signed QR token for this invoice today: the one already printed for it, else a new one with the next
    /// seq (recorded for a reprint). Null: no registered key, no claim base, refused before, or a pool code was printed.
    /// </summary>
    string? TakeSigned(string key, string invoiceNo, decimal total)
    {
        string? token = null;
        var signer = Signer();
        string inv = DeskSigner.NormalizeInvoice(invoiceNo);
        long fils = (long)Math.Round(total * 1000m, MidpointRounding.AwayFromZero);
        string cutoff = _now().AddDays(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _cfg.Update(c =>
        {
            foreach (var old in c.PrintedCodes.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) c.PrintedCodes.Remove(old);
            if (c.PrintedCodes.TryGetValue(key, out var had))
            {
                if (had.StartsWith("r:", StringComparison.Ordinal)) token = had.Substring(2);
                return;
            }
            if (signer == null || !c.KeyRegistered || c.DeviceNumber is not int num || num < 1 || num > 32767
                || !ValidClaimBase(c.ClaimBase) || inv.Length == 0 || inv.Length > 40 || c.NextSeq < 0 || c.NextSeq > uint.MaxValue) return;
            uint s = (uint)c.NextSeq;
            c.NextSeq++;
            token = DeskSigner.Token(DeskSigner.Payload(num, s, fils, DeskSigner.Day(_now()), inv), signer);
            c.PrintedCodes[key] = "r:" + token;
        });
        return token;
    }

    System.Security.Cryptography.ECDsa? _signKey;
    string? _signKeyFor;

    /// <summary>The signing key opened from its DPAPI seal (cached per sealed value), or null.</summary>
    System.Security.Cryptography.ECDsa? Signer()
    {
        var sealedKey = _cfg.Current.SignKeyProtected;
        if (sealedKey == null) return null;
        lock (_lock)
        {
            if (_signKeyFor == sealedKey) return _signKey;
            _signKey?.Dispose();
            _signKey = null;
            _signKeyFor = sealedKey;
            try
            {
                var plain = OpenKey(sealedKey);
                if (plain != null) _signKey = DeskSigner.Open(plain);
                else _log.Warn("signing key could not be opened (DPAPI): pool codes only");
            }
            catch (Exception ex) { _log.Warn("signing key unusable: " + ex.GetType().Name); }
            return _signKey;
        }
    }

    string? OpenKey(string sealedKey) => _cfg.Protector == null ? sealedKey : _cfg.Protector.Unprotect(sealedKey);
    string SealKey(string plain) => _cfg.Protector == null ? plain : _cfg.Protector.Protect(plain);

    /// <summary>A new key pair, sealed. Null when this PC cannot make or seal one (the program then uses pool codes).</summary>
    (string Sealed, string Spki)? NewKey()
    {
        try
        {
            var (priv, spki) = DeskSigner.CreateKey();
            return (SealKey(priv), spki);
        }
        catch (Exception ex) { _log.Warn("signing key not created: " + ex.GetType().Name); return null; }
    }

    bool _keyRefused;

    /// <summary>
    /// WO-95: a device paired without a key (1.x, or an older server) makes one and registers it (POST /key), which
    /// also answers its device number. KEY_SET: the server holds another key; signed QR waits for a new pairing.
    /// </summary>
    public async Task EnsureKeyAsync()
    {
        var cfg = _cfg.Current;
        if (!cfg.Paired || cfg.KeyRegistered || _keyRefused) return;
        string token = cfg.DeviceToken!;
        if (cfg.SignKeyProtected == null)
        {
            var k = NewKey();
            if (k == null) return;
            _cfg.Update(c => { if (c.DeviceToken == token && c.SignKeyProtected == null) c.SignKeyProtected = k.Value.Sealed; });
        }
        var sealedKey = _cfg.Current.SignKeyProtected;
        string? plain = sealedKey == null ? null : OpenKey(sealedKey);
        if (plain == null) return;
        var res = await _api.KeyAsync(token, new KeyRequest(DeskSigner.SpkiOf(plain))).ConfigureAwait(false);
        if (res.Ok && res.Value != null)
        {
            _cfg.Update(c => { if (c.DeviceToken == token) { c.DeviceNumber = res.Value.DeviceNumber; c.KeyRegistered = true; } });
            _log.Info("signing key registered");
        }
        else if (res.Unauthorized) Unpair(res.Error);
        else
        {
            if (res.Status == 409) _keyRefused = true;
            _log.Warn($"key: {res}"); // a server before v1.3 has no /key: pool codes as before
        }
    }

    void Remember(string key, string code)
    {
        try { _cfg.Update(c => c.PrintedCodes[key] = code); }
        catch (Exception ex) { _log.Warn("printed code not saved: " + ex.GetType().Name); }
    }

    /// <summary>The slip with a pool code printed at once; then the binding (/receipt with code), queued when offline.</summary>
    async Task PrePrintedAsync(ReceiptRequest req, long seq, ReceiptRecord rec, string invoiceKey, AnalysisResult analysis,
        ExtractResult ext, ImageRead image, string? signedToken = null)
    {
        var c = _cfg.Current;
        byte[]? slip = null;
        string url = signedToken != null ? DeskSigner.Url(c.ClaimBase!, signedToken) : c.ClaimBase + req.Code;
        try { slip = QrReceipt.Build(url, c.CodePage, c.CodeTable, req.InvoiceNo, req.Amount); }
        catch (ArgumentException) { _log.Error("pool slip not printable"); }
        rec.QrPrinted = slip != null && _printIfCurrent(seq, slip);
        if (rec.QrPrinted) ClearProblem(Problems.QrPrintFailed);
        else _log.Warn("pool qr skipped: another job was forwarded or the slip did not print");

        var res = await _api.ReceiptAsync(c.DeviceToken!, req).ConfigureAwait(false);
        _log.Info($"receipt ({(signedToken != null ? "signed" : "pool")}) -> {res}");
        string printed = rec.QrPrinted ? "QR مطبوع" : "QR لم يُطبع (طُبعت فاتورة بعدها)";
        if (res.Ok && res.Value != null)
        {
            rec.Registered = true;
            ClearProblem(Problems.Offline, Problems.RateLimited, Problems.ParseFailed, Problems.Rejected, Problems.OutOfRange,
                Problems.ImageUnreadable, Problems.PrinterUnreachable);
            if (req.Code != null && res.Value.Code != req.Code)
            {
                // Accepted edge case: another till registered this invoice first. Its code stands, this slip's is void.
                if (ValidCode(res.Value.Code)) Remember(invoiceKey, res.Value.Code);
                rec.Status = "مسجّلة على جهاز آخر: QR هذه النسخة لا يعمل";
                await ReportAsync("warn", "other", "pool code replaced: the invoice already had another code (printed on another till)").ConfigureAwait(false);
            }
            else rec.Status = printed + (res.Value.Already ? " (فاتورة مكررة)" : "");
        }
        else if (res.Unauthorized)
        {
            Unpair(res.Error);
            rec.Status = "أُلغي ربط الجهاز: QR هذه الفاتورة لا يعمل";
        }
        else if (res.Transient)
        {
            // The printed code stays this receipt's: the queue binds it when the server answers again.
            _queue.Enqueue("receipt", req);
            SetProblem(res.Status == 429 ? Problems.RateLimited : Problems.Offline);
            rec.Status = printed + "، يُسجَّل عند عودة الاتصال";
            await ReportAsync("warn", "other", $"receipt queued with its pool code: {res}").ConfigureAwait(false);
        }
        else
        {
            // Refused: the server voided the printed code (a scan says so). A reprint today prints no QR.
            Remember(invoiceKey, "");
            if (res.Status == 409 && res.Error == "INVOICE_VOIDED") rec.Status = StatusVoided;
            else if (res.Status == 409 && res.Error == "INVOICE_USED") rec.Status = StatusCashier;
            else
            {
                if (res.Status == 422) { rec.Status = "المبلغ خارج الحد: QR المطبوع لا يعمل"; SetProblem(Problems.OutOfRange); }
                else { rec.Status = $"رفضها الخادم ({res.Error}): QR المطبوع لا يعمل"; SetProblem(Problems.Rejected, res.Error); }
                await ReportAsync("error", "parse_failed", $"receipt with pool code rejected {res}: {Summary(analysis, ext, image)}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tops the pool up (POST /codes) when it runs low. One at a time; after a failure, not again for a minute.</summary>
    public async Task RefillPoolAsync()
    {
        var cfg = _cfg.Current;
        var now = _now();
        if (!cfg.Paired || !cfg.QrEnabled || cfg.CodePool.Count(p => p.Until > now) >= PoolRefillAt || now - _poolFailedAt < PoolRetry) return;
        if (Interlocked.Exchange(ref _refilling, 1) == 1) return;
        try
        {
            string token = cfg.DeviceToken!;
            var res = await _api.CodesAsync(token, new CodesRequest(PoolRefillCount)).ConfigureAwait(false);
            if (res.Ok && res.Value != null && ValidClaimBase(res.Value.ClaimBase))
            {
                var fresh = (res.Value.Codes ?? new()).Where(ValidCode).Distinct().ToList();
                _cfg.Update(c =>
                {
                    if (c.DeviceToken != token) return; // paired again meanwhile: these codes belong to the old device
                    c.ClaimBase = res.Value.ClaimBase;
                    var until = _now() + PoolLifetime;
                    foreach (var code in fresh) if (!c.CodePool.Any(p => p.Code == code)) c.CodePool.Add(new PoolCode { Code = code, Until = until });
                });
                if (fresh.Count == 0) _poolFailedAt = _now();
                _log.Info($"pool: +{fresh.Count}, {_cfg.Current.CodePool.Count} ready");
                return;
            }
            _poolFailedAt = _now();
            if (res.Unauthorized) Unpair(res.Error);
            else _log.Warn($"pool refill: {res}"); // a 1.1 server has no /codes: receipts wait for /receipt as before
        }
        catch (Exception ex) { _poolFailedAt = _now(); _log.Warn("pool refill failed: " + ex.GetType().Name); }
        finally { Interlocked.Exchange(ref _refilling, 0); }
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
        string head = BitConverter.ToString(job, 0, Math.Min(job.Length, 8)).Replace("-", "");
        await ReportAsync("warn", "unknown_format", $"format {format}: {reason}, first bytes {head}", job, force: true).ConfigureAwait(false);
    }

    static FieldConfirmation BuildConfirmation(IReadOnlyList<string> lines, ExtractResult? ext, bool fromImage, string? ocrLanguage)
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
            OcrLanguage = ocrLanguage,
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
        // WO-91: the same rule as the window (ReceiptExtractor.*LineProblem), so a value it shows is never refused here.
        if (ReceiptExtractor.InvoiceLineProblem(p.Lines, invoiceLine) is { } invProblem) return invProblem;
        if (ReceiptExtractor.TotalLineProblem(p.Lines, totalLine) is { } totProblem) return totProblem;
        var inv = ReceiptExtractor.LearnInvoice(p.Lines, invoiceLine, invoiceLine == p.InvoiceLine ? p.InvoiceNo : null)!;
        var tot = ReceiptExtractor.LearnTotal(p.Lines, totalLine)!;
        // WO-91: shapes only count together, so when one field has no letter label both are learned by shape.
        bool invShape = inv.Value.field.Shape != null, totShape = tot.Value.field.Shape != null;
        if (invShape && !totShape) tot = ReceiptExtractor.LearnTotal(p.Lines, totalLine, forceShape: true) ?? tot;
        if (totShape && !invShape) inv = ReceiptExtractor.LearnInvoice(p.Lines, invoiceLine, invoiceLine == p.InvoiceLine ? p.InvoiceNo : null, forceShape: true) ?? inv;
        // The learned labels must find the same values on this very receipt, confidently (a shape that repeats on the
        // receipt is not), or later receipts would go wrong.
        var check = ReceiptExtractor.Extract(p.Lines, inv.Value.field, tot.Value.field, ocr: p.FromImage);
        if (check.InvoiceNo != inv.Value.value || check.Total != tot.Value.value
            || check.InvoiceConfidence < MinConfidence || check.TotalConfidence < MinConfidence)
            return "يتكرر عنوان السطر المختار في الفاتورة. اختر السطر الأقرب إلى العنوان الصحيح، أو تواصل مع دعم دايماً.";
        _cfg.Update(c =>
        {
            c.LearnedInvoice = inv.Value.field;
            c.LearnedTotal = tot.Value.field;
            c.FieldsConfirmed = true;
            c.FieldsOcrLanguage = p.FromImage ? p.OcrLanguage : null;
        });
        // WO-97: the sample on the owner's dashboard is done too (acknowledged on the next heartbeat).
        lock (_lock) { if (_remote != null) _appliedSampleId = _remote.SampleId; _pending = null; _remote = null; _remoteProblem = null; }
        _log.Info($"fields confirmed (invoice line {invoiceLine}, total line {totalLine}, of {p.Lines.Count})");
        _ = ReportAsync("info", "other", $"fields confirmed by owner: invoice {(invoiceLine == p.InvoiceLine ? "as detected" : "corrected")}, total {(totalLine == p.TotalLine ? "as detected" : "corrected")}");
        return null;
    }

    /// <summary>«لاحقاً» in «تأكيد الحقول» (WO-91): drop the pending receipt; the next receipt opens the window fresh.</summary>
    public void DismissConfirmation()
    {
        lock (_lock) _pending = null;
        _log.Info("field confirmation postponed by owner");
    }

    /// <summary>«إعادة تأكيد الحقول»: forget the learned fields; the next text receipt asks again.</summary>
    public void RequestReconfirm()
    {
        _cfg.Update(c => { c.FieldsConfirmed = false; c.LearnedInvoice = null; c.LearnedTotal = null; c.FieldsOcrLanguage = null; });
        lock (_lock) { _pending = null; _remote = null; _remoteProblem = null; }
        _log.Info("fields confirmation reset by owner");
    }

    // A Jordanian mobile (07x, 7x, (+|00)?9627x, spaces or dashes allowed), never part of a longer digit run.
    static readonly Regex PhoneRe = new(@"(?<![0-9])(?:(?:\+|00)?962[\s-]?|0)?7[789](?:[\s-]?[0-9]){7}(?![0-9])", RegexOptions.CultureInvariant);

    /// <summary>WO-97: Latin digits, Jordanian mobile numbers hidden (the server does the same); long invoice numbers stay.</summary>
    public static string ScrubLine(string line)
    {
        var latin = new string(line.Select(ch => ch is >= '\u0660' and <= '\u0669' ? (char)('0' + ch - '\u0660')
            : ch is >= '\u06F0' and <= '\u06F9' ? (char)('0' + ch - '\u06F0') : ch).ToArray());
        return PhoneRe.Replace(latin, "[رقم مخفي]");
    }

    /// <summary>WO-97: the receipt as sent to the owner (POST /fields): the first 60 lines, scrubbed, 80 characters each.</summary>
    public static FieldsRequest FieldsSample(FieldConfirmation p, string? problem = null)
    {
        var lines = p.Lines.Take(MaxSampleLines).Select(l => { var t = ScrubLine(l); return t.Length > MaxSampleChars ? t[..MaxSampleChars] : t; }).ToList();
        int Index(int i) => i >= 0 && i < lines.Count ? i : -1;
        return new FieldsRequest(p.SampleId, lines, Index(p.InvoiceLine), Index(p.TotalLine), problem == null ? null : ScrubLine(problem));
    }

    /// <summary>WO-97: sends the receipt waiting for the owner, once (again after a failure, on the next heartbeat).</summary>
    public async Task SendFieldsAsync()
    {
        FieldConfirmation? p;
        string? problem;
        lock (_lock) { p = _remote; problem = _remoteProblem; if (p == null || _remoteSent) return; }
        var cfg = _cfg.Current;
        if (!cfg.Paired || p.Lines.Count == 0) return;
        var res = await _api.FieldsAsync(cfg.DeviceToken!, FieldsSample(p, problem)).ConfigureAwait(false);
        if (res.Ok) { lock (_lock) if (_remote == p) _remoteSent = true; _log.Info("first receipt sent for the owner's confirmation"); }
        else if (res.Unauthorized) Unpair(res.Error);
        else _log.Warn($"fields sample: {res}");
    }

    /// <summary>
    /// WO-97: the owner's choice from his dashboard (/heartbeat fieldsConfirmed), applied through the same
    /// ConfirmFields as the window. Null on success, else the reason (the receipt goes back to him with it).
    /// </summary>
    public string? ApplyOwnerFields(FieldsChoice choice)
    {
        FieldConfirmation? p;
        lock (_lock)
        {
            if (_appliedSampleId == choice.SampleId) return null; // already handled; the server is waiting for the ack
            _appliedSampleId = choice.SampleId;
            p = _remote;
        }
        if (p == null || p.SampleId != choice.SampleId)
        {
            _log.Warn("owner's fields are for another receipt: ignored");
            return "not pending";
        }
        var err = ConfirmFields(p, choice.InvoiceLine, choice.TotalLine);
        if (err != null)
        {
            _log.Warn("owner's fields refused: sent back to the owner");
            // Back to the owner under a fresh id, so his next choice is not taken for the one just handled.
            var again = new FieldConfirmation
            {
                Lines = p.Lines, InvoiceLine = p.InvoiceLine, TotalLine = p.TotalLine, InvoiceNo = p.InvoiceNo, Total = p.Total,
                At = p.At, FromImage = p.FromImage, OcrLanguage = p.OcrLanguage,
            };
            lock (_lock)
            {
                if (_remote == p) { _remote = again; _remoteProblem = err; _remoteSent = false; }
                if (_pending == p) _pending = again;
            }
        }
        return err;
    }

    /// <summary>Raised when the learned fields were dropped because the OCR language changed (the UI says «أكّد الحقول مرة أخرى»).</summary>
    public event Action? ReconfirmNeeded;

    /// <summary>
    /// WO-91: fields confirmed on an image receipt belong to the OCR language that read it. Arabic OCR often arrives
    /// after the first receipts (Windows Update); its lines differ from the English read, so the learned labels would
    /// stop matching (no QR, silently). Another language: forget the fields and ask again on this very receipt.
    /// </summary>
    void CheckOcrLanguage()
    {
        var c = _cfg.Current;
        string? now = _ocr?.LanguageTag;
        if (!c.FieldsConfirmed || now == null) return;
        if (c.FieldsOcrLanguage == null)
        {
            // Confirmed before 1.2.2 (or on a text receipt): this read's language becomes the one the fields belong to.
            _cfg.Update(x => x.FieldsOcrLanguage = now);
            return;
        }
        if (now == c.FieldsOcrLanguage) return;
        _log.Warn($"ocr language changed ({c.FieldsOcrLanguage} -> {now}): fields must be confirmed again");
        RequestReconfirm();
        ReconfirmNeeded?.Invoke();
    }

    const string PairAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// WO-91: the pairing code as the server makes it (6 characters of PairAlphabet, case-insensitive). Null when
    /// valid, else the Arabic reason; Arabic letters mean the keyboard is on Arabic.
    /// </summary>
    public static string? PairCodeProblem(string? code)
    {
        string c = (code ?? "").Trim().ToUpperInvariant();
        if (c.Any(TextNormalizer.IsArabicLetter))
            return "لوحة المفاتيح على العربية. بدّل لوحة المفاتيح إلى الإنجليزية (Alt+Shift) ثم اكتب رمز الربط.";
        if (c.Length != 6 || !c.All(ch => PairAlphabet.Contains(ch)))
            return "رمز الربط 6 خانات من حروف إنجليزية وأرقام (بلا 0 ولا 1 ولا O ولا I ولا L). انسخه كما يظهر في لوحة التحكم.";
        return null;
    }

    static string Summary(AnalysisResult a, ExtractResult? e, ImageRead image) =>
        $"kind {KindWire(a.Kind)}, enc {a.EncodingName}, {a.Reason}, lines {a.Lines.Count}{(image != ImageRead.NotImage ? ", image " + image : "")}, " +
        $"invoice {(e?.InvoiceNo != null ? "found" : "none")} {e?.InvoiceConfidence:0.00}, " +
        $"total {(e?.Total != null ? "found" : "none")} {e?.TotalConfidence:0.00}";

    void Unpair(string? why)
    {
        _log.Error("device token refused (" + why + "): unpaired");
        ClearPairing();
        SetProblem(Problems.Revoked);
    }

    /// <summary>WO-101: «إلغاء الربط» in the window: this device forgets its pairing (the owner revokes it in the dashboard).</summary>
    public void ForgetPairing()
    {
        _log.Info("unpaired from the window");
        ClearPairing();
    }

    void ClearPairing()
    {
        // WO-90: a revoked device's pool is void on the server; a new pairing starts a new one.
        _cfg.Update(c =>
        {
            c.DeviceToken = null; c.CodePool.Clear(); c.ClaimBase = null; c.PrintedCodes.Clear();
            // WO-95: a key belongs to one pairing (a new pairing sends a new one).
            c.SignKeyProtected = null; c.DeviceNumber = null; c.KeyRegistered = false;
        });
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
        string? problemText = string.IsNullOrEmpty(cfg.LastProblem) ? null : ScrubLine(Problems.Describe(cfg.LastProblem, cfg.LastProblemDetail));
        if (problemText?.Length > 400) problemText = problemText[..400];
        var res = await _api.HeartbeatAsync(cfg.DeviceToken!, new HeartbeatRequest(_version, cfg.LastPrintKind, _queue.Count, LastForwardMs,
            string.IsNullOrEmpty(cfg.LastProblem) ? null : cfg.LastProblem, problemText, AppliedSampleId)).ConfigureAwait(false);
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
            if (v.FieldsConfirmed != null) ApplyOwnerFields(v.FieldsConfirmed);
            await SendFieldsAsync().ConfigureAwait(false);
            await FlushQueueAsync().ConfigureAwait(false);
            await EnsureKeyAsync().ConfigureAwait(false);
            await RefillPoolAsync().ConfigureAwait(false);
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
                    // WO-90: another till registered this invoice first; the code printed here is void (accepted edge case).
                    if (r.Ok && req.Code != null && r.Value != null && r.Value.Code != req.Code)
                        _log.Warn("queued receipt: the invoice already had another code, the printed pool code is void");
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
        var key = NewKey(); // WO-95: one key per pairing, sent with it (v1.3)
        var res = await _api.PairAsync(new PairRequest(code.Trim().ToUpperInvariant(), deviceName, _version, key?.Spki)).ConfigureAwait(false);
        if (res.Ok && res.Value != null && !string.IsNullOrEmpty(res.Value.Token))
        {
            var v = res.Value;
            _cfg.Update(c =>
            {
                c.DeviceToken = v.Token; c.ShopName = v.ShopName; c.StampMode = v.StampMode;
                c.CodeValidityDays = v.CodeValidityDays; c.QrEnabled = v.QrEnabled; c.DeviceName = deviceName;
                c.SamplesSent = 0;
                // WO-90: the old pool belongs to the old device (the server refuses it for this one).
                c.CodePool.Clear(); c.ClaimBase = null; c.PrintedCodes.Clear();
                c.SignKeyProtected = key?.Sealed; c.DeviceNumber = v.DeviceNumber;
                c.KeyRegistered = key != null && v.DeviceNumber != null; // an older server ignored the key: /key later
                // A new pairing asks again on the first receipt (it may be another shop or another POS).
                c.FieldsConfirmed = false; c.LearnedInvoice = null; c.LearnedTotal = null;
                if (c.LastProblem is Problems.Revoked or Problems.TokenUnreadable) { c.LastProblem = null; c.LastProblemDetail = null; c.LastProblemAt = null; }
            });
            lock (_lock) { _pending = null; _remote = null; _remoteProblem = null; }
            _keyRefused = false;
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
