using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaymanDesk.Core;

// Shapes of docs/desk-api.md (v1.2). Property names serialize camelCase.
public sealed record PairRequest(string Code, string DeviceName, string AppVersion);
public sealed record PairResponse(string Token, string ShopName, string StampMode, int CodeValidityDays, bool QrEnabled);
/// <summary>v1.2: <paramref name="Code"/> is the pool code already printed on the slip (omitted when none).</summary>
public sealed record ReceiptRequest(string ClientId, string InvoiceNo, string Amount, string PrintedAt, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);
public sealed record ReceiptResponse(string Code, string ClaimUrl, string ExpiresAt, bool Already);
/// <summary>v1.2 (WO-90): a refill of the device's pool of pre-issued codes.</summary>
public sealed record CodesRequest(int Count);
public sealed record CodesResponse(List<string>? Codes, string? ClaimBase);
public sealed record HeartbeatRequest(string AppVersion, string PrintKind, int QueueLength, long LastForwardMs);
/// <summary>v1.1: latestVersion and downloadUrl are optional (omitted when the server has no download set).</summary>
public sealed record HeartbeatResponse(string ServerTime, bool QrEnabled, int CodeValidityDays,
    string? LatestVersion = null, string? DownloadUrl = null);
public sealed record ReportRequest(
    string Level, string Kind, string Message, string AppVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sample = null);

public sealed class ApiResult<T>
{
    /// <summary>HTTP status; 0 when the server was not reached (network error or timeout).</summary>
    public int Status { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public int? RetryAfterSeconds { get; init; }

    public bool Ok => Status >= 200 && Status < 300;
    /// <summary>Worth retrying later: not reached, timeout, rate limited or a server error.</summary>
    public bool Transient => Status == 0 || Status == 408 || Status == 429 || Status >= 500;
    /// <summary>The device token is missing, wrong or revoked: the program is no longer paired.</summary>
    public bool Unauthorized => Status == 401;

    public override string ToString() => Ok ? $"{Status}" : $"{Status} {Error}";
}

/// <summary>HTTP client for /api/desk (docs/desk-api.md). Never throws for HTTP or network failures.</summary>
public sealed class DeskApiClient
{
    public const string DefaultBaseUrl = "https://daymanjo.com/api/desk";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly string _base;

    public DeskApiClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
    }

    public Task<ApiResult<PairResponse>> PairAsync(PairRequest req, CancellationToken ct = default) =>
        PostAsync<PairResponse>("/pair", req, null, ct);

    public Task<ApiResult<ReceiptResponse>> ReceiptAsync(string token, ReceiptRequest req, CancellationToken ct = default) =>
        PostAsync<ReceiptResponse>("/receipt", req, token, ct);

    public Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(string token, HeartbeatRequest req, CancellationToken ct = default) =>
        PostAsync<HeartbeatResponse>("/heartbeat", req, token, ct);

    public Task<ApiResult<CodesResponse>> CodesAsync(string token, CodesRequest req, CancellationToken ct = default) =>
        PostAsync<CodesResponse>("/codes", req, token, ct);

    public Task<ApiResult<object>> ReportAsync(string token, ReportRequest req, CancellationToken ct = default) =>
        PostAsync<object>("/report", req, token, ct);

    async Task<ApiResult<T>> PostAsync<T>(string path, object body, string? token, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _base + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json"),
            };
            if (token != null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            int status = (int)res.StatusCode;
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                T? value = default;
                if (typeof(T) != typeof(object) && text.Length > 0)
                {
                    try { value = JsonSerializer.Deserialize<T>(text, Json); }
                    catch (JsonException) { return new ApiResult<T> { Status = 502, Error = "BAD_JSON" }; }
                    if (value == null) return new ApiResult<T> { Status = 502, Error = "BAD_JSON" };
                }
                return new ApiResult<T> { Status = status, Value = value };
            }
            string? error = null, message = null;
            int? retry = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (root.TryGetProperty("retryAfterSeconds", out var r) && r.TryGetInt32(out var ri)) retry = ri;
            }
            catch (JsonException) { }
            if (retry == null && res.Headers.RetryAfter?.Delta is TimeSpan d) retry = (int)d.TotalSeconds;
            return new ApiResult<T> { Status = status, Error = error ?? res.StatusCode.ToString(), Message = message, RetryAfterSeconds = retry };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResult<T> { Status = 0, Error = "TIMEOUT" };
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult<T> { Status = 0, Error = "NETWORK", Message = ex.Message };
        }
    }
}
