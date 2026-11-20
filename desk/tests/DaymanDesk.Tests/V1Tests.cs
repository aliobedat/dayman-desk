using System.Text.Json;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

/// <summary>WO-86: first-receipt confirmation, token at rest, update check, status counters.</summary>
public static class ConfirmationTests
{
    const string Claim = "https://daymanjo.com/c/shop/s/K7Q2M9XP";

    sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly List<byte[]> Printed = new();
        public readonly ConfigStore Cfg;
        public readonly DeskAgent Agent;
        public readonly List<FieldConfirmation> Asked = new();
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T10:00:00+03:00");

        public Rig()
        {
            var dir = Tmp.Dir();
            Cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            Cfg.Update(c => { c.DeviceToken = "tok"; c.SamplesSent = DeskAgent.FirstSamples; });
            Agent = new DeskAgent(Cfg, new DeskApiClient(new HttpClient(Http), "https://t/api/desk"),
                new OfflineQueue(Path.Combine(dir, "queue.json")), new RollingLog(Path.Combine(dir, "logs")),
                (_, b) => { Printed.Add(b); return true; }, "1.0.0", () => Now);
            Agent.ConfirmationNeeded += p => Asked.Add(p);
            Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/receipt")
                ? FakeHandler.Json(200, "{\"code\":\"K7Q2M9XP\",\"claimUrl\":\"" + Claim + "\",\"expiresAt\":\"x\",\"already\":false}")
                : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }

        public int Receipts => Http.Calls.Count(c => c.req.RequestUri!.AbsolutePath.EndsWith("/receipt"));
        public string LastAmount => JsonDocument.Parse(Http.Calls.Last(c => c.req.RequestUri!.AbsolutePath.EndsWith("/receipt")).body)
            .RootElement.GetProperty("amount").GetString()!;
        public string LastInvoice => JsonDocument.Parse(Http.Calls.Last(c => c.req.RequestUri!.AbsolutePath.EndsWith("/receipt")).body)
            .RootElement.GetProperty("invoiceNo").GetString()!;
    }

    public static async Task TestFirstReceiptAsksAndDoesNotRegister()
    {
        var rig = new Rig();
        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.Equal(0, rig.Receipts, "nothing registered before the owner confirms");
        Assert.Equal(1, rig.Asked.Count, "asked once");
        Assert.True(!rec.QrPrinted && rig.Printed.Count == 0, "no qr");
        var p = rig.Asked[0];
        Assert.Equal("10432", p.InvoiceNo, "detected invoice");
        Assert.Equal<decimal?>(6.670m, p.Total, "detected total");
        Assert.True(p.Lines[p.InvoiceLine].Contains("10432"), "invoice line highlighted");
        Assert.True(p.Lines[p.TotalLine].Contains("6.670"), "total line highlighted");

        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.Equal(1, rig.Asked.Count, "a second receipt while pending does not ask again");
        Assert.Equal(0, rig.Receipts, "still not registered");
        Assert.True(rig.Agent.PendingConfirmation != null, "pending kept for the status window");
    }

    public static async Task TestConfirmAsDetectedThenRegisters()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        var p = rig.Asked[0];
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, p.InvoiceLine, p.TotalLine), "confirmed");
        Assert.True(rig.Cfg.Current.FieldsConfirmed, "stored");
        Assert.True(rig.Agent.PendingConfirmation == null, "pending cleared");
        var again = new ConfigStore(rig.Cfg.Path).Current;
        Assert.True(again.FieldsConfirmed && again.LearnedTotal != null && again.LearnedInvoice != null, "persisted per device");

        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.True(rec.QrPrinted, "registered and qr: " + rec.Status);
        Assert.Equal("6.670", rig.LastAmount, "amount");
        Assert.Equal(ReceiptExtractor.LearnedConfidence, rec.Confidence, "learned confidence");
    }

    public static async Task TestCorrectedTotalLineIsUsedAfterwards()
    {
        // The owner says the total is «المجموع» (before tax in the heuristic's view): later receipts must follow him.
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        var p = rig.Asked[0];
        int sum = p.Lines.ToList().FindIndex(l => l.StartsWith("المجموع"));
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, p.InvoiceLine, sum), "confirmed");

        var next = DaymanDesk.Tests.Receipts.Arabic.Select(l => l
            .Replace("10432", "10433").Replace("المجموع: 5.750", "المجموع: 8.250").Replace("الإجمالي: 6.670", "الإجمالي: 9.570")).ToArray();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(next), 5, 1);
        Assert.Equal("8.250", rig.LastAmount, "learned line wins over the heuristic");
        Assert.Equal("10433", rig.LastInvoice, "invoice from the learned line");
    }

    public static async Task TestLearnedLabelWorksInVisualOrderAndCp864()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp864(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        var p = rig.Asked[0];
        Assert.True(p.InvoiceLine >= 0 && p.TotalLine >= 0, "both detected on CP864");
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, p.InvoiceLine, p.TotalLine), "confirmed");
        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp864(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.True(rec.QrPrinted, "cp864 registered: " + rec.Status);
        Assert.Equal("6.670", rig.LastAmount, "amount");
    }

    public static async Task TestConfirmRejectsLinesWithoutValues()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        var p = rig.Asked[0];
        int thanks = p.Lines.Count - 1; // «شكرا لزيارتكم»
        Assert.True(rig.Agent.ConfirmFields(p, thanks, p.TotalLine)!.Contains("رقم الفاتورة"), "invoice line without number refused");
        Assert.True(rig.Agent.ConfirmFields(p, p.InvoiceLine, thanks)!.Contains("المجموع"), "total line without amount refused");
        Assert.True(rig.Agent.ConfirmFields(p, p.TotalLine, p.TotalLine) != null, "same line twice refused");
        Assert.True(rig.Agent.ConfirmFields(p, -1, 999) != null, "out of range refused, no crash");
        Assert.True(!rig.Cfg.Current.FieldsConfirmed, "nothing stored");
    }

    public static void TestValueOnNextLine()
    {
        var lines = new[] { "SHOP", "Invoice No", "77812", "Item 1   2.000", "TOTAL", "2.000 JOD", "Thanks" };
        var inv = ReceiptExtractor.LearnInvoice(lines, 2);
        Assert.True(inv != null && inv.Value.field.ValueOnNextLine && inv.Value.value == "77812", "label from the line above");
        var tot = ReceiptExtractor.LearnTotal(lines, 5);
        Assert.True(tot != null && tot.Value.field.ValueOnNextLine && tot.Value.value == 2.000m, "total label above");
        var later = new[] { "SHOP", "Invoice No", "77813", "Item 1   2.000", "Item 2   1.500", "TOTAL", "3.500 JOD", "Thanks" };
        var e = ReceiptExtractor.Extract(later, inv!.Value.field, tot!.Value.field);
        Assert.Equal("77813", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(3.500m, e.Total, "total");
        Assert.True(e.Confidence >= DeskAgent.MinConfidence, "confident");
    }

    public static void TestLearnedFieldMissingFallsBackToHeuristics()
    {
        var learned = new LearnedField { Label = "something else", Position = 3 };
        var e = ReceiptExtractor.Extract(DaymanDesk.Tests.Receipts.Arabic, learned, learned);
        Assert.Equal("10432", e.InvoiceNo, "heuristic invoice");
        Assert.Equal<decimal?>(6.670m, e.Total, "heuristic total");
    }

    public static void TestLabelKeyIgnoresNumbers()
    {
        Assert.Equal(ReceiptExtractor.LabelKey("الإجمالي: 6.670"), ReceiptExtractor.LabelKey("الاجمالي : 120.500"), "same label");
        Assert.Equal("invoice no", ReceiptExtractor.LabelKey("Invoice No: #INV-2291"), "latin, case folded");
    }

    public static void TestLabelKeyIgnoresTimeAndAmPm()
    {
        Assert.Equal("invoice no", ReceiptExtractor.LabelKey("Invoice No: 10432 10:30 PM"), "pm dropped");
        Assert.Equal("invoice no", ReceiptExtractor.LabelKey("Invoice No: 10433 09:05:12 a.m."), "a.m. dropped");
        Assert.Equal(ReceiptExtractor.LabelKey("رقم الفاتورة: 10432 10:30 م"), ReceiptExtractor.LabelKey("رقم الفاتورة: 10433 9:15 ص"), "ص and م dropped");
        Assert.Equal("رقم الفاتوره", ReceiptExtractor.LabelKey("رقم الفاتورة: 10432 10:30 م"), "arabic label kept");
    }

    public static void TestLearnedLabelMatchesAcrossAmPm()
    {
        var morning = new[] { "SHOP", "Invoice No: 10432 09:10 AM", "Item 1   2.000", "TOTAL 2.000", "Thanks" };
        var inv = ReceiptExtractor.LearnInvoice(morning, 1, "10432");
        Assert.True(inv != null, "learned");
        var night = new[] { "SHOP", "Invoice No: 10433 10:30 PM", "Item 1   2.000", "TOTAL 2.000", "Thanks" };
        var e = ReceiptExtractor.Extract(night, inv!.Value.field, null);
        Assert.Equal("10433", e.InvoiceNo, "learned label matched at night");
        Assert.Equal(ReceiptExtractor.LearnedConfidence, e.InvoiceConfidence, "learned confidence");
        // A label saved before this fix still carries "am" and must still match.
        var old = new LearnedField { Label = "invoice no am", Position = 1 };
        Assert.Equal("10433", ReceiptExtractor.Extract(night, old, null).InvoiceNo, "old saved label matches");
    }

    public static void TestUnmatchedLearnedFieldCapsHeuristicsBelowMin()
    {
        var learned = new LearnedField { Label = "something else", Position = 3 };
        var e = ReceiptExtractor.Extract(DaymanDesk.Tests.Receipts.Arabic, learned, learned);
        Assert.True(e.InvoiceConfidence < DeskAgent.MinConfidence, "invoice capped: " + e.InvoiceConfidence);
        Assert.True(e.TotalConfidence < DeskAgent.MinConfidence, "total capped: " + e.TotalConfidence);
        var free = ReceiptExtractor.Extract(DaymanDesk.Tests.Receipts.Arabic);
        Assert.True(free.Confidence >= DeskAgent.MinConfidence, "no learned field: heuristics uncapped");
    }

    public static async Task TestReconfirmAndRepairAskAgain()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        rig.Agent.ConfirmFields(rig.Asked[0], rig.Asked[0].InvoiceLine, rig.Asked[0].TotalLine);
        rig.Agent.RequestReconfirm();
        Assert.True(!rig.Cfg.Current.FieldsConfirmed && rig.Cfg.Current.LearnedTotal == null, "forgotten");
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.Equal(2, rig.Asked.Count, "asked again");

        rig.Agent.ConfirmFields(rig.Asked[1], rig.Asked[1].InvoiceLine, rig.Asked[1].TotalLine);
        rig.Http.Respond = (_, _) => FakeHandler.Json(200, "{\"token\":\"" + new string('b', 64) + "\",\"shopName\":\"x\",\"stampMode\":\"spend\",\"codeValidityDays\":2,\"qrEnabled\":true}");
        await rig.Agent.PairAsync("AB12CD", "كاشير 1");
        Assert.True(!rig.Cfg.Current.FieldsConfirmed, "a new pairing asks again");
    }

    public static async Task TestWeakFieldIsNotRegistered()
    {
        // Invoice value only on the next line under a bare «فاتورة» label: 0.8 x 0.8 = 0.64 < 0.75.
        var rig = new Rig();
        rig.Cfg.Update(c => c.FieldsConfirmed = true);
        var lines = new[] { "مطعم جمرة", "فاتورة", "10432", "صنف واحد  5.000", "الإجمالي: 5.000 دينار", "شكرا لزيارتكم" };
        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(lines), 5, 1);
        Assert.Equal(0, rig.Receipts, "below 0.75: not registered");
        Assert.True(rec.Status.Contains("لم تُقرأ"), "status says why");
        Assert.Equal(Problems.ParseFailed, rig.Cfg.Current.LastProblem, "problem shown");
    }

    public static async Task TestCountersAndDayRollover()
    {
        var rig = new Rig();
        rig.Cfg.Update(c => c.FieldsConfirmed = true);
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Raster(), 5, 1);
        var c = rig.Cfg.Current;
        Assert.Equal(2, c.TodayReceipts, "receipts");
        Assert.Equal(1, c.TodayRegistered, "registered");
        Assert.Equal(1, c.TodayQr, "qr");
        Assert.Equal(1, c.TodayNotRegistered, "image not registered");
        rig.Now = rig.Now.AddDays(1);
        rig.Http.Respond = (_, _) => FakeHandler.Json(409, "{\"error\":\"INVOICE_USED\",\"message\":\"x\"}");
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        c = rig.Cfg.Current;
        Assert.Equal(1, c.TodayReceipts, "new day");
        Assert.Equal(0, c.TodayNotRegistered, "a cashier-entered invoice is not a failure");
    }

    public static async Task TestBadBytesNeverThrow()
    {
        var rig = new Rig();
        rig.Cfg.Update(c => c.FieldsConfirmed = true);
        foreach (var job in new[] { Array.Empty<byte>(), new byte[] { 0x1B }, new byte[] { 0x1D, 0x76, 0x30, 0, 0xFF, 0xFF, 0xFF, 0xFF },
                     new byte[] { 0x1D, 0x38, 0x4C, 0xFF, 0xFF, 0xFF, 0x7F }, DaymanDesk.Tests.Receipts.Garbage(1, 20000) })
        {
            var rec = await rig.Agent.ProcessJobAsync(job, 5, 1);
            Assert.True(!rec.QrPrinted && rec.Status.Length > 0, "handled: " + rec.Status);
        }
        Assert.Equal(0, rig.Receipts, "nothing registered from noise");
    }
}

public static class TokenTests
{
    /// <summary>A stand-in for DPAPI on Linux: reversible, and obviously not the plain token.</summary>
    sealed class FakeProtector : ITokenProtector
    {
        public bool Broken;
        public string Protect(string plain) => "S:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plain.Reverse().ToArray())));
        public string? Unprotect(string s) => Broken || !s.StartsWith("S:") ? null
            : new string(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s[2..])).Reverse().ToArray());
    }

    static readonly string Token = new string('c', 30) + "0123456789abcdef0123456789abcdef01";

    public static void TestPlainV0TokenIsMigrated()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        // A v0 file: plain token, and a backup that also holds it.
        var v0 = "{\"printerName\":\"POS-80\",\"deviceToken\":\"" + Token + "\",\"shopName\":\"جمرة\"}";
        File.WriteAllText(path, v0);
        File.WriteAllText(path + ".bak", v0);
        var store = new ConfigStore(path, new FakeProtector());
        Assert.True(store.TokenMigrated, "migrated");
        Assert.Equal(Token, store.Current.DeviceToken, "token usable in memory");
        Assert.True(!File.ReadAllText(path).Contains(Token), "config.json has no plain token");
        Assert.True(!File.ReadAllText(path + ".bak").Contains(Token), "config.json.bak has no plain token");
        Assert.True(!File.ReadAllText(path).Contains("\"deviceToken\""), "no plain field");
        Assert.True(File.ReadAllText(path).Contains("tokenProtected"), "sealed field");

        var again = new ConfigStore(path, new FakeProtector());
        Assert.True(!again.TokenMigrated && again.Current.Paired, "second start: opened, no migration");
        Assert.Equal("POS-80", again.Current.PrinterName, "rest of the config kept");
    }

    public static void TestUnreadableTokenUnpairs()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var p = new FakeProtector();
        new ConfigStore(path, p).Update(c => c.DeviceToken = Token);
        var store = new ConfigStore(path, new FakeProtector { Broken = true });
        Assert.True(store.TokenUnreadable && !store.Current.Paired, "unreadable means pair again");
        Assert.True(!File.ReadAllText(path).Contains("tokenProtected"), "dead sealed token dropped");
    }

    public static void TestUnpairClearsSealedToken()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var store = new ConfigStore(path, new FakeProtector());
        store.Update(c => c.DeviceToken = Token);
        store.Update(c => c.DeviceToken = null);
        Assert.True(!File.ReadAllText(path).Contains("tokenProtected"), "cleared");
        Assert.True(!new ConfigStore(path, new FakeProtector()).Current.Paired, "unpaired after restart");
    }

    sealed class CountingProtector : ITokenProtector
    {
        public int Calls;
        public bool Fail;
        public string Protect(string plain) { Calls++; if (Fail) throw new InvalidOperationException("dpapi down"); return "S:" + plain.Length; }
        public string? Unprotect(string s) => null;
    }

    public static void TestSealOncePerTokenAndNeverPlainOnFailure()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var p = new CountingProtector();
        var store = new ConfigStore(path, p);
        store.Update(c => c.DeviceToken = Token);
        for (int i = 0; i < 20; i++) store.Update(c => c.TodayReceipts++);
        Assert.Equal(1, p.Calls, "DPAPI called once for one token");

        var path2 = Path.Combine(Tmp.Dir(), "config.json");
        var failing = new ConfigStore(path2, new CountingProtector { Fail = true });
        failing.Update(c => c.DeviceToken = Token);
        Assert.True(failing.SealFailed, "failure flagged");
        Assert.True(failing.Current.Paired, "still paired in memory");
        Assert.True(!File.ReadAllText(path2).Contains(Token), "never written in plain text");
    }

    public static void TestV0TokenKeptWhenSealFails()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var v0 = "{\"printerName\":\"POS-80\",\"deviceToken\":\"" + Token + "\"}";
        File.WriteAllText(path, v0);
        File.WriteAllText(path + ".bak", v0);
        var p = new CountingProtector { Fail = true };
        var store = new ConfigStore(path, p);
        Assert.True(!store.TokenMigrated && store.SealFailed && store.Current.Paired, "not migrated, still paired");
        Assert.Equal(v0, File.ReadAllText(path), "config.json untouched");
        Assert.Equal(v0, File.ReadAllText(path + ".bak"), "config.json.bak untouched");
        store.Update(c => c.TodayReceipts++);
        Assert.True(File.ReadAllText(path).Contains(Token), "a later save keeps the v0 token");
        p.Fail = false;
        store.Update(c => c.TodayReceipts++);
        Assert.True(store.TokenMigrated, "sealed once DPAPI works");
        Assert.True(!File.ReadAllText(path).Contains(Token) && !File.ReadAllText(path + ".bak").Contains(Token), "plain gone from both files");
    }

    public static void TestV0TokenMigrationDeferred()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var v0 = "{\"deviceToken\":\"" + Token + "\"}";
        File.WriteAllText(path, v0);
        var store = new ConfigStore(path, new FakeProtector(), migrateNow: false);
        Assert.True(store.Current.Paired && !store.TokenMigrated, "usable, not migrated");
        Assert.Equal(v0, File.ReadAllText(path), "untouched before MigrateToken");
        store.Update(c => c.TodayReceipts++);
        Assert.True(File.ReadAllText(path).Contains(Token) && !File.ReadAllText(path).Contains("tokenProtected"), "saves keep it plain");
        Assert.True(store.MigrateToken() && store.TokenMigrated, "migrated");
        Assert.True(!File.ReadAllText(path).Contains(Token) && !File.ReadAllText(path + ".bak").Contains(Token), "sealed in both files");
        Assert.Equal(Token, new ConfigStore(path, new FakeProtector()).Current.DeviceToken, "reopened");
    }

    public static void TestDpapiRoundTripOnWindows()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("  (skipped: DPAPI needs Windows; runs in CI)"); return; }
        var d = new DpapiProtector();
        var sealedText = d.Protect(Token);
        Assert.True(!sealedText.Contains(Token), "sealed");
        Assert.Equal(Token, d.Unprotect(sealedText), "opened");
        Assert.Equal<string?>(null, d.Unprotect("bm90IGRwYXBp"), "foreign blob gives null, no throw");
        Assert.Equal<string?>(null, d.Unprotect("%%%"), "bad base64 gives null");
        var path = Path.Combine(Tmp.Dir(), "config.json");
        File.WriteAllText(path, "{\"deviceToken\":\"" + Token + "\"}");
        var store = new ConfigStore(path, d);
        Assert.True(store.TokenMigrated && !File.ReadAllText(path).Contains(Token), "real migration");
        Assert.Equal(Token, new ConfigStore(path, d).Current.DeviceToken, "reopened");
    }
}

public static class UpdateTests
{
    public static void TestSemVer()
    {
        Assert.True(SemVer.IsNewer("1.0.1", "1.0.0"), "patch");
        Assert.True(SemVer.IsNewer("1.10.0", "1.9.9"), "numeric, not text");
        Assert.True(SemVer.IsNewer("v2.0", "1.9.9"), "v prefix and two parts");
        Assert.True(!SemVer.IsNewer("1.0.0", "1.0.0"), "same");
        Assert.True(!SemVer.IsNewer("0.9.0", "1.0.0"), "older");
        Assert.True(SemVer.IsNewer("1.0.0", "1.0.0-beta"), "release after its beta");
        Assert.True(!SemVer.IsNewer("1.0.0-beta", "1.0.0"), "beta before release");
        foreach (var bad in new[] { null, "", "latest", "1", "1.x.0", "1.0.0.0.0", "99999999.0.0" })
            Assert.True(!SemVer.IsNewer(bad, "1.0.0"), "refused: " + bad);
        Assert.True(SemVer.SafeUrl("https://github.com/aliobedat/dayman/releases/tag/desk-latest"), "https ok");
        Assert.True(!SemVer.SafeUrl("http://x.com/a.exe") && !SemVer.SafeUrl("file:///c:/a.exe") && !SemVer.SafeUrl("https://u:p@x.com/")
            && !SemVer.SafeUrl("javascript:alert(1)") && !SemVer.SafeUrl(null), "unsafe refused");
    }

    static (FakeHandler http, ConfigStore cfg, DeskAgent agent, List<string> seen) Rig(string heartbeatJson)
    {
        var dir = Tmp.Dir();
        var cfg = new ConfigStore(Path.Combine(dir, "config.json"));
        cfg.Update(c => c.DeviceToken = "tok");
        var http = new FakeHandler { Respond = (_, _) => FakeHandler.Json(200, heartbeatJson) };
        var agent = new DeskAgent(cfg, new DeskApiClient(new HttpClient(http), "https://t/api/desk"),
            new OfflineQueue(Path.Combine(dir, "q.json")), new RollingLog(Path.Combine(dir, "logs")), (_, _) => true, "1.0.0");
        var seen = new List<string>();
        agent.UpdateAvailable += (v, u) => seen.Add(v + " " + u);
        return (http, cfg, agent, seen);
    }

    public static async Task TestHeartbeatAnnouncesUpdate()
    {
        var r = Rig("{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2,\"latestVersion\":\"1.1.0\",\"downloadUrl\":\"https://github.com/aliobedat/dayman/releases/tag/desk-latest\"}");
        await r.agent.HeartbeatAsync();
        Assert.Equal("1.1.0", r.cfg.Current.UpdateVersion, "stored");
        Assert.Equal(1, r.seen.Count, "raised");
    }

    public static async Task TestHeartbeatWithoutUpdateFields()
    {
        var r = Rig("{\"serverTime\":\"x\",\"qrEnabled\":false,\"codeValidityDays\":2}");
        r.cfg.Update(c => { c.UpdateVersion = "9.9.9"; c.UpdateUrl = "https://x.com"; });
        await r.agent.HeartbeatAsync();
        Assert.Equal<string?>(null, r.cfg.Current.UpdateVersion, "v1 server: no update, old notice cleared");
        Assert.True(!r.cfg.Current.QrEnabled, "v1 fields still read");
        Assert.Equal(0, r.seen.Count, "not raised");
    }

    public static async Task TestUnsafeOrOlderUpdateIgnored()
    {
        var r = Rig("{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2,\"latestVersion\":\"2.0.0\",\"downloadUrl\":\"http://evil.example/a.exe\"}");
        await r.agent.HeartbeatAsync();
        Assert.Equal<string?>(null, r.cfg.Current.UpdateVersion, "http link refused");
        var r2 = Rig("{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2,\"latestVersion\":\"1.0.0\",\"downloadUrl\":\"https://x.com/a\"}");
        await r2.agent.HeartbeatAsync();
        Assert.Equal(0, r2.seen.Count, "same version: nothing");
    }

    public static async Task TestOfflineHeartbeatShowsProblemAndRecovers()
    {
        var r = Rig("{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2}");
        r.http.Respond = (_, _) => FakeHandler.Offline();
        await r.agent.HeartbeatAsync();
        Assert.Equal(Problems.Offline, r.cfg.Current.LastProblem, "offline shown");
        r.http.Respond = (_, _) => FakeHandler.Json(200, "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2}");
        await r.agent.HeartbeatAsync();
        Assert.Equal<string?>(null, r.cfg.Current.LastProblem, "cleared when back");
    }

    public static void TestEveryProblemHasArabicAdvice()
    {
        foreach (var f in typeof(Problems).GetFields().Where(f => f.IsLiteral))
        {
            var text = Problems.Describe((string)f.GetValue(null)!);
            Assert.True(text.Length > 20 && text.Any(TextNormalizer.IsArabicLetter), "advice for " + f.Name);
            Assert.True(!text.Any(ch => ch >= '٠' && ch <= '٩'), "Latin digits only: " + f.Name);
        }
        Assert.Equal("", Problems.Describe(null), "no problem, no text");
    }
}
