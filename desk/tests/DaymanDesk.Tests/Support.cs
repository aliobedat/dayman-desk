using System.Net;
using System.Text;
using DaymanDesk.Core;

namespace DaymanDesk.Tests;

public sealed class AssertException : Exception { public AssertException(string m) : base(m) { } }

public static class Assert
{
    public static void True(bool c, string what) { if (!c) throw new AssertException(what); }
    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what} expected <{expected}> got <{actual}>");
    }
}

/// <summary>Builds synthetic ESC/POS jobs the way POS programs send them.</summary>
public static class Receipts
{
    public static readonly string[] Arabic =
    {
        "مطعم جمرة",
        "فرع عين الباشا",
        "هاتف: 0791234567",
        "رقم الفاتورة: 10432",
        "التاريخ: 19/11/2026  18:02",
        "شاورما عربي   2   5.000",
        "بيبسي   1   0.750",
        "المجموع: 5.750",
        "الضريبة 16%: 0.920",
        "الإجمالي: 6.670",
        "نقدا: 10.000",
        "الباقي: 3.330",
        "شكرا لزيارتكم",
    };

    public static readonly string[] English =
    {
        "JAMRA RESTAURANT",
        "Tel 06 5555555",
        "Invoice No: INV-2291",
        "Date 19/11/2026 18:02",
        "Burger x2        8.000",
        "Subtotal        10.000",
        "Tax 16%          1.600",
        "Grand Total     11.600 JOD",
        "Cash            20.000",
        "Change           8.400",
        "Thank you",
    };

    public static byte[] Job(IEnumerable<byte[]> lines, int? codeTable = null)
    {
        var o = new List<byte> { 0x1B, 0x40 };
        if (codeTable != null) o.AddRange(new byte[] { 0x1B, 0x74, (byte)codeTable.Value });
        o.AddRange(new byte[] { 0x1B, 0x61, 0x01, 0x1B, 0x45, 0x01 }); // centre, bold
        bool first = true;
        foreach (var l in lines)
        {
            o.AddRange(l);
            o.Add(0x0A);
            if (first) { o.AddRange(new byte[] { 0x1B, 0x45, 0x00, 0x1B, 0x61, 0x00 }); first = false; }
        }
        o.AddRange(new byte[] { 0x1B, 0x64, 0x03, 0x1D, 0x56, 0x42, 0x00 });
        return o.ToArray();
    }

    public static byte[] Cp864(string[] lines) =>
        Job(lines.Select(l => ArabicPrintEncoder.Encode(l, 864)), codeTable: 22);

    public static byte[] Cp1256(string[] lines, bool visual = false) =>
        Job(lines.Select(l => ReceiptAnalyzer.GetDecoder(1256).GetBytes(visual ? ArabicPrintEncoder.ToVisual(l) : l)));

    public static byte[] Utf8(string[] lines) => Job(lines.Select(l => Encoding.UTF8.GetBytes(l)));

    public static byte[] Ascii(string[] lines) => Job(lines.Select(l => Encoding.ASCII.GetBytes(l)));

    /// <summary>A receipt a Windows driver rendered as one raster image (GS v 0), 576 dots x 400 lines.</summary>
    public static byte[] Raster()
    {
        var o = new List<byte> { 0x1B, 0x40 };
        int xBytes = 72, y = 400;
        o.AddRange(new byte[] { 0x1D, 0x76, 0x30, 0x00, (byte)xBytes, 0, (byte)(y & 0xFF), (byte)(y >> 8) });
        var rnd = new Random(7);
        for (int i = 0; i < xBytes * y; i++) o.Add((byte)(rnd.Next(4) == 0 ? rnd.Next(256) : 0));
        o.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 });
        return o.ToArray();
    }

    public static byte[] Garbage(int seed = 42, int n = 4000)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }
}

public sealed class FakeHandler : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage req, string body)> Calls = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond = (_, _) => Json(200, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
        Calls.Add((request, body));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Offline() => throw new HttpRequestException("no route to host");
}

public sealed class FakeShell : IShell
{
    public readonly List<string> Scripts = new();
    public string Port = "USB001";
    public Func<string, ShellResult?> Override = _ => null;

    public ShellResult Run(string script)
    {
        Scripts.Add(script);
        var o = Override(script);
        if (o != null) return o;
        if (script == PrinterSetup.ListScript)
            return new ShellResult(0, "POS-80\t" + Port + "\tPOS-80 Driver\t\t0\t0\nMicrosoft Print to PDF\tPORTPROMPT:\tx\t\t0\t0", "");
        if (script.EndsWith("$p[0].PortName")) return new ShellResult(0, Port, "");
        if (script.EndsWith("[void]$p[0].Put()"))
        {
            var i = script.LastIndexOf("$p[0].PortName = '", StringComparison.Ordinal);
            Port = script.Substring(i + 18, script.Length - (i + 18) - "'; [void]$p[0].Put()".Length);
        }
        return new ShellResult(0, "", "");
    }
}

public static class Tmp
{
    public static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dayman-desk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
