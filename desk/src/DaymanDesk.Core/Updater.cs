using System.Security.Cryptography;

namespace DaymanDesk.Core;

/// <summary>
/// WO-96: the silent signed auto-update. The heartbeat's latestVersion/downloadUrl are untrusted: the exe is installed
/// only if its ECDSA P-256 signature (SHA-256, IEEE P1363, base64 in url + ".sig") verifies with the key built in here
/// and its file version is the announced one and newer.
/// </summary>
public static class Updater
{
    /// <summary>SubjectPublicKeyInfo of the release signing key (CI secret DESK_UPDATE_KEY).</summary>
    public const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZf9Nxn3kWWAgaorAXmMJWysyB7TrLGGvch8AezWrKgIGRo4HwLbdBVXQWfU9LIZ6WahSEF4vDY7/+XzOXV2oVA==";

    public const long MaxBytes = 150L << 20;
    public const int MaxSigBytes = 1024;
    public static readonly TimeSpan IdleAfterJob = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AttemptEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan BadVersionSkip = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    static readonly string[] Hosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "daymanjo.com" };

    /// <summary>Every hop (the link and each redirect): https, no user info, default port, an allowed host.</summary>
    public static bool AllowedUrl(Uri? u) =>
        u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) && u.IsDefaultPort
        && Hosts.Contains(u.Host.ToLowerInvariant());

    /// <summary>The downloaded file says it is the announced version, and that is newer than this one.</summary>
    public static bool VersionOk(string? announced, string? fileVersion, string current)
    {
        string? f = fileVersion?.Split('+')[0].Trim();
        return SemVer.TryParse(announced, out var a) && SemVer.TryParse(f, out var b) && a == b && SemVer.IsNewer(f, current);
    }

    /// <summary>No open pass-through connection and no job in the last 3 minutes.</summary>
    public static bool Idle(int openConnections, DateTimeOffset? lastJob, DateTimeOffset now) =>
        openConnections == 0 && (lastJob == null || now - lastJob.Value >= IdleAfterJob);

    /// <summary>The P-256 public key from a base64 SPKI (the exact 91-byte form DeskSigner writes), or null.</summary>
    public static ECDsa? ImportSpki(string spkiB64)
    {
        byte[] der;
        try { der = Convert.FromBase64String(spkiB64); } catch (FormatException) { return null; }
        var prefix = Convert.FromBase64String(DeskSigner.SpkiOf(Convert.ToBase64String(new byte[96])));
        if (der.Length != 91 || !der.Take(27).SequenceEqual(prefix.Take(27))) return null;
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = der.Skip(27).Take(32).ToArray(), Y = der.Skip(59).Take(32).ToArray() },
        });
    }

    /// <summary>True only when <paramref name="sigText"/> (base64 r‖s) signs the file with the given key.</summary>
    public static bool Verify(string file, string sigText, string spkiB64 = PublicKeySpki)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(sigText.Trim()); } catch (FormatException) { return false; }
        if (sig.Length != 64) return false;
        using var key = ImportSpki(spkiB64);
        if (key == null) return false;
        byte[] hash;
        using (var s = File.OpenRead(file))
        using (var sha = SHA256.Create()) hash = sha.ComputeHash(s);
        try { return key.VerifyHash(hash, sig); } catch (CryptographicException) { return false; }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="path"/>, following at most 5 redirects by hand (each hop
    /// checked by AllowedUrl), refusing more than <paramref name="max"/> bytes. The client must not auto-redirect.
    /// False on 404 (nothing published there); any other failure throws.
    /// </summary>
    public static async Task<bool> DownloadAsync(HttpClient http, string url, string path, long max, CancellationToken ct = default)
    {
        var u = new Uri(url);
        for (int hop = 0; ; hop++)
        {
            if (!AllowedUrl(u)) throw new InvalidOperationException("host not allowed: " + u.Host);
            using var res = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)res.StatusCode;
            if (code >= 300 && code < 400 && res.Headers.Location != null)
            {
                if (hop >= 5) throw new InvalidOperationException("too many redirects");
                u = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(u, res.Headers.Location);
                continue;
            }
            if (code == 404) return false;
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException("http " + code);
            if (res.Content.Headers.ContentLength > max) throw new InvalidOperationException("too large");
            using var src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var dst = File.Create(path);
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > max) throw new InvalidOperationException("too large");
                await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
            }
            return true;
        }
    }
}
