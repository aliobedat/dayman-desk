using System.Text;

namespace DaymanDesk.Core;

/// <summary>
/// Builds the QR slip printed after a registered receipt: native ESC/POS QR (GS ( k) + caption + the invoice line
/// (WO-88: with two tills on one printer the customer matches the slip to his receipt) + cut.
/// </summary>
public static class QrReceipt
{
    public const string Caption = "امسح لتحصل على نقاطك";

    /// <summary>«فاتورة 10432 · 12.500 د.أ»: the invoice number and the amount exactly as sent to /receipt.</summary>
    public static string InvoiceLine(string invoiceNo, string amount) => $"فاتورة {Printable(invoiceNo)} · {Printable(amount)} د.أ";

    /// <summary>The same line in ASCII, for a printer whose decoding is unknown (no Arabic).</summary>
    public static string InvoiceLineAscii(string invoiceNo, string amount) => $"{Printable(invoiceNo)} - {Printable(amount)} JOD";

    // Never a control byte from an extracted field into the printer stream.
    static string Printable(string s) => new(s.Where(c => c >= 0x20 && c != 0x7F).ToArray());

    /// <param name="codePage">The shop's decoding (864, 720, 1256, 65001); null prints no Arabic caption.</param>
    /// <param name="codeTable">The ESC t value seen in the shop's own receipts, re-sent so the caption uses the same table.</param>
    /// <param name="invoiceNo">The invoice number as sent to /receipt; with <paramref name="amount"/>, one line under the caption.</param>
    public static byte[] Build(string url, int? codePage, int? codeTable, string? invoiceNo = null, string? amount = null, int size = 6)
    {
        var data = Encoding.ASCII.GetBytes(url);
        if (data.Length == 0 || data.Length > 700) throw new ArgumentException("url length", nameof(url));
        if (url.Any(c => c > 0x7E || c < 0x21)) throw new ArgumentException("url must be printable ASCII", nameof(url));

        var o = new List<byte>(data.Length + 64);
        // No ESC @: it would reset the code table the POS selected.
        o.AddRange(new byte[] { 0x1B, 0x61, 0x01 });                                     // centre
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });  // model 2
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)size });  // module size
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });        // error correction M
        int len = data.Length + 3;
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30 }); // store
        o.AddRange(data);
        o.AddRange(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });        // print
        o.Add(0x0A);
        if (codePage != null)
        {
            if (codeTable != null) o.AddRange(new byte[] { 0x1B, 0x74, (byte)codeTable.Value });
            o.AddRange(ArabicPrintEncoder.Encode(Caption, codePage.Value));
            o.Add(0x0A);
            if (invoiceNo != null && amount != null)
            {
                o.AddRange(ArabicPrintEncoder.Encode(InvoiceLine(invoiceNo, amount), codePage.Value));
                o.Add(0x0A);
            }
        }
        else if (invoiceNo != null && amount != null)
        {
            o.AddRange(Encoding.ASCII.GetBytes(InvoiceLineAscii(invoiceNo, amount)));
            o.Add(0x0A);
        }
        o.AddRange(new byte[] { 0x1B, 0x61, 0x00 });       // left
        o.AddRange(new byte[] { 0x1B, 0x64, 0x04 });       // feed 4 lines
        o.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // feed and partial cut
        return o.ToArray();
    }
}
