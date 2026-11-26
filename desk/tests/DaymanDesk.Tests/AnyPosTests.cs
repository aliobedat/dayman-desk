using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

/// <summary>WO-89: several printers, TCP/IP printers, «وضع الشبكة», kitchen tickets, image receipts, unknown formats.</summary>
public static class AnyPosTests
{
    const string Claim = "https://daymanjo.com/c/shop/s/K7Q2M9XP";

    // ---------- fakes ----------

    sealed class FakeQueue : IRawQueue
    {
        public readonly ConcurrentQueue<(string queue, byte[] data, string doc)> Writes = new();
        public bool Fail;
        public void Write(string queue, byte[] data, string docName)
        {
            if (Fail) throw new IOException("queue paused");
            Writes.Enqueue((queue, data, docName));
        }
        public List<byte[]> To(string queue) => Writes.Where(w => w.queue == queue).Select(w => w.data).ToList();
    }

    /// <summary>A RAW TCP printer on 127.0.0.1: one job per connection; answers <see cref="Reply"/> on connect.</summary>
    sealed class FakePrinter : IDisposable
    {
        readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public readonly ConcurrentQueue<byte[]> Jobs = new();
        public byte[]? Reply;
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

        public FakePrinter() { _l.Start(); _ = Loop(); }

        async Task Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        if (Reply != null) await s.WriteAsync(Reply);
                        var ms = new MemoryStream();
                        var b = new byte[8192];
                        try { int n; while ((n = await s.ReadAsync(b)) > 0) ms.Write(b, 0, n); } catch { }
                        Jobs.Enqueue(ms.ToArray());
                    }
                });
            }
        }

        public void Dispose() => _l.Stop();
    }

    sealed class FakeOcr : IOcr
    {
        public Func<MonoBitmap, IReadOnlyList<string>?> Read = _ => null;
        public readonly List<MonoBitmap> Seen = new();
        public Func<MonoBitmap, string, string?> ReadLine = (_, _) => null;
        public Task<IReadOnlyList<OcrLine>?> ReadAsync(MonoBitmap image)
        {
            Seen.Add(image);
            var r = Read(image);
            return Task.FromResult(r == null ? null : (IReadOnlyList<OcrLine>)r.Select((t, i) => new OcrLine(t, i * 10, i * 10 + 8)).ToList());
        }
        public Task<string?> ReadLineAsync(MonoBitmap crop, string allowedChars) => Task.FromResult(ReadLine(crop, allowedChars));
        public string? LanguageTag { get; set; }
    }

    /// <summary>A shell with three Windows printers: USB, a Standard TCP/IP (RAW) network printer, and COM.</summary>
    sealed class MultiShell : IShell
    {
        public readonly Dictionary<string, string> Ports = new() { ["POS-80"] = "USB001", ["Bar"] = "IP_192.168.1.50", ["Kitchen"] = "COM3" };
        public readonly List<string> Scripts = new();
        public Func<string, ShellResult?> Override = _ => null;

        public ShellResult Run(string s)
        {
            Scripts.Add(s);
            var o = Override(s);
            if (o != null) return o;
            if (s == PrinterSetup.ListScript)
                return new ShellResult(0, string.Join("\n", Ports.Select(p => p.Key == "Bar"
                    ? $"{p.Key}\t{p.Value}\tEPSON TM-T20\t192.168.1.50\t9100\t1"
                    : $"{p.Key}\t{p.Value}\tPOS Driver\t\t0\t0")), "");
            var m = Regex.Match(s, @"\$_\.Name -eq '(.*?)' \}\); if \(\$p\.Count -ne 1\) \{ throw 'printer not found' \}; \$p\[0\]\.PortName$");
            if (m.Success) return new ShellResult(0, Ports[m.Groups[1].Value], "");
            m = Regex.Match(s, @"\$_\.Name -eq '(.*?)' \}\); if .*\$p\[0\]\.PortName = '(.*)'; \[void\]\$p\[0\]\.Put\(\)$");
            if (m.Success) Ports[m.Groups[1].Value] = m.Groups[2].Value;
            return new ShellResult(0, "", "");
        }
    }

    sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly List<byte[]> Printed = new();
        public readonly ConfigStore Cfg;
        public readonly DeskAgent Agent;
        public readonly FakeOcr Ocr = new();
        public readonly List<FieldConfirmation> Asked = new();
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T10:00:00+03:00");
        public Func<long, byte[], bool>? PrintIfCurrent;

        public Rig(bool confirmed = true)
        {
            var dir = Tmp.Dir();
            Cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            Cfg.Update(c => { c.DeviceToken = "tok"; c.SamplesSent = DeskAgent.FirstSamples; c.FieldsConfirmed = confirmed; });
            Agent = new DeskAgent(Cfg, new DeskApiClient(new HttpClient(Http), "https://t/api/desk"),
                new OfflineQueue(Path.Combine(dir, "queue.json")), new RollingLog(Path.Combine(dir, "logs")),
                (seq, b) => { if (PrintIfCurrent != null) return PrintIfCurrent(seq, b); Printed.Add(b); return true; }, "1.1.0", () => Now, Ocr);
            Agent.ConfirmationNeeded += p => Asked.Add(p);
            Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/receipt")
                ? FakeHandler.Json(200, "{\"code\":\"K7Q2M9XP\",\"claimUrl\":\"" + Claim + "\",\"expiresAt\":\"x\",\"already\":false}")
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        public List<string> Calls(string path) => Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith(path)).Select(c => c.body).ToList();
        public List<string> ReportKinds => Calls("/report").Select(b => JsonDocument.Parse(b).RootElement.GetProperty("kind").GetString()!).ToList();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static void Send(int port, byte[] data)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.GetStream().Write(data);
    }

    static async Task<T> Within<T>(Task<T> t, int ms = 10_000)
    {
        if (await Task.WhenAny(t, Task.Delay(ms)) != t) throw new AssertException("timed out");
        return await t;
    }

    static async Task Until(Func<bool> cond, string what, int ms = 10_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new AssertException("timed out: " + what);
            await Task.Delay(20);
        }
    }

    static PrinterRoute Win(string name, int port, string? directHost = null, int directPort = 0) => new()
    {
        PrinterName = name, OriginalPort = "USB001", RawQueueName = DeskConfig.RawQueueFor(name), ListenPort = port,
        DirectHost = directHost, DirectPort = directPort,
    };

    // ---------- 1. several printers: routing ----------

    public static async Task TestEachPrinterHasItsOwnListenerAndQrGoesToIt()
    {
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => throw new AssertException("no fallback"));
        var jobs = new ConcurrentQueue<ForwardedJob>();
        server.JobForwarded += j => jobs.Enqueue(j);
        var a = Win("POS-80", FreePort());
        var b = Win("Bar", FreePort());
        Assert.True(server.Start(a) && server.Start(b), "both listen");
        try
        {
            Send(a.ListenPort, Encoding.ASCII.GetBytes("receipt A ........"));
            await Until(() => jobs.Count == 1, "job A");
            Send(b.ListenPort, Encoding.ASCII.GetBytes("receipt B ........"));
            await Until(() => jobs.Count == 2, "job B");
            var ja = jobs.First(j => j.Route.PrinterName == "POS-80");
            var jb = jobs.First(j => j.Route.PrinterName == "Bar");
            Assert.Equal("receipt A ........", Encoding.ASCII.GetString(q.To(DeskConfig.RawQueueFor("POS-80")).Single()), "A forwarded to A's raw queue");
            Assert.Equal("receipt B ........", Encoding.ASCII.GetString(q.To(DeskConfig.RawQueueFor("Bar")).Single()), "B forwarded to B's raw queue");
            Assert.True(ja.Seq != jb.Seq, "sequences unique across printers");

            // A job on B does not stop A's QR: the slip goes to A's printer.
            Assert.True(server.PrintIfCurrent(ja.Seq, Encoding.ASCII.GetBytes("QR A")), "A still current");
            Assert.Equal(2, q.To(DeskConfig.RawQueueFor("POS-80")).Count, "QR on A");
            Assert.Equal(1, q.To(DeskConfig.RawQueueFor("Bar")).Count, "nothing extra on B");
            // A newer job on A: A's old QR must not print under it.
            Send(a.ListenPort, Encoding.ASCII.GetBytes("receipt A2 ......."));
            await Until(() => jobs.Count == 3, "job A2");
            Assert.True(!server.PrintIfCurrent(ja.Seq, Encoding.ASCII.GetBytes("late QR")), "stale QR refused");
            Assert.True(server.PrintIfCurrent(jb.Seq, Encoding.ASCII.GetBytes("QR B")), "B still current");
            Assert.Equal("QR B", Encoding.ASCII.GetString(q.To(DeskConfig.RawQueueFor("Bar")).Last()), "QR on B");
        }
        finally { server.StopAll(); }
        Assert.True(!server.Running, "stopped");
    }

    public static async Task TestReceiptOnSecondPrinterGetsItsQrThere()
    {
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => { });
        var rig = new Rig();
        rig.PrintIfCurrent = server.PrintIfCurrent;
        var done = new ConcurrentQueue<Task<ReceiptRecord>>();
        server.JobForwarded += j => done.Enqueue(rig.Agent.ProcessJobAsync(j.Data, j.ForwardMs, j.Seq, j.PrinterUnreachable));
        var a = Win("POS-80", FreePort());
        var b = Win("Bar", FreePort());
        server.Start(a); server.Start(b);
        try
        {
            Send(b.ListenPort, Receipts.Cp1256(Receipts.Arabic));
            await Until(() => done.Count == 1, "processed");
            var rec = await Within(done.Single());
            Assert.True(rec.QrPrinted, "qr printed: " + rec.Status);
            Assert.Equal(0, q.To(DeskConfig.RawQueueFor("POS-80")).Count, "nothing on the other printer");
            var onB = q.To(DeskConfig.RawQueueFor("Bar"));
            Assert.Equal(2, onB.Count, "receipt then QR on the same printer");
            Assert.True(Convert.ToHexString(onB[1]).Contains(Convert.ToHexString(Encoding.ASCII.GetBytes(Claim))), "slip carries the claim url");
        }
        finally { server.StopAll(); }
    }

    public static async Task TestRawQueueFailureFallsBack()
    {
        var q = new FakeQueue { Fail = true };
        var failed = new TaskCompletionSource<(PrinterRoute, byte[])>();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (r, d) => failed.TrySetResult((r, d)));
        var a = Win("POS-80", FreePort());
        server.Start(a);
        try
        {
            Send(a.ListenPort, Encoding.ASCII.GetBytes("receipt"));
            var (route, data) = await Within(failed.Task);
            Assert.Equal("POS-80", route.PrinterName, "the failed printer");
            Assert.Equal("receipt", Encoding.ASCII.GetString(data), "the job handed back to print directly");
        }
        finally { server.StopAll(); }
    }

    // ---------- 2. TCP/IP printers ----------

    public static async Task TestTcpIpPrinterGetsTheJobDirectly()
    {
        using var printer = new FakePrinter();
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => { });
        var jobs = new ConcurrentQueue<ForwardedJob>();
        server.JobForwarded += j => jobs.Enqueue(j);
        var r = Win("Bar", FreePort(), "127.0.0.1", printer.Port);
        server.Start(r);
        try
        {
            Send(r.ListenPort, Encoding.ASCII.GetBytes("network receipt ..."));
            await Until(() => jobs.Count == 1 && printer.Jobs.Count == 1, "direct job");
            Assert.Equal("network receipt ...", Encoding.ASCII.GetString(printer.Jobs.Single()), "printer got the bytes");
            Assert.Equal(0, q.Writes.Count, "spooler not used");
            Assert.True(!jobs.Single().PrinterUnreachable, "reachable");
            Assert.True(server.PrintIfCurrent(jobs.Single().Seq, Encoding.ASCII.GetBytes("QR")), "qr");
            await Until(() => printer.Jobs.Count == 2, "qr on the network printer");
        }
        finally { server.StopAll(); }
    }

    public static async Task TestUnreachableTcpIpPrinterQueuesInSpoolerWithoutQr()
    {
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => throw new AssertException("no fallback"));
        var rig = new Rig();
        rig.PrintIfCurrent = server.PrintIfCurrent;
        var done = new ConcurrentQueue<Task<ReceiptRecord>>();
        server.JobForwarded += j => done.Enqueue(rig.Agent.ProcessJobAsync(j.Data, j.ForwardMs, j.Seq, j.PrinterUnreachable));
        var r = Win("Bar", FreePort(), "127.0.0.1", FreePort()); // nothing listens there
        server.Start(r);
        try
        {
            Send(r.ListenPort, Receipts.Cp1256(Receipts.Arabic));
            await Until(() => done.Count == 1, "processed", 15_000);
            var rec = await Within(done.Single());
            Assert.Equal(1, q.To(DeskConfig.RawQueueFor("Bar")).Count, "the job waits in the spooler queue on the real port");
            Assert.True(!rec.QrPrinted && !rec.Registered, "no qr: " + rec.Status);
            Assert.Equal(0, rig.Calls("/receipt").Count, "not registered");
            Assert.Equal(Problems.PrinterUnreachable, rig.Cfg.Current.LastProblem, "problem shown");
            Assert.True(rig.ReportKinds.Contains("other"), "reported");
        }
        finally { server.StopAll(); }
    }

    // ---------- 3. «وضع الشبكة» ----------

    static PrinterRoute Lan(int listen, int printer) => new() { Lan = true, ListenPort = listen, DirectHost = "127.0.0.1", DirectPort = printer };

    public static async Task TestNetworkModeProxiesBothWaysAndCapturesTheJob()
    {
        using var printer = new FakePrinter { Reply = new byte[] { 0x12 } }; // a status byte
        var server = new PassThroughServer(new FakeQueue(), new RollingLog(Tmp.Dir()), (_, _) => { }, IPAddress.Loopback);
        var jobs = new ConcurrentQueue<ForwardedJob>();
        server.JobForwarded += j => jobs.Enqueue(j);
        var r = Lan(FreePort(), printer.Port);
        Assert.True(server.Start(r), "listens");
        try
        {
            using (var pos = new TcpClient())
            {
                pos.Connect(IPAddress.Loopback, r.ListenPort);
                var s = pos.GetStream();
                var status = new byte[1];
                s.ReadTimeout = 5000;
                Assert.Equal(1, s.Read(status, 0, 1), "the POS gets the printer's answer");
                Assert.Equal((byte)0x12, status[0], "status byte");
                s.Write(Receipts.Cp1256(Receipts.Arabic));
            }
            await Until(() => jobs.Count == 1 && printer.Jobs.Count == 1, "proxied");
            Assert.True(printer.Jobs.Single().SequenceEqual(Receipts.Cp1256(Receipts.Arabic)), "printer got every byte");
            Assert.True(jobs.Single().Data.SequenceEqual(Receipts.Cp1256(Receipts.Arabic)), "captured for analysis");
            Assert.True(server.PrintIfCurrent(jobs.Single().Seq, Encoding.ASCII.GetBytes("QR")), "qr");
            await Until(() => printer.Jobs.Count == 2, "qr on the real printer");

            // A status query alone (DLE EOT 1) is not a print job.
            using (var pos = new TcpClient())
            {
                pos.Connect(IPAddress.Loopback, r.ListenPort);
                pos.GetStream().Write(new byte[] { 0x10, 0x04, 0x01 });
            }
            await Until(() => printer.Jobs.Count == 3, "query proxied");
            await Task.Delay(200);
            Assert.Equal(1, jobs.Count, "no job for a status query");
        }
        finally { server.StopAll(); }
    }

    public static async Task TestNetworkModePrinterDownRefusesThePos()
    {
        var server = new PassThroughServer(new FakeQueue(), new RollingLog(Tmp.Dir()), (_, _) => { }, IPAddress.Loopback);
        var down = new TaskCompletionSource<string>();
        server.PrinterUnreachable += (_, why) => down.TrySetResult(why);
        var r = Lan(FreePort(), FreePort());
        server.Start(r);
        try
        {
            using var pos = new TcpClient();
            pos.Connect(IPAddress.Loopback, r.ListenPort);
            await Within(down.Task, 15_000);
            var s = pos.GetStream();
            s.ReadTimeout = 5000;
            bool closed;
            try { closed = s.Read(new byte[1], 0, 1) == 0; } catch (IOException) { closed = true; }
            Assert.True(closed, "the POS sees the printer as unreachable");
        }
        finally { server.StopAll(); }
    }

    public static void TestNetworkValidation()
    {
        var none = new List<PrinterRoute>();
        var local = new[] { "192.168.1.20" };
        Assert.Equal<string?>(null, PassThroughServer.ValidateNetwork("192.168.1.50", 9100, 9100, none, local), "valid");
        Assert.True(PassThroughServer.ValidateNetwork("printer", 9100, 9100, none, local) != null, "ip only");
        Assert.True(PassThroughServer.ValidateNetwork("192.168.1", 9100, 9100, none, local) != null, "four parts");
        Assert.True(PassThroughServer.ValidateNetwork("192.168.1.20", 9100, 9100, none, local) != null, "not this PC");
        Assert.True(PassThroughServer.ValidateNetwork("127.0.0.1", 9100, 9100, none, local) != null, "not loopback");
        Assert.True(PassThroughServer.ValidateNetwork("192.168.1.50", 80, 9100, none, local) != null, "port range");
        var msg = PassThroughServer.ValidateNetwork("192.168.1.50", 9100, 9100, new List<PrinterRoute> { Win("POS-80", 9100) }, local);
        Assert.True(msg != null && msg.Contains("9101"), "port of a printer refused, a free one suggested: " + msg);
        Assert.True(Firewall.AddScript(9100).Contains("localport=9100 remoteip=localsubnet"), "firewall: this port, local subnet only");
        Assert.True(Firewall.RemoveScript.Contains("delete rule name=DaymanDeskNetwork"), "firewall removal");
    }

    // ---------- 1. several printers: install, watchdog, uninstall ----------

    public static void TestInstallSeveralPrinters()
    {
        var sh = new MultiShell();
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        var started = new List<int>();
        var setup = new PrinterSetup(sh, null);
        setup.Install(store, new[] { "POS-80", "Bar" }, r => { started.Add(r.ListenPort); return true; });
        var c = store.Current;
        Assert.True(c.Active, "active");
        Assert.Equal(2, c.Printers.Count, "two routes");
        Assert.Equal("DAYMAN_9100", sh.Ports["POS-80"], "first on 9100");
        Assert.Equal("DAYMAN_9101", sh.Ports["Bar"], "second on 9101");
        Assert.Equal("COM3", sh.Ports["Kitchen"], "unchecked printer untouched");
        Assert.True(started.SequenceEqual(new[] { 9100, 9101 }), "a listener per printer");
        var bar = c.Printers.Single(r => r.PrinterName == "Bar");
        Assert.Equal("192.168.1.50", bar.DirectHost, "TCP/IP printer forwarded directly");
        Assert.Equal(9100, bar.DirectPort, "its RAW port");
        Assert.Equal<string?>(null, c.Printers.Single(r => r.PrinterName == "POS-80").DirectHost, "USB printer via its raw queue");
        Assert.True(sh.Scripts.Any(s => s == PrinterSetup.AddQueueScript(DeskConfig.RawQueueFor("Bar"), "IP_192.168.1.50")), "Bar's spooler fallback on its real port");
        Assert.True(sh.Scripts.Any(s => s == PrinterSetup.AddTcpPortScript("DAYMAN_9101", 9101)), "second tcp port");
        Assert.Equal("POS-80", c.PrinterName, "v1.0 mirror is the 9100 printer");

        // The watchdog restores only the printer whose listener is gone.
        var log = new RollingLog(Tmp.Dir());
        Assert.True(Watchdog.Run(c, setup, p => p == 9100, log), "one restored");
        Assert.Equal("DAYMAN_9100", sh.Ports["POS-80"], "listening printer kept");
        Assert.Equal("IP_192.168.1.50", sh.Ports["Bar"], "dead listener's printer restored");

        setup.Assign(c, _ => true);
        Assert.Equal("DAYMAN_9101", sh.Ports["Bar"], "assigned again at start");
        bool threw = false;
        try { setup.Install(store, new[] { "POS-80" }, _ => true); } catch (SetupException) { threw = true; }
        Assert.True(threw, "a different set while running needs a stop first");

        store.Update(x => x.Network.Enabled = true);
        setup.Uninstall(store);
        Assert.Equal("USB001", sh.Ports["POS-80"], "restored");
        Assert.Equal("IP_192.168.1.50", sh.Ports["Bar"], "restored");
        Assert.True(sh.Scripts.Contains(PrinterSetup.RemoveQueueScript(DeskConfig.RawQueueFor("POS-80"))) && sh.Scripts.Contains(PrinterSetup.RemoveQueueScript(DeskConfig.RawQueueFor("Bar"))), "raw queues removed");
        Assert.True(!sh.Scripts.Any(s => s.Contains("advfirewall")), "printers only: the firewall is not touched");
        Assert.True(!store.Current.Active && store.Current.Network.Enabled, "off; network mode left to StopAndRestore");
    }

    public static void TestInstallRollbackLeavesNetworkMode()
    {
        var sh = new MultiShell();
        sh.Override = s => s.StartsWith(PrinterSetup.GetPortScript("Bar").Replace("$p[0].PortName", "")) && s.Contains("PortName = 'DAYMAN_") ? new ShellResult(1, "", "driver refuses") : null;
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        store.Update(c => { c.Network.Enabled = true; c.Network.ListenPort = 9100; c.Network.PrinterHost = "192.168.1.60"; });
        try { new PrinterSetup(sh, null).Install(store, new[] { "POS-80", "Bar" }, _ => true); } catch (SetupException) { }
        Assert.Equal("USB001", sh.Ports["POS-80"], "printers rolled back");
        Assert.True(store.Current.Network.Enabled, "network mode still on");
        Assert.True(!sh.Scripts.Any(s => s.Contains("advfirewall")), "firewall rule kept");
    }

    public static void TestWatchdogRestartsNetworkMode()
    {
        var c = new DeskConfig();
        Assert.True(!Watchdog.NetworkDown(c, _ => false), "off: nothing to do");
        c.Network.Enabled = true; c.Network.PrinterHost = "192.168.1.60"; c.Network.ListenPort = 9100;
        Assert.True(Watchdog.NetworkDown(c, _ => false), "on and nothing listens");
        Assert.True(!Watchdog.NetworkDown(c, p => p == 9100), "listening");
        Assert.Equal(@"& schtasks.exe /Run /TN '\DaymanDesk\Tray'", ScheduledTasks.StartTrayScript(), "starts the tray task");
    }

    public static void TestNetworkModeAcceptsOnlyLocalAddresses()
    {
        foreach (var ok in new[] { "10.0.0.5", "172.16.0.1", "172.31.255.9", "192.168.1.20", "169.254.3.4", "127.0.0.1", "::ffff:192.168.1.20", "::1" })
            Assert.True(PassThroughServer.IsPrivate(IPAddress.Parse(ok)), "local " + ok);
        foreach (var bad in new[] { "8.8.8.8", "172.32.0.1", "172.15.0.1", "192.169.1.1", "100.64.0.1", "2001:db8::1" })
            Assert.True(!PassThroughServer.IsPrivate(IPAddress.Parse(bad)), "refused " + bad);
    }

    public static async Task TestOcrReadsMustAgree()
    {
        // The 2x read sees a different total: no QR.
        var rig = new Rig();
        rig.Ocr.Read = b => b.Width > 600 ? OcrReceipt.Select(l => l.Replace("٦ ٫ ٦٧٠", "٨ ٫ ٦٧٠")).ToArray() : OcrReceipt;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Calls("/receipt").Count == 0, "different totals: not registered: " + rec.Status);
        Assert.Equal(Problems.ImageUnreadable, rig.Cfg.Current.LastProblem, "problem shown");

        // The 2x read sees a different invoice number: no QR.
        var rig2 = new Rig();
        rig2.Ocr.Read = b => b.Width > 600 ? OcrReceipt.Select(l => l.Replace("١٠٤٣٢", "١٠٤٨٢")).ToArray() : OcrReceipt;
        await rig2.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(0, rig2.Calls("/receipt").Count, "different invoice numbers: not registered");

        // The second read fails: no QR.
        var rig3 = new Rig();
        rig3.Ocr.Read = b => b.Width > 600 ? Array.Empty<string>() : OcrReceipt;
        await rig3.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(0, rig3.Calls("/receipt").Count, "one read only: not registered");
    }

    public static void TestInstallFailureRestoresEveryPrinter()
    {
        var sh = new MultiShell();
        sh.Override = s => s.StartsWith(PrinterSetup.GetPortScript("Bar").Replace("$p[0].PortName", "")) && s.Contains("PortName = 'DAYMAN_") ? new ShellResult(1, "", "driver refuses") : null;
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        bool threw = false;
        try { new PrinterSetup(sh, null).Install(store, new[] { "POS-80", "Bar" }, _ => true); }
        catch (SetupException ex) { threw = true; Assert.True(ex.Arabic.Contains("Bar"), "names the printer"); }
        Assert.True(threw, "threw");
        Assert.Equal("USB001", sh.Ports["POS-80"], "the first printer, already moved, is restored");
        Assert.Equal("IP_192.168.1.50", sh.Ports["Bar"], "the second untouched");
        Assert.True(!store.Current.Active, "inactive");
    }

    public static void TestPortsSkipNetworkMode()
    {
        var sh = new MultiShell();
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        store.Update(c => { c.Network.Enabled = true; c.Network.ListenPort = 9100; c.Network.PrinterHost = "192.168.1.60"; });
        new PrinterSetup(sh, null).Install(store, new[] { "POS-80", "Kitchen" }, _ => true);
        Assert.True(store.Current.Printers.Select(r => r.ListenPort).SequenceEqual(new[] { 9101, 9102 }), "9100 belongs to network mode");
    }

    public static void TestV10ConfigBecomesFirstRoute()
    {
        var path = Path.Combine(Tmp.Dir(), "config.json");
        File.WriteAllText(path, "{\"printerName\":\"POS-80\",\"originalPort\":\"USB001\",\"originalDriver\":\"d\",\"rawQueueName\":\"Dayman Raw (POS-80)\",\"active\":true}");
        var c = new ConfigStore(path).Current;
        Assert.Equal(1, c.Printers.Count, "one route");
        Assert.Equal("POS-80", c.Printers[0].PrinterName, "printer");
        Assert.Equal("USB001", c.Printers[0].OriginalPort, "original port");
        Assert.Equal(9100, c.Printers[0].ListenPort, "9100");
        Assert.Equal("DAYMAN_9100", c.Printers[0].TcpPortName, "port name");
        Assert.True(!c.Network.Enabled, "network mode off by default");
    }

    // ---------- 4. kitchen tickets ----------

    public static async Task TestKitchenTicketIsForwardedAndNothingElse()
    {
        foreach (bool confirmed in new[] { false, true })
        {
            var rig = new Rig(confirmed);
            var kitchen = Receipts.Cp1256(new[] { "طلب مطبخ", "طاولة 4", "2 شاورما عربي", "1 بيبسي", "بدون بصل" });
            var rec = await rig.Agent.ProcessJobAsync(kitchen, 5, 1);
            Assert.True(rec.NonReceipt, "not a receipt");
            Assert.True(!rec.QrPrinted, "no qr");
            Assert.Equal(0, rig.Http.Calls.Count, "no receipt, no report");
            Assert.Equal(0, rig.Asked.Count, "never opens «تأكيد الحقول»");
            Assert.Equal(0, rig.Cfg.Current.TodayReceipts + rig.Cfg.Current.TodayNotRegistered, "not counted");
            Assert.Equal<string?>(null, rig.Cfg.Current.LastProblem, "not a problem");

            var english = Receipts.Ascii(new[] { "KITCHEN", "Table 4", "2 x Burger", "1 x Cola 0.500" });
            Assert.True((await rig.Agent.ProcessJobAsync(english, 5, 1)).NonReceipt, "English kitchen ticket with a price");
        }
        var order = new Rig();
        var rec2 = await order.Agent.ProcessJobAsync(Receipts.Ascii(new[] { "ORDER 17", "Table 4", "2 x Burger", "1 x Cola" }), 5, 1);
        Assert.True(rec2.NonReceipt && order.Http.Calls.Count == 0, "no total, no invoice: not a receipt");
        // A real receipt with a confident total is never a kitchen ticket.
        var real = new Rig();
        var rec3 = await real.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 5, 1);
        Assert.True(!rec3.NonReceipt && rec3.QrPrinted, "receipt");
    }

    public static void TestNonReceiptRules()
    {
        ExtractResult E(params string[] l) => ReceiptExtractor.Extract(l);
        string[] kitchen = { "المطبخ", "2 شاورما", "Invoice No: 55" };
        Assert.True(ReceiptExtractor.IsNonReceipt(kitchen, E(kitchen)), "marker, no total");
        string[] withTotal = { "Kitchen copy", "Invoice No: 55", "Grand Total 5.000" };
        Assert.True(!ReceiptExtractor.IsNonReceipt(withTotal, E(withTotal)), "a confident total wins over a marker");
        string[] noTotal = { "Invoice No: 55", "2 x Burger" };
        Assert.True(!ReceiptExtractor.IsNonReceipt(noTotal, E(noTotal)), "invoice read: a receipt we failed to read");
        string[] prices = { "Welcome", "Burger 2.500", "Thanks" };
        Assert.True(!ReceiptExtractor.IsNonReceipt(prices, E(prices)), "prices without a readable total: a reading failure, reported");
        string[] noDecimals = { "ORDER 17", "Table 4", "2 x Burger" };
        Assert.True(ReceiptExtractor.IsNonReceipt(noDecimals, E(noDecimals)), "no decimal amount at all: not a receipt");
    }

    public static async Task TestPricesWithoutTotalAreReported()
    {
        var rig = new Rig();
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Ascii(new[] { "JAMRA", "Burger 2.500", "Cola 0.500", "Thank you" }), 5, 1);
        Assert.True(!rec.NonReceipt && !rec.QrPrinted, "a receipt we could not read");
        Assert.Equal(Problems.ParseFailed, rig.Cfg.Current.LastProblem, "problem shown");
        Assert.True(rig.ReportKinds.Contains("parse_failed"), "reported");
    }

    // ---------- 5. image receipts ----------

    public static void TestGsV0DecodesToBitmap()
    {
        // 16 x 3 dots: row 0 = left 8 black, row 1 = alternating, row 2 = last dot only.
        var job = new List<byte> { 0x1B, 0x40, 0x1D, 0x76, 0x30, 0x00, 2, 0, 3, 0, 0xFF, 0x00, 0xAA, 0xAA, 0x00, 0x01 };
        job.AddRange(Enumerable.Repeat((byte)0, 64)); // padding so the analyser sees an image
        var bmp = RasterDecoder.Decode(job.ToArray())!;
        Assert.Equal(16, bmp.Width, "width");
        Assert.Equal(3, bmp.Height, "height");
        Assert.True(Enumerable.Range(0, 8).All(x => bmp[x, 0]) && Enumerable.Range(8, 8).All(x => !bmp[x, 0]), "row 0");
        Assert.True(bmp[0, 1] && !bmp[1, 1] && bmp[2, 1] && bmp[14, 1] && !bmp[15, 1], "row 1");
        Assert.True(bmp[15, 2] && bmp.Pixels.Skip(32).Take(15).All(p => p == 0), "row 2");

        // m = 3: double width and height.
        var big = RasterDecoder.Decode(new byte[] { 0x1D, 0x76, 0x30, 0x03, 1, 0, 1, 0, 0x80 })!;
        Assert.Equal(16, big.Width, "scaled width");
        Assert.Equal(2, big.Height, "scaled height");
        Assert.True(big[0, 0] && big[1, 0] && big[0, 1] && big[1, 1] && !big[2, 0], "one dot becomes 2x2");
    }

    public static void TestEscStarAndGraphicsDecodeAndStack()
    {
        var job = new List<byte>();
        // ESC * 33: 24-dot band, 2 columns; column 0 top dot, column 1 bottom dot.
        job.AddRange(new byte[] { 0x1B, 0x2A, 33, 2, 0, 0x80, 0, 0, 0, 0, 0x01 });
        job.Add(0x0A);
        // ESC * 0: 8-dot single density, 1 column with the top dot: 2 wide and 3 high per dot.
        job.AddRange(new byte[] { 0x1B, 0x2A, 0, 1, 0, 0x80 });
        // GS ( L function 112: 10 x 2 dots, then function 50 prints it.
        var data = new byte[] { 0xC0, 0x00, 0x00, 0x40 };
        int len = 10 + data.Length;
        job.AddRange(new byte[] { 0x1D, 0x28, 0x4C, (byte)len, 0, 0x30, 0x70, 0x30, 1, 1, 0x31, 10, 0, 2, 0 });
        job.AddRange(data);
        job.AddRange(new byte[] { 0x1D, 0x28, 0x4C, 0x02, 0x00, 0x30, 0x32 });
        var p = EscPosParser.Parse(job.ToArray());
        Assert.Equal(3, p.RasterPieces.Count, "three image commands");
        var bmp = RasterDecoder.Decode(job.ToArray())!;
        Assert.Equal(10, bmp.Width, "widest piece");
        Assert.Equal(24 + 24 + 2, bmp.Height, "stacked: 24-dot band, 8 dots x 3, 2 rows");
        Assert.True(bmp[0, 0] && !bmp[1, 0] && bmp[1, 23] && !bmp[0, 23], "ESC * 33 columns");
        Assert.True(bmp[0, 24] && bmp[1, 24] && bmp[0, 26] && !bmp[0, 27] && !bmp[2, 24], "ESC * 0 scaled 2 x 3");
        Assert.True(bmp[0, 48] && bmp[1, 48] && !bmp[2, 48] && bmp[9, 49] && !bmp[8, 49], "GS ( L raster");
    }

    public static void TestSplitForOcrCutsOnBlankRows()
    {
        var bmp = new MonoBitmap(8, 100);
        for (int y = 0; y < 100; y++) if (y % 10 < 7) bmp.Pixels[y * 8] = 1; // text lines of 7 rows, 3 blank rows
        var parts = bmp.SplitForOcr(45);
        Assert.Equal(100, parts.Sum(x => x.Height), "every row kept");
        Assert.True(parts.All(x => x.Height <= 45), "within the OCR limit");
        Assert.True(parts.Take(parts.Count - 1).All(x => x.BlankRow(x.Height - 1)), "cuts on blank rows");
        Assert.Equal(1, bmp.SplitForOcr(100).Count, "short image in one piece");
    }

    public static void TestExtractorOnOcrNoise()
    {
        var lines = OcrText.Clean(new[]
        {
            "مطعم جمرة", "رقم الفاتورة : ١٠٤٣٢", "شاورما عربي ٢ ٥٫٠٠٠", "المجموع ٥ ٫ ٧٥٠", "الإجمالي : ٦ . ٦٧٠ د.أ", "  ", "شكرا لزيارتكم",
        });
        Assert.Equal(6, lines.Count, "blank OCR lines dropped");
        Assert.Equal("الإجمالي : 6.670 د.أ", lines[4], "digits, separator and spaces cleaned");
        var e = ReceiptExtractor.Extract(lines, ocr: true);
        Assert.Equal("10432", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(6.670m, e.Total, "total");
        Assert.True(e.Confidence >= DeskAgent.MinConfidence, "confident " + e.Confidence);

        // OCR dropped the point: 6670 must never become the total.
        var lost = ReceiptExtractor.Extract(OcrText.Clean(new[] { "Invoice No: 77", "Grand Total 6670" }), ocr: true);
        Assert.Equal<decimal?>(null, lost.Total, "integer total refused in OCR text");
        Assert.Equal<decimal?>(6670m, ReceiptExtractor.Extract(new[] { "Invoice No: 77", "Grand Total 6670" }).Total, "printed text keeps integers");
        // «1 250.000»: 1250 or 250? Refused.
        var split = ReceiptExtractor.Extract(OcrText.Clean(new[] { "Invoice No: 77", "Grand Total 1 250.000" }), ocr: true);
        Assert.Equal<decimal?>(null, split.Total, "ambiguous digit groups refused");
        // A quantity and a price stay two numbers.
        Assert.Equal("شاورما عربي 2 5.000", lines[2], "quantity not glued to the price");
    }

    static string[] OcrReceipt => new[]
    {
        "مطعم جمرة", "رقم الفاتورة: ١٠٤٣٢", "شاورما عربي 2 5.000", "الضريبة 16%: 0.920", "الإجمالي: ٦ ٫ ٦٧٠ د.أ", "شكرا لزيارتكم",
    };

    public static async Task TestImageReceiptReadByOcrRegisters()
    {
        var rig = new Rig();
        rig.Ocr.Read = _ => OcrReceipt;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(2, rig.Ocr.Seen.Count, "read twice");
        Assert.Equal(576, rig.Ocr.Seen[0].Width, "OCR got the decoded bitmap (72 bytes x 8)");
        Assert.Equal(400, rig.Ocr.Seen[0].Height, "400 rows");
        Assert.Equal(1152, rig.Ocr.Seen[1].Width, "second read upscaled 2x");
        Assert.Equal(800, rig.Ocr.Seen[1].Height, "second read upscaled 2x");
        Assert.True(rec.QrPrinted, "qr printed: " + rec.Status);
        using var doc = JsonDocument.Parse(rig.Calls("/receipt").Single());
        Assert.Equal("image", doc.RootElement.GetProperty("kind").GetString(), "kind image");
        Assert.Equal("10432", doc.RootElement.GetProperty("invoiceNo").GetString(), "invoice");
        Assert.Equal("6.670", doc.RootElement.GetProperty("amount").GetString(), "amount");
        Assert.Equal(1, rig.Cfg.Current.TodayQr, "counted");
    }

    /// <summary>WO-104: a large bold total the page read loses («الإجمالي 0 د.أ») is read again on its own line crop.</summary>
    public static async Task TestOcrTotalLineReadAgain()
    {
        var rig = new Rig();
        var lost = OcrReceipt.Select(l => l.StartsWith("الإجمالي") ? "الإجمالي 0 د.أ" : l).ToArray();
        rig.Ocr.Read = _ => lost;
        var asked = new List<string>();
        rig.Ocr.ReadLine = (crop, chars) => { asked.Add(chars); return chars.Contains('A') ? null : "6.670"; };
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(asked.Count >= 4, "invoice and total lines re-read in both reads: " + asked.Count);
        Assert.True(rec.QrPrinted, "qr printed: " + rec.Status);
        using var doc = JsonDocument.Parse(rig.Calls("/receipt").Single());
        Assert.Equal("6.670", doc.RootElement.GetProperty("amount").GetString(), "amount from the line read");

        Assert.Equal("رقم الفاتورة: INV-71", ReceiptExtractor.MergeLineRead("رقم الفاتورة: INV-71", "Ixl INV-77 7", total: false), "a page token is kept");
        Assert.Equal("رقم الفاتورة: INV-777", ReceiptExtractor.MergeLineRead("رقم الفاتورة:", "Ixl INV-777 7", total: false), "a lost invoice taken from the line read");
        Assert.Equal("Thanks", ReceiptExtractor.MergeLineRead("Thanks", "77", total: false), "junk digits never become an invoice");
        Assert.Equal("Total 12.500", ReceiptExtractor.MergeLineRead("Total 12.500", "42.500", total: true), "a page amount is kept");
        Assert.Equal("Total 0 JOD", ReceiptExtractor.MergeLineRead("Total 0 JOD", "7", total: true), "no decimal amount: page kept");
    }

    public static async Task TestImageReceiptConfirmationThenRegisters()
    {
        var rig = new Rig(confirmed: false);
        rig.Ocr.Read = _ => OcrReceipt;
        await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(0, rig.Calls("/receipt").Count, "not before the owner confirms");
        var p = rig.Asked.Single();
        Assert.True(p.FromImage, "confirmation from OCR text");
        Assert.Equal("10432", p.InvoiceNo, "detected invoice");
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, p.InvoiceLine, p.TotalLine), "confirmed");
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(rec.QrPrinted, "registered after confirmation: " + rec.Status);
    }

    public static async Task TestImageReceiptDoubtfulTotalNoQr()
    {
        var rig = new Rig();
        rig.Ocr.Read = _ => new[] { "مطعم جمرة", "رقم الفاتورة: 10432", "الإجمالي: 6670" };
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Calls("/receipt").Count == 0, "no wrong QR: " + rec.Status);
        Assert.Equal(Problems.ImageUnreadable, rig.Cfg.Current.LastProblem, "problem shown");
        Assert.True(rig.ReportKinds.Contains("parse_failed"), "reported");
    }

    public static async Task TestImageReceiptWithoutOcrLanguage()
    {
        var rig = new Rig();
        rig.Ocr.Read = _ => null;
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(!rec.QrPrinted && rig.Calls("/receipt").Count == 0, "no qr");
        Assert.Equal(Problems.OcrUnavailable, rig.Cfg.Current.LastProblem, "says what is missing");
        Assert.True(OcrLanguages.InstallScript().Contains("Language.OCR~~~ar-SA~0.0.1.0") && OcrLanguages.InstallScript().Contains("Add-WindowsCapability"), "installer adds Arabic OCR");
    }

    // ---------- 6. unknown formats ----------

    public static async Task TestUnknownFormatReportedOncePerFormatPerDay()
    {
        var rig = new Rig();
        for (int i = 0; i < 3; i++)
        {
            var rec = await rig.Agent.ProcessJobAsync(Receipts.Garbage(5), 5, 1);
            Assert.True(!rec.QrPrinted && rec.Kind == ReceiptKind.Unknown, "unknown, no qr");
        }
        Assert.Equal(1, rig.ReportKinds.Count(k => k == "unknown_format"), "one report for three receipts");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nInvoice No 5 Total 3.000 lots of text here to pass the length\n");
        await rig.Agent.ProcessJobAsync(pdf, 5, 1);
        await rig.Agent.ProcessJobAsync(pdf, 5, 1);
        Assert.Equal(2, rig.ReportKinds.Count(k => k == "unknown_format"), "a second format: one more report");
        Assert.True(rig.Calls("/report").Last().Contains("format pdf"), "the format is named");
        rig.Now = rig.Now.AddDays(1);
        await rig.Agent.ProcessJobAsync(Receipts.Garbage(5), 5, 1);
        Assert.Equal(3, rig.ReportKinds.Count(k => k == "unknown_format"), "again the next day");
        Assert.Equal(1, rig.Cfg.Current.TodayNotRegistered, "still counted as a receipt that did not register");
    }

    // ---------- WO-91: field audit ----------

    public static async Task TestTinyJobKeepsTheQrAndIsNotAnalysed()
    {
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => throw new AssertException("no fallback"));
        var jobs = new ConcurrentQueue<ForwardedJob>();
        server.JobForwarded += j => jobs.Enqueue(j);
        var a = Win("POS-80", FreePort());
        server.Start(a);
        try
        {
            Send(a.ListenPort, Encoding.ASCII.GetBytes("receipt A ........"));
            await Until(() => jobs.Count == 1, "receipt");
            Send(a.ListenPort, new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }); // ESC p: open the cash drawer
            await Until(() => q.To(DeskConfig.RawQueueFor("POS-80")).Count == 2, "drawer kick forwarded");
            await Task.Delay(100);
            Assert.Equal(1, jobs.Count, "the drawer kick is not a job");
            Assert.True(server.PrintIfCurrent(jobs.Single().Seq, Encoding.ASCII.GetBytes("QR A")), "the receipt's QR still prints");
        }
        finally { server.StopAll(); }
    }

    public static async Task TestShortUnknownJobIsNotAReceipt()
    {
        var rig = new Rig();
        var rec = await rig.Agent.ProcessJobAsync(new byte[] { 0x1B, 0x40, 0x1D, 0x72, 0x01, 0x10, 0x04, 0x01, 0x1B, 0x40, 0x0A, 0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x00 }, 5, 1);
        Assert.True(rec.NonReceipt && !rec.QrPrinted, "not a receipt: " + rec.Status);
        Assert.Equal(0, rig.Cfg.Current.TodayReceipts, "not counted");
        Assert.Equal(0, rig.ReportKinds.Count, "no report");
    }

    public static async Task TestSampleAndSlowReportsGoAfterTheSlip()
    {
        var rig = new Rig();
        rig.Cfg.Update(c => c.SamplesSent = 0);
        var order = new List<string>();
        rig.PrintIfCurrent = (_, _) => { lock (order) order.Add("slip"); return true; };
        var inner = rig.Http.Respond;
        rig.Http.Respond = (r, b) => { lock (order) order.Add(r.RequestUri!.AbsolutePath.EndsWith("/report") ? "report" : "receipt"); return inner(r, b); };
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(Receipts.Arabic), 450, 1);
        Assert.True(rec.QrPrinted, "qr: " + rec.Status);
        int slip = order.IndexOf("slip"), firstReport = order.IndexOf("report");
        Assert.True(slip >= 0 && firstReport > slip, "slip before the sample and slow-forward reports: " + string.Join(",", order));
        Assert.True(rig.ReportKinds.Contains("sample") && rig.ReportKinds.Contains("slow_forward"), "both still sent");
    }

    public static async Task TestOcrLanguageChangeAsksAgain()
    {
        var rig = new Rig(confirmed: false);
        var reconfirm = 0;
        rig.Agent.ReconfirmNeeded += () => reconfirm++;
        rig.Ocr.Read = _ => OcrReceipt;
        rig.Ocr.LanguageTag = "en-US";
        await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        var p = rig.Asked.Single();
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, p.InvoiceLine, p.TotalLine), "confirmed under English OCR");
        Assert.Equal("en-US", rig.Cfg.Current.FieldsOcrLanguage, "language stored with the fields");
        Assert.True((await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1)).QrPrinted, "same language: registers");

        rig.Ocr.LanguageTag = "ar-SA"; // Arabic OCR arrived from Windows Update
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(!rec.QrPrinted && !rig.Cfg.Current.FieldsConfirmed, "fields dropped, no qr: " + rec.Status);
        Assert.Equal(1, reconfirm, "the owner is told");
        Assert.Equal(2, rig.Asked.Count, "the window opens on this receipt");
    }

    public static void TestPairCodeUsesTheServerAlphabet()
    {
        Assert.Equal<string?>(null, DeskAgent.PairCodeProblem(" ab3cd9 "), "case-insensitive, trimmed");
        Assert.True(DeskAgent.PairCodeProblem("AB1CD9") != null, "1 is not in the alphabet");
        Assert.True(DeskAgent.PairCodeProblem("ABCDE") != null, "length");
        Assert.True(DeskAgent.PairCodeProblem("شسيبلا")!.Contains("Alt+Shift"), "Arabic keyboard named");
    }

    public static void TestUnsupportedPrintersRefused()
    {
        Assert.True(new PrinterInfo("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF").Unsupported != null, "pdf");
        Assert.True(new PrinterInfo("Microsoft XPS Document Writer", "XPSPort:", "Microsoft XPS Document Writer v4").Unsupported != null, "xps");
        Assert.True(new PrinterInfo("OneNote (Desktop)", "nul:", "Send to Microsoft OneNote 16 Driver").Unsupported != null, "onenote");
        Assert.True(new PrinterInfo("Fax", "SHRFAX:", "Microsoft Shared Fax Driver").Unsupported != null, "fax");
        Assert.True(new PrinterInfo(@"\\CASHIER2\POS-80", @"\\CASHIER2\POS-80", "POS Driver").Unsupported != null, "shared");
        var file = new PrinterInfo("Generic", "FILE:", "Generic / Text Only");
        Assert.True(file.Unsupported == null && file.FilePort, "FILE: allowed with a note");
        Assert.True(new PrinterInfo("POS-80", "USB001", "POS-80 Driver").Unsupported == null, "a receipt printer");

        var sh = new FakeShell();
        var store = new ConfigStore(Path.Combine(Tmp.Dir(), "config.json"));
        try { new PrinterSetup(sh, null).Install(store, new[] { "Microsoft Print to PDF" }, _ => true); throw new AssertException("install accepted a pdf printer"); }
        catch (SetupException ex) { Assert.True(ex.Arabic.Contains("لا تصلح"), "refused: " + ex.Arabic); }
        Assert.True(!sh.Scripts.Any(s => s.Contains(".Put()") && s.Contains("$p[0].PortName =")), "nothing moved");
    }

    public static void TestDeletedPrinterIsNothingToRestore()
    {
        var sh = new MultiShell();
        sh.Override = s => s == PrinterSetup.GetPortScript("Gone") ? new ShellResult(1, "", "not found")
            : s.Contains("'missing'") && s.Contains("'Gone'") ? new ShellResult(0, "missing", "") : null;
        var setup = new PrinterSetup(sh, null);
        Assert.True(!setup.Restore(new PrinterRoute { PrinterName = "Gone", OriginalPort = "USB002", ListenPort = 9100 }), "deleted: no throw");
        sh.Override = s => s == PrinterSetup.GetPortScript("Gone") ? new ShellResult(1, "", "rpc error") : null;
        try { setup.Restore(new PrinterRoute { PrinterName = "Gone", OriginalPort = "USB002", ListenPort = 9100 }); throw new AssertException("unreadable port accepted"); }
        catch (SetupException) { }
    }

    public static void TestStoppedSpoolerIsNotAMissingPrinter()
    {
        var sh = new MultiShell();
        string spooler = "Running";
        sh.Override = s => s == PrinterSetup.GetPortScript("POS-80") ? new ShellResult(1, "", "spooler not running")
            : s.StartsWith("if ((Get-Service") ? new ShellResult(0, spooler == "Running" ? "missing" : "spooler", "") : null;
        Assert.True(PrinterSetup.MissingScript("POS-80").Contains("Get-Service -Name Spooler") && PrinterSetup.MissingScript("POS-80").Contains("-ErrorAction Stop"), "script checks the spooler");
        var setup = new PrinterSetup(sh, null);
        var route = new PrinterRoute { PrinterName = "POS-80", OriginalPort = "USB001", ListenPort = 9100 };
        spooler = "Stopped";
        try { setup.Restore(route); throw new AssertException("a stopped spooler counted as a deleted printer"); }
        catch (SetupException) { }
        Assert.True(!setup.SkippedMissing, "nothing skipped");
        spooler = "Running";
        Assert.True(!setup.Restore(route) && setup.SkippedMissing, "deleted while the spooler runs: skipped, and said so");
    }

    public static async Task TestPrinterCommandKeepsThePreviousQrPending()
    {
        var q = new FakeQueue();
        var server = new PassThroughServer(q, new RollingLog(Tmp.Dir()), (_, _) => throw new AssertException("no fallback"));
        var jobs = new ConcurrentQueue<ForwardedJob>();
        server.JobForwarded += j => jobs.Enqueue(j);
        var a = Win("POS-80", FreePort());
        server.Start(a);
        try
        {
            Send(a.ListenPort, Encoding.ASCII.GetBytes("receipt A ........"));
            await Until(() => jobs.Count == 1, "receipt");
            var ja = jobs.Single();
            // 20 bytes of commands (init, status, feed, cut): 16 or more, so it is a job until analysed.
            var cmd = new byte[] { 0x1B, 0x40, 0x1D, 0x72, 0x01, 0x10, 0x04, 0x01, 0x1B, 0x40, 0x0A, 0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x00, 0x1B, 0x40, 0x0A };
            Send(a.ListenPort, cmd);
            await Until(() => jobs.Count == 2, "command job");
            var jc = jobs.Last();
            Assert.True(!server.PrintIfCurrent(ja.Seq, Encoding.ASCII.GetBytes("QR")), "before the analysis the receipt is not current");

            var rig = new Rig();
            bool released = false;
            var rec = await rig.Agent.ProcessJobAsync(jc.Data, 5, jc.Seq, printedNothing: () => { released = true; server.Release(jc.Seq, jc.PrevSeq); });
            Assert.True(rec.NonReceipt && released, "a printer command, released: " + rec.Status);
            Assert.True(server.PrintIfCurrent(ja.Seq, Encoding.ASCII.GetBytes("QR A")), "the previous receipt's QR still prints");

            bool kitchenReleased = false;
            var kitchen = await rig.Agent.ProcessJobAsync(Receipts.Cp1256(new[] { "طلب مطبخ", "شاورما عربي 2", "طاولة 4" }), 5, 99, printedNothing: () => kitchenReleased = true);
            Assert.True(kitchen.NonReceipt && !kitchenReleased, "a kitchen ticket printed paper: never released");
        }
        finally { server.StopAll(); }
    }

    public static async Task TestPreV122ImageFieldsAdoptTheCurrentOcrLanguage()
    {
        var rig = new Rig(confirmed: false);
        rig.Ocr.Read = _ => OcrReceipt;
        await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        var p = rig.Asked.Single();
        rig.Agent.ConfirmFields(p, p.InvoiceLine, p.TotalLine);
        Assert.True(rig.Cfg.Current.FieldsOcrLanguage == null, "no engine language known (a 1.2.1 config)");
        int reconfirm = 0;
        rig.Agent.ReconfirmNeeded += () => reconfirm++;
        rig.Ocr.LanguageTag = "ar-SA";
        var rec = await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.True(rec.QrPrinted && reconfirm == 0, "no forced reconfirm: " + rec.Status);
        Assert.Equal("ar-SA", rig.Cfg.Current.FieldsOcrLanguage, "language recorded on the first read");
        rig.Ocr.LanguageTag = "en-US";
        await rig.Agent.ProcessJobAsync(Receipts.Raster(), 5, 1);
        Assert.Equal(1, reconfirm, "a later change still asks again");
    }
}
