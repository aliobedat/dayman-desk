using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DaymanDesk.Core;

namespace DaymanDesk.App;


/// <summary>
/// WO-91: a text box whose digits show as 0-9 on Arabic Windows with native digits. Its window text starts with
/// U+206F (Ui.Nods); Text and MaxLength hide it from the code, so what the owner typed reads back unchanged.
/// </summary>
sealed class LatinTextBox : TextBox
{
    public LatinTextBox() => BorderStyle = BorderStyle.FixedSingle;

    [AllowNull]
    public override string Text
    {
        get => base.Text.Replace(Ui.NodsText, "");
        set => base.Text = string.IsNullOrEmpty(value) ? "" : Ui.NodsText + value.Replace(Ui.NodsText, "");
    }

    public override int MaxLength { get => base.MaxLength - 1; set => base.MaxLength = value + 1; }

    protected override void OnTextChanged(EventArgs e)
    {
        string t = base.Text;
        string want = t.Length == 0 ? "" : Ui.NodsText + t.Replace(Ui.NodsText, "");
        if (t != want)
        {
            // Typed or pasted text: one mark at the start, the caret stays after what was typed.
            int caret = Math.Max(0, Math.Min(SelectionStart, t.Length));
            int marksBefore = t[..caret].Count(ch => ch == Ui.Nods);
            base.Text = want; // raises TextChanged again, with the final text
            SelectionStart = Math.Max(0, Math.Min(caret - marksBefore + 1, want.Length));
            return;
        }
        base.OnTextChanged(e);
    }
}


/// <summary>The status window: state, today's receipts, the last problem with what to do, pairing and help.</summary>
sealed class MainForm : Form
{
    const int CardWidth = 820;
    readonly Runtime _rt;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _trayUpdate = new() { Visible = false };

    // Header and banners
    readonly Label _headline = Ui.Text("", Ui.Heading);
    readonly Label _subline = Ui.Text("", Ui.Body, Ui.Muted);
    readonly Panel _confirmBanner = new() { Dock = DockStyle.Top, Height = Ui.Px(60), BackColor = Ui.WarningBg, Visible = false, Padding = new Padding(16, 8, 16, 8) };
    readonly Panel _updateBanner = new() { Dock = DockStyle.Top, Height = Ui.Px(52), BackColor = Ui.SuccessBg, Visible = false, Padding = new Padding(16, 6, 16, 6) };
    readonly Label _updateText = Ui.Text("", Ui.Strong, Ui.Success);
    readonly Pill _pill = new();
    Action? _fit; // receipts columns to the list width

    // Status tab
    // WO-89: one or more receipt printers (each Windows queue: USB, COM or a network printer's TCP/IP port).
    readonly CheckedListBox _printers = new() { Width = 460, Height = 110, CheckOnClick = true, Font = Ui.Body, Margin = new Padding(4, 8, 4, 4), BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

    // «وضع الشبكة»: POS devices without Windows print to this PC, which forwards to the real printer.
    readonly CheckBox _netOn = new() { Text = "تفعيل وضع الشبكة", AutoSize = true, Font = Ui.Strong, Margin = new Padding(4, 8, 4, 4) };
    readonly LatinTextBox _netPrinter = new() { Width = 170, MaxLength = 15, RightToLeft = RightToLeft.No, Font = Ui.Mono, Margin = new Padding(4, 8, 4, 4) };
    readonly LatinTextBox _netPort = new() { Width = 70, MaxLength = 5, RightToLeft = RightToLeft.No, Font = Ui.Mono, Margin = new Padding(4, 8, 4, 4) };
    readonly Button _netSave = Ui.Secondary("حفظ وضع الشبكة");
    readonly Label _netIp = Ui.Text("", Ui.Strong, Ui.Ink, CardWidth - 60);
    readonly Button _refresh = Ui.Secondary("تحديث القائمة");
    readonly Button _start = Ui.Primary("تشغيل دايماً ديسك");
    readonly Button _stop = Ui.Secondary("إيقاف وإرجاع الطابعة");
    readonly Dictionary<string, Label> _info = new();
    readonly Label[] _today = new Label[4];
    readonly Label _problem = Ui.Text("", Ui.Body, Ui.Ink, CardWidth - 60);
    readonly Label _problemAt = Ui.Text("", Ui.Body, Ui.Muted);

    // Receipts tab
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Ui.Body, RightToLeftLayout = true, BorderStyle = BorderStyle.None };

    // Pairing tab
    readonly Label _pairStatus = Ui.Text("", Ui.Strong, Ui.Ink, CardWidth - 60);
    readonly LatinTextBox _code = new() { Width = 130, MaxLength = 6, CharacterCasing = CharacterCasing.Upper, RightToLeft = RightToLeft.No, Font = Ui.Mono, Margin = new Padding(4, 8, 4, 4) };
    readonly LatinTextBox _deviceName = new() { Width = 180, Text = "كاشير 1", MaxLength = 40, Font = Ui.Body, Margin = new Padding(4, 8, 4, 4) };
    readonly Button _pair = Ui.Primary("ربط");
    readonly Label _fieldsStatus = Ui.Text("", Ui.Body, Ui.Ink, CardWidth - 60);
    readonly Button _reconfirm = Ui.Secondary("إعادة تأكيد الحقول");
    readonly LatinTextBox _url = new() { Width = 380, RightToLeft = RightToLeft.No, Font = Ui.Mono, Margin = new Padding(4, 8, 4, 4) };
    readonly Button _saveUrl = Ui.Secondary("حفظ العنوان");

    // Help tab
    readonly Label _manualPort = Ui.Text("", Ui.Body, Ui.Muted, CardWidth - 60);

    bool _exiting, _busy, _confirmOpen;
    string? _announcedUpdate;

    public MainForm(Runtime rt, bool startHidden)
    {
        _rt = rt;
        Text = "دايماً ديسك " + Runtime.Version;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = Ui.Body;
        BackColor = Ui.Canvas;
        ClientSize = new Size(900, 700);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Value;

        var tabs = new FlatTabs { Dock = DockStyle.Fill, RightToLeftLayout = true, Font = Ui.Body };
        tabs.TabPages.Add(StatusTab());
        tabs.TabPages.Add(ReceiptsTab());
        tabs.TabPages.Add(PairTab());
        // WO-91: a device revoked in the dashboard unlocks pairing at once, not at the next 5-minute heartbeat.
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 2) RefreshFromServer(); };
        tabs.TabPages.Add(HelpTab());

        Controls.Add(tabs);
        Controls.Add(Nav(tabs));
        Controls.Add(BuildBanners());
        Controls.Add(Header());

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, Font = Ui.Body };
        menu.Items.Add("فتح دايماً ديسك", null, (_, _) => ShowWindow());
        _trayUpdate.Click += (_, _) => { if (_rt.Cfg.Current.UpdateUrl is { } u) Ui.OpenUrl(u); };
        menu.Items.Add(_trayUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("خروج (إرجاع الطابعة)", null, (_, _) => ExitApp());
        _tray = new NotifyIcon { Icon = AppIcon.Value, Text = "دايماً ديسك", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _rt.ReceiptProcessed += r => BeginInvokeSafe(() => AddReceipt(r));
        _rt.StateChanged += () => BeginInvokeSafe(RefreshState);
        // WO-97: the owner confirms on his dashboard; the window no longer opens on the cashier's PC by itself.
        _rt.ConfirmationNeeded += _ => BeginInvokeSafe(RefreshState);
        _rt.ReconfirmNeeded += () => BeginInvokeSafe(() =>
            _tray.ShowBalloonTip(5000, "دايماً ديسك", "تغيّرت لغة قراءة فواتير الصورة: أكّد الحقول مرة أخرى في الفاتورة التالية.", ToolTipIcon.Warning));
        _rt.UpdateAvailable += (v, _) => BeginInvokeSafe(() =>
        {
            RefreshState();
            if (_announcedUpdate == v) return;
            _announcedUpdate = v;
            _tray.ShowBalloonTip(5000, "دايماً ديسك", Ui.Latin($"يوجد تحديث: الإصدار {v}. سيحدّث البرنامج نفسه تلقائياً حين لا تُطبع فواتير."), ToolTipIcon.Info);
        });
        _rt.UpdateReady += (v, path) => BeginInvokeSafe(() =>
        {
            if (!AutoUpdate.Apply(_rt, v, path)) { RefreshState(); return; }
            _exiting = true; // the new version listens on the same ports: no restore
            _tray.Visible = false;
            Application.Exit();
        });
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _rt.ExitRestore();

        if (startHidden)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }
        Shown += (_, _) => { if (startHidden) Hide(); LoadPrinters(); LoadNetwork(); RefreshState(); };
        RefreshState();
        Ui.LatinDigits(this);
    }

    // ---------- layout ----------

    Control Header()
    {
        var p = new Panel { Dock = DockStyle.Top, Height = Ui.Px(112), BackColor = Ui.Surface, Padding = new Padding(24, 14, 24, 12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Ui.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new PictureBox { Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(4, 6, 4, 4), BackColor = Ui.Surface };
        try { logo.Image = new Icon(AppIcon.Value, 48, 48).ToBitmap(); } catch { }
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, BackColor = Ui.Surface, Margin = Padding.Empty };
        var title = Ui.Text("دايماً ديسك", Ui.Title);
        title.Margin = new Padding(4, 0, 4, 0);
        _subline.Font = Ui.Strong;
        _subline.ForeColor = Ui.Ink2;
        _subline.Margin = new Padding(4, 0, 4, 0);
        _headline.Font = Ui.Body;
        _headline.Margin = new Padding(4, 2, 4, 0);
        stack.Controls.Add(title);
        stack.Controls.Add(_subline);
        stack.Controls.Add(_headline);
        var end = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 4, 0, 0) };
        var version = Ui.Text("الإصدار " + Runtime.Version, Ui.Body, Ui.Muted);
        end.Controls.Add(_pill);
        end.Controls.Add(version);
        grid.Controls.Add(logo, 0, 0);
        grid.Controls.Add(stack, 1, 0);
        grid.Controls.Add(end, 2, 0);
        p.Controls.Add(grid);
        p.Paint += (_, e) => { using var pen = new Pen(Ui.Line); e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1); };
        return p;
    }

    /// <summary>The pill nav over the borderless tabs (Ctrl+Tab inside a page still switches them).</summary>
    static Control Nav(TabControl tabs)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.Px(64), BackColor = Ui.Canvas, Padding = new Padding(20, 12, 20, 4), WrapContents = false };
        var buttons = new List<RoundButton>();
        void Paint()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (i == tabs.SelectedIndex) buttons[i].SetColors(Ui.AccentSoft, Ui.AccentSoft, Ui.Accent, Color.Empty);
                else buttons[i].SetColors(Ui.Canvas, Ui.Surface2, Ui.Ink2, Color.Empty);
                buttons[i].Font = i == tabs.SelectedIndex ? Ui.Strong : Ui.Body;
            }
        }
        for (int i = 0; i < tabs.TabCount; i++)
        {
            int index = i;
            var b = new RoundButton(tabs.TabPages[i].Text, Ui.Canvas, Ui.Surface2, Ui.Ink2, Color.Empty) { MinimumSize = new Size(96, 40) };
            b.Click += (_, _) => tabs.SelectedIndex = index;
            buttons.Add(b);
            bar.Controls.Add(b);
        }
        tabs.SelectedIndexChanged += (_, _) => Paint();
        Paint();
        return bar;
    }

    Control BuildBanners()
    {
        var host = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var confirmBtn = Ui.Primary("تأكيد الحقول");
        confirmBtn.Dock = DockStyle.Left;
        confirmBtn.Click += (_, _) => { if (_rt.Agent.PendingConfirmation is { } p) OpenConfirm(p); };
        var confirmText = Ui.Text(DeskAgent.AwaitingOwner + " (الإعدادات ← دايماً ديسك). يمكن للمثبّت التأكيد هنا.", Ui.Strong, Ui.Warning);
        confirmText.Dock = DockStyle.Fill;
        confirmText.AutoSize = false;
        confirmText.TextAlign = ContentAlignment.MiddleRight;
        _confirmBanner.Controls.Add(confirmText);
        _confirmBanner.Controls.Add(confirmBtn);

        var download = Ui.Secondary("تنزيل التحديث");
        download.Dock = DockStyle.Left;
        download.Click += (_, _) => { if (_rt.Cfg.Current.UpdateUrl is { } u) Ui.OpenUrl(u); };
        _updateText.Dock = DockStyle.Fill;
        _updateText.AutoSize = false;
        _updateText.TextAlign = ContentAlignment.MiddleRight;
        _updateBanner.Controls.Add(_updateText);
        _updateBanner.Controls.Add(download);

        host.Controls.Add(_updateBanner);
        host.Controls.Add(_confirmBanner);
        return host;
    }

    static TabPage Page(string title, out FlowLayoutPanel stack)
    {
        var page = new TabPage(title) { BackColor = Ui.Canvas, Padding = new Padding(20, 8, 20, 8) };
        stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Canvas };
        page.Controls.Add(stack);
        return page;
    }

    TabPage StatusTab()
    {
        var page = Page("الحالة", out var stack);

        _refresh.Click += (_, _) => LoadPrinters();
        _printers.ItemCheck += (_, e) =>
        {
            if (e.NewValue == CheckState.Checked && _printers.Items[e.Index] is PrinterItem { Refused: true } p)
            {
                e.NewValue = CheckState.Unchecked;
                Msg($"«{p.Name}» لا تصلح طابعةَ فواتير ({p.Note}). اختر طابعة الفواتير الحقيقية.", MessageBoxIcon.Warning);
            }
        };
        _start.Click += (_, _) => RunBusy(() => _rt.Install(CheckedPrinters()), InstallFailed,
            "تم التشغيل. كل فاتورة تمر الآن عبر دايماً ديسك وتُطبع كالمعتاد.", needsPrinter: true);
        _stop.Click += (_, _) => StopAndRestore();
        stack.Controls.Add(new Card("طابعات الفواتير", CardWidth).Add(
            Ui.Text("ضع علامة على كل طابعة يطبع عليها برنامج الكاشير الفواتير (طابعة USB أو COM أو طابعة شبكة). طابعة المطبخ لا تحتاج علامة.", Ui.Body, Ui.Muted, CardWidth - 60),
            Ui.Row(_printers, _refresh), Ui.Row(_start, _stop)));

        _netSave.Click += (_, _) => SaveNetwork();
        stack.Controls.Add(new Card("وضع الشبكة (أجهزة كاشير بلا Windows)", CardWidth).Add(
            Ui.Text("للتابلت وأجهزة الكاشير التي تطبع مباشرة إلى عنوان IP الطابعة. اكتب هنا عنوان الطابعة الحقيقي، ثم اكتب عنوان هذا الجهاز في جهاز الكاشير مكان عنوان الطابعة، مرة واحدة.", Ui.Body, Ui.Muted, CardWidth - 60),
            _netOn,
            Ui.Row(Ui.Text("عنوان الطابعة (IP)"), _netPrinter, Ui.Text("منفذ هذا الجهاز"), _netPort, _netSave),
            _netIp,
            Ui.Text("يجب أن يبقى هذا الجهاز يعمل ومتصلاً بالشبكة. إن أُطفئ فلن تطبع أجهزة الكاشير حتى يعود، أو أعد فيها عنوان الطابعة الحقيقي.", Ui.Body, Ui.Muted, CardWidth - 60)));

        stack.Controls.Add(new Card("آخر مشكلة", CardWidth).Add(_problem, _problemAt));

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 4, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var key in new[] { "المحل", "اسم الجهاز", "الطابعات", "عنوان هذا الجهاز", "نوع الفواتير", "QR بعد الفاتورة", "بانتظار الإرسال", "الإصدار" })
        {
            var value = Ui.Text("-", Ui.Strong, Ui.Ink, CardWidth - 260);
            _info[key] = value;
            grid.Controls.Add(Ui.Text(key, Ui.Body, Ui.Muted));
            grid.Controls.Add(value);
        }
        stack.Controls.Add(new Card("الاتصال", CardWidth).Add(grid));

        var counters = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 4, 0, 0) };
        string[] captions = { "كل الفواتير", "مسجّلة", "طُبع لها QR", "لم تُسجّل" };
        for (int i = 0; i < 4; i++)
        {
            counters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Ui.Surface, WrapContents = false };
            _today[i] = Ui.Text("0", Ui.BigNumber, i == 3 ? Ui.Danger : Ui.Ink);
            _today[i].RightToLeft = RightToLeft.No;
            cell.Controls.Add(_today[i]);
            cell.Controls.Add(Ui.Text(captions[i], Ui.Body, Ui.Muted));
            counters.Controls.Add(cell, i, 0);
        }
        stack.Controls.Add(new Card("فواتير اليوم", CardWidth).Add(counters,
            Ui.Text("استلام الزبائن للنقاط يظهر في لوحة التحكم ← الإعدادات ← دايماً ديسك.", Ui.Body, Ui.Muted, CardWidth - 60)));
        return page;
    }

    TabPage ReceiptsTab()
    {
        var page = new TabPage("الفواتير") { BackColor = Ui.Canvas, Padding = new Padding(20, 8, 20, 8) };
        foreach (var (h, w) in new[] { ("الوقت", 90), ("النوع", 80), ("رقم الفاتورة", 140), ("المجموع", 100), ("الثقة", 70), ("الحالة", 360) })
            _list.Columns.Add(h, w);
        // Clean rows: taller (an empty image list sets the row height), no grid lines, columns that fill the width
        // (status widest, no horizontal scroll), and a painted header in muted text without separators.
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Px(34)) };
        _list.HeaderStyle = ColumnHeaderStyle.None;
        int[] weights = { 90, 80, 140, 100, 70, 360 };
        var header = new Panel { Dock = DockStyle.Top, Height = Ui.Px(36), BackColor = Ui.Surface };
        _fit = Fit;
        void Fit()
        {
            int w = _list.ClientSize.Width, total = weights.Sum(), used = 0;
            if (w <= 0) return;
            for (int i = 0; i < weights.Length; i++)
            {
                int cw = i == weights.Length - 1 ? w - used : w * weights[i] / total;
                _list.Columns[i].Width = cw;
                used += cw;
            }
            header.Invalidate();
        }
        _list.Resize += (_, _) => Fit();
        header.Paint += (_, e) =>
        {
            int x = header.Width; // the first column is at the right (RTL)
            foreach (ColumnHeader c in _list.Columns)
            {
                var r = new Rectangle(x - c.Width + 4, 0, c.Width - 10, header.Height - 1);
                TextRenderer.DrawText(e.Graphics, c.Text, Ui.Strong, r, Ui.Muted,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                x -= c.Width;
            }
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        var listHost = new Card("آخر الفواتير", 0) { Dock = DockStyle.Fill, AutoSize = false, MinimumSize = Size.Empty, MaximumSize = Size.Empty };
        listHost.Controls.Clear();
        listHost.Controls.Add(_list);
        listHost.Controls.Add(header);
        var export = new LinkLabel { Text = "تصدير العينات إلى سطح المكتب", AutoSize = true, LinkColor = Ui.Accent, ActiveLinkColor = Ui.Accent, Margin = new Padding(4, 8, 4, 4) };
        export.LinkClicked += (_, _) => ExportSamples();
        var logs = new LinkLabel { Text = "فتح السجل", AutoSize = true, LinkColor = Ui.Accent, ActiveLinkColor = Ui.Accent, Margin = new Padding(16, 8, 4, 4) };
        // WO-91: Notepad from this (elevated) program opens the log in the locked data folder without an Explorer prompt.
        logs.LinkClicked += (_, _) => { try { Process.Start("notepad.exe", "\"" + _rt.Log.CurrentPath + "\""); } catch { } };
        var bottom = Ui.Row(Ui.Text("آخر 100 فاتورة منذ تشغيل البرنامج.", Ui.Body, Ui.Muted), export, logs);
        bottom.Dock = DockStyle.Bottom;
        page.Controls.Add(listHost);
        page.Controls.Add(bottom);
        return page;
    }

    TabPage PairTab()
    {
        var page = Page("الربط", out var stack);
        _pair.Click += async (_, _) => await Pair();
        _code.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Pair(); } };
        stack.Controls.Add(new Card("ربط الجهاز بالمحل", CardWidth).Add(_pairStatus,
            Ui.Text("أنشئ رمز ربط من لوحة التحكم ← الإعدادات ← دايماً ديسك. الرمز 6 خانات ويصلح 10 دقائق.", Ui.Body, Ui.Muted, CardWidth - 60),
            Ui.Row(Ui.Text("رمز الربط"), _code, Ui.Text("اسم الجهاز"), _deviceName, _pair)));

        _reconfirm.Click += (_, _) =>
        {
            if (Ask("سيطلب البرنامج تأكيد سطر رقم الفاتورة وسطر المجموع في الفاتورة التالية، ولا تُسجَّل الفواتير حتى تؤكدهما. هل تريد المتابعة؟"))
                _rt.RequestReconfirm();
        };
        stack.Controls.Add(new Card("حقول الفاتورة", CardWidth).Add(_fieldsStatus, _reconfirm));

        _saveUrl.Click += (_, _) =>
        {
            bool wasPaired = _rt.Cfg.Current.Paired;
            if (!_rt.SetServerUrl(_url.Text))
                Msg("العنوان غير صحيح. اكتب عنواناً كاملاً يبدأ بـ https:// مثل https://daymanjo.com/api/desk", MessageBoxIcon.Warning);
            else if (wasPaired && !_rt.Cfg.Current.Paired)
                Msg("تغيّر الخادم، فأُلغي الربط. اربط الجهاز من جديد برمز من الخادم الجديد.", MessageBoxIcon.Information);
            _url.Text = _rt.Cfg.Current.ServerUrl;
            RefreshState();
        };
        stack.Controls.Add(new Card("عنوان الخادم (للدعم الفني فقط)", CardWidth).Add(
            Ui.Text("لا تغيّر هذا العنوان إلا بطلب من دعم دايماً. تغيير الخادم يلغي الربط.", Ui.Body, Ui.Muted, CardWidth - 60),
            Ui.Row(_url, _saveUrl)));
        return page;
    }

    TabPage HelpTab()
    {
        var page = Page("مساعدة", out var stack);
        string[] steps =
        {
            "1. نزّل الملف DaymanDesk.exe وشغّله، ووافق على صلاحيات المدير. إن ظهر تحذير Windows فاضغط More info ثم Run anyway لمتابعة التشغيل. بعدها تجد «دايماً ديسك» في قائمة Start، وأيقونته بجانب الساعة (إن لم تظهر فاضغط السهم ^).",
            "2. ضع علامة على طابعة الفواتير (أو أكثر) في تبويب «الحالة» واضغط «تشغيل دايماً ديسك». تبقى الفواتير تُطبع كالمعتاد. إن كان جهاز الكاشير تابلت أو جهازاً بلا Windows ففعّل «وضع الشبكة».",
            "3. أنشئ رمز ربط من لوحة التحكم ← الإعدادات ← دايماً ديسك، واكتبه في تبويب «الربط» ثم اضغط «ربط».",
            "4. اطبع فاتورة من برنامج الكاشير، ثم أكّد في نافذة «تأكيد الحقول» سطر رقم الفاتورة وسطر المجموع.",
            "5. اطبع فاتورة أخرى: تخرج بعدها قسيمة QR. امسحها بالجوال وتأكد من وصول النقاط إلى البطاقة.",
        };
        var install = new Card("خطوات التثبيت", CardWidth);
        foreach (var s in steps) install.Add(Ui.Text(s, Ui.Body, Ui.Ink, CardWidth - 60));
        stack.Controls.Add(install);

        var stop = Ui.Secondary("إيقاف وإرجاع الطابعة");
        stop.Click += (_, _) => StopAndRestore();
        stack.Controls.Add(new Card("إيقاف وإرجاع الطابعة", CardWidth).Add(
            Ui.Text("يرجع طابعة الفواتير إلى منفذها الأصلي، ويحذف طابعة دايماً ومهام البرنامج. بعدها تُطبع الفواتير مباشرة كما كانت قبل التثبيت.", Ui.Body, Ui.Ink, CardWidth - 60),
            stop, _manualPort));

        stack.Controls.Add(new Card("الأمان", CardWidth).Add(
            Ui.Text("الفاتورة تُطبع أولاً ثم يقرؤها البرنامج، فلا يؤخر الطباعة. إن توقف البرنامج لأي سبب تعود الطابعة إلى منفذها الأصلي خلال دقيقة.", Ui.Body, Ui.Ink, CardWidth - 60),
            Ui.Text("لا يرسل البرنامج إلى الخادم إلا رقم الفاتورة والمجموع، وعينات من أول الفواتير لتحسين القراءة إن فعّل صاحب المحل ذلك.", Ui.Body, Ui.Muted, CardWidth - 60)));
        return page;
    }

    // ---------- state ----------

    /// <summary>WO-91: a heartbeat now (a revoked device is unpaired at once), then the window again.</summary>
    void RefreshFromServer() => Task.Run(async () =>
    {
        try { await _rt.Agent.HeartbeatAsync(); }
        catch (Exception ex) { _rt.Log.Warn("heartbeat from the pairing tab: " + ex.Message); }
        BeginInvokeSafe(RefreshState);
    });

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    void LoadPrinters()
    {
        Task.Run(() =>
        {
            try { return _rt.Setup.ListPrinters(); }
            catch (Exception ex) { _rt.Log.Error("list printers: " + ex.Message); return new List<PrinterInfo>(); }
        }).ContinueWith(t => BeginInvokeSafe(() =>
        {
            _printers.Items.Clear();
            var chosen = _rt.Cfg.Current.Printers.Select(r => r.PrinterName).ToHashSet();
            foreach (var p in t.Result.Where(p => !p.Name.StartsWith("Dayman Raw (", StringComparison.Ordinal)))
            {
                // The original port, not DAYMAN_9100, for a printer that is already intercepted.
                var port = DeskConfig.IsDaymanPort(p.PortName)
                    ? _rt.Cfg.Current.Printers.FirstOrDefault(r => r.PrinterName == p.Name)?.OriginalPort ?? p.PortName
                    : p.PortName;
                // WO-91: PDF/XPS, OneNote, fax and shared printers are listed with why, and cannot be ticked.
                string? note = p.Unsupported is { } why ? "لا تصلح: " + why : p.FilePort ? "منفذ ملف: للتجربة فقط" : null;
                _printers.Items.Add(new PrinterItem(p.Name, port, note, p.Unsupported != null), chosen.Contains(p.Name) && p.Unsupported == null);
            }
            if (_printers.Items.Count == 0) Msg("لم يُعثر على أي طابعة. تأكد أن طابعة الفواتير معرّفة في Windows ثم اضغط «تحديث القائمة».", MessageBoxIcon.Warning);
        }));
    }

    sealed record PrinterItem(string Name, string Port, string? Note = null, bool Refused = false)
    {
        public override string ToString() => Ui.Latin((Port.Length > 0 ? $"{Name}   ({Port})" : Name) + (Note != null ? "   [" + Note + "]" : ""));
    }

    List<string> CheckedPrinters() => _printers.CheckedItems.OfType<PrinterItem>().Select(p => p.Name).ToList();

    /// <summary>The «وضع الشبكة» fields from the config (at start and after a save, never while the owner types).</summary>
    void LoadNetwork()
    {
        var n = _rt.Cfg.Current.Network;
        _netOn.Checked = n.Enabled;
        _netPrinter.Text = n.PrinterHost ?? "";
        _netPort.Text = n.ListenPort.ToString(CultureInfo.InvariantCulture);
    }

    void SaveNetwork()
    {
        bool on = _netOn.Checked;
        if (!int.TryParse(_netPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)) port = 0;
        string host = _netPrinter.Text.Trim();
        string ips = string.Join("، ", PassThroughServer.LocalIPv4());
        RunBusy(() => _rt.SetNetworkMode(on, host, port, DeskConfig.ListenPort), "بقي وضع الشبكة كما كان قبل الحفظ.",
            on ? $"تم تفعيل وضع الشبكة. اكتب في جهاز الكاشير عنوان الطابعة {ips} والمنفذ {port}، ثم اطبع فاتورة للتجربة."
               : "أُوقف وضع الشبكة. أعد في جهاز الكاشير عنوان الطابعة الحقيقي.",
            after: LoadNetwork);
    }

    void RefreshState()
    {
        var c = _rt.Cfg.Current;
        bool on = _rt.On;
        bool printersOn = c.Active && c.Printers.Any(r => _rt.Server.IsRunning(r.Id));
        var net = c.Network.Route();
        bool netOn = net != null && _rt.Server.IsRunning(net.Id);

        if (on && c.Paired && c.FieldsConfirmed && !c.QrEnabled) SetHeadline("يعمل: تُسجَّل الفواتير بلا QR (أوقف صاحب المحل QR من لوحة التحكم).", Ui.Warning);
        else if (on && c.Paired && c.FieldsConfirmed && c.LastPrintKind == "image" && c.LastProblem == Problems.OcrUnavailable)
            SetHeadline("يعمل، لكن قراءة فواتير الصورة غير مثبّتة: بلا QR. انظر «آخر مشكلة».", Ui.Warning);
        else if (on && c.Paired && c.FieldsConfirmed) SetHeadline("يعمل: كل فاتورة تمر عبر دايماً ديسك ويُطبع لها QR.", Ui.Success);
        else if (on && c.Paired) SetHeadline("يعمل: " + DeskAgent.AwaitingOwner + ".", Ui.Warning);
        else if (on) SetHeadline("يعمل بلا ربط: تُطبع الفواتير كالمعتاد بلا QR. اربط الجهاز من تبويب «الربط».", Ui.Warning);
        else SetHeadline("متوقف: الطابعة تطبع مباشرة بلا دايماً ديسك.", Ui.Muted);

        _start.Enabled = !printersOn && !_busy;
        _stop.Enabled = (c.Active || c.Printers.Any(r => r.RawQueueName != null) || c.Network.Enabled) && !_busy;
        _printers.Enabled = !printersOn && !_busy;
        _netSave.Enabled = !_busy;

        var ips = PassThroughServer.LocalIPv4();
        string ipText = ips.Count == 0 ? "غير متصل بشبكة" : string.Join("، ", ips);
        _netIp.Text = netOn
            ? $"يعمل. في جهاز الكاشير: عنوان الطابعة {ipText} والمنفذ {net!.ListenPort}. الطابعة الحقيقية {net.DirectHost}."
            : $"عنوان هذا الجهاز: {ipText}";
        _netIp.ForeColor = netOn ? Ui.Success : Ui.Ink;

        SetPill(on, c.Paired, !string.IsNullOrEmpty(c.LastProblem));
        _subline.Text = c.Paired ? c.ShopName ?? "" : "غير مربوط";
        _info["المحل"].Text = c.Paired ? c.ShopName ?? "-" : "غير مربوط";
        _info["اسم الجهاز"].Text = c.Paired ? c.DeviceName ?? "-" : "-";
        var names = new List<string>();
        if (printersOn) names.AddRange(c.Printers.Where(r => _rt.Server.IsRunning(r.Id)).Select(r => r.PrinterName ?? "-"));
        if (netOn) names.Add("وضع الشبكة ← " + net!.DirectHost);
        _info["الطابعات"].Text = names.Count == 0 ? "لا شيء" : string.Join("، ", names);
        _info["عنوان هذا الجهاز"].Text = ipText;
        _info["نوع الفواتير"].Text = c.LastPrintKind switch
        {
            "text" => "نص (يقرؤها البرنامج)",
            "image" => _rt.Ocr.LanguageTag is { } lang
                ? $"صورة (يقرؤها البرنامج بقراءة النص من الصورة، اللغة {lang})"
                : "صورة (يقرؤها البرنامج بقراءة النص من الصورة)",
            _ => "لم تُطبع فاتورة بعد",
        };
        _info["QR بعد الفاتورة"].Text = !c.Paired ? "بعد ربط الجهاز" : !c.QrEnabled ? "أوقفه صاحب المحل من لوحة التحكم"
            : !c.FieldsConfirmed ? "بعد تأكيد صاحب المحل للحقول من لوحة التحكم" : "يعمل";
        int q = _rt.Queue.Count;
        _info["بانتظار الإرسال"].Text = q == 0 ? "لا شيء" : q.ToString(CultureInfo.InvariantCulture) + " (تُرسل عند عودة الاتصال)";
        _info["الإصدار"].Text = Runtime.Version + (c.UpdateVersion != null ? $"  (يوجد تحديث: {c.UpdateVersion})"
            : c.UpdatedTo == Runtime.Version ? $"  (تم التحديث إلى {Runtime.Version})" : "  (أحدث إصدار)");

        string today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isToday = c.StatsDay == today;
        int[] vals = isToday ? new[] { c.TodayReceipts, c.TodayRegistered, c.TodayQr, c.TodayNotRegistered } : new int[4];
        for (int i = 0; i < 4; i++) _today[i].Text = vals[i].ToString(CultureInfo.InvariantCulture);
        _today[3].ForeColor = vals[3] > 0 ? Ui.Danger : Ui.Ink;

        if (string.IsNullOrEmpty(c.LastProblem))
        {
            _problem.Text = "لا توجد مشاكل.";
            _problem.ForeColor = Ui.Success;
            _problemAt.Text = "";
        }
        else
        {
            _problem.Text = Problems.Describe(c.LastProblem, c.LastProblemDetail);
            _problem.ForeColor = Ui.Danger;
            _problemAt.Text = c.LastProblemAt is { } at ? ProblemTime(at.ToLocalTime()) : "";
        }

        _pairStatus.Text = c.Paired ? $"مربوط بمحل {c.ShopName} باسم «{c.DeviceName}»." : "الجهاز غير مربوط بأي محل.";
        _code.Enabled = _pair.Enabled = _deviceName.Enabled = !c.Paired;
        if (!_url.Focused) _url.Text = c.ServerUrl;
        _fieldsStatus.Text = !c.Paired ? "يُطلب التأكيد في أول فاتورة بعد الربط."
            : c.FieldsConfirmed ? "أكّد صاحب المحل سطر رقم الفاتورة وسطر المجموع. إن تغيّر شكل الفاتورة فاضغط «إعادة تأكيد الحقول»."
            : "لم تُؤكَّد بعد: اطبع فاتورة من برنامج الكاشير، ثم يؤكد صاحب المحل سطريها من لوحة التحكم.";
        _reconfirm.Enabled = c.Paired && c.FieldsConfirmed;
        var originals = c.Printers.Where(r => !string.IsNullOrEmpty(r.OriginalPort)).Select(r => $"«{r.PrinterName}» ← {r.OriginalPort}").ToList();
        _manualPort.Text = originals.Count > 0
            ? "إن لم تطبع الفواتير بعد الإيقاف: افتح خصائص الطابعة ← Ports واختر منفذها الأصلي: " + string.Join("، ", originals) + "."
            : "";

        _confirmBanner.Visible = c.Paired && !c.FieldsConfirmed && _rt.Agent.PendingConfirmation != null;
        _updateBanner.Visible = c.UpdateVersion != null && c.UpdateUrl != null;
        _updateText.Text = $"يوجد تحديث: الإصدار {c.UpdateVersion}. يحدّث البرنامج نفسه تلقائياً حين لا تُطبع فواتير. إن لم يتم، نزّله وأغلق البرنامج من الأيقونة بجانب الساعة وشغّل الملف الجديد.";
        _trayUpdate.Visible = _updateBanner.Visible;
        _trayUpdate.Text = Ui.Latin($"يوجد تحديث: الإصدار {c.UpdateVersion}");

        string tray = on ? (c.Paired ? "دايماً ديسك: يعمل" : "دايماً ديسك: يعمل بلا ربط") : "دايماً ديسك: متوقف";
        if (c.UpdateVersion != null) tray += " (يوجد تحديث)";
        tray = Ui.Latin(tray);
        _tray.Text = tray.Length > 63 ? tray[..63] : tray; // NotifyIcon.Text is limited to 63 characters
    }

    /// <summary>«الساعة 22:08» today, else «يوم 19.11 الساعة 22:08» (no dashes: they reorder in RTL text).</summary>
    static string ProblemTime(DateTimeOffset at)
    {
        string time = "الساعة " + at.ToString("HH:mm", CultureInfo.InvariantCulture);
        return at.Date == DateTimeOffset.Now.Date ? time : "يوم " + at.ToString("dd.MM", CultureInfo.InvariantCulture) + " " + time;
    }

    void SetPill(bool on, bool paired, bool problem)
    {
        if (problem) _pill.Set("مشكلة", Ui.Danger, Ui.DangerBg);
        else if (on && paired) _pill.Set("متصل", Ui.Success, Ui.SuccessBg);
        else _pill.Set("غير متصل", Ui.Warning, Ui.WarningBg);
    }

    void SetHeadline(string text, Color color)
    {
        _headline.Text = text;
        _headline.ForeColor = color;
    }

    internal void AddReceiptForPreview(ReceiptRecord r) => AddReceipt(r);

    void AddReceipt(ReceiptRecord r)
    {
        var item = new ListViewItem(Ui.Latin(r.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        item.SubItems.Add(r.KindLabel);
        item.SubItems.Add(Ui.Latin(r.InvoiceNo ?? "-"));
        item.SubItems.Add(Ui.Latin(r.Total?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-"));
        item.SubItems.Add(Ui.Latin(r.Kind != ReceiptKind.Unknown && !r.NonReceipt && r.Confidence > 0 ? ((int)Math.Round(r.Confidence * 100)).ToString(CultureInfo.InvariantCulture) + "%" : "-"));
        item.SubItems.Add(Ui.Latin(r.Status));
        if (!r.Registered && r.Status.Contains("بلا QR")) item.ForeColor = Ui.Muted;
        _list.Items.Insert(0, item);
        while (_list.Items.Count > 100) _list.Items.RemoveAt(_list.Items.Count - 1);
        _fit?.Invoke(); // the vertical scrollbar may have just appeared
    }

    // ---------- actions ----------

    void OpenConfirm(FieldConfirmation p)
    {
        if (_confirmOpen || IsDisposed) return;
        _confirmOpen = true;
        try
        {
            using var dlg = new ConfirmFieldsForm(p, (i, t) => _rt.ConfirmFields(p, i, t));
            if (dlg.ShowDialog(this) == DialogResult.OK)
                Msg("حُفظ التأكيد. من الآن تُسجَّل كل فاتورة ويُطبع لها QR.", MessageBoxIcon.Information);
            else _rt.Agent.DismissConfirmation(); // «لاحقاً»: the next receipt opens the window fresh (WO-91)
        }
        finally { _confirmOpen = false; }
        RefreshState();
    }

    void StopAndRestore()
    {
        if (!Ask("سترجع طابعات الفواتير إلى منافذها الأصلية، ويتوقف وضع الشبكة ودايماً ديسك عن العمل على هذا الجهاز. هل تريد المتابعة؟")) return;
        RunBusy(_rt.StopAndRestore, "لم نتأكد من إرجاع كل الطابعات. اضغط «إيقاف وإرجاع الطابعة» مرة أخرى، أو أرجع منفذ الطابعة يدوياً كما في تبويب «مساعدة».", "أُعيدت الطابعات إلى منافذها الأصلية وتوقف دايماً ديسك. تُطبع الفواتير الآن مباشرة. إن كان وضع الشبكة مفعّلاً فأعد في جهاز الكاشير عنوان الطابعة الحقيقي.", after: LoadNetwork);
    }

    const string InstallFailed = "أعاد البرنامج الطابعات إلى منافذها. إن لم تطبع الفواتير فاضغط «إيقاف وإرجاع الطابعة» في تبويب «مساعدة».";

    /// <param name="failed">WO-91: what is true after this action failed (never a claim the action cannot back).</param>
    void RunBusy(Action work, string failed, string done, bool needsPrinter = false, Action? after = null)
    {
        if (_busy) return;
        if (needsPrinter && _printers.CheckedItems.Count == 0)
        {
            Msg("ضع علامة على طابعة الفواتير أولاً.", MessageBoxIcon.Warning);
            return;
        }
        _busy = true;
        UseWaitCursor = true;
        RefreshState();
        Task.Run(work).ContinueWith(t => BeginInvokeSafe(() =>
        {
            _busy = false;
            UseWaitCursor = false;
            after?.Invoke();
            RefreshState();
            if (t.Exception?.InnerException is { } ex)
            {
                _rt.Log.Error("ui action: " + ex.Message);
                string text = ex is SetupException se ? se.Arabic : "حدث خطأ غير متوقع.";
                Msg(text + "\n\n" + failed + " التفاصيل في سجل البرنامج (تبويب «الفواتير» ← فتح السجل).", MessageBoxIcon.Error);
            }
            else Msg(done, MessageBoxIcon.Information);
        }));
    }

    async Task Pair()
    {
        if (!_pair.Enabled) return;
        string code = _code.Text.Trim();
        if (DeskAgent.PairCodeProblem(code) is { } problem)
        {
            Msg(problem, MessageBoxIcon.Warning);
            return;
        }
        string name = string.IsNullOrWhiteSpace(_deviceName.Text) ? "كاشير" : _deviceName.Text.Trim();
        _pair.Enabled = false;
        _pair.Text = "جارٍ الربط...";
        ApiResult<PairResponse> r;
        try { r = await _rt.Agent.PairAsync(code, name); }
        finally { _pair.Text = "ربط"; }
        RefreshState();
        if (r.Ok)
        {
            _code.Text = "";
            Msg($"تم الربط بمحل {r.Value!.ShopName}. اطبع الآن فاتورة من برنامج الكاشير لتأكيد الحقول.", MessageBoxIcon.Information);
            _ = _rt.Agent.HeartbeatAsync();
        }
        else Msg(DeskAgent.PairError(r), MessageBoxIcon.Warning);
    }

    void ExportSamples()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "dayman-samples-" + DateTime.Now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".zip");
            _rt.Samples.ExportZip(path);
            Msg("حُفظت العينات على سطح المكتب:\n" + path + "\n\nأرسلها إلى دعم دايماً فقط، فقد تحوي بيانات زبائن.", MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _rt.Log.Warn("export samples: " + ex.Message);
            Msg("تعذّر حفظ العينات على سطح المكتب. أغلق أي ملف عينات مفتوح ثم أعد المحاولة.", MessageBoxIcon.Error);
        }
    }

    /// <summary>Set by --screenshots: no message box may block the CI run.</summary>
    internal static bool Preview;

    void Msg(string text, MessageBoxIcon icon)
    {
        if (Preview) return;
        MessageBox.Show(this, Ui.Latin(text), "دايماً ديسك", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }

    bool Ask(string text) => !Preview &&
        MessageBox.Show(this, Ui.Latin(text), "دايماً ديسك", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == DialogResult.Yes;

    void ShowWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _rt.ExitRestore();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Closing the window keeps the program in the tray: printing keeps going through it.
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, "دايماً ديسك", "البرنامج يعمل في الخلفية بجانب الساعة.", ToolTipIcon.Info);
            return;
        }
        if (!_exiting) _rt.ExitRestore();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}
