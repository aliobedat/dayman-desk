using System.Drawing;

namespace DaymanDesk.App;

/// The Dayman logo embedded in the exe (ApplicationIcon), for the windows and the tray.
static class AppIcon
{
    static readonly Lazy<Icon> Lazy = new(() =>
    {
        try { return Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    });

    public static Icon Value => Lazy.Value;
}
