using System.Text;

namespace DaymanDesk.Core;

/// <summary>
/// Builds the QR slip printed after a registered receipt: a line, the bold caption, native ESC/POS QR (GS ( k), a small
/// footer, cut (WO-107: no invoice line).
/// </summary>
public static class QrReceipt
{
    /// <summary>WO-113 (Ali): one fixed caption on every slip, whatever the shop's mode or the receipt's amount.</summary>
    public const string FixedCaption = "امسح لتجميع النقاط والحصول على مكافآت";

    /// <summary>The bold caption over the QR (WO-113: fixed; the parameters are kept for the call sites).</summary>
    public static string Caption(string? stampMode, string? amount) => FixedCaption;

    /// <summary>The small footer: the brand without its tanween (no code page the printers use has the mark).</summary>
    public const string Footer = "دايما";
    /// <summary>A thin line in ASCII (the same bytes in every code page), 32 columns: fits 58 mm and 80 mm paper.</summary>
    public const string Separator = "--------------------------------";

    /// <param name="codePage">The shop's decoding (864, 720, 1256, 65001); null prints no Arabic caption.</param>
    /// <param name="codeTable">The ESC t value seen in the shop's own receipts, re-sent so the caption uses the same table.</param>
    /// <param name="amount">The receipt's amount in JOD as sent to /receipt; null when not known yet.</param>
    /// <param name="stampMode">The shop's mode ("spend" or "visit"); unknown counts as spend.</param>
    public static byte[] Build(string url, int? codePage, int? codeTable, string? amount = null,
        string? stampMode = null, int size = 6)
    {
        var data = Encoding.ASCII.GetBytes(url);
        if (data.Length == 0 || data.Length > 700) throw new ArgumentException("url length", nameof(url));
        if (url.Any(c => c > 0x7E || c < 0x21)) throw new ArgumentException("url must be printable ASCII", nameof(url));

        var o = new List<byte>(data.Length + 128);
        // WO-101 layout, centred: a thin line, the caption in bold, the QR, a small «دايما» (WO-107).
        // No ESC @: it would reset the code table the POS selected. Text modes go back to normal (ESC ! 0) at the end.
        o.AddRange(new byte[] { 0x1B, 0x61, 0x01 });                                     // centre
        if (codePage != null && codeTable != null) o.AddRange(new byte[] { 0x1B, 0x74, (byte)codeTable.Value });
        o.AddRange(Encoding.ASCII.GetBytes(Separator));
        o.Add(0x0A);
        if (codePage != null)
        {
            o.AddRange(new byte[] { 0x1B, 0x21, 0x08 });                                 // bold
            o.AddRange(ArabicPrintEncoder.Encode(Caption(stampMode, amount), codePage.Value));
            o.AddRange(new byte[] { 0x1B, 0x21, 0x00 });
            o.Add(0x0A);
        }
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });  // model 2
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)size });  // module size
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });        // error correction M
        int len = data.Length + 3;
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30 }); // store
        o.AddRange(data);
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });        // print
        o.Add(0x0A);
        o.AddRange(new byte[] { 0x1B, 0x21, 0x01 });                                     // small (font B)
        o.AddRange(codePage != null ? ArabicPrintEncoder.Encode(Footer, codePage.Value) : Encoding.ASCII.GetBytes("Dayman"));
        o.AddRange(new byte[] { 0x1B, 0x21, 0x00 });                                     // normal
        o.Add(0x0A);
        o.AddRange(new byte[] { 0x1B, 0x61, 0x00 });       // left
        o.AddRange(new byte[] { 0x1B, 0x64, 0x04 });       // feed 4 lines
        o.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // feed and partial cut
        return o.ToArray();
    }
}
