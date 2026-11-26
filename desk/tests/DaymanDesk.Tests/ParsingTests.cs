using DaymanDesk.Core;

namespace DaymanDesk.Tests;

public static class DetectionTests
{
    static void ArabicReceipt(byte[] job, int expectedCp)
    {
        var a = ReceiptAnalyzer.Analyze(job);
        Assert.Equal(ReceiptKind.Text, a.Kind, "kind");
        Assert.Equal<int?>(expectedCp, a.CodePage, "code page");
        var e = ReceiptExtractor.Extract(a.Lines);
        Assert.Equal("10432", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(6.670m, e.Total, "total");
        Assert.True(e.Confidence >= 0.75, "confidence " + e.Confidence);
    }

    public static void TestCp864VisualReceipt()
    {
        var job = Receipts.Cp864(Receipts.Arabic);
        ArabicReceipt(job, 864);
        Assert.Equal<int?>(22, ReceiptAnalyzer.Analyze(job).CodeTable, "code table");
    }

    public static void TestCp1256LogicalReceipt() => ArabicReceipt(Receipts.Cp1256(Receipts.Arabic), 1256);

    public static void TestCp1256VisualReceipt() => ArabicReceipt(Receipts.Cp1256(Receipts.Arabic, visual: true), 1256);

    public static void TestUtf8Receipt() => ArabicReceipt(Receipts.Utf8(Receipts.Arabic), 65001);

    public static void TestCp720Receipt()
    {
        var job = Receipts.Job(Receipts.Arabic.Select(l => ReceiptAnalyzer.GetDecoder(720).GetBytes(l.Replace('إ', 'ا'))));
        ArabicReceipt(job, 720);
    }

    public static void TestEnglishReceipt()
    {
        var a = ReceiptAnalyzer.Analyze(Receipts.Ascii(Receipts.English));
        Assert.Equal(ReceiptKind.Text, a.Kind, "kind");
        Assert.Equal<int?>(null, a.CodePage, "no arabic code page");
        Assert.Equal("Latin", a.EncodingName, "encoding");
        var e = ReceiptExtractor.Extract(a.Lines);
        Assert.Equal("INV-2291", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(11.600m, e.Total, "total");
    }

    public static void TestRasterReceipt()
    {
        var a = ReceiptAnalyzer.Analyze(Receipts.Raster());
        Assert.Equal(ReceiptKind.Image, a.Kind, "kind");
        Assert.True(a.HasRaster && a.HasCut, "raster and cut");
    }

    public static void TestEscStarRaster()
    {
        var o = new List<byte> { 0x1B, 0x40 };
        for (int band = 0; band < 30; band++)
        {
            o.AddRange(new byte[] { 0x1B, 0x2A, 33, 0x40, 0x02 }); // 576 columns x 3 bytes
            o.AddRange(Enumerable.Repeat((byte)0x0A, 576 * 3)); // payload full of LF bytes: must not become lines
            o.Add(0x0A);
        }
        var a = ReceiptAnalyzer.Analyze(o.ToArray());
        Assert.Equal(ReceiptKind.Image, a.Kind, "kind");
    }

    public static void TestTextReceiptWithLogo()
    {
        var logo = new List<byte> { 0x1D, 0x76, 0x30, 0x00, 48, 0, 100, 0 };
        logo.AddRange(Receipts.Garbage(3, 48 * 100));
        var job = logo.Concat(Receipts.Cp1256(Receipts.Arabic)).ToArray();
        var a = ReceiptAnalyzer.Analyze(job);
        Assert.Equal(ReceiptKind.Text, a.Kind, "kind");
        Assert.True(a.HasRaster, "has logo");
        Assert.Equal("10432", ReceiptExtractor.Extract(a.Lines).InvoiceNo, "invoice");
    }

    public static void TestGarbageIsUnknown()
    {
        for (int seed = 1; seed <= 40; seed++)
            Assert.Equal(ReceiptKind.Unknown, ReceiptAnalyzer.Analyze(Receipts.Garbage(seed)).Kind, "seed " + seed);
    }

    public static void TestForeignFormatIsUnknown()
    {
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\nInvoice No 5 Total 3.000 lots of text here to pass the length\n");
        Assert.Equal(ReceiptKind.Unknown, ReceiptAnalyzer.Analyze(pdf).Kind, "pdf");
        Assert.Equal(ReceiptKind.Unknown, ReceiptAnalyzer.Analyze(Array.Empty<byte>()).Kind, "empty");
    }

    public static void TestCommandsDoNotLeakIntoText()
    {
        var job = Receipts.Ascii(new[] { "Invoice No: 77", "Total 1.500" }).ToList();
        // a QR (GS ( k) whose payload looks like text
        var url = System.Text.Encoding.ASCII.GetBytes("https://daymanjo.com/c/x/s/ABCDEFGH");
        job.InsertRange(2, new byte[] { 0x1D, 0x28, 0x6B, (byte)(url.Length + 3), 0, 0x31, 0x50, 0x30 }.Concat(url));
        var p = EscPosParser.Parse(job.ToArray());
        var text = string.Join("\n", p.Lines.Select(l => System.Text.Encoding.ASCII.GetString(l)));
        Assert.True(!text.Contains("daymanjo"), "qr payload leaked: " + text);
        Assert.True(text.Contains("Invoice No: 77"), "text kept");
    }
}

public static class ExtractionTests
{
    static ExtractResult X(params string[] lines) => ReceiptExtractor.Extract(lines);

    public static void TestEnglishLabels()
    {
        var e = X("Bill # 55", "Sub Total 3.000", "Net 4.000");
        Assert.Equal("55", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(4m, e.Total, "total");
    }

    public static void TestCommaDecimal()
    {
        var e = X("Inv no 9001", "Total: 7,250");
        Assert.Equal("9001", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(7.25m, e.Total, "total");
    }

    public static void TestArabicIndicDigits()
    {
        var e = X("رقم الفاتورة ٥٥٢١", "المطلوب ١٢٫٥٠٠ د.أ");
        Assert.Equal("5521", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(12.5m, e.Total, "total");
        Assert.True(e.TotalConfidence >= 0.95, "currency boost");
    }

    public static void TestFatooraRaqam()
    {
        var e = X("فاتورة رقم 778", "عدد الأصناف 3", "الصافي 3.5 دينار");
        Assert.Equal("778", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(3.5m, e.Total, "total");
    }

    public static void TestPhoneAndTaxNumberAreNotInvoice()
    {
        var e = X("الرقم الضريبي 4455667", "هاتف 0791234567", "فاتورة 31", "الإجمالي شامل الضريبة 9.000");
        Assert.Equal("31", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(9m, e.Total, "inclusive total kept");
    }

    public static void TestGrandTotalBeatsTotal()
    {
        var e = X("Invoice 12", "Total 10.000", "Discount 1.000", "Grand Total 9.000", "Cash 10.000", "Change 1.000");
        Assert.Equal<decimal?>(9m, e.Total, "total");
    }

    public static void TestThousands()
    {
        var e = X("Invoice No 1", "Total 1,250.750");
        Assert.Equal<decimal?>(1250.75m, e.Total, "total");
    }

    public static void TestValueOnNextLine()
    {
        var e = X("رقم الفاتورة", "A-77", "المجموع", "15.000");
        Assert.Equal("A-77", e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(15m, e.Total, "total");
        Assert.True(e.Confidence < 0.95, "lower confidence");
    }

    public static void TestOrderNumberIsNotInvoice()
    {
        var e = X("رقم الطلب 17", "Order no 17", "Bill 17", "Total 5.000");
        Assert.Equal<string?>(null, e.InvoiceNo, "daily order number is never the invoice");
    }

    public static void TestNothingFound()
    {
        var e = X("Welcome", "Thank you");
        Assert.Equal<string?>(null, e.InvoiceNo, "invoice");
        Assert.Equal<decimal?>(null, e.Total, "total");
        Assert.Equal(0.0, e.Confidence, "confidence");
    }

    public static void TestParseAmount()
    {
        void P(string s, decimal? v) => Assert.Equal(v, ReceiptExtractor.ParseAmount(s).value, s);
        P("12.500", 12.5m); P("12,500", 12.5m); P("1.250,750", 1250.75m); P("1,250,000", 1250000m);
        P("12", 12m); P("0.75", 0.75m); P("1.2345", null); P("1,25,0", null);
    }
}

public static class QrTests
{
    public static void TestQrBytes()
    {
        const string url = "https://daymanjo.com/c/abc/s/K7Q2M9XP";
        var b = QrReceipt.Build(url, 864, 22);
        var hex = Convert.ToHexString(b);
        Assert.True(!hex.StartsWith("1B40"), "no ESC @");
        Assert.True(hex.Contains("1D286B040031413200"), "model 2");
        Assert.True(hex.Contains("1D286B03003143" + "06"), "size 6");
        Assert.True(hex.Contains("1D286B0300314531"), "error M");
        int len = url.Length + 3;
        Assert.True(hex.Contains("1D286B" + len.ToString("X2") + "00315030" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(url))), "store");
        Assert.True(hex.Contains("1D286B0300315130"), "print");
        Assert.True(hex.Contains("1B7416"), "code table re-sent");
        Assert.True(hex.EndsWith("1D564200"), "cut at the end");
        // WO-101 layout: centre, code table, line, bold caption, QR, small footer, back to normal text.
        var caption = Convert.ToHexString(ArabicPrintEncoder.Encode(QrReceipt.Caption(null, null), 864));
        var line = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(QrReceipt.Separator));
        Assert.True(hex.StartsWith("1B61011B7416" + line + "0A" + "1B2108" + caption + "1B21000A1D286B"), "line and bold caption over the QR");
        var footer = Convert.ToHexString(ArabicPrintEncoder.Encode(QrReceipt.Footer, 864));
        Assert.True(hex.EndsWith("1D286B03003151300A" + "1B2101" + footer + "1B21000A" + "1B61001B64041D564200"), "small footer after the QR");
        Assert.True(hex.IndexOf("1D286B0300315130", StringComparison.Ordinal) > hex.IndexOf(caption, StringComparison.Ordinal), "caption before the QR");
    }

    public static void TestCaptionRoundTrips()
    {
        foreach (int cp in new[] { 864, 720, 1256 })
        {
            Assert.True(!ArabicPrintEncoder.Encode(QrReceipt.Footer, cp).Contains((byte)'?'), "footer encodable in " + cp);
            foreach (var (mode, amount) in new[] { ("spend", (string?)null), ("spend", "1.000"), ("spend", "2.5"), ("visit", "7.000") })
            {
                var caption = QrReceipt.Caption(mode, amount);
                var bytes = ArabicPrintEncoder.Encode(caption, cp);
                Assert.True(!bytes.Contains((byte)'?'), "unencodable glyph in " + cp + ": " + caption);
                var back = ReceiptAnalyzer.GetDecoder(cp).GetString(bytes);
                var logical = TextNormalizer.Normalize(TextNormalizer.Reverse(back));
                Assert.Equal(TextNormalizer.Normalize(caption), logical, "caption " + cp);
            }
        }
    }

    // WO-107: the caption says what the scan gives, in the Arabic count form.
    public static void TestCaptionCountForms()
    {
        const string s = QrReceipt.CaptionStart;
        Assert.Equal(s + "نقطة واحدة", QrReceipt.Caption("spend", "1.000"), "1");
        Assert.Equal(s + "نقطتين", QrReceipt.Caption("spend", "2.990"), "2");
        Assert.Equal(s + "3 نقاط", QrReceipt.Caption("spend", "3.000"), "3");
        Assert.Equal(s + "10 نقاط", QrReceipt.Caption("spend", "10.500"), "10");
        Assert.Equal(s + "11 نقطة", QrReceipt.Caption("spend", "11.000"), "11");
        Assert.Equal(s + "125 نقطة", QrReceipt.Caption(null, "125.750"), "unknown mode counts as spend");
        Assert.Equal(s + "نقاط فاتورتك", QrReceipt.Caption("spend", "0.750"), "under 1 JOD");
        Assert.Equal(s + "نقاط فاتورتك", QrReceipt.Caption("spend", null), "amount not known");
        Assert.Equal(s + "نقاط فاتورتك", QrReceipt.Caption("spend", "abc"), "amount unreadable");
        Assert.Equal(s + "ختم", QrReceipt.Caption("visit", "12.500"), "visit");
        Assert.Equal(s + "ختم", QrReceipt.Caption("visit", null), "visit instant");
        // The slip carries it bold; the instant pool slip has no amount yet.
        const string url = "https://daymanjo.com/c/abc/s/K7Q2M9XP";
        var full = Convert.ToHexString(QrReceipt.Build(url, 864, 22, "12.500", "spend"));
        Assert.True(full.Contains("1B2108" + Convert.ToHexString(ArabicPrintEncoder.Encode(s + "12 نقطة", 864)) + "1B2100"), "points caption");
        var visit = Convert.ToHexString(QrReceipt.Build(url, 864, 22, "12.500", "visit"));
        Assert.True(visit.Contains("1B2108" + Convert.ToHexString(ArabicPrintEncoder.Encode(s + "ختم", 864)) + "1B2100"), "stamp caption");
        var instant = Convert.ToHexString(QrReceipt.Build(url, 864, 22, stampMode: "spend"));
        Assert.True(instant.Contains("1B2108" + Convert.ToHexString(ArabicPrintEncoder.Encode(s + "نقاط فاتورتك", 864)) + "1B2100"), "instant caption");
    }

    // WO-107: no invoice or amount line in any code page, nor in ASCII; the footer follows the QR.
    public static void TestQrNoInvoiceLine()
    {
        const string url = "https://daymanjo.com/c/abc/s/K7Q2M9XP";
        foreach (int cp in new[] { 864, 720, 1256, 65001 })
        {
            var slip = Convert.ToHexString(QrReceipt.Build(url, cp, 22, "12.500", "spend"));
            Assert.True(slip.Contains("1D286B03003151300A1B2101"), "footer right under the QR in " + cp);
        }
        var plain = System.Text.Encoding.ASCII.GetString(QrReceipt.Build(url, null, null, "12.500", "spend"));
        Assert.True(!plain.Contains("12.500") && !plain.Contains("JOD"), "no ascii amount line");
        Assert.True(plain.Contains("Dayman") && !plain.Contains("\u001B!\u0008"), "ascii footer, no Arabic caption");
    }

    public static void TestQrRejectsBadUrl()
    {
        bool threw = false;
        try { QrReceipt.Build("https://x/م", 864, null); } catch (ArgumentException) { threw = true; }
        Assert.True(threw, "non-ascii url refused");
    }
}
