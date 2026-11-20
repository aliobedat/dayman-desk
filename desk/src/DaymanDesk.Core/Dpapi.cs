using System.Runtime.InteropServices;
using System.Text;

namespace DaymanDesk.Core;

/// <summary>
/// The device token sealed with Windows DPAPI (CryptProtectData, LocalMachine scope) through crypt32.dll,
/// no NuGet package. LocalMachine: the tray program (the cashier's account) and the watchdog (SYSTEM) both
/// open it, and a config.json copied to another PC opens nowhere. The data folder ACL still guards it.
/// Windows only: on other systems every call throws PlatformNotSupportedException.
/// </summary>
public sealed class DpapiProtector : ITokenProtector
{
    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;
    static readonly byte[] Entropy = Encoding.ASCII.GetBytes("DaymanDesk.DeviceToken.v1");

    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    public string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI");
        var output = Run(Encoding.UTF8.GetBytes(plain), protect: true)
            ?? throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());
        return Convert.ToBase64String(output);
    }

    public string? Unprotect(string sealedBase64)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI");
        byte[] input;
        try { input = Convert.FromBase64String(sealedBase64); }
        catch (FormatException) { return null; }
        var output = Run(input, protect: false);
        return output == null ? null : Encoding.UTF8.GetString(output);
    }

    static byte[]? Run(byte[] input, bool protect)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var enH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inH.AddrOfPinnedObject() };
            var enBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = enH.AddrOfPinnedObject() };
            int flags = CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE;
            DATA_BLOB outBlob;
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Dayman Desk", ref enBlob, IntPtr.Zero, IntPtr.Zero, flags, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref enBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok) return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            inH.Free();
            enH.Free();
        }
    }
}
