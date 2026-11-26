using System.Net;
using System.Net.NetworkInformation;
using DaymanDesk.Core;

namespace DaymanDesk.App;

static class Program
{
    static Mutex? _single;

    /// <summary>WO-96: the old version lets the new one start (call on the main thread, the mutex owner).</summary>
    public static void ReleaseSingle() { try { _single?.ReleaseMutex(); } catch (ApplicationException) { } }

    /// <summary>WO-96: the update was rolled back; this process owns the program again.</summary>
    public static bool RetakeSingle()
    {
        try { return _single?.WaitOne(5000) ?? false; } catch (AbandonedMutexException) { return true; }
    }

    internal const string NeedsNet48 = "يحتاج دايماً ديسك إلى ‎.NET Framework 4.8 (وليس 4.8.1). نزّل ملف التثبيت من daymanjo.com/desk/download وشغّله، فيثبّت 4.8 تلقائياً.";
    internal const string AlreadyRunning = "دايماً ديسك يعمل الآن. افتحه من الأيقونة بجانب الساعة.\n\nلتثبيت إصدار جديد: اختر «خروج (إرجاع الطابعة)» من الأيقونة، ثم شغّل هذا الملف مرة أخرى.";

    /// <summary>WO-101: a message before the main window exists, in the themed dialog (the system box if it cannot be drawn).</summary>
    static void Notice(string text, MessageBoxIcon icon)
    {
        try
        {
            Application.EnableVisualStyles();
            Ui.Msg(null, text, "دايماً ديسك", icon);
        }
        catch
        {
            MessageBox.Show(text, "دايماً ديسك", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // WO-95b: on an older .NET Framework 4.x the exe would start and fail later; say what is missing instead.
        if (!HasNet48())
        {
            if (!args.Contains("--watchdog") && !args.Contains("--tray"))
                Notice(NeedsNet48, MessageBoxIcon.Warning);
            return 2;
        }
        ReceiptAnalyzer.EnsureCodePages();
        if (args.Contains("--watchdog")) return RunWatchdog();
        // CI only (WO-95): reads a receipt picture with the bundled OCR, as an image receipt is read.
        int selftest = Array.IndexOf(args, "--ocr-selftest");
        if (selftest >= 0 && selftest + 1 < args.Length) return OcrSelfTest(args[selftest + 1]);
        int signtest = Array.IndexOf(args, "--sign-selftest");
        if (signtest >= 0 && signtest + 1 < args.Length) return SignSelfTest(args[signtest + 1]);
        int updtest = Array.IndexOf(args, "--update-selftest");
        if (updtest >= 0 && updtest + 2 < args.Length) return AutoUpdate.SelfTest(args[updtest + 1], args[updtest + 2]);
        int from = Array.IndexOf(args, "--updated-from");
        string? updatedFrom = from >= 0 && from + 1 < args.Length ? args[from + 1] : null;

        using var single = new Mutex(true, @"Global\DaymanDesk.App", out bool first);
        // WO-96: started by the previous version, which releases the mutex just before.
        if (!first && updatedFrom != null)
        {
            try { first = single.WaitOne(15000); } catch (AbandonedMutexException) { first = true; }
        }
        _single = single;
        if (!first)
        {
            if (!args.Contains("--tray"))
                Notice(AlreadyRunning, MessageBoxIcon.Information);
            return 0;
        }

        // WO-95: .NET Framework 4.8 on Windows 7 does not offer TLS 1.2 by default; the server accepts nothing older.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        EnsureIsrgRootX1();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // CI only: renders every tab and dialog to PNG files (no printer, no network, no Start), on a fresh temporary
        // data folder: the preview writes a fake pairing, which must never reach a shop's real config (WO-101).
        int shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0)
        {
            if (Environment.GetEnvironmentVariable("CI") != "true" || shots + 1 >= args.Length) return 2;
            Runtime.UseTempDataDir();
            using var preview = new Runtime();
            return Screenshots.Run(preview, args[shots + 1]);
        }
        using var rt = new Runtime();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            rt.Log.Error("CRASH " + e.ExceptionObject);
            try { rt.Agent.ReportAsync("error", "crash", e.ExceptionObject.ToString() ?? "crash").Wait(3000); } catch { }
            rt.ExitRestore();
        };
        Application.ThreadException += (_, e) =>
        {
            rt.Log.Error("UI error " + e.Exception);
            _ = rt.Agent.ReportAsync("error", "crash", "ui: " + e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { rt.Log.Error("task error " + e.Exception.Message); e.SetObserved(); };

        rt.Start();
        if (updatedFrom != null) rt.UpdatedFrom(updatedFrom);
        Application.Run(new MainForm(rt, startHidden: args.Contains("--tray")));
        return 0;
    }

    /// <summary>
    /// Reads the PNG twice (as printed and upscaled, as DeskAgent does) and writes the result to &lt;png&gt;.txt. Exit 0
    /// only when both reads give the same invoice number and total.
    /// </summary>
    static int OcrSelfTest(string png)
    {
        var o = new System.Text.StringBuilder();
        int code = 1;
        try
        {
            MonoBitmap m;
            using (var bmp = new System.Drawing.Bitmap(png))
            {
                m = new MonoBitmap(bmp.Width, bmp.Height);
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        m.Pixels[y * bmp.Width + x] = bmp.GetPixel(x, y).GetBrightness() < 0.5f ? (byte)1 : (byte)0;
            }
            var log = new RollingLog(Path.Combine(Path.GetTempPath(), "dayman-ocr-selftest"));
            using var ocr = new TesseractOcr(log, Installer.LockDataDir(log));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var first = ocr.ReadAsync(m).GetAwaiter().GetResult();
            long t1 = sw.ElapsedMilliseconds;
            var second = ocr.ReadAsync(m.Scale2x()).GetAwaiter().GetResult();
            long t2 = sw.ElapsedMilliseconds - t1;
            if (first == null || second == null) o.AppendLine("ocr unavailable (see the log in %TEMP%\\dayman-ocr-selftest)");
            else
            {
                var a = ReceiptExtractor.Extract(OcrText.Clean(first), ocr: true);
                var b = ReceiptExtractor.Extract(OcrText.Clean(second), ocr: true);
                o.AppendLine("read 1 (" + t1 + " ms): " + string.Join(" | ", first));
                o.AppendLine("read 2 (" + t2 + " ms): " + string.Join(" | ", second));
                o.AppendLine($"invoice {a.InvoiceNo}/{b.InvoiceNo} total {a.Total}/{b.Total}");
                if (a.InvoiceNo != null && a.Total != null && a.InvoiceNo == b.InvoiceNo && a.Total == b.Total) code = 0;
            }
        }
        catch (Exception ex) { o.AppendLine("error: " + ex); }
        File.WriteAllText(png + ".txt", o.ToString());
        return code;
    }

    /// <summary>
    /// WO-103: an old Windows 7 store lacks ISRG Root X1, the root of the server's certificate. When elevated, add it
    /// from the embedded file (SHA-256 checked), as DaymanDeskSetup does. Best effort: never stops the program.
    /// </summary>
    static void EnsureIsrgRootX1()
    {
        try
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (!new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return;
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.Root, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            if (store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                    "CABD2A79A1076A31F21D253635CB039D4329A5E8", false).Count > 0) return;
            using var s = typeof(Program).Assembly.GetManifestResourceStream("certs/isrg-root-x1.der");
            if (s == null) return;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var der = ms.ToArray();
            using var sha = System.Security.Cryptography.SHA256.Create();
            if (BitConverter.ToString(sha.ComputeHash(der)).Replace("-", "") != "96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6") return;
            store.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(der));
        }
        catch { }
    }

    /// <summary>.NET Framework 4.8 or later (registry Release 528040+).</summary>
    static bool HasNet48()
    {
        try
        {
            using var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            return k?.GetValue("Release") is int r && r >= 528040;
        }
        catch { return true; } // unreadable: try to run
    }

    /// <summary>
    /// CI only (WO-95b): the signing path on .NET Framework: a new key sealed with DPAPI and opened again, a payload
    /// signed, and the signature verified with the public key rebuilt from the SPKI. Result in &lt;out&gt;.
    /// </summary>
    static int SignSelfTest(string outFile)
    {
        string result; int code = 1;
        try
        {
            var (priv, spki) = DeskSigner.CreateKey();
            var dp = new DpapiProtector();
            var opened = dp.Unprotect(dp.Protect(priv)) ?? throw new InvalidOperationException("DPAPI round trip");
            var payload = DeskSigner.Payload(1, 42, 12_500, DeskSigner.Day(Amman.Now), "10432");
            using var key = DeskSigner.Open(opened);
            var token = DeskSigner.Token(payload, key);
            var sig = DeskSigner.FromB64Url(token.Split('.')[1]);
            var der = Convert.FromBase64String(spki);
            var q = new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new System.Security.Cryptography.ECPoint { X = der.Skip(27).Take(32).ToArray(), Y = der.Skip(59).Take(32).ToArray() },
            };
            using var pub = System.Security.Cryptography.ECDsa.Create(q);
            bool ok = der.Length == 91 && sig.Length == 64 && pub.VerifyData(payload, sig, System.Security.Cryptography.HashAlgorithmName.SHA256)
                && DeskSigner.SeqOf(token) == 42;
            result = $"{(ok ? "ok" : "FAILED")} {key.GetType().Name} sig {sig.Length} B, token {token.Length} chars";
            if (ok) code = 0;
        }
        catch (Exception ex) { result = "error: " + ex; }
        File.WriteAllText(outFile, result);
        return code;
    }

    /// <summary>Run by the scheduled task every minute as SYSTEM. Cheap when all is well: one socket table read.</summary>
    static int RunWatchdog()
    {
        try
        {
            var cfgPath = Path.Combine(Runtime.DataDir, "config.json");
            if (!File.Exists(cfgPath) && !File.Exists(cfgPath + ".bak")) return 0;
            // No token protector and no Update: the watchdog never writes config.json, so it never writes the token.
            var store = new ConfigStore(cfgPath); // falls back to config.json.bak
            var cfg = store.Current;
            // WO-91: after a confirmed «خروج» restore the printers are on their own ports: nothing to watch, no PowerShell.
            bool printersIdle = cfg.Printers.Count == 0 || cfg.RestoredAtExit;
            if (printersIdle && !cfg.Network.Enabled) return 0;
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            bool Listening(int port) => listeners.Any(e => e.Port == port && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any)));
            bool networkDown = Watchdog.NetworkDown(cfg, Listening);
            if ((printersIdle || cfg.Printers.All(r => Listening(r.ListenPort))) && !networkDown) return 0;
            var log = new RollingLog(Path.Combine(Runtime.DataDir, "logs"));
            if (store.LoadedFromBackup) log.Warn("watchdog: config.json unreadable, using config.json.bak");
            var shell = new PowerShellShell(log);
            if (networkDown)
            {
                // Network mode has no Windows port to restore (the address is set on the tablet): start the program again.
                log.Warn($"watchdog: nothing listens on network-mode port {cfg.Network.ListenPort}, starting the tray task");
                shell.Run(ScheduledTasks.StartTrayScript());
            }
            if (printersIdle) return 0;
            try { Watchdog.Run(cfg, new PrinterSetup(shell, log), Listening, log); }
            catch (Exception ex) { log.Error("watchdog: restore not confirmed: " + ex.Message); return 1; }
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
