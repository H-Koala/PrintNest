using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PrintNest;
internal static class SelfTest
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; }
        Check(PagePlanner.ParseRange("1-3,5,3", 5).SequenceEqual(new[] { 0, 1, 2, 4 }), "range order and deduplication");
        foreach (string bad in new[] { "0", "6", "3-1", "x", "1,", "1-" }) { bool rejected = false; try { PagePlanner.ParseRange(bad, 5); } catch (FormatException) { rejected = true; } Check(rejected, "reject " + bad); }
        string[] names = { "01-山野旅行.png", "02-城市散步.png", "03-周末计划.png", "04-阅读记录.png" };
        Color[] colors = { Color.FromRgb(26, 120, 110), Color.FromRgb(47, 89, 140), Color.FromRgb(190, 122, 65), Color.FromRgb(102, 80, 142) };
        for (int i = 0; i < 4; i++)
        {
            var visual = new DrawingVisual(); using (var d = visual.RenderOpen())
            {
                d.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(0, 0, 900, 1200));
                d.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), null, new Point(800, 280), 370, 370);
                d.DrawEllipse(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, new Point(150, 1100), 500, 500);
                d.DrawText(new FormattedText("PRINTNEST / SAMPLE", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 28, Brushes.White, 1), new Point(65, 75));
                d.DrawText(new FormattedText(names[i][3..^4], System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 76, Brushes.White, 1), new Point(65, 730));
                d.DrawText(new FormattedText($"0{i + 1}   /   让每一页，恰到好处。", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 27, Brushes.White, 1), new Point(70, 860));
            }
            using var file = File.Create(Path.Combine(directory, names[i])); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(LayoutEngine.Rasterize(visual, new Size(900, 1200), 96))); encoder.Save(file);
        }
        string sourcePdf = Path.Combine(directory, "05-示例文档.pdf");
        using (var pdf = new PdfDocument())
        {
            for (int i = 0; i < 3; i++)
            {
                var page = pdf.AddPage(); using var g = XGraphics.FromPdfPage(page);
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 246, 249)), 0, 0, page.Width.Point, page.Height.Point);
                using var image = XImage.FromFile(Path.Combine(directory, names[i])); g.DrawImage(image, 40, 40, page.Width.Point - 80, page.Height.Point - 80);
            }
            pdf.Save(sourcePdf);
        }
        var items = new List<PrintItem>(); foreach (string name in names) items.Add(await DocumentService.InspectAsync(Path.Combine(directory, name)));
        var doc = await DocumentService.InspectAsync(sourcePdf); Check(doc.PageCount == 3, "WinRT PDF page count"); items.Add(doc);
        doc.Range = "1,3"; doc.Copies = 2;
        var options = new PrintOptions { Layout = 2, DpiIndex = 0 };
        var sheets = PagePlanner.Build(items, options); Check(sheets.Count == 2 && sheets.Sum(x => x.Pages.Count) == 8, "mixed N-up copies and ranges");
        foreach (int layout in Enumerable.Range(0, 5)) foreach (int orientation in new[] { 0, 1 })
        {
            var o = new PrintOptions { Layout = layout, Orientation = orientation, DpiIndex = 0 };
            var plan = PagePlanner.Build(items, o); var v = await LayoutEngine.ComposeAsync(plan[0], o, 96);
            Check(v.Drawing.Bounds.Width > 100, $"layout {layout} orientation {orientation}");
        }
        var preview = await LayoutEngine.ComposeAsync(sheets[0], options, 130);
        using (var output = File.Create(Path.Combine(directory, "preview-4up.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(LayoutEngine.Rasterize(preview, LayoutEngine.PaperSize(options), 130))); encoder.Save(output); }
        string exported = Path.Combine(directory, "export-mixed-4up.pdf");
        await OutputService.ExportAsync(exported, sheets, options, new Progress<string>(), CancellationToken.None);
        Check((await DocumentService.InspectAsync(exported)).PageCount == 2, "export PDF roundtrip");
        var rotated = await DocumentService.RenderAsync(new PageRef(items[0].Path, false, 0, 90), 400); Check(rotated.PixelWidth > rotated.PixelHeight, "rotation");
        var fit = LayoutEngine.FitRect(new Size(100, 50), new Rect(0, 0, 100, 100), 0); Check(fit == new Rect(0, 25, 100, 50), "fit no crop");
        var fill = LayoutEngine.FitRect(new Size(100, 50), new Rect(0, 0, 100, 100), 1); Check(fill.Width == 200 && fill.X == -50, "fill crop");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { await OutputService.ExportAsync(Path.Combine(directory, "cancelled.pdf"), sheets, options, new Progress<string>(), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !File.Exists(Path.Combine(directory, "cancelled.pdf")), "cancel leaves no partial PDF");
        Check(GdiPrinter.TestBitmapTransfer(), "GDI bitmap transfer");
        File.WriteAllText(Path.Combine(directory, "test-results.txt"), $"PASS: {checks} checks\nPDF decoding, images, all N-up layouts, orientations, page ranges, copies, rotation, PDF export roundtrip, cancellation, native GDI bitmap transfer.\nPhysical printer output was not tested.");
    }
}
