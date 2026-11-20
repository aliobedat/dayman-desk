using System.Drawing.Imaging;
using DaymanDesk.Core;

namespace DaymanDesk.App;

/// <summary>
/// `DaymanDesk.exe --screenshots &lt;dir&gt;` (CI): renders the status window's tabs and the confirmation dialog to
/// PNG so the Arabic RTL layout can be reviewed without a Windows machine. Touches no printer and no server.
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
            }
            var p = new FieldConfirmation { Lines = sample, InvoiceLine = 2, TotalLine = 8, InvoiceNo = "10432", Total = 6.670m, At = DateTimeOffset.Now };
            using var dlg = new ConfirmFieldsForm(p, (_, _) => null);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Show();
            Pump();
            Save(dlg, Path.Combine(dir, "confirm.png"));
            dlg.Close();
            form.Hide();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            return 1;
        }
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
