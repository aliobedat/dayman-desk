using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DaymanDesk.App;

/// <summary>Sends bytes to a Windows print queue untouched (datatype RAW) through winspool.drv.</summary>
static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void Write(string printer, byte[] data, string docName)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter " + printer);
        try
        {
            if (StartDocPrinter(h, 1, new DOC_INFO_1 { pDocName = docName }) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter");
            try
            {
                if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter");
                int offset = 0;
                while (offset < data.Length)
                {
                    var chunk = offset == 0 && data.Length <= 1 << 20 ? data : data.AsSpan(offset, Math.Min(1 << 20, data.Length - offset)).ToArray();
                    if (!WritePrinter(h, chunk, chunk.Length, out int written) || written <= 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter");
                    offset += written; // a partial write continues from the new offset
                }
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}

/// <summary>The forwarder's raw queue (Core IRawQueue) on winspool.</summary>
sealed class WinspoolQueue : DaymanDesk.Core.IRawQueue
{
    public void Write(string queue, byte[] data, string docName) => RawPrinter.Write(queue, data, docName);
}
