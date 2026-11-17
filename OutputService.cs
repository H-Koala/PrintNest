using System.IO;
using System.Printing;
using System.Windows;
using System.Printing.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PrintNest;
public static class OutputService
{
    public static Task OnSta(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Exception? failure = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await work(); } catch (Exception ex) { failure = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
            if (failure == null) completion.SetResult(); else completion.SetException(failure);
        }) { IsBackground = true, Name = "PrintNest.Output" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static Task ExportAsync(string path, List<Sheet> sheets, PrintOptions options, IProgress<string> progress, CancellationToken token) => OnSta(async () =>
    {
        // Commit only a complete PDF. An interrupted export never destroys the previous output.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var pdf = new PdfDocument(); pdf.Info.Title = "印巢 · 批量打印"; pdf.Info.Creator = "PrintNest 1.0";
            var size = LayoutEngine.PaperSize(options); int done = 0;
            for (int copy = 0; copy < options.Copies; copy++) foreach (var sheet in sheets)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在导出 {++done} / {sheets.Count * options.Copies} 页…");
                var visual = await LayoutEngine.ComposeAsync(sheet, options, options.Dpi, token: token);
                var raster = LayoutEngine.Rasterize(visual, size, options.Dpi);
                using var memory = new MemoryStream();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster)); encoder.Save(memory); memory.Position = 0;
                using var image = XImage.FromStream(memory);
                var page = pdf.AddPage(); page.Width = XUnit.FromPoint(size.Width * 72 / 96); page.Height = XUnit.FromPoint(size.Height * 72 / 96);
                using var gfx = XGraphics.FromPdfPage(page); gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            token.ThrowIfCancellationRequested(); pdf.Save(temporary); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });
    public static Task PrintAsync(List<Sheet> sheets, PrintOptions options, byte[]? ticketXml, IProgress<string> progress, CancellationToken token) => OnSta(async () =>
    {
        using var server = new LocalPrintServer(); using var queue = server.GetPrintQueue(options.Printer);
        using var stream = ticketXml == null ? null : new MemoryStream(ticketXml);
        var ticket = stream == null ? queue.DefaultPrintTicket.Clone() : new PrintTicket(stream);
        ticket.CopyCount = options.Copies; ticket.Collation = Collation.Collated;
        ticket.PageOrientation = options.Orientation == 1 ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.PageMediaSize = new PageMediaSize(options.Paper switch { 1 => PageMediaSizeName.ISOA5, 2 => PageMediaSizeName.NorthAmericaLetter, 3 => PageMediaSizeName.NorthAmerica4x6, _ => PageMediaSizeName.ISOA4 });
        ticket.OutputColor = options.Color == 1 ? OutputColor.Grayscale : OutputColor.Color;
        ticket.Duplexing = options.Duplex switch { 1 => Duplexing.TwoSidedLongEdge, 2 => Duplexing.TwoSidedShortEdge, _ => Duplexing.OneSided };
        var validated = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
        if (validated.Duplexing != ticket.Duplexing && options.Duplex != 0) throw new InvalidOperationException("打印机不支持所选双面方式，请选择单面或更换打印机。");
        if (validated.PageMediaSize?.PageMediaSizeName != ticket.PageMediaSize.PageMediaSizeName) throw new InvalidOperationException("打印机不支持所选纸张，请在右侧选择该打印机支持的尺寸。");
        using var converter = new PrintTicketConverter(options.Printer, PrintTicketConverter.MaxPrintSchemaVersion);
        byte[] devMode = converter.ConvertPrintTicketToDevMode(validated, BaseDevModeType.UserDefault, PrintTicketScope.JobScope);
        using var printer = new GdiPrinter(options.Printer, devMode);
        var printable = printer.PrintableArea;
        var size = LayoutEngine.PaperSize(options);
        // Cache to disk instead of retaining every full-resolution page in RAM.
        string folder = Path.Combine(Path.GetTempPath(), "PrintNest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var files = new List<string>();
            {
                for (int i = 0; i < sheets.Count; i++)
                {
                    token.ThrowIfCancellationRequested(); progress.Report($"正在准备打印 {i + 1} / {sheets.Count} 页…");
                    var visual = await LayoutEngine.ComposeAsync(sheets[i], options, options.Dpi, printable, token);
                    var bitmap = LayoutEngine.Rasterize(visual, size, options.Dpi);
                    string file = Path.Combine(folder, $"{i:00000}.png"); using var output = File.Create(file);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output); files.Add(file);
                }
            }
            token.ThrowIfCancellationRequested();
            progress.Report("正在提交到 Windows 打印队列…");
            printer.Begin();
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在提交打印 {i + 1} / {files.Count} 页…");
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(files[i]); bitmap.EndInit(); bitmap.Freeze();
                printer.WritePage(bitmap);
            }
            token.ThrowIfCancellationRequested(); printer.Complete();
        }
        finally { try { Directory.Delete(folder, true); } catch { /* A driver can retain a file briefly. */ } }
    });
}
