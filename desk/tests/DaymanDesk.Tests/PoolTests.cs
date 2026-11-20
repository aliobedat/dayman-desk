using System.Text;
using System.Text.Json;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

/// <summary>WO-90 (contract v1.2): pre-issued codes print the QR slip at once, then /receipt binds them.</summary>
public static class PoolTests
{
    const string Base = "https://daymanjo.com/c/shop/s/";
    const string A = "K7Q2M9XP0A", B = "K7Q2M9XP0B", C = "K7Q2M9XP0C";

    sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly List<byte[]> Printed = new();
        /// <summary>For each /receipt call: how many slips had printed when it was sent.</summary>
        public readonly List<int> PrintedBeforeReceipt = new();
        public readonly ConfigStore Cfg;
        public readonly OfflineQueue Queue;
        public readonly DeskAgent Agent;
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T18:00:00+03:00");

        public Rig(params string[] pool)
        {
            var dir = Tmp.Dir();
            Cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            Cfg.Update(c =>
            {
                c.DeviceToken = "tok"; c.SamplesSent = DeskAgent.FirstSamples; c.FieldsConfirmed = true;
                c.CodePool = pool.Select(x => new PoolCode { Code = x, Until = Now + DeskAgent.PoolLifetime }).ToList();
                c.ClaimBase = pool.Length > 0 ? Base : null;
            });
            Queue = new OfflineQueue(Path.Combine(dir, "queue.json"), () => Now);
            Agent = new DeskAgent(Cfg, new DeskApiClient(new HttpClient(Http), "https://t/api/desk"), Queue,
                new RollingLog(Path.Combine(dir, "logs")), (_, b) => { Printed.Add(b); return true; }, "1.2.0", () => Now);
        }

        public IEnumerable<string> Bodies(string path) => Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith(path)).Select(c => c.body);

        /// <summary>/receipt answers `code` (the offered one when null); /codes answers `refill`; anything else 204.</summary>
        public void Online(string? code = null, string[]? refill = null, bool already = false)
        {
            Http.Respond = (r, body) =>
            {
                var path = r.RequestUri!.AbsolutePath;
                if (path.EndsWith("/receipt"))
                {
                    PrintedBeforeReceipt.Add(Printed.Count);
                    using var doc = JsonDocument.Parse(body);
                    var sent = doc.RootElement.TryGetProperty("code", out var p) ? p.GetString() : null;
                    var c = code ?? sent ?? "SRVRC0DE11";
                    return FakeHandler.Json(200, $"{{\"code\":\"{c}\",\"claimUrl\":\"{Base}{c}\",\"expiresAt\":\"2026-11-22T00:00:00Z\",\"already\":{(already ? "true" : "false")}}}");
                }
                if (path.EndsWith("/codes"))
                    return refill == null ? FakeHandler.Json(404, "{}")
                        : FakeHandler.Json(200, "{\"codes\":[" + string.Join(",", refill.Select(x => $"\"{x}\"")) + "],\"claimBase\":\"" + Base + "\"}");
                if (path.EndsWith("/heartbeat")) return FakeHandler.Json(200, "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2}");
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            };
        }
    }

    static bool SlipHas(byte[] slip, string url) => Convert.ToHexString(slip).Contains(Convert.ToHexString(Encoding.ASCII.GetBytes(url)));
    static string? CodeSent(string body) { using var d = JsonDocument.Parse(body); return d.RootElement.TryGetProperty("code", out var p) ? p.GetString() : null; }
    static byte[] Receipt() => Receipts.Cp864(Receipts.Arabic); // invoice 10432, total 6.670

    public static async Task TestPoolCodePrintsBeforeTheServerAnswers()
    {
        var rig = new Rig(A, B);
        rig.Online();
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted && rec.Registered, rec.Status);
        Assert.True(SlipHas(rig.Printed[0], Base + A), "slip carries the pool code's claim url");
        Assert.Equal(1, rig.PrintedBeforeReceipt.Single(), "printed before /receipt was sent");
        Assert.Equal(A, CodeSent(rig.Bodies("/receipt").Single()), "binds the printed code");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "one code used");
        Assert.Equal(A, rig.Cfg.Current.PrintedCodes["2026-11-20|10432"], "remembered for a reprint");
    }

    public static async Task TestReprintSameInvoiceSameDayPrintsTheSameCode()
    {
        var rig = new Rig(A, B);
        rig.Online();
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        rig.Online(already: true);
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.Equal(2, rig.Printed.Count, "two slips");
        Assert.True(SlipHas(rig.Printed[1], Base + A), "the same code again");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "no second code taken");
        Assert.True(rec.Status.Contains("مكررة"), rec.Status);
        // The next day the same number is another invoice: a new code.
        rig.Now = rig.Now.AddDays(1);
        rig.Online();
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(SlipHas(rig.Printed[2], Base + B), "next day, next code");
    }

    public static async Task TestOfflinePrintsAndQueuesTheBinding()
    {
        var rig = new Rig(A);
        rig.Http.Respond = (_, _) => FakeHandler.Offline();
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted && !rec.Registered, rec.Status);
        Assert.True(SlipHas(rig.Printed[0], Base + A), "printed offline");
        var queued = JsonSerializer.Deserialize<ReceiptRequest>(rig.Queue.Snapshot().Single(i => i.Type == "receipt").Payload, DeskApiClient.Json)!;
        Assert.Equal(A, queued.Code, "queued with its code");
        // Back online: the heartbeat flushes the queue (binding the same code) and refills the pool.
        rig.Online(refill: new[] { B, C, "bad", B });
        await rig.Agent.HeartbeatAsync();
        Assert.Equal(0, rig.Queue.Count, "flushed");
        Assert.Equal(A, CodeSent(rig.Bodies("/receipt").Last()), "bound later with the printed code");
        Assert.Equal(1, rig.Printed.Count, "no second slip");
        Assert.Equal(string.Join(",", new[] { B, C }), string.Join(",", rig.Cfg.Current.CodePool.Select(p => p.Code)), "refilled, invalid and duplicate codes dropped");
        using var refill = JsonDocument.Parse(rig.Bodies("/codes").Single());
        Assert.Equal(DeskAgent.PoolRefillCount, refill.RootElement.GetProperty("count").GetInt32(), "asks for 50");
    }

    public static async Task TestEmptyPoolWaitsForTheServerAsBefore()
    {
        var rig = new Rig();
        rig.Online(code: "SRVRC0DE11");
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted, rec.Status);
        Assert.Equal(0, rig.PrintedBeforeReceipt.Single(), "printed only after /receipt");
        Assert.Equal<string?>(null, CodeSent(rig.Bodies("/receipt").Single()), "a 1.1 request: no code field");
        Assert.True(SlipHas(rig.Printed[0], Base + "SRVRC0DE11"), "the server's code");
        Assert.Equal(1, rig.Bodies("/codes").Count(), "tried to refill after the registered receipt");
        // A server without /codes (404): no retry within a minute.
        await rig.Agent.RefillPoolAsync();
        Assert.Equal(1, rig.Bodies("/codes").Count(), "throttled");
        Assert.Equal(0, rig.Cfg.Current.CodePool.Count, "still empty");
    }

    public static async Task TestAnotherTillsCodeIsReported()
    {
        var rig = new Rig(A, B);
        rig.Online(code: C, already: true);
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.Registered && rec.Status.Contains("جهاز آخر"), rec.Status);
        Assert.True(rig.Bodies("/report").Any(b => b.Contains("pool code replaced")), "reported");
        Assert.Equal(C, rig.Cfg.Current.PrintedCodes["2026-11-20|10432"], "a reprint prints the live code");
        rig.Online(already: true);
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(SlipHas(rig.Printed[1], Base + C), "reprint carries the first till's code");
    }

    public static async Task TestRefusedBindingReprintsWithoutQr()
    {
        var rig = new Rig(A, B);
        rig.Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/receipt")
            ? FakeHandler.Json(409, "{\"error\":\"INVOICE_USED\",\"message\":\"x\"}") : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        var rec = await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(rec.QrPrinted && !rec.Registered, rec.Status);
        Assert.Equal(0, rig.Queue.Count, "not queued");
        Assert.Equal("", rig.Cfg.Current.PrintedCodes["2026-11-20|10432"], "refused");
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.Equal(1, rig.Printed.Count, "the reprint gets no QR");
        Assert.Equal<string?>(null, CodeSent(rig.Bodies("/receipt").Last()), "and no pool code");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "B is kept");
    }

    public static async Task TestAmountUnderOneJodUsesNoPoolCode()
    {
        var rig = new Rig(A);
        rig.Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/receipt")
            ? FakeHandler.Json(422, "{\"error\":\"OUT_OF_RANGE\",\"message\":\"x\"}") : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        var small = Receipts.Arabic.Select(l => l.StartsWith("المجموع") ? "المجموع: 0.650" : l.StartsWith("الإجمالي") ? "الإجمالي: 0.750" : l).ToArray();
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp864(small), 5, 1);
        var body = rig.Bodies("/receipt").Single();
        Assert.True(body.Contains("\"amount\":\"0.750\""), "the small total was read: " + body);
        Assert.Equal<string?>(null, CodeSent(body), "no pool code offered");
        Assert.Equal(0, rig.Printed.Count, "no QR on 422");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "pool kept");
        Assert.True(!rec.QrPrinted, rec.Status);
    }

    public static async Task TestPoolCodesPastTheirLifetimeAreNotPrinted()
    {
        var rig = new Rig(A, B);
        rig.Cfg.Update(c => c.CodePool[0].Until = rig.Now.AddMinutes(-1)); // A: past its 25 days
        rig.Online(refill: new[] { C });
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.True(SlipHas(rig.Printed[0], Base + B), "the expired code is skipped");
        Assert.True(rig.Cfg.Current.CodePool.All(p => p.Code != A), "and dropped");
        Assert.True(rig.Cfg.Current.CodePool.Single(p => p.Code == C).Until > rig.Now.AddDays(24), "a refilled code lives 25 days here");
    }

    public static async Task TestQrOffUsesNoPoolCode()
    {
        var rig = new Rig(A);
        rig.Cfg.Update(c => c.QrEnabled = false);
        rig.Online(code: "SRVRC0DE11");
        await rig.Agent.ProcessJobAsync(Receipt(), 5, 1);
        Assert.Equal(0, rig.Printed.Count, "no slip");
        Assert.Equal<string?>(null, CodeSent(rig.Bodies("/receipt").Single()), "no code");
        Assert.Equal(1, rig.Cfg.Current.CodePool.Count, "pool kept");
    }

    public static async Task TestPairingAndRevocationClearThePool()
    {
        var rig = new Rig(A, B);
        rig.Http.Respond = (_, _) => FakeHandler.Json(200, "{\"token\":\"new\",\"shopName\":\"جمرة\",\"stampMode\":\"spend\",\"codeValidityDays\":2,\"qrEnabled\":true}");
        Assert.True((await rig.Agent.PairAsync("AB12CD", "كاشير 1")).Ok, "paired");
        Assert.True(rig.Cfg.Current.CodePool.Count == 0 && rig.Cfg.Current.ClaimBase == null, "a new device starts a new pool");

        var revoked = new Rig(A, B);
        revoked.Http.Respond = (_, _) => FakeHandler.Json(401, "{\"error\":\"REVOKED\",\"message\":\"x\"}");
        await revoked.Agent.HeartbeatAsync();
        Assert.True(!revoked.Cfg.Current.Paired && revoked.Cfg.Current.CodePool.Count == 0, "revoked: pool gone");
    }

    public static void TestCodeAndClaimBaseValidation()
    {
        Assert.True(DeskAgent.ValidCode(A), "code");
        foreach (var bad in new[] { null, "", "K7Q2M9XP0", "K7Q2M9XP0I", "k7q2m9xp0a" }) Assert.True(!DeskAgent.ValidCode(bad), "bad code " + bad);
        Assert.True(DeskAgent.ValidClaimBase(Base), "base");
        foreach (var bad in new[] { null, "http://daymanjo.com/c/x/s/", "https://daymanjo.com/c/x/", "https://u:p@daymanjo.com/c/x/s/", "https://daymanjo.com/c/ x/s/" })
            Assert.True(!DeskAgent.ValidClaimBase(bad), "bad base " + bad);
    }

    public static void TestV11QueueItemStillSendsWithoutCode()
    {
        var old = JsonSerializer.Deserialize<ReceiptRequest>("{\"clientId\":\"i\",\"invoiceNo\":\"1\",\"amount\":\"1.000\",\"printedAt\":\"x\",\"kind\":\"text\"}", DeskApiClient.Json)!;
        Assert.Equal<string?>(null, old.Code, "no code");
        Assert.True(!JsonSerializer.Serialize(old, DeskApiClient.Json).Contains("code\""), "sent without a code field");
    }
}
