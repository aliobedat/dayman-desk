using System.Diagnostics;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>
/// WO-96: the silent signed update of the installed exe. After a good heartbeat that announced a newer version:
/// download exe + .sig (Updater: allowed https hosts, 150 MB), verify the signature and the file version, then on the
/// UI thread, when idle, swap the exe in Program Files (the tasks and the shortcut keep their path) and hand over to it.
/// </summary>
static class AutoUpdate
{
    /// <summary>Set by the new process once it is listening (started with --updated-from).</summary>
    public const string UpEvent = @"Local\DaymanDesk.UpdateUp";
    static int _busy;

    static string OldExe => Installer.InstalledExe + ".old";

    /// <summary>Called after every good heartbeat: the previous version's file goes once this one has been up.</summary>
    public static void AfterGoodHeartbeat(Runtime rt)
    {
        try { if (File.Exists(OldExe)) { File.Delete(OldExe); rt.Log.Info("update: old version removed"); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static async Task TryAsync(Runtime rt)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        string? exe = null;
        try
        {
            var c = rt.Cfg.Current;
            var now = DateTimeOffset.Now;
            // The data folder must be ours alone (SYSTEM + Administrators), as for the OCR natives.
            if (c.UpdateVersion == null || c.UpdateUrl == null || !Installer.RunningInstalled || !rt.DataDirLocked) return;
            // A link to a page, not to the exe: no auto-update, the banner stays the manual way.
            if (!Uri.TryCreate(c.UpdateUrl, UriKind.Absolute, out var link) || !link.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            if (c.UpdateBadVersion == c.UpdateVersion && c.UpdateBadUntil > now) return;
            if (c.UpdateLastAttempt is { } last && now - last < Updater.AttemptEvery && now >= last) return;
            if (!Updater.Idle(rt.Server.OpenConnections, rt.Server.LastJobAt, now)) return;
            string v = c.UpdateVersion, url = c.UpdateUrl;
            rt.Cfg.Update(x => x.UpdateLastAttempt = now);
            var dir = Path.Combine(Runtime.DataDir, "update");
            Directory.CreateDirectory(dir);
            exe = Path.Combine(dir, "DaymanDesk.exe");
            try
            {
                rt.Log.Info($"update: downloading {v}");
                using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("DaymanDesk/" + Runtime.Version);
                    // The .sig first: an unsigned release (404) is no failure, the banner stays the manual way.
                    if (!await Updater.DownloadAsync(http, url + ".sig", exe + ".sig", Updater.MaxSigBytes))
                    {
                        rt.Log.Info($"update: {v} has no signature, manual update only");
                        Delete(exe);
                        return;
                    }
                    if (!await Updater.DownloadAsync(http, url, exe, Updater.MaxBytes)) throw new InvalidOperationException("exe 404");
                }
                if (!Updater.Verify(exe, File.ReadAllText(exe + ".sig"))) throw new InvalidOperationException("bad signature");
                var fv = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!Updater.VersionOk(v, fv, Runtime.Version)) throw new InvalidOperationException($"file version {fv}, announced {v}");
            }
            catch (Exception ex)
            {
                Fail(rt, v, "download: " + ex.Message);
                Delete(exe);
                return;
            }
            rt.Log.Info($"update: {v} downloaded and verified");
            rt.RaiseUpdateReady(v, exe);
            exe = null; // Apply owns the file now
        }
        catch (Exception ex) { rt.Log.Error("update: " + ex.Message); if (exe != null) Delete(exe); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>
    /// On the UI thread. True: the new version (or, after a rollback, another instance) is up, this process exits now (without restoring the printers: the new
    /// one listens on the same ports). False: nothing changed, or everything was rolled back and this one runs on.
    /// </summary>
    public static bool Apply(Runtime rt, string v, string newExe)
    {
        if (!Updater.Idle(rt.Server.OpenConnections, rt.Server.LastJobAt, DateTimeOffset.Now))
        {
            rt.Log.Info("update: busy printing, retried in an hour");
            Delete(newExe);
            return false;
        }
        string exe = Installer.InstalledExe;
        bool swapped = false, released = false;
        Process? p = null;
        using var up = new EventWaitHandle(false, EventResetMode.ManualReset, UpEvent);
        rt.Suspend();
        try
        {
            if (File.Exists(OldExe)) File.Delete(OldExe);
            File.Move(exe, OldExe); // a running exe may be renamed, not overwritten
            try { File.Copy(newExe, exe); } // a copy takes the Program Files ACL
            catch { File.Move(OldExe, exe); throw; }
            swapped = true;
            Program.ReleaseSingle();
            released = true;
            p = Process.Start(new ProcessStartInfo(exe, "--tray --updated-from " + Runtime.Version) { UseShellExecute = false, WorkingDirectory = Installer.InstallDir });
            if (up.WaitOne(Updater.StartTimeout))
            {
                rt.Log.Info($"update: {v} is up, this version ({Runtime.Version}) exits");
                Delete(newExe);
                return true;
            }
            throw new InvalidOperationException("the new version did not start within 20 s");
        }
        catch (Exception ex)
        {
            try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
            if (swapped)
            {
                try { File.Delete(exe); File.Move(OldExe, exe); }
                catch (Exception e2) { rt.Log.Error("update: rollback of the exe failed: " + e2.Message); }
            }
            if (released && !Program.RetakeSingle())
            {
                // Another instance owns the program (and the printers) now: exit without restoring them.
                rt.Log.Error("update: rolled back but the program mutex was taken by another instance: exiting");
                Fail(rt, v, "apply: " + ex.Message);
                Delete(newExe);
                return true;
            }
            rt.Resume();
            Fail(rt, v, "apply: " + ex.Message);
            Delete(newExe);
            return false;
        }
    }

    static void Fail(Runtime rt, string v, string why)
    {
        rt.Log.Error($"update {v} failed: {why}");
        rt.Cfg.Update(c => { c.UpdateBadVersion = v; c.UpdateBadUntil = DateTimeOffset.Now + Updater.BadVersionSkip; });
        rt.Agent.SetProblem(Problems.UpdateInstallFailed);
        _ = rt.Agent.ReportAsync("error", "other", $"{Problems.UpdateInstallFailed} {v}: {why}", force: true);
    }

    static void Delete(string path)
    {
        foreach (var f in new[] { path, path + ".sig" })
            try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>CI only: the file's .sig verifies (with DESK_UPDATE_TEST_SPKI, else the built-in key) and one changed byte does not.</summary>
    public static int SelfTest(string file, string outFile)
    {
        string result; int code = 1;
        try
        {
            var spki = Environment.GetEnvironmentVariable("DESK_UPDATE_TEST_SPKI");
            if (string.IsNullOrEmpty(spki)) spki = Updater.PublicKeySpki;
            var sig = File.ReadAllText(file + ".sig");
            bool good = Updater.Verify(file, sig, spki);
            var copy = Path.Combine(Path.GetTempPath(), "dayman-update-selftest.bin");
            var bytes = File.ReadAllBytes(file);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(copy, bytes);
            bool tampered = Updater.Verify(copy, sig, spki);
            File.Delete(copy);
            bool embedded = Updater.ImportSpki(Updater.PublicKeySpki) != null;
            bool ok = good && !tampered && embedded;
            result = $"{(ok ? "ok" : "FAILED")} good {good}, tampered accepted {tampered}, built-in key {embedded}";
            if (ok) code = 0;
        }
        catch (Exception ex) { result = "error: " + ex; }
        File.WriteAllText(outFile, result);
        return code;
    }
}
