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
    const int CardWidth = 820, Half = (CardWidth - 16) / 2;
    readonly Runtime _rt;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _trayUpdate = new() { Visible = false };

    // Header and banners
    readonly Label _headline = Ui.Text("", Ui.Heading);
    readonly Label _subline = Ui.Text("", Ui.Body, Ui.Muted);
    // WO-101: the owner-confirm and update banners are callouts under the header.
    readonly Callout _confirmBanner = new(CardWidth) { Dock = DockStyle.Top, Visible = false, Margin = Padding.Empty };
    readonly Callout _updateBanner = new(CardWidth) { Dock = DockStyle.Top, Visible = false, Margin = Padding.Empty };
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
    readonly Label[] _today = new Label[3];
    readonly Label _todayMore = Ui.Text("", Ui.Body, Ui.Muted, Half - 60);
    readonly Pill _connPill = new();
    readonly Card _connCard = new("الاتصال", Half, Glyph.Link);
    readonly Card _todayCard = new("اليوم", Half, Glyph.Chart);
    readonly FlowLayoutPanel _printerRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 0, 0, 8) };
    string _printerKey = "";
    readonly Label _printersLoading = Ui.Text("جارٍ البحث عن الطابعات...", Ui.Body, Ui.Muted);
    readonly Callout _problem = new(CardWidth);
    readonly RoundButton _problemAction = Ui.Secondary("");
    Action? _problemDo;
    readonly Label _version = Ui.Text("", Ui.Strong, Ui.Ink, CardWidth - 120);
    readonly Label _versionNote = Ui.Text("", Ui.Body, Ui.Muted, CardWidth - 120);
    TabControl? _tabs;

    // Receipts tab
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Ui.Body, RightToLeftLayout = true, BorderStyle = BorderStyle.None };

    // Pairing tab
    readonly Card _pairCard = new("ربط الجهاز بالمحل", CardWidth, Glyph.Link);
    readonly Card _pairedCard = new("الجهاز مربوط", CardWidth, Glyph.Check, Ui.Success, Ui.SuccessBg);
    readonly StepsBar _steps = new(CardWidth - 60, "حمّل من لوحة التحكم", "اكتب رمز الربط", "جاهز");
    readonly StepsBar _stepsDone = new(CardWidth - 60, "حمّل من لوحة التحكم", "اكتب رمز الربط", "جاهز") { Current = 3 };
    readonly CodeBox _code = new();
    readonly LatinTextBox _deviceName = new() { Width = 220, Text = "كاشير 1", MaxLength = 40, Font = Ui.Body, Margin = new Padding(4, 8, 4, 4) };
    readonly RoundButton _pair = Ui.Primary("ربط");
    readonly Callout _pairError = new(CardWidth - 60) { Visible = false, Margin = new Padding(4, 4, 4, 8) };
    readonly Label _pairedShop = Ui.Text("", Ui.Title, Ui.Ink, CardWidth - 60);
    readonly Label _pairedDevice = Ui.Text("", Ui.Body, Ui.Muted, CardWidth - 60);
    readonly Button _unpair = Ui.Secondary("إلغاء الربط");
    readonly Pill _fieldsPill = new();
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
        _tabs = tabs;
        tabs.TabPages.Add(StatusTab());
        tabs.TabPages.Add(ReceiptsTab());
        tabs.TabPages.Add(PairTab());
        // WO-91: a device revoked in the dashboard unlocks pairing at once, not at the next 5-minute heartbeat.
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 2 && !Preview) RefreshFromServer(); };
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
        MenuRenderer.Apply(menu);
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
        var host = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Canvas, Padding = new Padding(20, 12, 20, 0) };
        var confirmBtn = Ui.Primary("تأكيد الحقول");
        confirmBtn.Click += (_, _) => { if (_rt.Agent.PendingConfirmation is { } p) OpenConfirm(p); };
        _confirmBanner.Set(Glyph.Alert, Ui.Warning, Ui.WarningBg, DeskAgent.AwaitingOwner,
            "الإعدادات ← دايماً ديسك. يمكن للمثبّت التأكيد هنا.");
        _confirmBanner.SetAction(confirmBtn);

        var download = Ui.Secondary("تنزيل التحديث");
        download.Click += (_, _) => { if (_rt.Cfg.Current.UpdateUrl is { } u) Ui.OpenUrl(u); };
        _updateBanner.SetAction(download);

        var gap = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Ui.Canvas };
        host.Controls.Add(_updateBanner);
        host.Controls.Add(gap);
        host.Controls.Add(_confirmBanner);
        void Fit()
        {
            int w = Math.Max(300, ClientSize.Width - host.Padding.Horizontal);
            _confirmBanner.Fit(w);
            _updateBanner.Fit(w);
            gap.Visible = _confirmBanner.Visible && _updateBanner.Visible;
            host.Padding = new Padding(20, _confirmBanner.Visible || _updateBanner.Visible ? 12 : 0, 20, 0);
        }
        Resize += (_, _) => Fit();
        _confirmBanner.VisibleChanged += (_, _) => Fit();
        _updateBanner.VisibleChanged += (_, _) => Fit();
        Fit();
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

        // Row 1: the connection and today's counters side by side.
        foreach (var key in new[] { "المحل", "اسم الجهاز", "عنوان هذا الجهاز", "نوع الفواتير", "QR بعد الفاتورة" })
            _info[key] = Ui.Text("-", Ui.Strong, Ui.Ink, Half - 60 - 120);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 4, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var kv in _info)
        {
            grid.Controls.Add(Ui.Text(kv.Key, Ui.Body, Ui.Muted));
            grid.Controls.Add(kv.Value);
        }
        _connCard.Add(_connPill, grid);

        var counters = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Ui.Surface, Margin = new Padding(0, 4, 0, 4) };
        string[] captions = { "كل الفواتير", "طُبع لها QR", "بانتظار الإرسال" };
        for (int i = 0; i < 3; i++)
        {
            counters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (Half - 60) / 3));
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Ui.Surface, WrapContents = false };
            _today[i] = Ui.Text("0", Ui.BigNumber, Ui.Ink);
            _today[i].RightToLeft = RightToLeft.No;
            cell.Controls.Add(_today[i]);
            cell.Controls.Add(Ui.Text(captions[i], Ui.Body, Ui.Muted));
            counters.Controls.Add(cell, i, 0);
        }
        _todayCard.Add(counters, _todayMore,
            Ui.Text("استلام الزبائن للنقاط يظهر في لوحة التحكم ← الإعدادات ← دايماً ديسك.", Ui.Body, Ui.Muted, Half - 60));
        _connCard.Margin = new Padding(0, 0, 0, 16);
        _todayCard.Margin = new Padding(16, 0, 0, 16);
        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.Canvas, Margin = Padding.Empty };
        top.Controls.Add(_connCard);
        top.Controls.Add(_todayCard);
        stack.Controls.Add(top);

        // Row 2: the last problem as a callout, with what to do.
        _problemAction.Click += (_, _) => _problemDo?.Invoke();
        stack.Controls.Add(_problem);

        // Row 3: printers.
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
        stack.Controls.Add(new Card("طابعات الفواتير", CardWidth, Glyph.Printer).Add(
            _printerRows,
            Ui.Text("ضع علامة على كل طابعة يطبع عليها برنامج الكاشير الفواتير (طابعة USB أو COM أو طابعة شبكة). طابعة المطبخ لا تحتاج علامة.", Ui.Body, Ui.Muted, CardWidth - 60),
            _printersLoading, Ui.Row(new FieldBox(_printers), _refresh), Ui.Row(_start, _stop)));

        _netSave.Click += (_, _) => SaveNetwork();
        stack.Controls.Add(new Card("وضع الشبكة (أجهزة كاشير بلا Windows)", CardWidth, Glyph.Wifi).Add(
            Ui.Text("للتابلت وأجهزة الكاشير التي تطبع مباشرة إلى عنوان IP الطابعة. اكتب هنا عنوان الطابعة الحقيقي، ثم اكتب عنوان هذا الجهاز في جهاز الكاشير مكان عنوان الطابعة، مرة واحدة.", Ui.Body, Ui.Muted, CardWidth - 60),
            _netOn,
            Ui.Row(Ui.Text("عنوان الطابعة (IP)"), new FieldBox(_netPrinter), Ui.Text("منفذ هذا الجهاز"), new FieldBox(_netPort), _netSave),
            _netIp,
            Ui.Text("يجب أن يبقى هذا الجهاز يعمل ومتصلاً بالشبكة. إن أُطفئ فلن تطبع أجهزة الكاشير حتى يعود، أو أعد فيها عنوان الطابعة الحقيقي.", Ui.Body, Ui.Muted, CardWidth - 60)));

        stack.Controls.Add(new Card("الإصدار", CardWidth, Glyph.Update, Ui.Success, Ui.SuccessBg).Add(_version, _versionNote));
        return page;
    }

    /// <summary>One printer row: its state as a pill, its name, and the line that says its original port is kept.</summary>
    static Control PrinterRow(string name, bool running, string? originalPort)
    {
        var pill = new Pill();
        if (running) pill.Set("يعمل", Ui.Success, Ui.SuccessBg); else pill.Set("متوقف", Ui.Muted, Ui.Surface2);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Surface2, Margin = new Padding(4, 2, 4, 2) };
        text.Controls.Add(Ui.Text(Ui.Latin(name), Ui.Strong, Ui.Ink, CardWidth - 220));
        if (!string.IsNullOrEmpty(originalPort))
        {
            var port = Ui.Text(Ui.Latin($"المنفذ الأصلي محفوظ: {originalPort}، وترجع إليه الطابعة عند الإيقاف."), Ui.Body, Ui.Muted, CardWidth - 220);
            port.Margin = new Padding(4, 0, 4, 4);
            text.Controls.Add(port);
        }
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Ui.Surface2, Margin = new Padding(4, 0, 4, 8), Padding = new Padding(10, 6, 10, 6),
            MinimumSize = new Size(CardWidth - 60, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pill.Anchor = AnchorStyles.None;
        row.Controls.Add(pill, 0, 0);
        row.Controls.Add(text, 1, 0);
        return row;
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
        _code.Submit += async () => await Pair();
        _code.TextChanged += (_, _) => { _steps.Current = _code.Text.Length > 0 ? 1 : 0; _pairError.Visible = false; };
        _pair.MinimumSize = new Size(Ui.Px(140), Ui.Px(44));
        var codeLabel = Ui.Text("رمز الربط", Ui.Strong, Ui.Ink2);
        codeLabel.Margin = new Padding(4, 8, 4, 0);
        _pairCard.Add(_steps,
            Ui.Text("أنشئ رمز ربط من لوحة التحكم ← الإعدادات ← دايماً ديسك. الرمز 6 خانات ويصلح 10 دقائق.", Ui.Body, Ui.Muted, CardWidth - 60),
            codeLabel, _code, _pairError,
            Ui.Row(Ui.Text("اسم الجهاز", Ui.Strong, Ui.Ink2), new FieldBox(_deviceName)), _pair);
        stack.Controls.Add(_pairCard);

        _unpair.Click += (_, _) =>
        {
            if (!Ask("سيُلغى ربط هذا الجهاز بالمحل، ولا يُطبع QR بعد الفواتير حتى تربطه من جديد. ألغِ الجهاز أيضاً من لوحة التحكم ← الإعدادات ← دايماً ديسك. هل تريد المتابعة؟")) return;
            _rt.Agent.ForgetPairing();
            RefreshState();
        };
        _pairedShop.Margin = new Padding(4, 0, 4, 0);
        _pairedCard.Add(_stepsDone, _pairedShop, _pairedDevice, _unpair);
        stack.Controls.Add(_pairedCard);

        _reconfirm.Click += (_, _) =>
        {
            if (Ask("سيطلب البرنامج تأكيد سطر رقم الفاتورة وسطر المجموع في الفاتورة التالية، ولا تُسجَّل الفواتير حتى تؤكدهما. هل تريد المتابعة؟"))
                _rt.RequestReconfirm();
        };
        stack.Controls.Add(new Card("حقول الفاتورة", CardWidth, Glyph.Receipt).Add(_fieldsPill, _fieldsStatus, _reconfirm));

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
        stack.Controls.Add(new Card("عنوان الخادم (للدعم الفني فقط)", CardWidth, Glyph.Shield).Add(
            Ui.Text("لا تغيّر هذا العنوان إلا بطلب من دعم دايماً. تغيير الخادم يلغي الربط.", Ui.Body, Ui.Muted, CardWidth - 60),
            Ui.Row(new FieldBox(_url), _saveUrl)));
        return page;
    }

    /// <summary>A pairing refusal under the code boxes (in place of a message box).</summary>
    internal void ShowPairError(string text)
    {
        _code.Invalid = true;
        _code.Invalidate();
        _pairError.Set(Glyph.Error, Ui.Danger, Ui.DangerBg, "لم يتم الربط", Ui.Latin(text));
        _pairError.Visible = true;
    }

    internal void SetCodeForPreview(string code) => _code.Text = code;

    TabPage HelpTab()
    {
        var page = Page("مساعدة", out var stack);

        // The golden rule first: whatever happens, this button gives the shop its printer back.
        var stop = Ui.DangerOutline("إيقاف وإرجاع الطابعة");
        stop.MinimumSize = new Size(Ui.Px(220), Ui.Px(44));
        stop.Click += (_, _) => StopAndRestore();
        stack.Controls.Add(new Card("القاعدة الذهبية: إيقاف وإرجاع الطابعة", CardWidth, Glyph.Power, Ui.Danger, Ui.DangerBg).Add(
            Ui.Text("إن واجهت أي مشكلة في الطباعة فاضغط هذا الزر. يرجع طابعة الفواتير إلى منفذها الأصلي، ويحذف طابعة دايماً ومهام البرنامج. بعدها تُطبع الفواتير مباشرة كما كانت قبل التثبيت.", Ui.Body, Ui.Ink, CardWidth - 60),
            stop, _manualPort));

        string[] steps =
        {
            "1. نزّل الملف DaymanDesk.exe وشغّله، ووافق على صلاحيات المدير. إن ظهر تحذير Windows فاضغط More info ثم Run anyway لمتابعة التشغيل. بعدها تجد «دايماً ديسك» في قائمة Start، وأيقونته بجانب الساعة (إن لم تظهر فاضغط السهم ^).",
            "2. ضع علامة على طابعة الفواتير (أو أكثر) في تبويب «الحالة» واضغط «تشغيل دايماً ديسك». تبقى الفواتير تُطبع كالمعتاد. إن كان جهاز الكاشير تابلت أو جهازاً بلا Windows ففعّل «وضع الشبكة».",
            "3. أنشئ رمز ربط من لوحة التحكم ← الإعدادات ← دايماً ديسك، واكتبه في تبويب «الربط» ثم اضغط «ربط».",
            "4. اطبع فاتورة من برنامج الكاشير، ثم أكّد في نافذة «تأكيد الحقول» سطر رقم الفاتورة وسطر المجموع.",
            "5. اطبع فاتورة أخرى: تخرج بعدها قسيمة QR. امسحها بالجوال وتأكد من وصول النقاط إلى البطاقة.",
        };
        var how = new Card("كيف يعمل", CardWidth, Glyph.Info);
        foreach (var s in steps) how.Add(Ui.Text(s, Ui.Body, Ui.Ink, CardWidth - 60));
        how.Add(Ui.Text("الفاتورة تُطبع أولاً ثم يقرؤها البرنامج، فلا يؤخر الطباعة. إن توقف البرنامج لأي سبب تعود الطابعة إلى منفذها الأصلي خلال دقيقة.", Ui.Body, Ui.Ink2, CardWidth - 60),
            Ui.Text("لا يرسل البرنامج إلى الخادم إلا رقم الفاتورة والمجموع، وعينات من أول الفواتير لتحسين القراءة إن فعّل صاحب المحل ذلك.", Ui.Body, Ui.Muted, CardWidth - 60));
        stack.Controls.Add(how);

        (string q, string a)[] faq =
        {
            ("لا تُطبع الفواتير", "اضغط «إيقاف وإرجاع الطابعة» أعلاه، فترجع الطابعة إلى منفذها الأصلي وتطبع مباشرة."),
            ("لا يخرج QR بعد الفاتورة", "تأكد أن الجهاز مربوط في تبويب «الربط»، وأن صاحب المحل أكّد الحقول من لوحة التحكم، ثم انظر «آخر مشكلة» في تبويب «الحالة»."),
            ("رقم الفاتورة أو المجموع المقروء خطأ", "اضغط «إعادة تأكيد الحقول» في تبويب «الربط»، ثم أكّد السطرين الصحيحين في الفاتورة التالية."),
            ("انقطع الإنترنت", "تُطبع الفواتير كالمعتاد، وتُرسل إلى الخادم عند عودة الاتصال."),
            ("لا أجد أيقونة البرنامج", "اضغط السهم ^ بجانب الساعة، أو افتح «دايماً ديسك» من قائمة Start."),
        };
        var common = new Card("المشاكل الشائعة وحلولها", CardWidth, Glyph.Alert, Ui.Warning, Ui.WarningBg);
        foreach (var (q, a) in faq)
        {
            var ql = Ui.Text(q, Ui.Strong, Ui.Ink, CardWidth - 60);
            ql.Margin = new Padding(4, 8, 4, 0);
            var al = Ui.Text(a, Ui.Body, Ui.Muted, CardWidth - 60);
            al.Margin = new Padding(4, 2, 4, 6);
            common.Add(ql, al);
        }
        stack.Controls.Add(common);

        var site = Ui.Primary("فتح daymanjo.com");
        site.Click += (_, _) => Ui.OpenUrl("https://daymanjo.com");
        stack.Controls.Add(new Card("تواصل معنا", CardWidth, Glyph.Chat, Ui.Accent, Ui.AccentSoft).Add(
            Ui.Text("لأي سؤال أو مشكلة تواصل مع دعم دايماً من موقعنا.", Ui.Body, Ui.Ink, CardWidth - 60), site));
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
            _printersLoading.Visible = false;
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
        if (!c.Paired) _connPill.Set("غير مربوط", Ui.Warning, Ui.WarningBg);
        else if (c.LastProblem == Problems.Offline) _connPill.Set("غير متصل بالخادم", Ui.Danger, Ui.DangerBg);
        else _connPill.Set("متصل بالخادم", Ui.Success, Ui.SuccessBg);
        _subline.Text = c.Paired ? c.ShopName ?? "" : "غير مربوط";
        _info["المحل"].Text = c.Paired ? c.ShopName ?? "-" : "غير مربوط";
        _info["اسم الجهاز"].Text = c.Paired ? c.DeviceName ?? "-" : "-";
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

        // Printers: each configured one with its state, and the network route.
        var rows = c.Printers.Where(r => !string.IsNullOrEmpty(r.PrinterName))
            .Select(r => (name: r.PrinterName!, run: _rt.Server.IsRunning(r.Id), port: r.OriginalPort)).ToList();
        if (net != null) rows.Add(("وضع الشبكة ← " + net.DirectHost, netOn, null));
        string key = string.Join("|", rows.Select(r => r.name + r.run + r.port));
        if (key != _printerKey || _printerRows.Controls.Count == 0)
        {
            _printerKey = key;
            _printerRows.SuspendLayout();
            foreach (Control old in _printerRows.Controls.Cast<Control>().ToList()) old.Dispose();
            if (rows.Count == 0)
                _printerRows.Controls.Add(Ui.Text("لم تُشغَّل أي طابعة بعد. ضع علامة على طابعة الفواتير في القائمة ثم اضغط «تشغيل دايماً ديسك».", Ui.Body, Ui.Muted, CardWidth - 60));
            else foreach (var r in rows) _printerRows.Controls.Add(PrinterRow(r.name, r.run, r.port));
            _printerRows.ResumeLayout();
        }

        int q = _rt.Queue.Count;
        string today = Amman.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isToday = c.StatsDay == today;
        int[] vals = isToday ? new[] { c.TodayReceipts, c.TodayQr, q } : new[] { 0, 0, q };
        for (int i = 0; i < 3; i++) _today[i].Text = vals[i].ToString(CultureInfo.InvariantCulture);
        _today[2].ForeColor = q > 0 ? Ui.Warning : Ui.Ink;
        _todayMore.Text = !isToday || c.TodayReceipts == 0 ? "لم تمر فواتير اليوم بعد."
            : $"مسجّلة {c.TodayRegistered} · لم تُسجّل {c.TodayNotRegistered}" + (q > 0 ? " · تُرسل المنتظرة عند عودة الاتصال" : "");
        _todayMore.ForeColor = isToday && c.TodayNotRegistered > 0 ? Ui.Danger : Ui.Muted;
        Equalize(_connCard, _todayCard);

        _version.Text = Runtime.Version + " · يتحدّث تلقائياً";
        _versionNote.Text = c.UpdateVersion != null ? $"يوجد تحديث: الإصدار {c.UpdateVersion}."
            : c.UpdatedTo == Runtime.Version ? $"تم التحديث إلى {Runtime.Version}." : "أحدث إصدار.";

        if (string.IsNullOrEmpty(c.LastProblem))
        {
            _problem.Set(Glyph.Check, Ui.Success, Ui.SuccessBg, "لا توجد مشاكل.", "");
            _problem.SetAction(null);
            _problemDo = null;
        }
        else
        {
            string at = c.LastProblemAt is { } t ? " (" + ProblemTime(t.ToOffset(Amman.Offset)) + ")" : "";
            _problem.Set(Glyph.Alert, Ui.Danger, Ui.DangerBg, "آخر مشكلة" + at, Problems.Describe(c.LastProblem, c.LastProblemDetail));
            string? label = null;
            Action? act = null;
            switch (c.LastProblem)
            {
                case Problems.Revoked: case Problems.TokenUnreadable: case Problems.ParseFailed: case Problems.ImageUnreadable:
                    label = "فتح تبويب «الربط»"; act = () => { if (_tabs != null) _tabs.SelectedIndex = 2; }; break;
                case Problems.ForwardFailed: case Problems.ListenerBusy:
                    label = "تشغيل دايماً ديسك"; act = () => _start.PerformClick(); break;
                case Problems.RestoreFailed:
                    label = "فتح «مساعدة»"; act = () => { if (_tabs != null) _tabs.SelectedIndex = 3; }; break;
            }
            _problemDo = act;
            if (label != null) _problemAction.Text = label;
            _problem.SetAction(label != null ? _problemAction : null);
        }

        _pairCard.Visible = !c.Paired;
        _pairedCard.Visible = c.Paired;
        _pairedShop.Text = c.ShopName ?? "-";
        _pairedDevice.Text = $"هذا الجهاز مربوط باسم «{c.DeviceName}». تُرسل فواتيره إلى هذا المحل.";
        _code.Enabled = _pair.Enabled = _deviceName.Enabled = !c.Paired;
        if (!_url.Focused) _url.Text = c.ServerUrl;
        if (!c.Paired) _fieldsPill.Set("بعد الربط", Ui.Muted, Ui.Surface2);
        else if (c.FieldsConfirmed) _fieldsPill.Set("مؤكَّدة", Ui.Success, Ui.SuccessBg);
        else _fieldsPill.Set("بانتظار التأكيد", Ui.Warning, Ui.WarningBg);
        _fieldsStatus.Text = !c.Paired ? "يُطلب التأكيد في أول فاتورة بعد الربط."
            : c.FieldsConfirmed ? "أكّد صاحب المحل سطر رقم الفاتورة وسطر المجموع. إن تغيّر شكل الفاتورة فاضغط «إعادة تأكيد الحقول»."
            : "لم تُؤكَّد بعد: اطبع فاتورة من برنامج الكاشير، ثم يؤكد صاحب المحل سطريها من لوحة التحكم.";
        _reconfirm.Enabled = c.Paired && c.FieldsConfirmed;
        var originals = c.Printers.Where(r => !string.IsNullOrEmpty(r.OriginalPort)).Select(r => $"«{r.PrinterName}» ← {r.OriginalPort}").ToList();
        _manualPort.Text = originals.Count > 0
            ? "إن لم تطبع الفواتير بعد الإيقاف: افتح خصائص الطابعة ← Ports واختر منفذها الأصلي: " + string.Join("، ", originals) + "."
            : "";
        _manualPort.Visible = originals.Count > 0;

        _confirmBanner.Visible = c.Paired && !c.FieldsConfirmed && (_rt.Agent.PendingConfirmation != null || PreviewConfirm);
        _updateBanner.Visible = c.UpdateVersion != null && c.UpdateUrl != null;
        _updateBanner.Set(Glyph.Update, Ui.Success, Ui.SuccessBg, Ui.Latin($"يوجد تحديث: الإصدار {c.UpdateVersion}"),
            "يحدّث البرنامج نفسه تلقائياً حين لا تُطبع فواتير. إن لم يتم، نزّله وأغلق البرنامج من الأيقونة بجانب الساعة وشغّل الملف الجديد.");
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
        return at.Date == Amman.Now.Date ? time : "يوم " + at.ToString("dd.MM", CultureInfo.InvariantCulture) + " " + time;
    }

    void SetPill(bool on, bool paired, bool problem)
    {
        // WO-101: the program's state only; the server connection is in the «الاتصال» card.
        if (problem) _pill.Set("مشكلة", Ui.Danger, Ui.DangerBg);
        else if (!paired) _pill.Set("غير مربوط", Ui.Warning, Ui.WarningBg);
        else if (on) _pill.Set("يعمل", Ui.Success, Ui.SuccessBg);
        else _pill.Set("متوقف", Ui.Muted, Ui.Surface2);
    }

    void SetHeadline(string text, Color color)
    {
        _headline.Text = text;
        _headline.ForeColor = color;
    }

    /// <summary>Two cards side by side get the taller one's height.</summary>
    static void Equalize(Card a, Card b)
    {
        a.MinimumSize = new Size(a.MaximumSize.Width, 0);
        b.MinimumSize = new Size(b.MaximumSize.Width, 0);
        int h = Math.Max(a.GetPreferredSize(Size.Empty).Height, b.GetPreferredSize(Size.Empty).Height);
        a.MinimumSize = new Size(a.MaximumSize.Width, h);
        b.MinimumSize = new Size(b.MaximumSize.Width, h);
    }

    internal void AddReceiptForPreview(ReceiptRecord r) => AddReceipt(r);
    internal static bool PreviewConfirm;
    internal void RefreshForPreview() => RefreshState();
    internal ContextMenuStrip TrayMenuForPreview => _tray.ContextMenuStrip;

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
            ShowPairError(problem);
            return;
        }
        string name = string.IsNullOrWhiteSpace(_deviceName.Text) ? "كاشير" : _deviceName.Text.Trim();
        _pair.Enabled = false;
        _pairError.Visible = false;
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
        else ShowPairError(DeskAgent.PairError(r));
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
        Ui.Msg(this, text, "دايماً ديسك", icon);
    }

    bool Ask(string text) => !Preview && Ui.Ask(this, text, "دايماً ديسك");

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
