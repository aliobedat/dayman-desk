using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

/// <summary>WO-95 (contract v1.3): the device signs the receipt's QR itself; pool codes only without a registered key.</summary>
public static class SignedTests
{
    const string Base = "https://daymanjo.com/c/shop/s/";
    const string RBase = "https://daymanjo.com/c/shop/r/";

    sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly List<byte[]> Printed = new();
        public readonly ConfigStore Cfg;
        public readonly string CfgPath;
        public readonly DeskAgent Agent;
        public readonly string Spki;
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T18:00:00+03:00");

        public Rig(bool keyRegistered = true, params string[] pool)
        {
            var dir = Tmp.Dir();
            CfgPath = Path.Combine(dir, "config.json");
            Cfg = new ConfigStore(CfgPath);
            var (priv, spki) = DeskSigner.CreateKey();
            Spki = spki;
            Cfg.Update(c =>
            {
                c.DeviceToken = "tok"; c.SamplesSent = DeskAgent.FirstSamples; c.FieldsConfirmed = true; c.ClaimBase = Base;
                c.CodePool = pool.Select(x => new PoolCode { Code = x, Until = Now + DeskAgent.PoolLifetime }).ToList();
                if (keyRegistered) { c.SignKeyProtected = priv; c.DeviceNumber = 3; c.KeyRegistered = true; }
            });
            Agent = new DeskAgent(Cfg, new DeskApiClient(new HttpClient(Http), "https://t/api/desk"),
                new OfflineQueue(Path.Combine(dir, "queue.json"), () => Now), new RollingLog(Path.Combine(dir, "logs")),
                (_, b) => { Printed.Add(b); return true; }, "2.0.0", () => Now);
            Http.Respond = (r, body) =>
            {
                var path = r.RequestUri!.AbsolutePath;
                if (path.EndsWith("/receipt"))
                    return FakeHandler.Json(200, $"{{\"code\":\"SRVRC0DE11\",\"claimUrl\":\"{Base}SRVRC0DE11\",\"expiresAt\":\"x\",\"already\":false}}");
                if (path.EndsWith("/key")) return FakeHandler.Json(200, "{\"deviceNumber\":5}");
                if (path.EndsWith("/heartbeat")) return FakeHandler.Json(200, "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2}");
                if (path.EndsWith("/codes")) return FakeHandler.Json(404, "{}");
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            };
        }

        public List<JsonElement> Bodies(string path) => Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith(path))
            .Select(c => JsonDocument.Parse(c.body).RootElement.Clone()).ToList();
    }

    static string? UrlIn(byte[] slip)
    {
        var s = Encoding.ASCII.GetString(slip);
        int i = s.IndexOf("https://", StringComparison.Ordinal);
        if (i < 0) return null;
        int e = i;
        while (e < s.Length && s[e] > 0x20 && s[e] < 0x7F) e++;
        return s.Substring(i, e - i);
    }

    static byte[] Receipt() => Receipts.Cp864(Receipts.Arabic); // invoice 10432, total 6.670

    /// <summary>The layout of tests/wo94.test.mts payloadOf (the server's format), and a P1363 signature over it.</summary>
    public static void TestPayloadLayoutAndSignatureVerify()
    {
        var p = DeskSigner.Payload(2, 7, 12_500, 20_412, "10432");
        Assert.Equal("01000200000007000030D44FBC3130343332", Convert.ToHexString(p), "big-endian v|num|seq|fils|day|invoice");
        Assert.Equal(20_782, DeskSigner.Day(DateTimeOffset.Parse("2026-11-25T23:30:00+03:00")), "the local (Amman) date");
        Assert.Equal("A 12", DeskSigner.NormalizeInvoice("  A   12 "), "trimmed, single spaces");

        var (priv, spki) = DeskSigner.CreateKey();
        using var key = DeskSigner.Open(priv);
        var token = DeskSigner.Token(p, key);
        Assert.True(!token.Contains('=') && !token.Contains('+') && !token.Contains('/'), "base64url without padding");
        var parts = token.Split('.');
        Assert.Equal(Convert.ToHexString(p), Convert.ToHexString(DeskSigner.FromB64Url(parts[0])), "payload part");
        var sig = DeskSigner.FromB64Url(parts[1]);
        Assert.Equal(64, sig.Length, "IEEE P1363 r|s");
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _); // the DER SPKI the server parses
        Assert.True(pub.VerifyData(p, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "verifies");
        Assert.Equal(7u, DeskSigner.SeqOf(token), "seq read back");
        Assert.Equal(RBase + token, DeskSigner.Url(Base, token), "/s/ becomes /r/");
        Assert.True((RBase.Replace("shop", Guid.NewGuid().ToString()) + token).Length < 180, "short URL with a short invoice");
    }

    public static async Task TestLocalReadPrintsSignedQrThenSendsSeq()
    {
        var rig = new Rig(true, "K7Q2M9XP0A");
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted && rec.Registered, rec.Status);
        var url = UrlIn(rig.Printed.Single())!;
        Assert.True(url.StartsWith(RBase, StringComparison.Ordinal), url);
        var token = url.Substring(RBase.Length);
        var payload = DeskSigner.FromB64Url(token.Split('.')[0]);
        Assert.Equal(Convert.ToHexString(DeskSigner.Payload(3, 1, 6670, DeskSigner.Day(rig.Now), "10432")), Convert.ToHexString(payload), "payload");
        var body = rig.Bodies("/receipt").Single();
        Assert.Equal(1L, body.GetProperty("seq").GetInt64(), "seq sent");
        Assert.True(!body.TryGetProperty("code", out _), "no code with seq");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "pool untouched");
        Assert.Equal(2L, new ConfigStore(rig.CfgPath).Current.NextSeq, "seq persisted");

        // A reprint prints the same QR; the next invoice the next seq.
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 2);
        Assert.Equal(url, UrlIn(rig.Printed[1]), "reprint: same QR");
        Assert.Equal(1L, rig.Bodies("/receipt")[1].GetProperty("seq").GetInt64(), "reprint: same seq");
        var other = Receipts.Arabic.Select(l => l.Replace("10432", "10433")).ToArray();
        await rig.Agent.ProcessJobAsync(Receipts.Cp864(other), 5, 3);
        Assert.Equal(2L, rig.Bodies("/receipt")[2].GetProperty("seq").GetInt64(), "next invoice: next seq");
        Assert.Equal(3L, rig.Cfg.Current.NextSeq, "counter");
    }

    public static async Task TestWithoutKeyThePoolCodePrints()
    {
        var rig = new Rig(false, "K7Q2M9XP0A");
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted, rec.Status);
        Assert.Equal(Base + "K7Q2M9XP0A", UrlIn(rig.Printed.Single()), "pool slip");
        var body = rig.Bodies("/receipt").Single();
        Assert.True(!body.TryGetProperty("seq", out _), "no seq with a pool code");
        Assert.Equal(1L, rig.Cfg.Current.NextSeq, "no seq used");
    }

    public static async Task TestReadFailPrintsNothingAndUsesNoSeq()
    {
        var rig = new Rig(true, "K7Q2M9XP0A");
        var noInvoice = Receipts.Arabic.Where(l => !l.Contains("الفاتورة")).ToArray();
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp864(noInvoice), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Printed.Count == 0, rec.Status);
        Assert.Equal(0, rig.Bodies("/receipt").Count, "no /receipt");
        Assert.Equal(1L, rig.Cfg.Current.NextSeq, "no seq used");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "no pool code used");
    }

    public static async Task TestHeartbeatRegistersTheKeyOfAnOldPairing()
    {
        var rig = new Rig(false);
        await rig.Agent.HeartbeatAsync();
        var c = rig.Cfg.Current;
        Assert.True(c.KeyRegistered && c.DeviceNumber == 5 && c.SignKeyProtected != null, "registered with its number");
        var spki = rig.Bodies("/key").Single().GetProperty("publicKey").GetString()!;
        Assert.Equal(DeskSigner.SpkiOf(c.SignKeyProtected!), spki, "the stored key's public part");
        await rig.Agent.HeartbeatAsync();
        Assert.Equal(1, rig.Bodies("/key").Count, "once");
    }
}
