using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>WO-98: the Dayman design system v2 («Warm Wallet», light only) mapped to WinForms.</summary>
static class Ui
{
    public static readonly Color Canvas = Hex(0xF7F5F2), Surface = Color.White, Surface2 = Hex(0xF1EEE9), Line = Hex(0xE9E5DF),
        ControlLine = Hex(0xD5CFC6), Ink = Hex(0x1A1714), Ink2 = Hex(0x3F3A34), Muted = Hex(0x6E675E), Subtle = Hex(0x9C948A),
        Accent = Hex(0xC2410C), AccentSoft = Hex(0xFCEDE5),
        Success = Hex(0x17784A), SuccessBg = Hex(0xE5F4EC), Warning = Hex(0x8A5300), WarningBg = Hex(0xFDF2DE),
        Danger = Hex(0xB42318), DangerBg = Hex(0xFDEBE9);

    // IBM Plex Sans Arabic (OFL, embedded by CI), Segoe UI when it cannot be loaded. Codes in Consolas.
    public static readonly Font Body = Make(PlexFonts.Regular, "Segoe UI", 10f);
    public static readonly Font Strong = Make(PlexFonts.Semi, "Segoe UI Semibold", 10f);
    public static readonly Font Heading = Make(PlexFonts.Semi, "Segoe UI Semibold", 11.5f);
    public static readonly Font Title = Make(PlexFonts.Semi, "Segoe UI Semibold", 15f);
    public static readonly Font Mono = new("Consolas", 10f);
    public static readonly Font BigNumber = Make(PlexFonts.Semi, "Segoe UI Semibold", 20f);

    static Font Make(FontFamily? plex, string fallback, float size)
    {
        if (plex != null) { try { return new Font(plex, size); } catch { } }
        return new Font(fallback, size);
    }

    /// <summary>Logical (96 dpi) pixels to device pixels for fixed sizes (fonts scale by themselves).</summary>
    public static int Px(int v) => (int)Math.Round(v * Dpi / 96f);
    static readonly float Dpi = ReadDpi();
    static float ReadDpi()
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiX; } catch { return 96f; }
    }

    static Color Hex(int v) => Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);

    public static RoundButton Primary(string text) => new(text, Ink, Hex(0x3F3A34), Color.White, Color.Empty) { Font = Strong };
    public static RoundButton Secondary(string text) => new(text, Surface, Surface2, Ink, ControlLine) { Font = Body };

    public static Label Text(string text, Font? font = null, Color? color = null, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Body, ForeColor = color ?? Ink, Margin = new Padding(4, 6, 4, 6),
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty, UseMnemonic = false,
    };

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4), BackColor = Color.Transparent };
        f.Controls.AddRange(items);
        return f;
    }

    /// <summary>The first opaque background up the parent chain (for painting rounded corners).</summary>
    public static Color BackOf(Control? c)
    {
        while (c != null && c.BackColor.A < 255) c = c.Parent;
        return c?.BackColor ?? Canvas;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void OpenUrl(string url)
    {
        if (!SemVer.SafeUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>
    /// WO-91: U+206F NOMINAL DIGIT SHAPES. Windows with an Arabic locale and native digits draws 0-9 as ١٠٠١ in every
    /// GDI text (labels, lists, menus, message boxes, the tray tooltip); GDI draws the digits after this invisible
    /// mark as 0-9 whatever that setting (it is stronger than ETO_NUMERICSLATIN). Project rule: Latin digits only.
    /// </summary>
    public const char Nods = '⁯';
    public const string NodsText = "⁯";
    static readonly Regex DigitRun = new(@"(?<![0-9⁯])(?=[0-9])", RegexOptions.Compiled);

    /// <summary>The text with Latin digits for display: Arabic-Indic digits become 0-9, «٫» becomes '.', and U+206F
    /// goes before each digit run (each line of a wrapped label keeps it). Idempotent.</summary>
    public static string Latin(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length + 4);
        foreach (char ch in s)
        {
            if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
            else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
            else if (ch == '٫') sb.Append('.');
            else sb.Append(ch);
        }
        return DigitRun.Replace(sb.ToString(), NodsText);
    }

    /// <summary>
    /// Keeps the digits of every label, button, check box, tab and form title under <paramref name="root"/> Latin,
    /// now and on every later Text change (text boxes are LatinTextBox; list items and the tray call Latin).
    /// </summary>
    public static void LatinDigits(Control root)
    {
        if (root is Label or ButtonBase or TabPage or GroupBox or Form or Pill)
        {
            FixDigits(root);
            root.TextChanged += (_, _) => FixDigits(root);
        }
        foreach (Control child in root.Controls) LatinDigits(child);
        root.ControlAdded += (_, e) => { if (e.Control != null) LatinDigits(e.Control); };
    }

    static void FixDigits(Control c)
    {
        string t = c.Text, l = Latin(t);
        if (l != t) c.Text = l;
    }
}

/// <summary>
/// The two embedded Plex weights. PrivateFontCollection serves GDI+ and the Font objects; AddFontMemResourceEx makes
/// the same faces known to GDI, which draws every label and button (TextRenderer). Null when a file is missing.
/// </summary>
static class PlexFonts
{
    public static readonly FontFamily? Regular, Semi;
    static readonly PrivateFontCollection Pfc = new();

    [DllImport("gdi32.dll")]
    static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    static PlexFonts()
    {
        try
        {
            foreach (var name in new[] { "fonts/IBMPlexSansArabic-Regular.ttf", "fonts/IBMPlexSansArabic-SemiBold.ttf" })
            {
                using var s = typeof(PlexFonts).Assembly.GetManifestResourceStream(name);
                if (s == null) return;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                IntPtr p = Marshal.AllocCoTaskMem(bytes.Length); // kept for the life of the process
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Pfc.AddMemoryFont(p, bytes.Length);
                uint n = 0;
                AddFontMemResourceEx(p, (uint)bytes.Length, IntPtr.Zero, ref n);
            }
            foreach (var f in Pfc.Families)
            {
                if (f.Name == "IBM Plex Sans Arabic") Regular = f;
                else if (f.Name == "IBM Plex Sans Arabic SemiBold") Semi = f;
            }
        }
        catch { Regular = Semi = null; }
    }
}

/// <summary>A flat, fully rounded button (r-full): no 3D border, hover and press shades, an accent focus ring.</summary>
sealed class RoundButton : Button
{
    public Color Fill, Hover, Fore2, Border;
    bool _hover, _down;

    public RoundButton(string text, Color fill, Color hover, Color fore, Color border)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text; Fill = fill; Hover = hover; Fore2 = fore; Border = border;
        AutoSize = true; MinimumSize = new Size(0, 40); Padding = new Padding(16, 4, 16, 4); Margin = new Padding(4);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; UseMnemonic = false;
    }

    public void SetColors(Color fill, Color hover, Color fore, Color border)
    {
        Fill = fill; Hover = hover; Fore2 = fore; Border = border;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        Color fill = !Enabled ? Ui.Surface2 : _hover || _down ? Hover : Fill;
        using (var path = Ui.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Enabled && Border != Color.Empty) { using var pen = new Pen(Border); g.DrawPath(pen, path); }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Ui.Accent, 2);
            using var rp = Ui.Round(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f);
            g.DrawPath(ring, rp);
        }
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Fore2 : Ui.Subtle, flags);
    }
}

/// <summary>A status pill: a dot and a word on the status colour's soft background (never colour alone).</summary>
sealed class Pill : Control
{
    Color _fg = Ui.Muted, _bg = Ui.Surface2;

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Ui.Strong;
        Margin = new Padding(4, 4, 4, 2);
    }

    public void Set(string text, Color fg, Color bg)
    {
        _fg = fg; _bg = bg; Text = text;
        var s = TextRenderer.MeasureText(text, Font);
        Size = new Size(s.Width + 42, s.Height + 12);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
        using (var b = new SolidBrush(_bg)) g.FillPath(b, path);
        // The dot sits at the start of the word (the right in RTL).
        bool rtl = RightToLeft == RightToLeft.Yes;
        using (var d = new SolidBrush(_fg)) g.FillEllipse(d, rtl ? Width - 22 : 14, Height / 2f - 4, 8, 8);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(rtl ? 12 : 26, 0, Width - 38, Height), _fg, flags);
    }
}

/// <summary>A white card: r-md corners, a 1px line border and a faint sh-1 shadow, with a heading.</summary>
sealed class Card : Panel
{
    readonly FlowLayoutPanel _body;

    public Card(string heading, int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Ui.Canvas;
        Padding = new Padding(22, 16, 22, 18);
        Margin = new Padding(0, 0, 0, 16);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        _body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, BackColor = Ui.Surface };
        var h = Ui.Text(heading, Ui.Heading);
        h.Margin = new Padding(4, 2, 4, 8);
        _body.Controls.Add(h);
        Controls.Add(_body);
    }

    public FlowLayoutPanel Body => _body;

    public Card Add(params Control[] c) { _body.Controls.AddRange(c); return this; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var sh = Ui.Round(new RectangleF(0, 1.5f, Width - 1, Height - 2.5f), 16))
        using (var sb = new SolidBrush(Color.FromArgb(18, 26, 23, 20))) g.FillPath(sb, sh);
        using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 3), 16);
        using (var b = new SolidBrush(Ui.Surface)) g.FillPath(b, path);
        using var pen = new Pen(Ui.Line);
        g.DrawPath(pen, path);
    }
}

/// <summary>A TabControl without its 3D tab strip and border; the pages are switched by the pill nav above it.</summary>
sealed class FlatTabs : TabControl
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x1328 && !DesignMode) { m.Result = (IntPtr)1; return; } // TCM_ADJUSTRECT: pages fill the control
        base.WndProc(ref m);
    }
}
