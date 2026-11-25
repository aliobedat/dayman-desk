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
        Assert.True(!ReferenceEquals(rig.Agent.PendingConfirmation, p), "WO-91: the newest receipt replaces the pending one");
    }

    // ---------- WO-91: the «Generic / Text Only» driver prints every Arabic letter as '.' ----------

    static readonly string[] Garbled = { "...... ", "... ........: 1001", ".... 2.500", ".........: 2.500" };

    public static void TestShapeLabelLearnsGarbledLines()
    {
        var inv = ReceiptExtractor.LearnInvoice(Garbled, 1);
        Assert.True(inv != null && inv.Value.value == "1001", "invoice learned from a line without letters");
        Assert.Equal("... ........: 9", inv!.Value.field.Shape, "invoice shape");
        Assert.True(!inv.Value.field.ValueOnNextLine && inv.Value.field.Label == "", "same line, no letter label");
        var tot = ReceiptExtractor.LearnTotal(Garbled, 3);
        Assert.True(tot != null && tot.Value.value == 2.500m, "total learned");
        Assert.Equal(".........: 9", tot!.Value.field.Shape, "total shape");

        var later = new[] { "...... ", "... ........: 1002", ".... 3.000", ".... 1.250", ".........: 4.250" };
        var e = ReceiptExtractor.Extract(later, inv.Value.field, tot.Value.field);
        Assert.Equal("1002", e.InvoiceNo, "invoice by shape");
        Assert.Equal<decimal?>(4.250m, e.Total, "total by shape, not the item line");
        Assert.True(e.Confidence >= DeskAgent.MinConfidence, "unique shapes count as learned: " + e.Confidence);
    }

    public static void TestRepeatedShapeGivesNoConfidence()
    {
        var inv = ReceiptExtractor.LearnInvoice(Garbled, 1)!.Value.field;
        var tot = ReceiptExtractor.LearnTotal(Garbled, 3)!.Value.field;
        // A paid line with as many letters as the total line: two lines of the total's shape.
        var twoTotals = new[] { "...... ", "... ........: 1003", ".... 2.500", ".........: 2.500", ".........: 5.000" };
        var e = ReceiptExtractor.Extract(twoTotals, inv, tot);
        Assert.Equal("1003", e.InvoiceNo, "invoice still unique");
        Assert.True(e.TotalConfidence < DeskAgent.MinConfidence, "ambiguous total: no qr: " + e.TotalConfidence);
        var twoInvoices = new[] { "...... ", "... ........: 1004", "... ........: 77", ".........: 2.500" };
        Assert.True(ReceiptExtractor.Extract(twoInvoices, inv, tot).InvoiceConfidence < DeskAgent.MinConfidence, "ambiguous invoice: no qr");
        // Never bare position: the shape missing on a receipt is no match.
        var other = new[] { "...... ", "..: 1005", ".... 2.500", ".......: 2.500" };
        var o = ReceiptExtractor.Extract(other, inv, tot);
        Assert.True(o.InvoiceConfidence < DeskAgent.MinConfidence && o.TotalConfidence < DeskAgent.MinConfidence, "other shapes: no qr");
    }

    public static async Task TestGarbledReceiptConfirmsAndRegisters()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(Garbled), 5, 1);
        Assert.Equal(1, rig.Asked.Count, "asked");
        var p = rig.Asked[0];
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(p, 1, 3), "the field's «تأكيد وحفظ» now saves");
        Assert.True(rig.Cfg.Current.LearnedInvoice!.Shape != null, "shape stored");

        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(new[] { "...... ", "... ........: 1002", ".... 3.000", ".........: 3.000" }), 5, 1);
        Assert.True(rec.QrPrinted, "registered and qr: " + rec.Status);
        Assert.Equal("1002", rig.LastInvoice, "invoice");
        Assert.Equal("3.000", rig.LastAmount, "amount");

        int before = rig.Receipts;
        var dup = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(new[] { "...... ", "... ........: 1003", ".........: 2.000", ".........: 3.000" }), 5, 1);
        Assert.True(!dup.QrPrinted && rig.Receipts == before, "two lines of the total's shape: no qr, not sent");
    }

    /// <summary>WO-97: the owner's choice from the dashboard (via /heartbeat) learns exactly what the window learns.</summary>
    public static async Task TestOwnerFieldsFromDashboardEqualLocalConfirm()
    {
        var job = DaymanDesk.Tests.Receipts.Utf8(DaymanDesk.Tests.Receipts.Arabic);
        var local = new Rig();
        await local.Agent.ProcessJobAsync(job, 5, 1);
        Assert.Equal<string?>(null, local.Agent.ConfirmFields(local.Asked[0], 3, 9), "local confirm");

        var remote = new Rig();
        await remote.Agent.ProcessJobAsync(job, 5, 1);
        var sent = remote.Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith("/fields")).Select(c => c.body).ToList();
        Assert.Equal(1, sent.Count, "the first receipt goes to the owner once");
        Assert.True(!sent[0].Contains("0791234567"), "phone scrubbed on the device");
        Assert.True(!remote.Cfg.Current.FieldsConfirmed, "nothing learned before the owner confirms");
        string sampleId = JsonDocument.Parse(sent[0]).RootElement.GetProperty("sampleId").GetString()!;
        remote.Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/heartbeat")
            ? FakeHandler.Json(200, "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2,\"fieldsConfirmed\":{\"sampleId\":\"" + sampleId + "\",\"invoiceLine\":3,\"totalLine\":9}}")
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        await remote.Agent.HeartbeatAsync();
        Assert.True(remote.Cfg.Current.FieldsConfirmed, "confirmed from the dashboard");
        Assert.Equal(JsonSerializer.Serialize(local.Cfg.Current.LearnedInvoice), JsonSerializer.Serialize(remote.Cfg.Current.LearnedInvoice), "invoice field");
        Assert.Equal(JsonSerializer.Serialize(local.Cfg.Current.LearnedTotal), JsonSerializer.Serialize(remote.Cfg.Current.LearnedTotal), "total field");
        // A lost answer: the server repeats the choice until the next heartbeat acknowledges it; it is applied once.
        await remote.Agent.HeartbeatAsync();
        var acks = remote.Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith("/heartbeat")).Select(c => c.body).ToList();
        Assert.Equal(sampleId, JsonDocument.Parse(acks[^1]).RootElement.GetProperty("fieldsApplied").GetString(), "acknowledged");
        Assert.Equal(1, remote.Http.Calls.Count(c => c.req.RequestUri!.AbsolutePath.EndsWith("/report") && c.body.Contains("fields confirmed")), "applied once");
    }

    static string HeartbeatWith(string sampleId, int inv, int tot) =>
        "{\"serverTime\":\"x\",\"qrEnabled\":true,\"codeValidityDays\":2,\"fieldsConfirmed\":{\"sampleId\":\"" + sampleId + "\",\"invoiceLine\":" + inv + ",\"totalLine\":" + tot + "}}";
    static List<string> Bodies(Rig r, string path) => r.Http.Calls.Where(c => c.req.RequestUri!.AbsolutePath.EndsWith(path)).Select(c => c.body).ToList();

    /// <summary>WO-97: a refused owner choice goes back under a fresh id; his next choice applies.</summary>
    public static async Task TestRefusedOwnerFieldsResendWithFreshId()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Utf8(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        string first = JsonDocument.Parse(Bodies(rig, "/fields")[0]).RootElement.GetProperty("sampleId").GetString()!;
        string reply = HeartbeatWith(first, 0, 1);
        rig.Http.Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/heartbeat") ? FakeHandler.Json(200, reply)
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        await rig.Agent.HeartbeatAsync();
        Assert.True(!rig.Cfg.Current.FieldsConfirmed, "lines without the fields are refused");
        var resent = JsonDocument.Parse(Bodies(rig, "/fields")[^1]).RootElement;
        string second = resent.GetProperty("sampleId").GetString()!;
        Assert.True(second != first && resent.TryGetProperty("problem", out _), "re-sent with a fresh id and the reason");
        reply = HeartbeatWith(second, 3, 9);
        await rig.Agent.HeartbeatAsync();
        Assert.True(rig.Cfg.Current.FieldsConfirmed, "the owner's second choice applies");
    }

    /// <summary>WO-97: an installer's confirmation on the PC ends the sample on the dashboard (fieldsApplied).</summary>
    public static async Task TestLocalConfirmAcknowledgesTheDashboardSample()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Utf8(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        string id = JsonDocument.Parse(Bodies(rig, "/fields")[0]).RootElement.GetProperty("sampleId").GetString()!;
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(rig.Asked[0], 3, 9), "local confirm");
        await rig.Agent.HeartbeatAsync();
        Assert.Equal(id, JsonDocument.Parse(Bodies(rig, "/heartbeat")[^1]).RootElement.GetProperty("fieldsApplied").GetString(), "acknowledged");
    }

    public static void TestScrubHidesOnlyJordanianMobiles()
    {
        foreach (var phone in new[] { "0791234567", "079 123 4567", "791234567", "+962791234567", "00962-79-123-4567", "٠٧٨١٢٣٤٥٦٧" })
            Assert.True(!DeskAgent.ScrubLine("هاتف: " + phone).Any(char.IsDigit), "hidden: " + phone);
        Assert.Equal("رقم الفاتورة: 202611260001", DeskAgent.ScrubLine("رقم الفاتورة: 202611260001"), "a long invoice number stays");
        Assert.Equal("INV 10432", DeskAgent.ScrubLine("INV ١٠٤٣٢"), "Latin digits");
    }

    public static async Task TestConfirmValidationMatchesTheWindow()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(Garbled), 5, 1);
        var p = rig.Asked[0];
        for (int i = 0; i < Garbled.Length; i++)
        {
            // The window shows a value exactly when it accepts the line, and saving never says «لا يحوي» for it.
            bool shows = ReceiptExtractor.LearnInvoice(p.Lines, i) != null;
            Assert.Equal(shows, ReceiptExtractor.InvoiceLineProblem(p.Lines, i) == null, "invoice rule, line " + i);
            bool showsTotal = ReceiptExtractor.LearnTotal(p.Lines, i) != null;
            Assert.Equal(showsTotal, ReceiptExtractor.TotalLineProblem(p.Lines, i) == null, "total rule, line " + i);
            int other = i == 3 ? 2 : 3;
            var err = rig.Agent.ConfirmFields(p, i, other);
            if (shows) Assert.True(err == null || !err.Contains("لا يحوي رقماً"), "never «لا يحوي رقماً» for a shown value, line " + i);
            rig.Agent.RequestReconfirm();
        }
        // A shape that repeats on the confirmed receipt itself is refused with the repeat message.
        var twice = new[] { "...... ", "... ........: 1001", ".........: 2.500", ".........: 2.500" };
        var pt = new FieldConfirmation { Lines = twice, InvoiceLine = 1, TotalLine = 3 };
        Assert.True(rig.Agent.ConfirmFields(pt, 1, 3)!.Contains("يتكرر"), "repeated shape refused");
    }

    public static void TestShapeLookAlikesInAZReportGiveNoQr()
    {
        var inv = ReceiptExtractor.LearnInvoice(Garbled, 1)!.Value.field;
        var tot = ReceiptExtractor.LearnTotal(Garbled, 3)!.Value.field;
        // A Z/X report from the same POS: lines with the same letter counts, near the same places.
        var z = new[] { "...... ", ". .......", "... ........: 57", "....: 20/11/2026", "......: 12", ".........: 812.500", "..... ......: 3", "... ....... .......: 0" };
        var e = ReceiptExtractor.Extract(z, inv, tot);
        Assert.True(e.InvoiceConfidence < DeskAgent.MinConfidence && e.TotalConfidence < DeskAgent.MinConfidence, $"z report: no qr ({e.InvoiceConfidence}, {e.TotalConfidence})");
        // Same neighbours, but the total line far from its place (a refund slip with lines after the total).
        var far = new[] { "...... ", "... ........: 1020", ".... 2.500", ".........: 2.500", ".... .....", "... .......", "........ ...." };
        Assert.True(ReceiptExtractor.Extract(far, inv, tot).Confidence < DeskAgent.MinConfidence, "total moved more than 2 lines: no qr");
        // Never one field by shape alone: the invoice matches, the total line is missing.
        var half = new[] { "...... ", "... ........: 1021", ".... 2.500", "......: 2.500" };
        var h = ReceiptExtractor.Extract(half, inv, tot);
        Assert.True(h.InvoiceNo == "1021" && h.InvoiceConfidence < DeskAgent.MinConfidence, "the invoice alone does not count");
    }

    public static void TestShapeFieldsSurviveExtraItemLines()
    {
        var inv = ReceiptExtractor.LearnInvoice(Garbled, 1)!.Value.field;
        var tot = ReceiptExtractor.LearnTotal(Garbled, 3)!.Value.field;
        for (int extra = 1; extra <= 3; extra++)
        {
            var lines = new List<string> { "...... ", "... ........: 1030" };
            for (int i = 0; i <= extra; i++) lines.Add("........ 1.000");
            lines.Add(".........: " + (extra + 1) + ".000");
            var e = ReceiptExtractor.Extract(lines, inv, tot);
            Assert.True(e.Confidence >= DeskAgent.MinConfidence && e.InvoiceNo == "1030" && e.Total == extra + 1, $"{extra} extra item lines still match");
        }
    }

    public static async Task TestMixedLabelsAreBothLearnedByShape()
    {
        var rig = new Rig();
        var mixed = new[] { "...... ", "... ........: 1001", ".... 2.500", "Total: 2.500" };
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(mixed), 5, 1);
        Assert.Equal<string?>(null, rig.Agent.ConfirmFields(rig.Asked[0], 1, 3), "confirmed");
        Assert.True(rig.Cfg.Current.LearnedInvoice!.Shape != null && rig.Cfg.Current.LearnedTotal!.Shape == "total: 9", "both by shape, never mixed");
        var rec = await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(new[] { "...... ", "... ........: 1002", ".... 4.000", "Total: 4.000" }), 5, 1);
        Assert.True(rec.QrPrinted, "registers: " + rec.Status);
    }

    public static void TestOldLetterLabelConfigUnchanged()
    {
        // A 1.2.1 config: no "shape" property.
        var json = "{\"label\":\"رقم الفاتوره\",\"valueOnNextLine\":false,\"position\":3,\"tokenIndex\":0}";
        var f = JsonSerializer.Deserialize<LearnedField>(json, DeskApiClient.Json)!;
        Assert.True(f.Shape == null, "no shape");
        var e = ReceiptExtractor.Extract(DaymanDesk.Tests.Receipts.Arabic, f, null);
        Assert.Equal("10432", e.InvoiceNo, "letter label still matches");
        Assert.Equal(ReceiptExtractor.LearnedConfidence, e.InvoiceConfidence, "learned confidence");
        // Letter labels stay first when present.
        var inv = ReceiptExtractor.LearnInvoice(DaymanDesk.Tests.Receipts.Arabic, 3);
        Assert.True(inv != null && inv.Value.field.Shape == null && inv.Value.field.Label == "رقم الفاتوره", "letter label learned, no shape");
    }

    public static async Task TestNewestReceiptReplacesPendingAndLaterClearsIt()
    {
        var rig = new Rig();
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Ascii(Garbled), 5, 1);
        Assert.Equal(1, rig.Asked.Count, "the garbled one asked");
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.Equal(1, rig.Asked.Count, "no second window while one is pending");
        Assert.True(rig.Agent.PendingConfirmation!.Lines.Any(l => l.Contains("10432")), "the better, newer receipt is pending");

        rig.Agent.DismissConfirmation(); // «لاحقاً»
        Assert.True(rig.Agent.PendingConfirmation == null, "cleared");
        await rig.Agent.ProcessJobAsync(DaymanDesk.Tests.Receipts.Cp1256(DaymanDesk.Tests.Receipts.Arabic), 5, 1);
        Assert.Equal(2, rig.Asked.Count, "the next receipt opens the window fresh");
        Assert.Equal("10432", rig.Asked[1].InvoiceNo, "fresh receipt detected");
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
