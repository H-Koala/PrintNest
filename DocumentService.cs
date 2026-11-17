using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf.PdfDocument;

namespace PrintNest;
public static class DocumentService
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };
    public static async Task<PrintItem> InspectAsync(string path)
    {
        var item = new PrintItem { Path = System.IO.Path.GetFullPath(path) };
        if (item.IsPdf)
        {
            var doc = await LoadPdf(path);
            item.PageCount = (int)doc.PageCount;
        }
        else
        {
            using var file = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            item.PageCount = System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? 1 : decoder.Frames.Count;
        }
        if (item.PageCount == 0) throw new InvalidDataException("文件没有可打印的页面。");
        item.Thumbnail = await RenderAsync(new PageRef(path, item.IsPdf, 0, 0), 180);
        return item;
    }
    private static async Task<WinPdf> LoadPdf(string path) => await WinPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path)));
    public static async Task<BitmapSource> RenderAsync(PageRef page, int maxPixels)
    {
        BitmapSource bitmap;
        if (page.IsPdf)
        {
            var doc = await LoadPdf(page.Path);
            using var pdfPage = doc.GetPage((uint)page.Index);
            double scale = maxPixels / Math.Max(pdfPage.Size.Width, pdfPage.Size.Height);
            using var stream = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, Math.Round(pdfPage.Size.Width * scale)), DestinationHeight = (uint)Math.Max(1, Math.Round(pdfPage.Size.Height * scale)) });
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            byte[] bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes);
            bitmap = ReadBitmap(memory);
        }
        else
        {
            using var file = File.OpenRead(page.Path);
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap = decoder.Frames[page.Index];
            bitmap = ApplyExif(bitmap);
            double ratio = (double)maxPixels / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            if (ratio < 1) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(ratio, ratio));
            bitmap.Freeze();
        }
        if (page.Rotation != 0) { bitmap = new TransformedBitmap(bitmap, new RotateTransform(page.Rotation)); bitmap.Freeze(); }
        return bitmap;
    }
    private static BitmapSource ReadBitmap(Stream stream)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private static BitmapSource ApplyExif(BitmapSource source)
    {
        try
        {
            if (source is not BitmapFrame frame || frame.Metadata is not BitmapMetadata metadata) return source;
            object? raw = metadata.GetQuery("/app1/ifd/{ushort=274}") ?? metadata.GetQuery("/ifd/{ushort=274}");
            int orientation = raw == null ? 1 : Convert.ToInt32(raw);
            var group = new TransformGroup();
            switch (orientation)
            {
                case 2: group.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: group.Children.Add(new RotateTransform(180)); break;
                case 4: group.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: group.Children.Add(new ScaleTransform(-1, 1)); group.Children.Add(new RotateTransform(270)); break;
                case 6: group.Children.Add(new RotateTransform(90)); break;
                case 7: group.Children.Add(new ScaleTransform(-1, 1)); group.Children.Add(new RotateTransform(90)); break;
                case 8: group.Children.Add(new RotateTransform(270)); break;
                default: return source;
            }
            var transformed = new TransformedBitmap(source, group); transformed.Freeze(); return transformed;
        }
        catch { return source; }
    }
}
public static class LayoutEngine
{
    public static Size PaperSize(PrintOptions options)
    {
        Size size = options.Paper switch { 1 => new Size(148, 210), 2 => new Size(215.9, 279.4), 3 => new Size(101.6, 152.4), _ => new Size(210, 297) };
        if (options.Orientation == 1) size = new Size(size.Height, size.Width);
        return new Size(size.Width * 96 / 25.4, size.Height * 96 / 25.4);
    }
    public static Rect FitRect(Size image, Rect cell, int fit)
    {
        if (fit == 2) return cell;
        double scale = fit == 1 ? Math.Max(cell.Width / image.Width, cell.Height / image.Height) : Math.Min(cell.Width / image.Width, cell.Height / image.Height);
        double w = image.Width * scale, h = image.Height * scale;
        return new Rect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
    }
    public static async Task<DrawingVisual> ComposeAsync(Sheet sheet, PrintOptions options, int dpi, Rect? printable = null, CancellationToken token = default)
    {
        var size = PaperSize(options);
        double margin = options.MarginMm * 96 / 25.4;
        var area = new Rect(margin, margin, size.Width - margin * 2, size.Height - margin * 2);
        if (printable.HasValue) area.Intersect(printable.Value);
        if (area.IsEmpty || area.Width < 5 || area.Height < 5) throw new InvalidOperationException("边距过大，纸张没有可打印区域。");
        int count = options.PerSheet;
        int columns = count switch { 2 => options.Orientation == 1 ? 2 : 1, 4 => 2, 6 => options.Orientation == 1 ? 3 : 2, 9 => 3, _ => 1 };
        int rows = count / columns;
        double gap = count == 1 ? 0 : 4 * 96 / 25.4;
        double cw = (area.Width - (columns - 1) * gap) / columns, ch = (area.Height - (rows - 1) * gap) / rows;
        var visual = new DrawingVisual();
        // Await image decoding before opening a DrawingContext, so every continuation is on its owning dispatcher.
        var images = new List<BitmapSource>();
        foreach (var page in sheet.Pages)
        {
            token.ThrowIfCancellationRequested();
            var bitmap = await DocumentService.RenderAsync(page, Math.Min(7000, (int)Math.Ceiling(Math.Max(cw, ch) / 96 * dpi)));
            if (options.Color == 1) { bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0); bitmap.Freeze(); }
            images.Add(bitmap);
        }
        using var drawing = visual.RenderOpen();
        drawing.DrawRectangle(Brushes.White, null, new Rect(size));
        for (int i = 0; i < images.Count; i++)
        {
            var cell = new Rect(area.X + (i % columns) * (cw + gap), area.Y + (i / columns) * (ch + gap), cw, ch);
            var image = images[i];
            drawing.PushClip(new RectangleGeometry(cell));
            drawing.DrawImage(image, FitRect(new Size(image.PixelWidth, image.PixelHeight), cell, options.Fit));
            drawing.Pop();
        }
        return visual;
    }
    public static BitmapSource Rasterize(DrawingVisual visual, Size size, int dpi)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
