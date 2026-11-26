using System.Text.Json;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

public static class ApiClientTests
{
    const string Base = "https://test.local/api/desk";

    public static async Task TestPair()
    {
        var h = new FakeHandler { Respond = (_, _) => FakeHandler.Json(200, "{\"token\":\"ab\",\"shopName\":\"جمرة\",\"stampMode\":\"spend\",\"codeValidityDays\":2,\"qrEnabled\":true}") };
        var api = new DeskApiClient(new HttpClient(h), Base);
        var r = await api.PairAsync(new PairRequest("AB12CD", "كاشير 1", "0.1.0"));
        Assert.True(r.Ok, "ok");
        Assert.Equal("جمرة", r.Value!.ShopName, "shop");
        Assert.Equal(Base + "/pair", h.Calls[0].req.RequestUri!.ToString(), "url");
        Assert.True(h.Calls[0].req.Headers.Authorization == null, "pair sends no token");
        using var doc = JsonDocument.Parse(h.Calls[0].body);
        Assert.Equal("AB12CD", doc.RootElement.GetProperty("code").GetString(), "camelCase body");
        Assert.Equal("كاشير 1", doc.RootElement.GetProperty("deviceName").GetString(), "deviceName");
    }

    public static async Task TestReceiptSendsBearerAndShape()
    {
        var h = new FakeHandler { Respond = (_, _) => FakeHandler.Json(200, "{\"code\":\"K7Q2M9XP\",\"claimUrl\":\"https://daymanjo.com/c/t/s/K7Q2M9XP\",\"expiresAt\":\"2026-11-21T00:00:00Z\",\"already\":false}") };
        var api = new DeskApiClient(new HttpClient(h), Base);
        var r = await api.ReceiptAsync("tok", new ReceiptRequest("id-1", "10432", "12.500", "2026-11-19T18:02:11+03:00", "text"));
        Assert.True(r.Ok, "ok");
        Assert.Equal("K7Q2M9XP", r.Value!.Code, "code");
        Assert.Equal("Bearer tok", h.Calls[0].req.Headers.Authorization!.ToString(), "bearer");
        using var doc = JsonDocument.Parse(h.Calls[0].body);
        foreach (var p in new[] { "clientId", "invoiceNo", "amount", "printedAt", "kind" })
            Assert.True(doc.RootElement.TryGetProperty(p, out _), "field " + p);
        Assert.Equal("12.500", doc.RootElement.GetProperty("amount").GetString(), "amount is a string");
    }

    public static async Task TestErrorBody()
    {
        var h = new FakeHandler { Respond = (_, _) => FakeHandler.Json(409, "{\"error\":\"INVOICE_USED\",\"message\":\"مستخدمة\"}") };
        var r = await new DeskApiClient(new HttpClient(h), Base).ReceiptAsync("t", new ReceiptRequest("i", "1", "1.000", "x", "text"));
        Assert.Equal(409, r.Status, "status");
        Assert.Equal("INVOICE_USED", r.Error, "code");
        Assert.True(!r.Transient && !r.Ok, "permanent");
    }

    public static async Task TestRateLimited()
    {
        var h = new FakeHandler { Respond = (_, _) => FakeHandler.Json(429, "{\"error\":\"RATE_LIMITED\",\"message\":\"x\",\"retryAfterSeconds\":30}") };
        var r = await new DeskApiClient(new HttpClient(h), Base).HeartbeatAsync("t", new HeartbeatRequest("0.1.0", "text", 0, 1));
        Assert.True(r.Transient, "429 is transient");
        Assert.Equal<int?>(30, r.RetryAfterSeconds, "retry after");
    }

    public static async Task TestOfflineAndUnauthorized()
    {
        var off = new FakeHandler { Respond = (_, _) => FakeHandler.Offline() };
        var r = await new DeskApiClient(new HttpClient(off), Base).ReportAsync("t", new ReportRequest("info", "other", "m", "0.1.0"));
        Assert.Equal(0, r.Status, "not reached");
        Assert.True(r.Transient, "transient");

        var revoked = new FakeHandler { Respond = (_, _) => FakeHandler.Json(401, "{\"error\":\"REVOKED\",\"message\":\"x\"}") };
        var r2 = await new DeskApiClient(new HttpClient(revoked), Base).ReportAsync("t", new ReportRequest("info", "other", "m", "0.1.0"));
        Assert.True(r2.Unauthorized && !r2.Transient, "401");

        var nocontent = new FakeHandler { Respond = (_, _) => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent) };
        var r3 = await new DeskApiClient(new HttpClient(nocontent), Base).ReportAsync("t", new ReportRequest("info", "sample", "m", "0.1.0", "AAAA"));
        Assert.True(r3.Ok, "204 ok");
        Assert.True(nocontent.Calls[0].body.Contains("\"sample\":\"AAAA\""), "sample sent");
        var r4 = await new DeskApiClient(new HttpClient(nocontent), Base).ReportAsync("t", new ReportRequest("info", "other", "m", "0.1.0"));
        Assert.True(!nocontent.Calls[1].body.Contains("sample"), "no sample field when null");
    }
}

public static class QueueTests
{
    public static async Task TestFlushStopsAtTransientAndPersists()
    {
        var path = Path.Combine(Tmp.Dir(), "queue.json");
        var q = new OfflineQueue(path);
        q.Enqueue("receipt", new { n = 1 });
        q.Enqueue("receipt", new { n = 2 });
        q.Enqueue("report", new { n = 3 });
        int calls = 0;
        int sent = await q.FlushAsync(_ => Task.FromResult(++calls == 2 ? SendOutcome.Transient : SendOutcome.Sent));
        Assert.Equal(1, sent, "sent");
        Assert.Equal(2, q.Count, "left");
        var again = new OfflineQueue(path);
        Assert.Equal(2, again.Count, "persisted");
        Assert.Equal(1, again.Snapshot()[0].Attempts, "attempt counted");
        sent = await again.FlushAsync(i => Task.FromResult(i.Type == "receipt" ? SendOutcome.Rejected : SendOutcome.Sent));
        Assert.Equal(1, sent, "rejected item dropped, report sent");
        Assert.Equal(0, again.Count, "empty");
    }

    public static async Task TestOldItemsDropped()
    {
        var now = DateTimeOffset.Parse("2026-11-19T10:00:00+03:00");
        var path = Path.Combine(Tmp.Dir(), "queue.json");
        var q = new OfflineQueue(path, () => now);
        q.Enqueue("receipt", new { n = 1 });
        now = now.AddDays(8);
        int sent = await q.FlushAsync(_ => Task.FromResult(SendOutcome.Sent));
        Assert.Equal(0, sent, "expired item not sent");
        Assert.Equal(0, q.Count, "dropped");
    }

    public static void TestCorruptQueueStartsEmpty()
    {
        var path = Path.Combine(Tmp.Dir(), "queue.json");
        File.WriteAllText(path, "{not json");
        Assert.Equal(0, new OfflineQueue(path).Count, "empty");
    }
}

public static class AgentTests
{
    const string Claim = "https://daymanjo.com/c/shop/s/K7Q2M9XP";

    sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly List<byte[]> Printed = new();
        public readonly ConfigStore Cfg;
        public readonly OfflineQueue Queue;
        public readonly DeskAgent Agent;
        public long Seq = 1;

        public Rig(bool paired = true, bool confirmed = true)
        {
            var dir = Tmp.Dir();
            Cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            if (paired) Cfg.Update(c => { c.DeviceToken = "tok"; c.SamplesSent = DeskAgent.FirstSamples; c.FieldsConfirmed = confirmed; });
            Queue = new OfflineQueue(Path.Combine(dir, "queue.json"));
            Agent = new DeskAgent(Cfg, new DeskApiClient(new HttpClient(Http), "https://t/api/desk"), Queue,
                new RollingLog(Path.Combine(dir, "logs")), (seq, b) => { if (seq != Seq) return false; Printed.Add(b); return true; }, "0.1.0");
        }

        public int CallsTo(string path) => Http.Calls.Count(c => c.req.RequestUri!.AbsolutePath.EndsWith(path));
    }

    static HttpResponseMessage ReceiptOk(HttpRequestMessage r, string _) =>
        r.RequestUri!.AbsolutePath.EndsWith("/receipt")
            ? FakeHandler.Json(200, "{\"code\":\"K7Q2M9XP\",\"claimUrl\":\"" + Claim + "\",\"expiresAt\":\"2026-11-21T00:00:00Z\",\"already\":false}")
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);

    public static async Task TestRegisteredReceiptPrintsQr()
    {
        var rig = new Rig();
        rig.Http.Respond = ReceiptOk;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp864(Receipts.Arabic), 12, 1);
        Assert.True(rec.QrPrinted, "qr printed: " + rec.Status);
        Assert.Equal(1, rig.Printed.Count, "one slip");
        Assert.True(Convert.ToHexString(rig.Printed[0]).Contains(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(Claim))), "slip carries claim url");
        // WO-107: the caption carries the receipt's points (6.670 JOD), with no invoice line.
        Assert.True(Convert.ToHexString(rig.Printed[0]).Contains(Convert.ToHexString(ArabicPrintEncoder.Encode(QrReceipt.CaptionStart + "6 نقاط", 864))), "slip carries the points");
        var body = rig.Http.Calls.First(c => c.req.RequestUri!.AbsolutePath.EndsWith("/receipt")).body;
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("10432", doc.RootElement.GetProperty("invoiceNo").GetString(), "invoice");
        Assert.Equal("6.670", doc.RootElement.GetProperty("amount").GetString(), "amount");
        Assert.Equal("text", doc.RootElement.GetProperty("kind").GetString(), "kind");
        Assert.Equal<int?>(864, rig.Cfg.Current.CodePage, "learned code page");
    }

    public static async Task TestOfflineQueuesWithoutQr()
    {
        var rig = new Rig();
        rig.Http.Respond = (_, _) => FakeHandler.Offline();
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Printed.Count == 0, "no qr offline");
        Assert.True(rig.Queue.Snapshot().Any(i => i.Type == "receipt"), "receipt queued");
        // back online: the heartbeat flushes the queue, still no QR
        rig.Http.Respond = (r, b) => r.RequestUri!.AbsolutePath.EndsWith("/heartbeat")
            ? FakeHandler.Json(200, "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":3}")
            : ReceiptOk(r, b);
        await rig.Agent.HeartbeatAsync();
        Assert.Equal(0, rig.Queue.Count, "queue flushed");
        Assert.Equal(0, rig.Printed.Count, "late receipt gets no qr");
        Assert.Equal(3, rig.Cfg.Current.CodeValidityDays, "settings from heartbeat");
    }

    public static async Task TestQrDisabledByOwner()
    {
        var rig = new Rig();
        rig.Cfg.Update(c => c.QrEnabled = false);
        rig.Http.Respond = ReceiptOk;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.Equal(1, rig.CallsTo("/receipt"), "still registered");
        Assert.Equal(0, rig.Printed.Count, "no qr");
    }

    public static async Task TestInvoiceUsedNoQrNoQueue()
    {
        var rig = new Rig();
        rig.Http.Respond = (r, _) => FakeHandler.Json(409, "{\"error\":\"INVOICE_USED\",\"message\":\"x\"}");
        await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.Equal(0, rig.Printed.Count, "no qr");
        Assert.Equal(0, rig.Queue.Count, "not queued");
    }

    // WO-88: the owner voided this invoice's code: no QR, not queued, and said so.
    public static async Task TestInvoiceVoidedNoQrNoQueue()
    {
        var rig = new Rig();
        rig.Http.Respond = (r, _) => FakeHandler.Json(409, "{\"error\":\"INVOICE_VOIDED\",\"message\":\"x\"}");
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.Equal(0, rig.Printed.Count, "no qr");
        Assert.Equal(0, rig.Queue.Count, "not queued");
        Assert.Equal("ألغى صاحب المحل كودها: بلا QR", rec.Status, "status");
    }

    public static async Task TestRevokedUnpairs()
    {
        var rig = new Rig();
        rig.Http.Respond = (_, _) => FakeHandler.Json(401, "{\"error\":\"REVOKED\",\"message\":\"x\"}");
        await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.True(!rig.Cfg.Current.Paired, "token cleared");
        Assert.Equal(0, rig.Printed.Count, "no qr");
    }

    public static async Task TestUnpairedMakesNoCalls()
    {
        var rig = new Rig(paired: false);
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.Equal(0, rig.Http.Calls.Count, "no network");
        Assert.Equal("10432", rec.InvoiceNo, "still read locally");
    }

    public static async Task TestNextJobAlreadyForwardedSkipsQr()
    {
        var rig = new Rig();
        rig.Http.Respond = (r, b) => { rig.Seq = 2; return ReceiptOk(r, b); }; // another receipt printed while waiting
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Printed.Count == 0, "qr would land under the wrong receipt");
    }

    public static async Task TestImageReceiptReportsWithSample()
    {
        var rig = new Rig();
        rig.Http.Respond = ReceiptOk;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(0, rig.CallsTo("/receipt"), "no receipt call without an amount");
        var rep = rig.Http.Calls.Single(c => c.req.RequestUri!.AbsolutePath.EndsWith("/report"));
        Assert.True(rep.body.Contains("\"sample\":"), "sample attached");
        Assert.True(rep.body.Length < 70 * 1024, "sample capped");
        Assert.Equal(0, rig.Printed.Count, "no qr");
    }

    public static async Task TestParseFailureAndSlowForwardReported()
    {
        var rig = new Rig();
        rig.Http.Respond = ReceiptOk;
        var job = Receipts.Ascii(new[] { "JAMRA RESTAURANT", "Invoice No: 5521", "2x Shawarma", "1x Pepsi", "Thank you" }); // a receipt with no total
        await rig.Agent.ProcessJobAsync(job, 450, 1);
        var kinds = rig.Http.Calls.Select(c => JsonDocument.Parse(c.body).RootElement.GetProperty("kind").GetString()).ToList();
        Assert.True(kinds.Contains("slow_forward"), "slow forward reported");
        Assert.True(kinds.Contains("parse_failed"), "parse failure reported");
        // rate limit: the same failure again within 10 minutes is not sent again
        int before = rig.Http.Calls.Count;
        await rig.Agent.ProcessJobAsync(job, 450, 1);
        Assert.Equal(before, rig.Http.Calls.Count, "rate limited");
    }

    public static async Task TestFirstJobsSendSamples()
    {
        var rig = new Rig();
        rig.Cfg.Update(c => c.SamplesSent = 0);
        rig.Http.Respond = ReceiptOk;
        await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        int samples = rig.Http.Calls.Count(c => c.body.Contains("\"kind\":\"sample\""));
        Assert.Equal(2, samples, "first jobs sampled");
        Assert.Equal(2, rig.Cfg.Current.SamplesSent, "counted");
    }

    public static async Task TestPairStoresToken()
    {
        var rig = new Rig(paired: false);
        rig.Http.Respond = (_, _) => FakeHandler.Json(200, "{\"token\":\"" + new string('a', 64) + "\",\"shopName\":\"جمرة\",\"stampMode\":\"spend\",\"codeValidityDays\":2,\"qrEnabled\":true}");
        var r = await rig.Agent.PairAsync(" ab12cd ", "كاشير 1");
        Assert.True(r.Ok && rig.Cfg.Current.Paired, "paired");
        Assert.Equal("جمرة", rig.Cfg.Current.ShopName, "shop");
        Assert.True(rig.Http.Calls[0].body.Contains("\"code\":\"AB12CD\""), "code normalised");

        rig.Http.Respond = (_, _) => FakeHandler.Json(410, "{\"error\":\"CODE_EXPIRED\",\"message\":\"x\"}");
        var r2 = await rig.Agent.PairAsync("ZZZZZZ", "x");
        Assert.True(DeskAgent.PairError(r2).Contains("صلاحية"), "arabic error");
    }
}

public static class SetupTests
{
    static ConfigStore Store() => new(Path.Combine(Tmp.Dir(), "config.json"));

    public static void TestInstallMovesPrinterLast()
    {
        var sh = new FakeShell();
        var store = Store();
        bool listening = false;
        new PrinterSetup(sh, null).Install(store, new[] { "POS-80" }, _ => listening = true);
        Assert.True(listening, "listener started");
        Assert.Equal("DAYMAN_9100", sh.Port, "printer on dayman port");
        Assert.True(store.Current.Active, "active");
        Assert.Equal("USB001", store.Current.OriginalPort, "original saved");
        Assert.Equal("POS-80 Driver", store.Current.OriginalDriver, "driver saved");
        Assert.True(sh.Scripts.Any(s => s == PrinterSetup.AddQueueScript(DeskConfig.RawQueueFor("POS-80"), "USB001")), "raw queue on original port");
        Assert.True(sh.Scripts.Any(s => s == PrinterSetup.AddTcpPortScript("DAYMAN_9100", 9100)), "tcp port");
        Assert.True(sh.Scripts.Last() == PrinterSetup.SetPortScript("POS-80", "DAYMAN_9100"), "switch is the last step");
    }

    public static void TestInstallFailureRollsBack()
    {
        var sh = new FakeShell();
        sh.Override = s => s.Contains("Win32_TCPIPPrinterPort'") ? new ShellResult(1, "", "access denied") : null;
        var store = Store();
        bool threw = false;
        try { new PrinterSetup(sh, null).Install(store, new[] { "POS-80" }, _ => true); }
        catch (SetupException ex) { threw = true; Assert.True(ex.Arabic.Contains("9100"), "arabic message"); }
        Assert.True(threw, "threw");
        Assert.Equal("USB001", sh.Port, "printer untouched");
        Assert.True(!store.Current.Active, "inactive");
        Assert.True(sh.Scripts.Contains(PrinterSetup.RemoveQueueScript(DeskConfig.RawQueueFor("POS-80"))), "raw queue removed");
    }

    public static void TestListenerBusyRollsBack()
    {
        var sh = new FakeShell();
        var store = Store();
        bool threw = false;
        try { new PrinterSetup(sh, null).Install(store, new[] { "POS-80" }, _ => false); } catch (SetupException) { threw = true; }
        Assert.True(threw, "threw");
        Assert.Equal("USB001", sh.Port, "printer untouched");
    }

    public static void TestUninstallRestores()
    {
        var sh = new FakeShell();
        var store = Store();
        var setup = new PrinterSetup(sh, null);
        setup.Install(store, new[] { "POS-80" }, _ => true);
        setup.Uninstall(store);
        Assert.Equal("USB001", sh.Port, "restored");
        Assert.True(!store.Current.Active, "inactive");
        setup.Uninstall(store); // twice is fine
        Assert.Equal("USB001", sh.Port, "still restored");
    }

    public static void TestReinstallWhenAlreadyOnDaymanPort()
    {
        var sh = new FakeShell();
        var store = Store();
        var setup = new PrinterSetup(sh, null);
        setup.Install(store, new[] { "POS-80" }, _ => true);
        setup.Install(store, new[] { "POS-80" }, _ => true);
        Assert.Equal("USB001", store.Current.OriginalPort, "original port not overwritten by DAYMAN_9100");
    }

    public static void TestWatchdog()
    {
        var sh = new FakeShell();
        var store = Store();
        var setup = new PrinterSetup(sh, null);
        var log = new RollingLog(Tmp.Dir());
        setup.Install(store, new[] { "POS-80" }, _ => true);
        Assert.True(!Watchdog.Run(store.Current, setup, true, log), "listening: nothing to do");
        Assert.Equal("DAYMAN_9100", sh.Port, "still intercepted");
        Assert.True(Watchdog.Run(store.Current, setup, false, log), "down: restore");
        Assert.Equal("USB001", sh.Port, "restored");
        Assert.True(!Watchdog.Run(store.Current, setup, false, log), "already restored");
    }

    public static void TestUnreadablePortIsNotARestore()
    {
        var sh = new FakeShell();
        var store = Store();
        var setup = new PrinterSetup(sh, null);
        setup.Install(store, new[] { "POS-80" }, _ => true);
        sh.Override = s => s.EndsWith("$p[0].PortName") ? new ShellResult(1, "", "spooler down") : null;
        bool threw = false;
        try { setup.Uninstall(store); } catch (SetupException) { threw = true; }
        Assert.True(threw, "uninstall refuses to claim success");
        Assert.True(store.Current.Active, "still active");
        Assert.True(!sh.Scripts.Any(x => x.Contains(".Delete()") && x.Contains("Win32_Printer |")), "raw queue kept while the printer may still point at us");
    }

    public static void TestRestoreNotConfirmed()
    {
        var sh = new FakeShell();
        var store = Store();
        var setup = new PrinterSetup(sh, null);
        setup.Install(store, new[] { "POS-80" }, _ => true);
        // The port change "succeeds" but the port does not change
        sh.Override = s => s.EndsWith("[void]$p[0].Put()") ? new ShellResult(0, "", "") : null;
        bool threw = false;
        try { setup.Restore(store.Current); } catch (SetupException) { threw = true; }
        Assert.True(threw, "unconfirmed restore throws");
    }

    public static void TestTaskScripts()
    {
        var w = ScheduledTasks.WatchdogScript(@"C:\Program Files\DaymanDesk\DaymanDesk.exe");
        foreach (var part in new[] { @"schtasks.exe /Create /F /TN '\DaymanDesk\Watchdog' /XML", "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>",
                     "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
                     "<Interval>PT1M</Interval>", "<UserId>S-1-5-18</UserId>", "<RunLevel>HighestAvailable</RunLevel>", "<Arguments>--watchdog</Arguments>",
                     @"<Command>C:\Program Files\DaymanDesk\DaymanDesk.exe</Command>", "[Text.Encoding]::Unicode" })
            Assert.True(w.Contains(part), "watchdog: " + part);
        var t = ScheduledTasks.TrayScript(@"C:\x\DaymanDesk.exe", @"PC\ali");
        foreach (var part in new[] { @"<LogonTrigger><UserId>PC\ali</UserId>", "<LogonType>InteractiveToken</LogonType>", "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "<Arguments>--tray</Arguments>" })
            Assert.True(t.Contains(part), "tray: " + part);
    }

    public static void TestQuoting()
    {
        Assert.Equal("'Ali''s POS'", PrinterSetup.Q("Ali's POS"), "single quote");
        Assert.Equal("'a’’b'", PrinterSetup.Q("a’b"), "curly quote");
    }

    public static void TestParsePrinters()
    {
        // WO-95: ListScript prints tab-separated lines (PowerShell 2.0 has no ConvertTo-Json).
        var one = PrinterSetup.ParsePrinters("طابعة\tUSB001\td\t\t0\t0\r\n");
        Assert.Equal(1, one.Count, "single line");
        Assert.Equal("طابعة", one[0].Name, "arabic name");
        var tcp = PrinterSetup.ParsePrinters("Bar\tIP_1\tEpson\t192.168.1.50\t9100\t1")[0];
        Assert.True(tcp.RawTcp && tcp.Host == "192.168.1.50" && tcp.PortNumber == 9100, "raw tcp port");
        Assert.Equal(0, PrinterSetup.ParsePrinters("").Count, "empty");
    }
}

public static class StorageTests
{
    public static void TestSamplesKeepLast()
    {
        var store = new SampleStore(Tmp.Dir(), keep: 3);
        var t = DateTimeOffset.Parse("2026-11-19T10:00:00+03:00");
        for (int i = 0; i < 5; i++) store.Save(new byte[] { (byte)i }, t.AddSeconds(i));
        Assert.Equal(3, store.Count, "pruned");
        var zip = Path.Combine(Tmp.Dir(), "s.zip");
        store.ExportZip(zip);
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Equal(3, z.Entries.Count, "zipped");
    }

    public static void TestLogRolls()
    {
        var dir = Tmp.Dir();
        var log = new RollingLog(dir, maxBytes: 200, files: 3);
        for (int i = 0; i < 50; i++) log.Info("line " + i + " " + new string('x', 40));
        var files = log.Files().ToList();
        Assert.Equal(3, files.Count, "file count capped");
        Assert.True(files.All(f => new FileInfo(f).Length <= 300), "sizes capped");
    }

    public static void TestConfigBackup()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        var store = new ConfigStore(path);
        store.Update(c => c.Printers.Add(new PrinterRoute { PrinterName = "POS-80", OriginalPort = "USB001" }));
        store.Update(c => c.CodePage = 864); // a second save: config.json.bak exists
        File.WriteAllText(path, "{broken");
        var again = new ConfigStore(path);
        Assert.True(again.LoadedFromBackup, "backup used");
        Assert.Equal("POS-80", again.Current.Printers[0].PrinterName, "printer from backup");
        Assert.Equal("POS-80", again.Current.PrinterName, "v1.0 mirror for an old watchdog");
    }

    public static void TestServerUrl()
    {
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        store.Update(c => c.DeviceToken = "tok");
        Assert.True(!store.SetServerUrl("http://daymanjo.com/api/desk"), "http refused");
        Assert.True(!store.SetServerUrl("daymanjo.com/api/desk"), "relative refused");
        Assert.True(!store.SetServerUrl("https://u:p@daymanjo.com/api/desk"), "userinfo refused");
        Assert.True(store.SetServerUrl("https://daymanjo.com/api/desk/"), "same host ok");
        Assert.True(store.Current.Paired, "same host keeps the token");
        Assert.True(store.SetServerUrl("https://staging.daymanjo.com/api/desk"), "other host ok");
        Assert.True(!store.Current.Paired, "other host clears the token");
        Assert.Equal("https://staging.daymanjo.com/api/desk", store.Current.ServerUrl, "saved");
    }

    public static void TestConfigRoundTrip()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        new ConfigStore(path).Update(c => { c.Printers.Add(new PrinterRoute { PrinterName = "طابعة" }); c.CodePage = 864; });
        var again = new ConfigStore(path).Current;
        Assert.Equal("طابعة", again.Printers[0].PrinterName, "printer");
        Assert.Equal<int?>(864, again.CodePage, "code page");
        Assert.Equal(DeskApiClient.DefaultBaseUrl, again.ServerUrl, "default url");
    }
}
