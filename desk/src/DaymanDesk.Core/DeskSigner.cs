using System.Security.Cryptography;
using System.Text;

namespace DaymanDesk.Core;

/// <summary>
/// WO-95: the signed QR of docs/desk-api.md v1.3. One ECDSA P-256 key per pairing (CNG on Windows), its private part
/// kept sealed with DPAPI like the token; the QR is claimBase with /s/ replaced by /r/ plus base64url(payload) "."
/// base64url(signature P1363).
/// </summary>
public static class DeskSigner
{
    // DER SubjectPublicKeyInfo header of an uncompressed P-256 point (id-ecPublicKey, prime256v1).
    static readonly byte[] SpkiPrefix =
    {
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A,
        0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    };

    /// <summary>A new key: base64 of X‖Y‖D (96 bytes, to be sealed) and the base64 SPKI public key for /pair and /key.</summary>
    public static (string Private, string Spki) CreateKey()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = k.ExportParameters(true);
        var blob = new byte[96];
        Buffer.BlockCopy(Pad(p.Q.X!), 0, blob, 0, 32);
        Buffer.BlockCopy(Pad(p.Q.Y!), 0, blob, 32, 32);
        Buffer.BlockCopy(Pad(p.D!), 0, blob, 64, 32);
        return (Convert.ToBase64String(blob), SpkiOf(blob));
    }

    public static string SpkiOf(string privateBlob) => SpkiOf(Convert.FromBase64String(privateBlob));

    static string SpkiOf(byte[] blob)
    {
        var spki = new byte[SpkiPrefix.Length + 64];
        Buffer.BlockCopy(SpkiPrefix, 0, spki, 0, SpkiPrefix.Length);
        Buffer.BlockCopy(blob, 0, spki, SpkiPrefix.Length, 64);
        return Convert.ToBase64String(spki);
    }

    static byte[] Pad(byte[] b)
    {
        if (b.Length == 32) return b;
        var o = new byte[32];
        Buffer.BlockCopy(b, Math.Max(0, b.Length - 32), o, Math.Max(0, 32 - b.Length), Math.Min(32, b.Length));
        return o;
    }

    public static ECDsa Open(string privateBlob)
    {
        var b = Convert.FromBase64String(privateBlob);
        if (b.Length != 96) throw new CryptographicException("key blob");
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Slice(b, 0), Y = Slice(b, 32) },
            D = Slice(b, 64),
        };
        return ECDsa.Create(p);
    }

    static byte[] Slice(byte[] b, int at)
    {
        var o = new byte[32];
        Buffer.BlockCopy(b, at, o, 0, 32);
        return o;
    }

    /// <summary>The invoice as the payload carries it: trimmed, single spaces (the digits are already Latin).</summary>
    public static string NormalizeInvoice(string invoice) =>
        string.Join(" ", invoice.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Days since 1970-01-01 of the local (Amman) date of <paramref name="at"/>.</summary>
    public static int Day(DateTimeOffset at)
    {
        at = at.ToOffset(Amman.Offset);
        return (int)(new DateTime(at.Year, at.Month, at.Day) - new DateTime(1970, 1, 1)).TotalDays;
    }

    /// <summary>Big-endian: version 1, deviceNumber (2), seq (4), amount in fils (4), print day (2), invoice UTF-8.</summary>
    public static byte[] Payload(int deviceNumber, uint seq, long fils, int day, string invoice)
    {
        var inv = Encoding.UTF8.GetBytes(invoice);
        var b = new byte[13 + inv.Length];
        b[0] = 1;
        b[1] = (byte)(deviceNumber >> 8); b[2] = (byte)deviceNumber;
        b[3] = (byte)(seq >> 24); b[4] = (byte)(seq >> 16); b[5] = (byte)(seq >> 8); b[6] = (byte)seq;
        uint f = (uint)fils;
        b[7] = (byte)(f >> 24); b[8] = (byte)(f >> 16); b[9] = (byte)(f >> 8); b[10] = (byte)f;
        b[11] = (byte)(day >> 8); b[12] = (byte)day;
        Buffer.BlockCopy(inv, 0, b, 13, inv.Length);
        return b;
    }

    /// <summary>The seq a token carries (bytes 3-6 of its payload).</summary>
    public static uint SeqOf(string token)
    {
        var p = FromB64Url(token.Substring(0, token.IndexOf('.')));
        return (uint)(p[3] << 24 | p[4] << 16 | p[5] << 8 | p[6]);
    }

    /// <summary>The token: base64url(payload) "." base64url(r‖s), no padding. .NET signs in IEEE P1363 by default.</summary>
    public static string Token(byte[] payload, ECDsa key) =>
        B64Url(payload) + "." + B64Url(key.SignData(payload, HashAlgorithmName.SHA256));

    /// <summary>claimBase ends in /s/ (checked by DeskAgent.ValidClaimBase); the signed QR uses /r/.</summary>
    public static string Url(string claimBase, string token) => claimBase.Substring(0, claimBase.Length - 3) + "/r/" + token;

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}

/// <summary>
/// WO-103: Amman time is UTC+3 all year since October 2022. Old Windows 7 time-zone data still moves Amman to UTC+2
/// in winter, so every Amman day or time (print day, invoice day, the log, the window) comes from UTC, never from
/// the Windows time zone.
/// </summary>
public static class Amman
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);
}
