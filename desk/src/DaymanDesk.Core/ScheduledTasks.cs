namespace DaymanDesk.Core;

/// <summary>
/// The two scheduled tasks, registered through PowerShell so that no time limit stops them and they also run
/// on a laptop on battery (schtasks /Create sets a 72-hour limit and "AC power only").
/// </summary>
public static class ScheduledTasks
{
    public const string Folder = @"\DaymanDesk\";
    public const string Watchdog = "Watchdog";
    public const string Tray = "Tray";

    const string Settings =
        "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -StartWhenAvailable\n";

    public static string WatchdogScript(string exe) =>
        Settings +
        $"$a = New-ScheduledTaskAction -Execute {PrinterSetup.Q(exe)} -Argument '--watchdog'\n" +
        "$t = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1)\n" +
        "$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest\n" +
        $"Register-ScheduledTask -TaskName {PrinterSetup.Q(Watchdog)} -TaskPath {PrinterSetup.Q(Folder)} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";

    public static string TrayScript(string exe, string user) =>
        Settings +
        $"$a = New-ScheduledTaskAction -Execute {PrinterSetup.Q(exe)} -Argument '--tray'\n" +
        $"$t = New-ScheduledTaskTrigger -AtLogOn -User {PrinterSetup.Q(user)}\n" +
        $"$p = New-ScheduledTaskPrincipal -UserId {PrinterSetup.Q(user)} -LogonType Interactive -RunLevel Highest\n" +
        $"Register-ScheduledTask -TaskName {PrinterSetup.Q(Tray)} -TaskPath {PrinterSetup.Q(Folder)} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";

    /// <summary>The watchdog (SYSTEM) starts the logged-on user's tray task when «وضع الشبكة» has no listener.</summary>
    public static string StartTrayScript() =>
        $"Start-ScheduledTask -TaskPath {PrinterSetup.Q(Folder)} -TaskName {PrinterSetup.Q(Tray)}";

    public static string RemoveScript() =>
        $"Get-ScheduledTask -TaskPath {PrinterSetup.Q(Folder)} -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false";

    public static void Register(IShell sh, string exe, string user)
    {
        var w = sh.Run(WatchdogScript(exe));
        if (!w.Ok) throw new SetupException("تعذّر إنشاء مهمة الحماية (الحارس).", w.Stderr);
        var t = sh.Run(TrayScript(exe, user));
        if (!t.Ok) throw new SetupException("تعذّر إنشاء مهمة التشغيل عند الدخول.", t.Stderr);
    }

    public static void Remove(IShell sh) => sh.Run(RemoveScript());
}
