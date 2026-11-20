using System.Diagnostics;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>Copies the exe to Program Files and registers the watchdog and logon tasks.</summary>
static class Installer
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DaymanDesk");
    public static string InstalledExe => Path.Combine(InstallDir, "DaymanDesk.exe");

    public static bool RunningInstalled =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The SYSTEM task always points at Program Files, never at the download folder (a user could replace a
    /// file there and have it run as SYSTEM). If the copy fails, the install fails.
    /// </summary>
    public static void EnsureInstalled(IShell sh, RollingLog log)
    {
        string current = Environment.ProcessPath ?? InstalledExe;
        if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(current, InstalledExe, overwrite: true);
                log.Info("copied to " + InstalledExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error("copy to Program Files failed: " + ex.Message);
                throw new SetupException("تعذّر نسخ البرنامج إلى Program Files. إن كانت نسخة قديمة تعمل فأغلقها من الأيقونة بجانب الساعة ثم أعد المحاولة.", ex.Message);
            }
        }
        ScheduledTasks.Register(sh, InstalledExe, $"{Environment.UserDomainName}\\{Environment.UserName}");
        AddShortcut(sh, log);
    }

    public static void RemoveTasks(IShell sh)
    {
        ScheduledTasks.Remove(sh);
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>WO-91: «دايماً ديسك» in the Start menu of every user (the window is otherwise reachable only from the tray).</summary>
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "دايماً ديسك.lnk");

    /// <summary>Best effort: a missing shortcut never fails the install.</summary>
    static void AddShortcut(IShell sh, RollingLog log)
    {
        var r = sh.Run($"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({PrinterSetup.Q(ShortcutPath)}); " +
            $"$s.TargetPath = {PrinterSetup.Q(InstalledExe)}; $s.WorkingDirectory = {PrinterSetup.Q(InstallDir)}; $s.Save()");
        if (!r.Ok) log.Warn("start menu shortcut not created: " + r.Stderr);
    }

    /// <summary>The config holds the device token: only SYSTEM and Administrators may read the data folder.</summary>
    public static void LockDataDir(RollingLog log)
    {
        Directory.CreateDirectory(Runtime.DataDir);
        var psi = new ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { Runtime.DataDir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            if (p.ExitCode != 0) log.Warn($"icacls exit {p.ExitCode}: {err.Trim()}");
        }
        catch (Exception ex) { log.Error("icacls failed: " + ex.Message); }
    }
}
