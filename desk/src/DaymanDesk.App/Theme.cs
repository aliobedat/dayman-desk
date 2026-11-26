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
    /// <summary>WO-101: the danger-outline button (the golden rule «إيقاف وإرجاع الطابعة»).</summary>
    public static RoundButton DangerOutline(string text) => new(text, Surface, DangerBg, Danger, Danger) { Font = Strong };

    /// <summary>WO-101: every message of the program, in the themed dialog (MessageBox is no longer used).</summary>
    public static void Msg(IWin32Window? owner, string text, string caption, MessageBoxIcon icon) => ThemedDialog.Present(owner, text, caption, icon);
    public static bool Ask(IWin32Window? owner, string text, string caption) => ThemedDialog.Present(owner, text, caption, MessageBoxIcon.Question, ask: true) == DialogResult.Yes;

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

    public Card(string heading, int width, Glyph? glyph = null, Color? tint = null, Color? tintBg = null)
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
        if (glyph is { } gl)
        {
            // WO-101: an icon tile at the start of the heading row.
            h.Margin = new Padding(6, Ui.Px(12), 4, 4);
            var row = Ui.Row(new IconTile(gl, tint ?? Ui.Ink2, tintBg ?? Ui.Surface2), h);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, 8);
            _body.Controls.Add(row);
        }
        else _body.Controls.Add(h);
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

/// <summary>WO-101: the stroke icons the window uses (lucide-like, drawn on a 24-unit grid, round caps).</summary>
enum Glyph { Check, Alert, Info, Error, Help, Printer, Link, Receipt, Chat, Power, Update, Wifi, Shield, Chart }

static class Glyphs
{
    public static void Draw(Graphics g, Glyph k, RectangleF r, Color c, float width = 2f)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 24f, r.Height / 24f);
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        void Dot(float x, float y) => g.FillEllipse(brush, x - 1.3f, y - 1.3f, 2.6f, 2.6f);
        switch (k)
        {
            case Glyph.Check:
                g.DrawLines(pen, new[] { new PointF(20, 6), new PointF(9, 17), new PointF(4, 12) });
                break;
            case Glyph.Alert:
                g.DrawPolygon(pen, new[] { new PointF(12, 3), new PointF(22, 20), new PointF(2, 20) });
                g.DrawLine(pen, 12, 9, 12, 13.5f);
                Dot(12, 16.8f);
                break;
            case Glyph.Info:
                g.DrawEllipse(pen, 2, 2, 20, 20);
                g.DrawLine(pen, 12, 11, 12, 16.5f);
                Dot(12, 7.8f);
                break;
            case Glyph.Error:
                g.DrawEllipse(pen, 2, 2, 20, 20);
                g.DrawLine(pen, 15, 9, 9, 15);
                g.DrawLine(pen, 9, 9, 15, 15);
                break;
            case Glyph.Help:
                g.DrawEllipse(pen, 2, 2, 20, 20);
                using (var q = new GraphicsPath())
                {
                    q.AddBezier(9.2f, 9.3f, 9.6f, 6.4f, 14.8f, 6.4f, 14.8f, 9.6f);
                    q.AddBezier(14.8f, 9.6f, 14.8f, 11.6f, 12f, 11.8f, 12f, 13.8f);
                    g.DrawPath(pen, q);
                }
                Dot(12, 17.2f);
                break;
            case Glyph.Printer:
                g.DrawLines(pen, new[] { new PointF(6, 9), new PointF(6, 2), new PointF(18, 2), new PointF(18, 9) });
                g.DrawLines(pen, new[] { new PointF(6, 17), new PointF(3.8f, 17), new PointF(2, 15.2f), new PointF(2, 10.8f), new PointF(3.8f, 9),
                    new PointF(20.2f, 9), new PointF(22, 10.8f), new PointF(22, 15.2f), new PointF(20.2f, 17), new PointF(18, 17) });
                g.DrawRectangle(pen, 6, 14, 12, 8);
                break;
            case Glyph.Link:
                using (var a = Ui.Round(new RectangleF(1.5f, 7, 12, 10), 5)) g.DrawPath(pen, a);
                using (var b = Ui.Round(new RectangleF(10.5f, 7, 12, 10), 5)) g.DrawPath(pen, b);
                break;
            case Glyph.Receipt:
                g.DrawPolygon(pen, new[] { new PointF(4, 2), new PointF(20, 2), new PointF(20, 22), new PointF(17, 20), new PointF(14.5f, 22),
                    new PointF(12, 20), new PointF(9.5f, 22), new PointF(7, 20), new PointF(4, 22) });
                g.DrawLine(pen, 8, 8, 16, 8);
                g.DrawLine(pen, 8, 12, 16, 12);
                g.DrawLine(pen, 8, 16, 13, 16);
                break;
            case Glyph.Chat:
                using (var bubble = Ui.Round(new RectangleF(3, 4, 18, 13), 4)) g.DrawPath(pen, bubble);
                g.DrawLines(pen, new[] { new PointF(8, 17), new PointF(7, 21), new PointF(12, 17) });
                break;
            case Glyph.Power:
                g.DrawLine(pen, 12, 2, 12, 11);
                g.DrawArc(pen, 4, 5, 16, 16, 300, 300);
                break;
            case Glyph.Update:
                g.DrawArc(pen, 3, 3, 18, 18, 320, 290);
                g.DrawLines(pen, new[] { new PointF(21, 2.5f), new PointF(20.6f, 7.8f), new PointF(15.4f, 7.4f) });
                break;
            case Glyph.Wifi:
                foreach (float rr in new[] { 5f, 10f, 15f }) g.DrawArc(pen, 12 - rr, 20 - rr, 2 * rr, 2 * rr, 225, 90);
                Dot(12, 20);
                break;
            case Glyph.Shield:
                using (var s = new GraphicsPath())
                {
                    s.AddLine(12, 2, 20, 5);
                    s.AddLine(20, 5, 20, 12);
                    s.AddBezier(20, 12, 20, 17, 16, 20, 12, 22);
                    s.AddBezier(12, 22, 8, 20, 4, 17, 4, 12);
                    s.AddLine(4, 12, 4, 5);
                    s.CloseFigure();
                    g.DrawPath(pen, s);
                }
                break;
            case Glyph.Chart:
                g.DrawLine(pen, 6, 20, 6, 14);
                g.DrawLine(pen, 12, 20, 12, 4);
                g.DrawLine(pen, 18, 20, 18, 10);
                break;
        }
        g.Restore(state);
    }
}

/// <summary>An icon tile (40×40, r-sm 12): the glyph centred on a soft square; no square when Back is empty.</summary>
sealed class IconTile : Control
{
    public Glyph Glyph;
    public Color Fore2, Back2;

    public IconTile(Glyph glyph, Color fore, Color back, int size = 40)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Glyph = glyph; Fore2 = fore; Back2 = back;
        Size = new Size(Ui.Px(size), Ui.Px(size));
        Margin = new Padding(4);
    }

    public void Set(Glyph glyph, Color fore, Color back) { Glyph = glyph; Fore2 = fore; Back2 = back; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Back2.IsEmpty)
        {
            using var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Width * 0.3f);
            using var b = new SolidBrush(Back2);
            g.FillPath(b, path);
        }
        float icon = Back2.IsEmpty ? Width : Width * 0.5f;
        Glyphs.Draw(g, Glyph, new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon), Fore2);
    }
}

/// <summary>
/// WO-101: a coloured callout (r-md, the status colour's soft background and a faint border of it): icon, a title,
/// a sentence and an optional action at the end.
/// </summary>
sealed class Callout : Panel
{
    readonly TableLayoutPanel _grid = new() { ColumnCount = 3, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
    readonly IconTile _icon = new(Glyph.Info, Ui.Ink, Color.Empty, 24) { Margin = new Padding(4, 6, 4, 4) };
    readonly FlowLayoutPanel _text = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty, BackColor = Color.Transparent };
    readonly FlowLayoutPanel _actions = new() { AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0), BackColor = Color.Transparent, Anchor = AnchorStyles.None };
    public readonly Label Title = Ui.Text("", Ui.Strong), Body = Ui.Text("", Ui.Body);
    Color _fg = Ui.Ink, _bg = Ui.Surface2;
    int _width;

    public Callout(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14, 10, 14, 10);
        Margin = new Padding(0, 0, 0, 16);
        Title.Margin = new Padding(4, 4, 4, 0);
        Body.Margin = new Padding(4, 2, 4, 4);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _text.Controls.Add(Title);
        _text.Controls.Add(Body);
        _grid.Controls.Add(_icon, 0, 0);
        _grid.Controls.Add(_text, 1, 0);
        _grid.Controls.Add(_actions, 2, 0);
        Controls.Add(_grid);
        Fit(width);
    }

    public void Set(Glyph glyph, Color fg, Color bg, string title, string body)
    {
        _fg = fg; _bg = bg;
        _icon.Set(glyph, fg, Color.Empty);
        Title.Text = title; Title.ForeColor = fg; Title.Visible = title.Length > 0;
        Body.Text = body; Body.ForeColor = Ui.Ink; Body.Visible = body.Length > 0;
        _grid.BackColor = bg;
        Fit(_width);
        Invalidate(true);
    }

    public void SetAction(Control? action)
    {
        if (_actions.Controls.Count == 1 && _actions.Controls[0] == action) return;
        _actions.Controls.Clear();
        if (action != null) _actions.Controls.Add(action);
        Fit(_width);
    }

    public void Fit(int width)
    {
        if (width <= 0) return;
        _width = width;
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        int text = width - Padding.Horizontal - _icon.Width - 16 - (_actions.Controls.Count > 0 ? _actions.PreferredSize.Width + 16 : 0);
        Title.MaximumSize = Body.MaximumSize = new Size(Math.Max(120, text), 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 16);
        using (var b = new SolidBrush(_bg)) g.FillPath(b, path);
        using var pen = new Pen(Color.FromArgb(70, _fg));
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// WO-101: the one themed message window (replaces MessageBox): icon tile, title, text, primary/secondary buttons,
/// RTL, Esc and Enter, DPI. Returns what MessageBox returned (OK, or Yes/No with No the default).
/// </summary>
sealed class ThemedDialog : Form
{
    public ThemedDialog(string text, string caption, MessageBoxIcon icon, bool ask)
    {
        Text = caption;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = Ui.Body;
        BackColor = Ui.Surface;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Ui.Px(24), Ui.Px(22), Ui.Px(24), Ui.Px(16));
        KeyPreview = true;

        var (glyph, fg, bg, title) = icon switch
        {
            MessageBoxIcon.Error => (Glyph.Error, Ui.Danger, Ui.DangerBg, "تعذّر إتمام العملية"),
            MessageBoxIcon.Warning => (Glyph.Alert, Ui.Warning, Ui.WarningBg, "تنبيه"),
            MessageBoxIcon.Question => (Glyph.Help, Ui.Accent, Ui.AccentSoft, "هل تريد المتابعة؟"),
            _ => (Glyph.Info, Ui.Success, Ui.SuccessBg, caption),
        };
        var root = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, BackColor = Ui.Surface, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tile = new IconTile(glyph, fg, bg, 48) { Margin = new Padding(0, 2, 0, 0) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(Ui.Px(14), 0, 0, 0) };
        var head = Ui.Text(title, Ui.Heading);
        head.Margin = new Padding(0, 2, 0, 6);
        var body = Ui.Text(Ui.Latin(text), Ui.Body, Ui.Ink2, Ui.Px(440));
        body.Margin = new Padding(0, 0, 0, 4);
        body.MinimumSize = new Size(Ui.Px(300), 0);
        stack.Controls.Add(head);
        stack.Controls.Add(body);
        root.Controls.Add(tile, 0, 0);
        root.Controls.Add(stack, 1, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.Surface, Margin = new Padding(0, Ui.Px(18), 0, 0), Anchor = AnchorStyles.Right };
        if (ask)
        {
            var yes = Ui.Primary("نعم");
            var no = Ui.Secondary("لا");
            yes.DialogResult = DialogResult.Yes;
            no.DialogResult = DialogResult.No;
            yes.MinimumSize = no.MinimumSize = new Size(Ui.Px(96), Ui.Px(40));
            buttons.Controls.Add(yes);
            buttons.Controls.Add(no);
            AcceptButton = no; // MessageBoxDefaultButton.Button2: Enter never answers «نعم» by accident
            CancelButton = no;
            Shown += (_, _) => no.Focus();
        }
        else
        {
            var ok = Ui.Primary("حسناً");
            ok.DialogResult = DialogResult.OK;
            ok.MinimumSize = new Size(Ui.Px(110), Ui.Px(40));
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
            Shown += (_, _) => ok.Focus();
        }
        root.Controls.Add(buttons, 0, 1);
        root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        Ui.LatinDigits(this);
    }

    /// <summary>Shown modally; with no owner (before the main window exists) it is centred and on top.</summary>
    public static DialogResult Present(IWin32Window? owner, string text, string caption, MessageBoxIcon icon, bool ask = false)
    {
        using var d = new ThemedDialog(text, caption, icon, ask);
        if (owner == null)
        {
            d.StartPosition = FormStartPosition.CenterScreen;
            d.TopMost = true;
            return d.ShowDialog();
        }
        d.StartPosition = FormStartPosition.CenterParent;
        d.ShowInTaskbar = false;
        return d.ShowDialog(owner);
    }
}

/// <summary>WO-101: the tray menu in the theme: surface, a line border, rounded hover in surface-2, Plex, RTL.</summary>
sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() { RoundedEdges = false; }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.ShowImageMargin = false;
        menu.Font = Ui.Body;
        menu.BackColor = Ui.Surface;
        menu.Padding = new Padding(4, 6, 4, 6);
        foreach (ToolStripItem i in menu.Items)
        {
            if (i is ToolStripSeparator) continue;
            i.Padding = new Padding(10, 7, 10, 7);
            i.ForeColor = Ui.Ink;
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Ui.Surface);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Ui.ControlLine);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Round(new RectangleF(2, 1, e.Item.Width - 5, e.Item.Height - 3), 10);
        using var b = new SolidBrush(Ui.Surface2);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Ink : Ui.Subtle;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Ui.Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }
}

/// <summary>WO-101: the 6-character pairing code as six boxes (typing, Backspace, paste; Enter raises Submit).</summary>
sealed class CodeBox : Control
{
    public const int Length = 6;
    static readonly Font Big = new("Consolas", 20f);
    string _value = "";
    public event Action? Submit;
    public bool Invalid;

    public CodeBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        RightToLeft = RightToLeft.No;
        TabStop = true;
        Cursor = Cursors.IBeam;
        Size = new Size(Length * Ui.Px(52) + (Length - 1) * Ui.Px(10) + 4, Ui.Px(62));
        Margin = new Padding(4, 8, 4, 8);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _value;
        set { _value = Clean(value ?? ""); Invalid = false; Invalidate(); OnTextChanged(EventArgs.Empty); }
    }

    // Any printable character is kept (an Arabic letter still reaches PairCodeProblem, which names the keyboard).
    static string Clean(string s) => new(s.ToUpperInvariant().Where(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch) && ch != Ui.Nods).Take(Length).ToArray());

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Back or Keys.Delete or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Back or Keys.Delete) { if (_value.Length > 0) Text = _value.Substring(0, _value.Length - 1); e.Handled = e.SuppressKeyPress = true; }
        else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
        {
            try { if (Clipboard.ContainsText()) Text = _value + Clipboard.GetText(); } catch { }
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Submit?.Invoke(); }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        char ch = e.KeyChar;
        if (ch >= '٠' && ch <= '٩') ch = (char)('0' + (ch - '٠'));
        if (!char.IsControl(ch) && _value.Length < Length) Text = _value + ch;
        e.Handled = true;
        base.OnKeyPress(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Ui.Px(52), gap = Ui.Px(10), h = Height - 4;
        for (int i = 0; i < Length; i++)
        {
            var r = new RectangleF(2 + i * (w + gap), 2, w, h);
            bool active = Focused && Enabled && i == Math.Min(_value.Length, Length - 1);
            using (var path = Ui.Round(r, 14))
            {
                using (var b = new SolidBrush(Enabled ? Ui.Surface : Ui.Surface2)) g.FillPath(b, path);
                Color line = Invalid ? Ui.Danger : active ? Ui.Accent : i < _value.Length ? Ui.Ink2 : Ui.ControlLine;
                using var pen = new Pen(line, active || Invalid ? 2f : 1f);
                g.DrawPath(pen, path);
            }
            if (active)
            {
                using var ring = new Pen(Color.FromArgb(50, Ui.Accent), 3f);
                using var rp = Ui.Round(new RectangleF(r.X - 1.5f, r.Y - 1.5f, r.Width + 3, r.Height + 3), 15);
                g.DrawPath(ring, rp);
            }
            if (i < _value.Length)
                TextRenderer.DrawText(g, Ui.Latin(_value[i].ToString()), Big, Rectangle.Round(r), Ui.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            else if (active)
            {
                using var caret = new Pen(Ui.Accent, 2f);
                g.DrawLine(caret, r.X + r.Width / 2, r.Y + h * 0.3f, r.X + r.Width / 2, r.Y + h * 0.7f);
            }
        }
    }
}

/// <summary>WO-101: the three pairing steps as numbered discs joined by a line (step 1 at the right in RTL).</summary>
sealed class StepsBar : Control
{
    readonly string[] _labels;
    int _current; // 0-based; == count: all done

    public StepsBar(int width, params string[] labels)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _labels = labels;
        Size = new Size(width, Ui.Px(78));
        Margin = new Padding(4, 4, 4, 12);
    }

    public int Current { get => _current; set { if (_current != value) { _current = value; Invalidate(); } } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int n = _labels.Length, d = Ui.Px(32), seg = Width / n;
        float X(int i) => Width - seg * i - seg / 2f; // RTL: step 1 at the right
        for (int i = 0; i < n - 1; i++)
        {
            using var pen = new Pen(i < _current ? Ui.Success : Ui.Line, 2f);
            g.DrawLine(pen, X(i) - d / 2f - 6, 2 + d / 2f, X(i + 1) + d / 2f + 6, 2 + d / 2f);
        }
        for (int i = 0; i < n; i++)
        {
            bool done = i < _current, active = i == _current;
            var r = new RectangleF(X(i) - d / 2f, 2, d, d);
            using (var b = new SolidBrush(done ? Ui.Success : active ? Ui.Ink : Ui.Surface2)) g.FillEllipse(b, r);
            if (done) Glyphs.Draw(g, Glyph.Check, RectangleF.Inflate(r, -d * 0.22f, -d * 0.22f), Color.White, 3f);
            else TextRenderer.DrawText(g, Ui.Latin((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), Ui.Strong, Rectangle.Round(r),
                active ? Color.White : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            var lr = new Rectangle((int)(X(i) - seg / 2f), 2 + d + Ui.Px(6), seg, Height - d - Ui.Px(8));
            TextRenderer.DrawText(g, _labels[i], active ? Ui.Strong : Ui.Body, lr, done || active ? Ui.Ink : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.RightToLeft | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>WO-101: a text box as a rounded input (r-md, line-strong border, accent when focused).</summary>
sealed class FieldBox : Panel
{
    readonly Control _box;

    public FieldBox(Control box)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _box = box;
        Margin = new Padding(4, 4, 4, 4);
        BackColor = Ui.Surface;
        box.BackColor = Ui.Surface;
        box.Margin = Padding.Empty;
        if (box is TextBox tb)
        {
            int h = Ui.Px(42);
            tb.BorderStyle = BorderStyle.None;
            Size = new Size(box.Width + 28, h);
            Padding = new Padding(14, Math.Max(2, (h - tb.PreferredHeight) / 2), 14, 0);
        }
        else
        {
            if (box is ListBox lb) lb.BorderStyle = BorderStyle.None;
            Size = new Size(box.Width + 24, box.Height + 16);
            Padding = new Padding(12, 8, 12, 8);
        }
        box.Dock = DockStyle.Fill;
        Controls.Add(box);
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        box.EnabledChanged += (_, _) => { box.BackColor = box.Enabled ? Ui.Surface : Ui.Surface2; Invalidate(); };
        Cursor = Cursors.IBeam;
        Click += (_, _) => box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Round(new RectangleF(1, 1, Width - 3, Height - 3), 12);
        using (var b = new SolidBrush(_box.Enabled ? Ui.Surface : Ui.Surface2)) g.FillPath(b, path);
        using var pen = new Pen(_box.Focused ? Ui.Accent : Ui.ControlLine, _box.Focused ? 2f : 1f);
        g.DrawPath(pen, path);
    }
}
