using System.Drawing.Imaging;
using System.Globalization;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>
/// `DaymanDesk.exe --screenshots &lt;dir&gt;` (CI): renders the status window's tabs, its states (WO-101: paired,
/// pairing error, banners), the themed dialogs, the tray menu and the confirmation dialog to PNG so the Arabic RTL
/// layout can be reviewed without a Windows machine. Touches no printer and no server.
/// </summary>
static class Screenshots
{
    public static int Run(Runtime rt, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            MainForm.Preview = true;
            var sample = new[]
            {
                "مطعم جمرة", "فرع عين الباشا", "رقم الفاتورة: 10432", "التاريخ: 19/11/2026  18:02",
                "شاورما عربي   2   5.000", "بيبسي   1   0.750", "المجموع: 5.750", "الضريبة 16%: 0.920",
                "الإجمالي: 6.670", "نقدا: 10.000", "الباقي: 3.330", "شكرا لزيارتكم",
            };
            rt.Agent.SetProblem(Problems.ParseFailed);
            using var form = new MainForm(rt, startHidden: false);
            form.Show();
            form.AddReceiptForPreview(new ReceiptRecord { At = DateTimeOffset.Now, Kind = ReceiptKind.Text, InvoiceNo = "10432", Total = 6.670m, Confidence = 0.95, Status = "QR مطبوع", Registered = true, QrPrinted = true });
            form.AddReceiptForPreview(new ReceiptRecord { At = DateTimeOffset.Now, Kind = ReceiptKind.Image, Status = "فاتورة صورة: بلا QR" });
            Pump();
            var tabs = form.Controls.OfType<TabControl>().First();
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Pump();
                Save(form, Path.Combine(dir, $"main-{i}.png"));
                if (ScrollToEnd(tabs.TabPages[i])) { Pump(); Save(form, Path.Combine(dir, $"main-{i}-end.png")); ScrollToTop(tabs.TabPages[i]); }
            }

            // WO-101: a refused pairing code, under the code boxes.
            tabs.SelectedIndex = 2;
            form.SetCodeForPreview("K7Q");
            form.ShowPairError(DeskAgent.PairCodeProblem("K7Q") ?? "");
            Pump();
            Save(form, Path.Combine(dir, "pair-error.png"));
            form.SetCodeForPreview("");

            Dialog(dir, "dialog-info.png", "تم التشغيل. كل فاتورة تمر الآن عبر دايماً ديسك وتُطبع كالمعتاد.", MessageBoxIcon.Information, false);
            Dialog(dir, "dialog-ask.png", "سترجع طابعات الفواتير إلى منافذها الأصلية، ويتوقف وضع الشبكة ودايماً ديسك عن العمل على هذا الجهاز. هل تريد المتابعة؟", MessageBoxIcon.Question, true);
            Dialog(dir, "dialog-error.png", "حدث خطأ غير متوقع.\n\nأعاد البرنامج الطابعات إلى منافذها. التفاصيل في سجل البرنامج (تبويب «الفواتير» ← فتح السجل).", MessageBoxIcon.Error, false);
            Dialog(dir, "dialog-net48.png", Program.NeedsNet48, MessageBoxIcon.Warning, false);

            try
            {
                var menu = form.TrayMenuForPreview;
                menu.Show(new Point(300, 300));
                Pump();
                using var bmp = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
                bmp.Save(Path.Combine(dir, "tray-menu.png"), ImageFormat.Png);
                menu.Close();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "tray-menu.txt"), ex.ToString()); }

            // WO-101: a paired shop with today's counters, a printer, the update banner and the owner-confirm banner.
            rt.Cfg.Update(c =>
            {
                c.DeviceToken = "preview-only"; c.ShopName = "مطعم جمرة"; c.DeviceName = "كاشير 1"; c.FieldsConfirmed = false;
                c.UpdateVersion = "2.5.0"; c.UpdateUrl = "https://daymanjo.com/desk";
                c.StatsDay = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                c.TodayReceipts = 37; c.TodayRegistered = 35; c.TodayQr = 34; c.TodayNotRegistered = 2;
                c.LastProblem = null; c.LastProblemDetail = null; c.LastProblemAt = null; c.LastPrintKind = "text";
                c.Printers.Add(new PrinterRoute { PrinterName = "XP-80C", OriginalPort = "USB001" });
            });
            MainForm.PreviewConfirm = true;
            form.RefreshForPreview();
            tabs.SelectedIndex = 0;
            Pump();
            Save(form, Path.Combine(dir, "status-paired.png"));
            if (Stack(tabs.TabPages[0]) is { } st) { st.AutoScrollPosition = new Point(0, Ui.Px(480)); Pump(); Save(form, Path.Combine(dir, "status-paired-mid.png")); st.AutoScrollPosition = Point.Empty; }
            if (ScrollToEnd(tabs.TabPages[0])) { Pump(); Save(form, Path.Combine(dir, "status-paired-end.png")); ScrollToTop(tabs.TabPages[0]); }
            tabs.SelectedIndex = 2;
            Pump();
            Save(form, Path.Combine(dir, "pair-paired.png"));

            var p = new FieldConfirmation { Lines = sample, InvoiceLine = 2, TotalLine = 8, InvoiceNo = "10432", Total = 6.670m, At = DateTimeOffset.Now };
            using var dlg = new ConfirmFieldsForm(p, (_, _) => null);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Show();
            Pump();
            Save(dlg, Path.Combine(dir, "confirm.png"));
            dlg.Close();

            // WO-91: the field receipt of the «Generic / Text Only» driver (every Arabic letter a '.').
            var garbled = new[] { "...... ", "... ........: 1001", ".... 2.500", ".........: 2.500" };
            var pg = new FieldConfirmation { Lines = garbled, InvoiceLine = 1, TotalLine = 3, InvoiceNo = "1001", Total = 2.500m, At = DateTimeOffset.Now };
            using var dlg2 = new ConfirmFieldsForm(pg, (_, _) => null);
            dlg2.StartPosition = FormStartPosition.Manual;
            dlg2.Show();
            Pump();
            Save(dlg2, Path.Combine(dir, "confirm-garbled.png"));
            dlg2.Close();
            DigitCheck(Path.Combine(dir, "digits.png"));
            form.Hide();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            return 1;
        }
    }

    static void Dialog(string dir, string name, string text, MessageBoxIcon icon, bool ask)
    {
        using var d = new ThemedDialog(text, "دايماً ديسك", icon, ask) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        d.Show();
        Pump();
        Save(d, Path.Combine(dir, name));
        d.Close();
    }

    static FlowLayoutPanel? Stack(TabPage page) => page.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(f => f.AutoScroll);

    static bool ScrollToEnd(TabPage page)
    {
        var s = Stack(page);
        if (s == null || !s.VerticalScroll.Visible) return false;
        s.AutoScrollPosition = new Point(0, s.DisplayRectangle.Height);
        return true;
    }

    static void ScrollToTop(TabPage page) { if (Stack(page) is { } s) s.AutoScrollPosition = Point.Empty; }

    /// <summary>
    /// WO-91: CI sets an Arabic user locale with national digits before this run. Row 1 (no mark) shows what that
    /// setting does to plain GDI text; row 2 is Ui.Latin; row 3 is GDI+ with digit substitution off, for comparison.
    /// </summary>
    static void DigitCheck(string path)
    {
        using var bmp = new Bitmap(560, 150);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            const TextFormatFlags f = TextFormatFlags.RightToLeft | TextFormatFlags.Right | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, "1. بلا علامة: 1001 و 2.500", Ui.Body, new Rectangle(10, 10, 540, 40), Color.Black, f);
            TextRenderer.DrawText(g, Ui.Latin("2. Ui.Latin: 1001 و 2.500"), Ui.Body, new Rectangle(10, 55, 540, 40), Color.Black, f);
            using var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft);
            sf.SetDigitSubstitution(0, StringDigitSubstitute.None);
            g.DrawString("3. GDI+ None: 1001 و 2.500", Ui.Body, Brushes.Black, new RectangleF(10, 100, 540, 40), sf);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    static void Pump()
    {
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
    }

    static void Save(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, ImageFormat.Png);
    }
}
