using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrintNest;
public partial class MainWindow : Window
{
    public ObservableCollection<PrintItem> Items { get; } = new();
    private bool ready, updating, busy;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int previewVersion, pageIndex;
    private List<Sheet> sheets = new();
    private PrintOptions options = new();
    private byte[]? driverTicket;
    private Rect? printableArea;
    private CancellationTokenSource? operation;
    public MainWindow()
    {
        InitializeComponent(); DataContext = this;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(1340, workArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(880, workArea.Height - 32));
        options = SettingsStore.Load(); ApplyOptions(options);
        Loaded += async (_, _) => await RefreshPrintersAsync();
        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshPreviewAsync(); };
        ready = true; SchedulePreview();
    }
    private void Status(string message, bool error = false) { StatusText.Text = message; StatusText.Foreground = new SolidColorBrush(error ? System.Windows.Media.Color.FromRgb(180, 65, 50) : System.Windows.Media.Color.FromRgb(108, 123, 132)); }
    private void SchedulePreview() { if (!ready || updating) return; previewVersion++; debounce.Stop(); debounce.Start(); }
    private bool ReadOptions(out PrintOptions result)
    {
        result = new PrintOptions { Printer = PrinterBox.SelectedItem as string ?? "", Paper = Math.Max(0, PaperBox.SelectedIndex), Orientation = Math.Max(0, OrientationBox.SelectedIndex), Layout = Math.Max(0, LayoutBox.SelectedIndex), Fit = Math.Max(0, FitBox.SelectedIndex), Color = Math.Max(0, ColorBox.SelectedIndex), Duplex = Math.Max(0, DuplexBox.SelectedIndex), DpiIndex = Math.Max(0, DpiBox.SelectedIndex) };
        if (!double.TryParse(MarginBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out double margin) || !double.IsFinite(margin) || margin < 0 || margin > 40) { Status("边距请输入 0–40 mm。", true); return false; }
        if (!int.TryParse(BatchCopiesBox.Text, out int copies) || copies < 1 || copies > 999) { Status("整批份数请输入 1–999。", true); return false; }
        if (FileList.SelectedItem is PrintItem current && (!int.TryParse(ItemCopiesBox.Text, out int itemCopies) || itemCopies is < 1 or > 999)) { Status($"{current.Name}：份数请输入 1–999。", true); return false; }
        result.MarginMm = margin; result.Copies = copies; options = result; return true;
    }
    private void ApplyOptions(PrintOptions value)
    {
        updating = true;
        PaperBox.SelectedIndex = Math.Clamp(value.Paper, 0, 3); OrientationBox.SelectedIndex = Math.Clamp(value.Orientation, 0, 1); LayoutBox.SelectedIndex = Math.Clamp(value.Layout, 0, 4);
        FitBox.SelectedIndex = Math.Clamp(value.Fit, 0, 2); ColorBox.SelectedIndex = Math.Clamp(value.Color, 0, 1); DuplexBox.SelectedIndex = Math.Clamp(value.Duplex, 0, 2); DpiBox.SelectedIndex = Math.Clamp(value.DpiIndex, 0, 2);
        MarginBox.Text = value.MarginMm.ToString(CultureInfo.CurrentCulture); BatchCopiesBox.Text = value.Copies.ToString();
        if (PrinterBox.Items.Contains(value.Printer)) PrinterBox.SelectedItem = value.Printer;
        updating = false;
    }
    private async Task RefreshPreviewAsync()
    {
        int version = previewVersion;
        FileCount.Text = $"{Items.Count} 个文件"; QueueEmpty.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReadOptions(out var snapshot)) { InvalidatePreview(); return; }
        try
        {
            sheets = PagePlanner.Build(Items, snapshot);
            pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, sheets.Count - 1));
            var size = LayoutEngine.PaperSize(snapshot); PaperFrame.Width = size.Width; PaperFrame.Height = size.Height;
            PreviewDetail.Text = $"{((ComboBoxItem)PaperBox.SelectedItem).Content} · {(snapshot.Orientation == 0 ? "纵向" : "横向")} · {snapshot.PerSheet} 合 1";
            PageIndicator.Text = sheets.Count == 0 ? "0 / 0" : $"{pageIndex + 1} / {sheets.Count}";
            PreviewEmpty.Visibility = sheets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PrintButton.IsEnabled = ExportButton.IsEnabled = !busy && sheets.Count > 0;
            SummaryText.Text = sheets.Count == 0 ? "准备好开始了" : $"已勾选 {Items.Count(x => x.Included)} 个文件 · {sheets.Count} 个输出页面 × {snapshot.Copies} 份";
            if (sheets.Count == 0) { PreviewImage.Source = null; return; }
            var visual = await LayoutEngine.ComposeAsync(sheets[pageIndex], snapshot, 100, printableArea);
            if (version != previewVersion) return;
            PreviewImage.Source = LayoutEngine.Rasterize(visual, size, 100);
            Status(snapshot.Fit == 1 ? "铺满裁切会裁掉超出边框的内容，请检查预览。" : "预览已更新。打印以驱动支持的纸张和可打印区域为准。");
        }
        catch (Exception ex) { if (version == previewVersion) { InvalidatePreview(); Status(ex.Message, true); } }
    }
    private void InvalidatePreview() { sheets.Clear(); PreviewImage.Source = null; PrintButton.IsEnabled = ExportButton.IsEnabled = false; SummaryText.Text = "请检查设置"; PageIndicator.Text = "—"; }
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> sources, List<string> errors)
    {
        var files = new List<string>(); var pending = new Stack<string>(sources);
        while (pending.Count > 0)
        {
            string path = pending.Pop();
            try
            {
                if (File.Exists(path)) { if (DocumentService.Extensions.Contains(Path.GetExtension(path))) files.Add(Path.GetFullPath(path)); }
                else if (Directory.Exists(path))
                {
                    foreach (string f in Directory.EnumerateFiles(path)) if (DocumentService.Extensions.Contains(Path.GetExtension(f))) files.Add(f);
                    foreach (string dir in Directory.EnumerateDirectories(path)) if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) pending.Push(dir);
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
        }
        files.Sort((a, b) => NaturalCompare(a, b)); return files;
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "StrCmpLogicalW")]
    private static extern int NaturalCompare(string a, string b);
    public async Task ImportAsync(IEnumerable<string> sources)
    {
        if (busy) return;
        SetBusy(true); operation = new CancellationTokenSource(); var errors = new List<string>(); int added = 0;
        try
        {
            var files = await Task.Run(() => ExpandPaths(sources.ToArray(), errors).ToArray());
            var known = new HashSet<string>(Items.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                operation.Token.ThrowIfCancellationRequested(); if (!known.Add(file)) continue;
                Status($"正在添加：{Path.GetFileName(file)}");
                try
                {
                    var item = await Task.Run(() => DocumentService.InspectAsync(file));
                    item.PropertyChanged += ItemChanged; Items.Add(item); added++;
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}：{ex.Message}"); }
            }
            if (FileList.SelectedItem == null && Items.Count > 0) FileList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { Status("已停止添加，已导入的文件保留在队列中。"); }
        finally { SetBusy(false); operation?.Dispose(); operation = null; SchedulePreview(); }
        if (errors.Count > 0) MessageBox.Show(this, $"成功添加 {added} 个文件，以下文件 / 文件夹无法读取：\n\n" + string.Join("\n", errors.Take(12)), "部分文件未导入", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (added == 0) Status("没有新增文件：文件已在队列中，或格式暂不支持。");
    }
    private async void AddFilesClick(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Multiselect = true, Filter = "PDF 和图片|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*" }; if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileNames); }
    private async void AddFolderClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Multiselect = true, Title = "选择要批量导入的文件夹（包含子文件夹）" }; if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FolderNames); }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportAsync(files); }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => SchedulePreview();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, FileList)) return;
        if (!ready) return; updating = true;
        if (FileList.SelectedItem is PrintItem item)
        {
            SelectedName.Text = item.Name; SelectedName.ToolTip = item.Path; ItemSettings.IsEnabled = true; ItemCopiesBox.Text = item.Copies.ToString(); RangeBox.Text = item.Range;
            RotateButton.Content = $"顺时针旋转 90° · 当前 {item.Rotation}°";
            int first = sheets.FindIndex(s => s.Pages.Any(p => p.Path == item.Path)); if (first >= 0) pageIndex = first;
        }
        else { SelectedName.Text = "选中文件可单独设置"; ItemSettings.IsEnabled = false; ItemCopiesBox.Text = ""; RangeBox.Text = ""; }
        updating = false; SchedulePreview();
    }
    private void ItemTextChanged(object sender, TextChangedEventArgs e) { if (!ready || updating || FileList.SelectedItem is not PrintItem item) return; item.Copies = int.TryParse(ItemCopiesBox.Text, out int n) ? n : 0; item.Range = RangeBox.Text; SchedulePreview(); }
    private void RotateClick(object sender, RoutedEventArgs e) { if (FileList.SelectedItem is PrintItem item) { item.Rotation = (item.Rotation + 90) % 360; RotateButton.Content = $"顺时针旋转 90° · 当前 {item.Rotation}°"; } }
    private void MoveUpClick(object sender, RoutedEventArgs e) { int i = FileList.SelectedIndex; if (i > 0) { Items.Move(i, i - 1); SchedulePreview(); } }
    private void MoveDownClick(object sender, RoutedEventArgs e) { int i = FileList.SelectedIndex; if (i >= 0 && i < Items.Count - 1) { Items.Move(i, i + 1); SchedulePreview(); } }
    private void SortClick(object sender, RoutedEventArgs e) { var sorted = Items.OrderBy(x => x.Name, Comparer<string>.Create(NaturalCompare)).ToArray(); for (int i = 0; i < sorted.Length; i++) Items.Move(Items.IndexOf(sorted[i]), i); SchedulePreview(); }
    private void RemoveClick(object sender, RoutedEventArgs e) { if (FileList.SelectedItem is PrintItem item) { item.PropertyChanged -= ItemChanged; Items.Remove(item); SchedulePreview(); } }
    private void ClearClick(object sender, RoutedEventArgs e) { foreach (var item in Items) item.PropertyChanged -= ItemChanged; Items.Clear(); SchedulePreview(); }
    private void ToggleAllClick(object sender, RoutedEventArgs e) { bool value = !Items.All(x => x.Included); foreach (var item in Items) item.Included = value; }
    private void OptionChanged(object sender, SelectionChangedEventArgs e) { if (!ready || updating) return; PresetBox.SelectedIndex = 0; printableArea = null; SchedulePreview(); }
    private void TextOptionChanged(object sender, TextChangedEventArgs e) => SchedulePreview();
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || PresetBox.SelectedIndex <= 0) return;
        var value = new PrintOptions { Printer = PrinterBox.SelectedItem as string ?? "" };
        switch (PresetBox.SelectedIndex) { case 2: value.Duplex = 1; value.Color = 1; break; case 3: value.Layout = 2; break; case 4: value.Paper = 3; value.MarginMm = 0; value.Fit = 1; break; }
        ApplyOptions(value); printableArea = null; SchedulePreview();
    }
    private void PreviousClick(object sender, RoutedEventArgs e) { if (pageIndex > 0) { pageIndex--; SchedulePreview(); } }
    private void NextClick(object sender, RoutedEventArgs e) { if (pageIndex < sheets.Count - 1) { pageIndex++; SchedulePreview(); } }
    private void ZoomClick(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source == null) return;
        var image = new Image { Source = PreviewImage.Source, Stretch = Stretch.Uniform, Margin = new Thickness(24) };
        new Window { Owner = this, Title = $"纸张预览 · 第 {pageIndex + 1} 页", Width = 850, Height = 850, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 236, 239)), Content = new ScrollViewer { Content = image } }.ShowDialog();
    }
    private async Task RefreshPrintersAsync()
    {
        string wanted = PrinterBox.SelectedItem as string ?? options.Printer;
        try
        {
            var found = await Task.Run(() =>
            {
                using var server = new LocalPrintServer(); using var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
                var available = queues.Select(x => x.FullName).OrderBy(x => x).ToList();
                string? defaultPrinter = null; try { using var q = LocalPrintServer.GetDefaultPrintQueue(); defaultPrinter = q.FullName; } catch { }
                return (available, defaultPrinter);
            }).WaitAsync(TimeSpan.FromSeconds(8));
            var (names, defaultName) = found; PrinterBox.ItemsSource = names;
            PrinterBox.SelectedItem = names.Contains(wanted) ? wanted : names.Contains(defaultName ?? "") ? defaultName : names.FirstOrDefault();
            if (names.Count == 0) Status("没有检测到打印机，仍可导出 PDF。", true);
        }
        catch (Exception ex) { Status("无法读取打印机，可先导出 PDF。" + ex.Message, true); }
    }
    private async void RefreshPrintersClick(object sender, RoutedEventArgs e) => await RefreshPrintersAsync();
    private void PrinterChanged(object sender, SelectionChangedEventArgs e) { driverTicket = null; printableArea = null; SchedulePreview(); }
    private void PrinterPropertiesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var server = new LocalPrintServer();
            var dialog = new PrintDialog { PrintQueue = PrinterBox.SelectedItem is string name ? server.GetPrintQueue(name) : null };
            if (driverTicket != null) dialog.PrintTicket = new PrintTicket(new MemoryStream(driverTicket));
            // The standard dialog's Properties button opens the selected driver's own UI. No document is submitted here.
            if (dialog.ShowDialog() == true)
            {
                if (dialog.PrintQueue == null || dialog.PrintTicket == null) return;
                PrinterBox.SelectedItem = dialog.PrintQueue.FullName; using var xml = dialog.PrintTicket.GetXmlStream(); using var memory = new MemoryStream(); xml.CopyTo(memory); driverTicket = memory.ToArray();
                var ticket = dialog.PrintTicket; updating = true;
                if (ticket.PageOrientation.HasValue) OrientationBox.SelectedIndex = ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape ? 1 : 0;
                DuplexBox.SelectedIndex = ticket.Duplexing switch { Duplexing.TwoSidedLongEdge => 1, Duplexing.TwoSidedShortEdge => 2, _ => 0 };
                ColorBox.SelectedIndex = ticket.OutputColor is OutputColor.Grayscale or OutputColor.Monochrome ? 1 : 0;
                PaperBox.SelectedIndex = ticket.PageMediaSize?.PageMediaSizeName switch { PageMediaSizeName.ISOA5 => 1, PageMediaSizeName.NorthAmericaLetter => 2, PageMediaSizeName.NorthAmerica4x6 => 3, _ => 0 };
                BatchCopiesBox.Text = (ticket.CopyCount ?? 1).ToString(); updating = false; SchedulePreview();
                Status("已读取打印机属性；右侧纸张、方向、份数等设置会用于本次输出。");
            }
        }
        catch (Exception ex) { updating = false; Status(ex.Message, true); }
    }
    private void SetBusy(bool value)
    {
        busy = value; QueuePane.IsEnabled = SettingsPane.IsEnabled = QueueActions.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = true;
        PrintButton.IsEnabled = ExportButton.IsEnabled = !value && sheets.Count > 0;
    }
    private async Task RunOutput(bool export)
    {
        if (busy || !ReadOptions(out var snapshot)) return;
        List<Sheet> plan;
        try { plan = PagePlanner.Build(Items, snapshot); if (plan.Count == 0) { Status("请先添加并勾选文件。"); return; } }
        catch (Exception ex) { Status(ex.Message, true); return; }
        string? target = null;
        if (export)
        {
            var dialog = new SaveFileDialog { Filter = "PDF 文档|*.pdf", FileName = "印巢-打印排版.pdf", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            target = dialog.FileName;
            if (Items.Any(x => string.Equals(x.Path, target, StringComparison.OrdinalIgnoreCase))) { Status("导出文件不能覆盖队列中的源文件，请使用新的名称。", true); return; }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(snapshot.Printer)) { Status("请先选择打印机。", true); return; }
            string duplex = snapshot.Duplex == 0 ? "单面" : "双面";
            if (MessageBox.Show(this, $"打印机：{snapshot.Printer}\n输出：{plan.Count} 个页面 × {snapshot.Copies} 份\n方式：{duplex}，{(snapshot.Color == 0 ? "彩色" : "灰度")}\n\n确认将这批文件发送到打印机？", "确认批量打印", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        }
        SetBusy(true); operation = new CancellationTokenSource(); debounce.Stop();
        try
        {
            SettingsStore.Save(snapshot); var progress = new Progress<string>(s => Status(s));
            if (export) await OutputService.ExportAsync(target!, plan, snapshot.Clone(), progress, operation.Token);
            else await OutputService.PrintAsync(plan, snapshot.Clone(), driverTicket, progress, operation.Token);
            Status(export ? $"已导出：{target}" : "已提交到 Windows 打印队列。实际出纸状态请查看打印机队列。");
        }
        catch (OperationCanceledException) { Status(export ? "导出已取消。" : "已请求停止。已提交的页面如需取消，请打开 Windows 打印队列。"); }
        catch (Exception ex) { Status("输出失败：" + ex.Message, true); MessageBox.Show(this, ex.Message, "输出失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }
    private async void ExportClick(object sender, RoutedEventArgs e) => await RunOutput(true);
    private async void PrintClick(object sender, RoutedEventArgs e) => await RunOutput(false);
    private void CancelClick(object sender, RoutedEventArgs e) { operation?.Cancel(); CancelButton.IsEnabled = false; Status("正在停止，请等待当前页面处理完成…"); }
    private void SaveQueueClick(object sender, RoutedEventArgs e)
    {
        if (!ReadOptions(out var snapshot)) return;
        var dialog = new SaveFileDialog { Filter = "印巢队列|*.printnest", FileName = "我的打印队列.printnest" };
        if (dialog.ShowDialog(this) == true)
        {
            try { var saved = new SavedQueue(snapshot, Items.Select(x => new SavedItem(x.Path, x.Included, x.Copies, x.Range, x.Rotation)).ToList()); File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true })); Status("队列已保存，源文件仍保留在原位置。"); }
            catch (Exception ex) { Status(ex.Message, true); }
        }
    }
    private async void LoadQueueClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "印巢队列|*.printnest" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedQueue>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("无效的队列文件。");
            if (saved.Files == null || saved.Options == null) throw new InvalidDataException("队列缺少文件或设置。");
            if (Items.Count > 0 && MessageBox.Show(this, "用保存的队列替换当前列表？源文件不会被修改。", "打开队列", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            ClearClick(this, new RoutedEventArgs()); ApplyOptions(saved.Options);
            await ImportAsync(saved.Files.Select(x => x.Path)); updating = true;
            foreach (var record in saved.Files)
            {
                var item = Items.FirstOrDefault(x => x.Path.Equals(record.Path, StringComparison.OrdinalIgnoreCase));
                if (item == null) continue; item.Included = record.Included; item.Copies = record.Copies; item.Range = record.Range; item.Rotation = ((record.Rotation / 90) % 4 + 4) % 4 * 90;
            }
            var order = saved.Files.Select(x => Items.FirstOrDefault(i => i.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).Distinct().ToList();
            for (int i = 0; i < order.Count; i++) Items.Move(Items.IndexOf(order[i]!), i);
            updating = false; FileList.SelectedIndex = -1; if (Items.Count > 0) FileList.SelectedIndex = 0; SchedulePreview();
            int missing = saved.Files.Count(x => !File.Exists(x.Path)); if (missing > 0) MessageBox.Show(this, $"有 {missing} 个源文件已移动或不存在，已跳过。", "队列恢复提示");
        }
        catch (Exception ex) { updating = false; Status(ex.Message, true); }
    }
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, "1. 拖入 PDF、图片或文件夹；上下移动可调整输出顺序。\n2. 右侧设置纸张、拼版、颜色和边距。多页 PDF 也能拼版。\n3. 点击文件，在右侧下方设置单文件份数、页码和旋转。\n4. 先检查纸张预览，再导出 PDF 或开始打印。\n\nCtrl+O 添加文件 · Ctrl+P 打印 · Delete 移除当前文件\n队列文件只记录路径，移动源文件后需要重新添加。\nGIF 打印首帧；TIFF 支持多页。加密 PDF 请先解锁后添加。\n拼版依队列顺序连续填充；每个文件的份数先展开，整批份数再复制整套。\n\n打印与导出按所选 DPI 栅格化，导出 PDF 中的文字不可选中。\n无边距打印需要打印机支持；双面也由驱动能力决定。\n打印机属性中的系统“打印”按钮仅应用设置，不立即出纸。\n\n印巢 1.0 · WPF UI (MIT) · PDFsharp (MIT)\n文档在本机处理，无需账号。", "印巢 · 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (busy) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { AddFilesClick(this, new RoutedEventArgs()); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P) { PrintClick(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.Delete && e.OriginalSource is not TextBox) { RemoveClick(this, new RoutedEventArgs()); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Status("请先停止当前任务，再关闭窗口。"); return; }
        try { if (ReadOptions(out var value)) SettingsStore.Save(value); } catch { }
    }
}
