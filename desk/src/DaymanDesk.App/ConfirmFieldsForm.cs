using System.Globalization;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>
/// «تأكيد الحقول» (WO-86 item 1): the first text receipt after pairing, with the detected invoice-number line and
/// total line highlighted. The owner keeps them or taps the right line for each, then confirms.
/// </summary>
sealed class ConfirmFieldsForm : Form
{
    readonly FieldConfirmation _p;
    readonly Func<int, int, string?> _confirm;
    readonly ListBox _lines = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30, Dock = DockStyle.Fill, IntegralHeight = false,
        BorderStyle = BorderStyle.None, Font = Ui.Mono, RightToLeft = RightToLeft.Yes,
    };
    readonly Label _invoiceValue = Ui.Text("", Ui.Strong);
    readonly Label _totalValue = Ui.Text("", Ui.Strong);
    readonly Button _ok = Ui.Primary("تأكيد وحفظ");
    int _invoice, _total;

    public ConfirmFieldsForm(FieldConfirmation p, Func<int, int, string?> confirm)
    {
        _p = p;
        _confirm = confirm;
        _invoice = p.InvoiceLine;
        _total = p.TotalLine;

        Text = "تأكيد الحقول";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = Ui.Body;
        BackColor = Ui.Canvas;
        ClientSize = new Size(720, 680);
        MinimumSize = new Size(600, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = AppIcon.Value;

        var intro = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 4), BackColor = Ui.Canvas };
        intro.Controls.Add(Ui.Text("هذه أول فاتورة بعد ربط الجهاز.", Ui.Heading));
        intro.Controls.Add(Ui.Text("تأكد أن البرنامج اختار السطر الصحيح لرقم الفاتورة وللمجموع النهائي (المبلغ الذي دفعه الزبون). " +
            "إن كان أحدهما خطأ فاضغط على السطر الصحيح ثم على الزر المناسب. يحفظ البرنامج اختيارك ويستخدمه في كل الفواتير التالية.",
            Ui.Body, Ui.Ink, 680));

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 4), BackColor = Ui.Canvas };
        var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Ui.Line };
        border.Controls.Add(_lines);
        listHost.Controls.Add(border);
        foreach (var l in p.Lines) _lines.Items.Add(l);
        _lines.DrawItem += DrawLine;

        var setInvoice = Ui.Secondary("هذا سطر رقم الفاتورة");
        // WO-91: the same rule as «تأكيد وحفظ» (DeskAgent.ConfirmFields), so a line the window accepts is never refused.
        setInvoice.Click += (_, _) => Choose(ref _invoice, ReceiptExtractor.InvoiceLineProblem);
        var setTotal = Ui.Secondary("هذا سطر المجموع");
        setTotal.Click += (_, _) => Choose(ref _total, ReceiptExtractor.TotalLineProblem);

        var values = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Ui.Canvas };
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        values.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        values.Controls.Add(Ui.Text("رقم الفاتورة", Ui.Body, Ui.Muted));
        values.Controls.Add(_invoiceValue);
        values.Controls.Add(Ui.Text("المجموع", Ui.Body, Ui.Muted));
        values.Controls.Add(_totalValue);

        var later = Ui.Secondary("لاحقاً");
        later.DialogResult = DialogResult.Cancel;
        _ok.Click += (_, _) => Confirm();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 4, 16, 12), BackColor = Ui.Canvas };
        bottom.Controls.Add(Ui.Row(setInvoice, setTotal));
        bottom.Controls.Add(values);
        bottom.Controls.Add(Ui.Text("لا تُسجَّل الفواتير ولا يُطبع QR حتى تضغط «تأكيد وحفظ».", Ui.Body, Ui.Muted, 680));
        bottom.Controls.Add(Ui.Row(_ok, later));

        Controls.Add(listHost);
        Controls.Add(bottom);
        Controls.Add(intro);
        CancelButton = later;

        if (_invoice >= 0) _lines.SelectedIndex = _invoice;
        UpdateValues();
        Ui.LatinDigits(this);
    }

    void Choose(ref int field, Func<IReadOnlyList<string>, int, string?> problem)
    {
        int i = _lines.SelectedIndex;
        if (i < 0 || i >= _p.Lines.Count)
        {
            Msg("اضغط أولاً على السطر الصحيح في الفاتورة.", MessageBoxIcon.Information);
            return;
        }
        if (problem(_p.Lines, i) is { } reason)
        {
            Msg(reason, MessageBoxIcon.Warning);
            return;
        }
        field = i;
        _lines.Invalidate();
        UpdateValues();
    }

    void UpdateValues()
    {
        // Exactly the values «تأكيد وحفظ» learns: a line it cannot learn shows no value here.
        string? inv = ReceiptExtractor.LearnInvoice(_p.Lines, _invoice, _invoice == _p.InvoiceLine ? _p.InvoiceNo : null)?.value;
        decimal? tot = ReceiptExtractor.LearnTotal(_p.Lines, _total)?.value;
        _invoiceValue.Text = inv ?? "لم يُحدَّد. اضغط على سطر رقم الفاتورة ثم «هذا سطر رقم الفاتورة».";
        _invoiceValue.ForeColor = inv != null ? Ui.Ink : Ui.Danger;
        _totalValue.Text = tot != null ? tot.Value.ToString("0.000", CultureInfo.InvariantCulture) + " دينار" : "لم يُحدَّد. اضغط على سطر المجموع ثم «هذا سطر المجموع».";
        _totalValue.ForeColor = tot != null ? Ui.Ink : Ui.Danger;
        _ok.Enabled = inv != null && tot != null && _invoice != _total;
    }

    void Confirm()
    {
        var error = _confirm(_invoice, _total);
        if (error != null) { Msg(error, MessageBoxIcon.Warning); return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    void DrawLine(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _p.Lines.Count) return;
        bool inv = e.Index == _invoice, tot = e.Index == _total, sel = (e.State & DrawItemState.Selected) != 0;
        Color back = inv ? Ui.WarningBg : tot ? Ui.SuccessBg : Ui.Surface;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
        if (sel)
        {
            using var pen = new Pen(Ui.Ink, 2);
            e.Graphics.DrawRectangle(pen, e.Bounds.X + 1, e.Bounds.Y + 1, e.Bounds.Width - 3, e.Bounds.Height - 3);
        }
        var rect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
        string? tag = inv ? "رقم الفاتورة" : tot ? "المجموع" : null;
        const TextFormatFlags rtl = TextFormatFlags.RightToLeft | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int tagWidth = 0;
        if (tag != null)
        {
            // The tag (text, not colour alone) on the left edge; the receipt line keeps the rest.
            tagWidth = TextRenderer.MeasureText(tag, Ui.Strong).Width + 16;
            var tagRect = new Rectangle(rect.X, rect.Y, tagWidth, rect.Height);
            TextRenderer.DrawText(e.Graphics, tag, Ui.Strong, tagRect, inv ? Ui.Warning : Ui.Success, rtl | TextFormatFlags.Left);
        }
        var lineRect = new Rectangle(rect.X + tagWidth, rect.Y, rect.Width - tagWidth, rect.Height);
        TextRenderer.DrawText(e.Graphics, Ui.Latin(_p.Lines[e.Index]), Ui.Mono, lineRect, Ui.Ink, rtl | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
    }

    void Msg(string text, MessageBoxIcon icon) =>
        MessageBox.Show(this, Ui.Latin(text), "تأكيد الحقول", MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
}
