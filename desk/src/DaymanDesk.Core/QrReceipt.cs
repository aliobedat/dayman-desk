using System.Text;

namespace DaymanDesk.Core;

/// <summary>Builds the QR slip printed after a registered receipt: native ESC/POS QR (GS ( k) + caption + cut.</summary>
public static class QrReceipt
{
    public const string Caption = "امسح لتحصل على نقاطك";

    /// <param name="codePage">The shop's decoding (864, 720, 1256, 65001); null prints no Arabic caption.</param>
    /// <param name="codeTable">The ESC t value seen in the shop's own receipts, re-sent so the caption uses the same table.</param>
    public static byte[] Build(string url, int? codePage, int? codeTable, int size = 6)
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
        }
        o.AddRange(new byte[] { 0x1B, 0x61, 0x00 });       // left
        o.AddRange(new byte[] { 0x1B, 0x64, 0x04 });       // feed 4 lines
        o.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // feed and partial cut
        return o.ToArray();
    }
}
