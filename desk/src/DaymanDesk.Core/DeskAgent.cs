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
    public string KindLabel => Kind switch { ReceiptKind.Text => "نص", ReceiptKind.Image => "صورة", _ => "غير معروف" };
}

/// <summary>
/// Everything that happens to a print job after it was forwarded to the printer: analyse, extract,
/// register at /receipt, print the QR slip, report. It never touches the forwarded bytes.
/// </summary>
public sealed class DeskAgent
{
    public const double MinInvoiceConfidence = 0.6;
    public const double MinTotalConfidence = 0.75;
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
    readonly string _version;
    readonly Dictionary<string, DateTimeOffset> _lastReport = new();
    readonly object _lock = new();

    public long LastForwardMs { get; set; }
    public event Action<ReceiptRecord>? ReceiptProcessed;

    /// <param name="printIfCurrent">
    /// (sequence of the receipt, slip) -> prints the slip only if no other job was forwarded after that receipt,
    /// checking and printing under the same lock the forwarder holds. Returns false when it did not print.
    /// </param>
    public DeskAgent(ConfigStore cfg, DeskApiClient api, OfflineQueue queue, RollingLog log,
        Func<long, byte[], bool> printIfCurrent, string appVersion, Func<DateTimeOffset>? now = null)
    {
        _cfg = cfg; _api = api; _queue = queue; _log = log; _printIfCurrent = printIfCurrent;
        _version = appVersion; _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string KindWire(ReceiptKind k) => k switch { ReceiptKind.Text => "text", ReceiptKind.Image => "image", _ => "unknown" };

    public async Task<ReceiptRecord> ProcessJobAsync(byte[] job, long forwardMs, long seqAfterForward)
    {
        var at = _now();
        LastForwardMs = forwardMs;
        var analysis = ReceiptAnalyzer.Analyze(job);
        var ext = analysis.Kind == ReceiptKind.Text ? ReceiptExtractor.Extract(analysis.Lines) : null;
        var rec = new ReceiptRecord
        {
            At = at, Kind = analysis.Kind, InvoiceNo = ext?.InvoiceNo, Total = ext?.Total, Confidence = ext?.Confidence ?? 0,
        };
        _log.Info($"job {job.Length} B, forward {forwardMs} ms, {analysis.Kind} ({analysis.Reason}), invoice {(ext?.InvoiceNo != null ? "found" : "none")} {ext?.InvoiceConfidence:0.00}, total {ext?.Total?.ToString(CultureInfo.InvariantCulture) ?? "none"} {ext?.TotalConfidence:0.00}");

        _cfg.Update(c =>
        {
            c.LastPrintKind = KindWire(analysis.Kind);
            if (analysis.Kind == ReceiptKind.Text && analysis.CodePage != null)
            {
                c.CodePage = analysis.CodePage;
                if (analysis.CodeTable != null) c.CodeTable = analysis.CodeTable;
            }
        });

        try
        {
            await HandleAsync(job, forwardMs, seqAfterForward, analysis, ext, rec).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rec.Status = "خطأ داخلي";
            _log.Error("process failed: " + ex);
            await ReportAsync("error", "crash", "process: " + ex.GetType().Name + ": " + ex.Message).ConfigureAwait(false);
        }
        ReceiptProcessed?.Invoke(rec);
        return rec;
    }

    async Task HandleAsync(byte[] job, long forwardMs, long seq, AnalysisResult analysis, ExtractResult? ext, ReceiptRecord rec)
    {
        var cfg = _cfg.Current;
        if (forwardMs > 300)
        {
            _log.Warn($"slow forward: {forwardMs} ms");
            await ReportAsync("warn", "slow_forward", $"forward {forwardMs} ms for {job.Length} bytes").ConfigureAwait(false);
        }
        if (cfg.Paired && cfg.SamplesSent < FirstSamples)
        {
            _cfg.Update(c => c.SamplesSent++);
            await ReportAsync("info", "sample", Summary(analysis, ext), job, force: true).ConfigureAwait(false);
        }
        if (!cfg.Paired)
        {
            rec.Status = "غير مربوط بالحساب";
            return;
        }
        if (analysis.Kind == ReceiptKind.Image)
        {
            rec.Status = "صورة: بلا QR";
            await ReportAsync("info", "other", "image receipt: " + analysis.Reason, job).ConfigureAwait(false);
            return;
        }
        if (analysis.Kind == ReceiptKind.Unknown)
        {
            rec.Status = "صيغة غير معروفة: بلا QR";
            await ReportAsync("warn", "unknown_format", analysis.Reason, job).ConfigureAwait(false);
            return;
        }
        if (ext == null || ext.InvoiceNo == null || ext.Total == null
            || ext.InvoiceConfidence < MinInvoiceConfidence || ext.TotalConfidence < MinTotalConfidence)
        {
            rec.Status = "لم تُقرأ الفاتورة: بلا QR";
            await ReportAsync("warn", "parse_failed", Summary(analysis, ext), job).ConfigureAwait(false);
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
            if (!_cfg.Current.QrEnabled) { rec.Status = "مسجّلة (QR مُعطّل من صاحب المحل)"; return; }
            var c = _cfg.Current;
            bool printed = _now() - started <= QrDeadline
                && _printIfCurrent(seq, QrReceipt.Build(res.Value.ClaimUrl, c.CodePage, c.CodeTable));
            if (!printed)
            {
                // Another receipt printed meanwhile (or the answer came late): a QR now would sit under the wrong receipt.
                rec.Status = "مسجّلة: QR متأخر لم يُطبع";
                _log.Warn("qr skipped: another job was forwarded or the answer was late");
                await ReportAsync("warn", "other", "qr skipped: late answer or next job already printed").ConfigureAwait(false);
                return;
            }
            rec.QrPrinted = true;
            rec.Status = res.Value.Already ? "QR مطبوع (مكرر)" : "QR مطبوع";
            return;
        }
        if (res.Unauthorized)
        {
            Unpair(res.Error);
            rec.Status = "الجهاز لم يعد مربوطاً";
            return;
        }
        if (res.Transient)
        {
            _queue.Enqueue("receipt", req);
            rec.Status = "غير متصل: بلا QR، أُرسلت لاحقاً";
            await ReportAsync("warn", "other", $"receipt queued: {res}").ConfigureAwait(false);
            return;
        }
        if (res.Status == 409)
        {
            rec.Status = "الفاتورة سُجّلت عند الكاشير";
            return;
        }
        rec.Status = $"رُفضت ({res.Error})";
        await ReportAsync("error", "parse_failed", $"receipt rejected {res}: {Summary(analysis, ext)}").ConfigureAwait(false);
    }

    static string Summary(AnalysisResult a, ExtractResult? e) =>
        $"kind {KindWire(a.Kind)}, enc {a.EncodingName}, {a.Reason}, lines {a.Lines.Count}, " +
        $"invoice {(e?.InvoiceNo != null ? "found" : "none")} {e?.InvoiceConfidence:0.00}, " +
        $"total {(e?.Total != null ? "found" : "none")} {e?.TotalConfidence:0.00}";

    void Unpair(string? why)
    {
        _log.Error("device token refused (" + why + "): unpaired");
        _cfg.Update(c => c.DeviceToken = null);
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
            _cfg.Update(c => { c.QrEnabled = res.Value.QrEnabled; c.CodeValidityDays = res.Value.CodeValidityDays; });
            await FlushQueueAsync().ConfigureAwait(false);
        }
        else if (res.Unauthorized) Unpair(res.Error);
        else _log.Warn($"heartbeat: {res}");
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
            if (item.Type == "receipt")
            {
                var req = JsonSerializer.Deserialize<ReceiptRequest>(item.Payload, DeskApiClient.Json)!;
                var r = await _api.ReceiptAsync(token, req).ConfigureAwait(false);
                (status, ok, transient, unauthorized) = (r.Status, r.Ok, r.Transient, r.Unauthorized);
            }
            else
            {
                var req = JsonSerializer.Deserialize<ReportRequest>(item.Payload, DeskApiClient.Json)!;
                var r = await _api.ReportAsync(token, req).ConfigureAwait(false);
                (status, ok, transient, unauthorized) = (r.Status, r.Ok, r.Transient, r.Unauthorized);
            }
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
        if (res.Ok && res.Value != null)
        {
            var v = res.Value;
            _cfg.Update(c =>
            {
                c.DeviceToken = v.Token; c.ShopName = v.ShopName; c.StampMode = v.StampMode;
                c.CodeValidityDays = v.CodeValidityDays; c.QrEnabled = v.QrEnabled; c.DeviceName = deviceName;
                c.SamplesSent = 0;
            });
            _log.Info("paired with shop");
        }
        else _log.Warn($"pair failed: {res}");
        return res;
    }

    /// <summary>Arabic text for a pairing error.</summary>
    public static string PairError(ApiResult<PairResponse> r) => r.Error switch
    {
        "CODE_NOT_FOUND" => "رمز الربط غير صحيح.",
        "CODE_EXPIRED" => "انتهت صلاحية رمز الربط. أنشئ رمزاً جديداً من الإعدادات.",
        "RATE_LIMITED" => "محاولات كثيرة. انتظر قليلاً ثم أعد المحاولة.",
        "NETWORK" or "TIMEOUT" => "لا يوجد اتصال بالخادم. تأكد من الإنترنت.",
        _ => r.Message ?? $"تعذّر الربط ({r.Status}).",
    };
}
