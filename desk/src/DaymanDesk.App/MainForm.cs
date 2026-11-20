using System.Globalization;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>The one window: printer, pairing, last receipts. Arabic, right to left.</summary>
sealed class MainForm : Form
{
    readonly Runtime _rt;
    readonly NotifyIcon _tray;
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Padding = new Padding(0, 4, 0, 8) };
    readonly ComboBox _printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    readonly Button _refresh = new() { Text = "تحديث القائمة", AutoSize = true };
    readonly Button _start = new() { Text = "تشغيل دايماً ديسك", AutoSize = true };
    readonly Button _stop = new() { Text = "إيقاف وإرجاع الطابعة", AutoSize = true };
    readonly Label _pairStatus = new() { AutoSize = true };
    readonly TextBox _code = new() { Width = 120, MaxLength = 6, CharacterCasing = CharacterCasing.Upper, RightToLeft = RightToLeft.No };
    readonly TextBox _deviceName = new() { Width = 160, Text = "كاشير 1" };
    readonly Button _pair = new() { Text = "ربط", AutoSize = true };
    readonly TextBox _url = new() { Width = 360, RightToLeft = RightToLeft.No };
    readonly Button _saveUrl = new() { Text = "حفظ العنوان", AutoSize = true };
    readonly Label _qr = new() { AutoSize = true };
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly LinkLabel _export = new() { Text = "تصدير العينات", AutoSize = true };
    bool _exiting;

    public MainForm(Runtime rt, bool startHidden)
    {
        _rt = rt;
        Text = "دايماً ديسك " + Runtime.Version;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10f);
        Width = 900;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        var printerBox = Group("طابعة الفواتير", Row(_printers, _refresh), Row(_start, _stop));
        var pairBox = Group("ربط الحساب", Row(_pairStatus), Row(new Label { Text = "رمز الربط:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _code,
            new Label { Text = "اسم الجهاز:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _deviceName, _pair),
            Row(new Label { Text = "عنوان الخادم:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _url, _saveUrl), Row(_qr));

        foreach (var (h, w) in new[] { ("الوقت", 90), ("النوع", 80), ("رقم الفاتورة", 130), ("المجموع", 90), ("الثقة", 60), ("الحالة", 360) })
            _list.Columns.Add(h, w);
        var listBox = new GroupBox { Text = "آخر الفواتير", Dock = DockStyle.Fill, Padding = new Padding(8) };
        listBox.Controls.Add(_list);

        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(12) };
        top.Controls.AddRange(new Control[] { _status, printerBox, pairBox });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 4, 12, 8) };
        bottom.Controls.Add(_export);
        Controls.Add(listBox);
        Controls.Add(bottom);
        Controls.Add(top);

        _refresh.Click += (_, _) => LoadPrinters();
        _start.Click += (_, _) => RunBusy(() => _rt.Install((string)_printers.SelectedItem!), "تم التشغيل: كل فاتورة تمر الآن عبر دايماً ديسك.", needsPrinter: true);
        _stop.Click += (_, _) => RunBusy(_rt.StopAndRestore, "أُعيدت الطابعة إلى منفذها الأصلي.");
        _pair.Click += async (_, _) => await Pair();
        _saveUrl.Click += (_, _) =>
        {
            bool wasPaired = _rt.Cfg.Current.Paired;
            if (!_rt.SetServerUrl(_url.Text))
                Msg("العنوان يجب أن يبدأ بـ https:// ويكون كاملاً، مثل https://daymanjo.com/api/desk", MessageBoxIcon.Warning);
            else if (wasPaired && !_rt.Cfg.Current.Paired)
                Msg("تغيّر الخادم، فأُلغي الربط. اربط الجهاز من جديد برمز من الخادم الجديد.", MessageBoxIcon.Information);
            _url.Text = _rt.Cfg.Current.ServerUrl;
            RefreshState();
        };
        _export.LinkClicked += (_, _) => ExportSamples();

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add("فتح", null, (_, _) => ShowWindow());
        menu.Items.Add("خروج (إرجاع الطابعة)", null, (_, _) => ExitApp());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "دايماً ديسك", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _rt.ReceiptProcessed += r => BeginInvokeSafe(() => AddReceipt(r));
        _rt.StateChanged += () => BeginInvokeSafe(RefreshState);
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _rt.ExitRestore();

        if (startHidden)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }
        Shown += (_, _) => { if (startHidden) Hide(); LoadPrinters(); RefreshState(); };
        RefreshState();
    }

    static GroupBox Group(string title, params Control[] rows)
    {
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), MinimumSize = new Size(840, 0) };
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        f.Controls.AddRange(rows);
        g.Controls.Add(f);
        return g;
    }

    static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        f.Controls.AddRange(items);
        return f;
    }

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
            foreach (var p in t.Result.Where(p => !p.Name.StartsWith("Dayman Raw (", StringComparison.Ordinal)))
                _printers.Items.Add(p.Name);
            var cur = _rt.Cfg.Current.PrinterName;
            if (cur != null && _printers.Items.Contains(cur)) _printers.SelectedItem = cur;
            else if (_printers.Items.Count > 0) _printers.SelectedIndex = 0;
        }));
    }

    void RefreshState()
    {
        var c = _rt.Cfg.Current;
        bool on = c.Active && _rt.Server.Running;
        _status.Text = on ? $"دايماً ديسك يعمل على الطابعة: {c.PrinterName}" : "دايماً ديسك متوقف: الطابعة تطبع مباشرة.";
        _status.ForeColor = on ? Color.FromArgb(0x15, 0x80, 0x3D) : Color.FromArgb(0x6B, 0x72, 0x80);
        _start.Enabled = !on;
        _stop.Enabled = c.Active || c.RawQueueName != null;
        _printers.Enabled = !on;
        _pairStatus.Text = c.Paired ? $"مربوط بمحل: {c.ShopName}  ({c.DeviceName})" : "غير مربوط. أنشئ «جهاز ديسك» من إعدادات لوحة التحكم واكتب الرمز هنا.";
        _code.Enabled = _pair.Enabled = _deviceName.Enabled = !c.Paired;
        _pair.Text = "ربط";
        if (!_url.Focused) _url.Text = c.ServerUrl;
        _qr.Text = !c.Paired ? "QR على كل فاتورة: بعد ربط الحساب"
            : c.QrEnabled ? "QR على كل فاتورة: يعمل" : "QR على كل فاتورة: أوقفه صاحب المحل";
        _tray.Text = on ? "دايماً ديسك: يعمل" : "دايماً ديسك: متوقف";
    }

    void AddReceipt(ReceiptRecord r)
    {
        var item = new ListViewItem(r.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        item.SubItems.Add(r.KindLabel);
        item.SubItems.Add(r.InvoiceNo ?? "-");
        item.SubItems.Add(r.Total?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-");
        item.SubItems.Add(r.Kind == ReceiptKind.Text ? ((int)Math.Round(r.Confidence * 100)).ToString(CultureInfo.InvariantCulture) + "%" : "-");
        item.SubItems.Add(r.Status);
        _list.Items.Insert(0, item);
        while (_list.Items.Count > 100) _list.Items.RemoveAt(_list.Items.Count - 1);
    }

    void RunBusy(Action work, string done, bool needsPrinter = false)
    {
        if (needsPrinter && _printers.SelectedItem == null)
        {
            Msg("اختر طابعة الفواتير أولاً.", MessageBoxIcon.Warning);
            return;
        }
        UseWaitCursor = true;
        _start.Enabled = _stop.Enabled = false;
        Task.Run(work).ContinueWith(t => BeginInvokeSafe(() =>
        {
            UseWaitCursor = false;
            RefreshState();
            if (t.Exception?.InnerException is { } ex)
            {
                _rt.Log.Error("ui action: " + ex.Message);
                string text = ex is SetupException se ? se.Arabic : "حدث خطأ غير متوقع: " + ex.Message;
                Msg(text + "\n\nالطابعة تطبع مباشرة كما كانت. التفاصيل في سجل البرنامج.", MessageBoxIcon.Error);
            }
            else Msg(done, MessageBoxIcon.Information);
        }));
    }

    async Task Pair()
    {
        string code = _code.Text.Trim();
        if (code.Length != 6) { Msg("رمز الربط 6 خانات.", MessageBoxIcon.Warning); return; }
        string name = string.IsNullOrWhiteSpace(_deviceName.Text) ? "كاشير" : _deviceName.Text.Trim();
        _pair.Enabled = false;
        _pair.Text = "جارٍ الربط...";
        var r = await _rt.Agent.PairAsync(code, name);
        RefreshState();
        if (r.Ok)
        {
            _code.Text = "";
            Msg($"تم الربط بمحل {r.Value!.ShopName}.", MessageBoxIcon.Information);
            _ = _rt.Agent.HeartbeatAsync();
        }
        else Msg(DeskAgent.PairError(r), MessageBoxIcon.Warning);
    }

    void ExportSamples()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"dayman-samples-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            _rt.Samples.ExportZip(path);
            Msg("حُفظت العينات على سطح المكتب:\n" + path, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Msg("تعذّر التصدير: " + ex.Message, MessageBoxIcon.Error); }
    }

    void Msg(string text, MessageBoxIcon icon) =>
        MessageBox.Show(this, text, "دايماً ديسك", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

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
