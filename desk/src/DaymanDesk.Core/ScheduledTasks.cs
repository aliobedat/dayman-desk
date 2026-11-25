using System.Security;

namespace DaymanDesk.Core;

/// <summary>
/// The two scheduled tasks. WO-95: schtasks.exe with a task XML (Windows 7 to 11), so that no time limit stops them and
/// they also run on a laptop on battery (plain schtasks /Create sets a 72-hour limit and "AC power only").
/// </summary>
public static class ScheduledTasks
{
    public const string Folder = @"\DaymanDesk\";
    public const string Watchdog = "Watchdog";
    public const string Tray = "Tray";

    const string Settings =
        "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
        "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>" +
        "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>";

    static string Xml(string triggers, string principal, string exe, string args) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        $"<Triggers>{triggers}</Triggers><Principals><Principal id=\"Author\">{principal}<RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
        Settings + $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{args}</Arguments></Exec></Actions></Task>";

    public static string WatchdogXml(string exe, DateTime start) =>
        Xml($"<TimeTrigger><StartBoundary>{start.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}</StartBoundary><Repetition><Interval>PT1M</Interval></Repetition><Enabled>true</Enabled></TimeTrigger>",
            "<UserId>S-1-5-18</UserId>", exe, "--watchdog");

    public static string TrayXml(string exe, string user) =>
        Xml($"<LogonTrigger><UserId>{SecurityElement.Escape(user)}</UserId><Enabled>true</Enabled></LogonTrigger>",
            $"<UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType>", exe, "--tray");

    /// <summary>Writes the XML (UTF-16, as schtasks wants it) to a temp file and imports it, replacing the task.</summary>
    static string CreateScript(string name, string xml) =>
        $"$f = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($f, {PrinterSetup.Q(xml)}, [Text.Encoding]::Unicode)\n" +
        // Continue: under Stop, Windows PowerShell turns a native stderr line into an exception before the exit code.
        $"$ErrorActionPreference = 'Continue'; $o = & schtasks.exe /Create /F /TN {PrinterSetup.Q(Folder + name)} /XML $f 2>&1; $e = $LASTEXITCODE; Remove-Item $f\n" +
        "if ($e -ne 0) { throw \"schtasks $e $o\" }";

    public static string WatchdogScript(string exe) => CreateScript(Watchdog, WatchdogXml(exe, DateTime.Now));

    public static string TrayScript(string exe, string user) => CreateScript(Tray, TrayXml(exe, user));

    /// <summary>The watchdog (SYSTEM) starts the logged-on user's tray task when «وضع الشبكة» has no listener.</summary>
    public static string StartTrayScript() => $"& schtasks.exe /Run /TN {PrinterSetup.Q(Folder + Tray)}";

    public static string RemoveScript() =>
        $"$ErrorActionPreference = 'Continue'; & schtasks.exe /Delete /F /TN {PrinterSetup.Q(Folder + Watchdog)} | Out-Null; " +
        $"& schtasks.exe /Delete /F /TN {PrinterSetup.Q(Folder + Tray)} | Out-Null; $global:LASTEXITCODE = 0";

    public static void Register(IShell sh, string exe, string user)
    {
        var w = sh.Run(WatchdogScript(exe));
        if (!w.Ok) throw new SetupException("تعذّر إنشاء مهمة الحماية (الحارس).", w.Stderr);
        var t = sh.Run(TrayScript(exe, user));
        if (!t.Ok) throw new SetupException("تعذّر إنشاء مهمة التشغيل عند الدخول.", t.Stderr);
    }

    public static void Remove(IShell sh) => sh.Run(RemoveScript());
}
