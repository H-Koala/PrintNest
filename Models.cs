using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PrintNest;
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(name); }
}
public class PrintItem : Observable
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsPdf => System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public int PageCount { get; set; }
    public string Info => $"{(IsPdf ? "PDF" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant())}  ·  {PageCount} 页";
    private bool included = true;
    public bool Included { get => included; set => Set(ref included, value); }
    private int copies = 1;
    public int Copies { get => copies; set => Set(ref copies, value); }
    private string range = "";
    public string Range { get => range; set => Set(ref range, value); }
    private int rotation;
    public int Rotation { get => rotation; set { Set(ref rotation, value); Changed(nameof(RotationText)); } }
    public string RotationText => $"旋转 {Rotation}°";
    public BitmapSource? Thumbnail { get; set; }
    public string Error { get; set; } = "";
}
public class PrintOptions
{
    public string Printer { get; set; } = "";
    public int Paper { get; set; }
    public int Orientation { get; set; }
    public int Layout { get; set; }
    public int Fit { get; set; }
    public int Color { get; set; }
    public int Duplex { get; set; }
    public int DpiIndex { get; set; } = 1;
    public double MarginMm { get; set; } = 8;
    public int Copies { get; set; } = 1;
    public int PerSheet => new[] { 1, 2, 4, 6, 9 }[Math.Clamp(Layout, 0, 4)];
    public int Dpi => new[] { 150, 300, 600 }[Math.Clamp(DpiIndex, 0, 2)];
    public PrintOptions Clone() => (PrintOptions)MemberwiseClone();
}
public record PageRef(string Path, bool IsPdf, int Index, int Rotation);
public record Sheet(IReadOnlyList<PageRef> Pages);
public record SavedItem(string Path, bool Included, int Copies, string Range, int Rotation);
public record SavedQueue(PrintOptions Options, List<SavedItem> Files);
public static class SettingsStore
{
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintNest");
    public static PrintOptions Load() { try { return JsonSerializer.Deserialize<PrintOptions>(File.ReadAllText(System.IO.Path.Combine(Folder, "settings.json"))) ?? new(); } catch { return new(); } }
    public static void Save(PrintOptions options) { Directory.CreateDirectory(Folder); File.WriteAllText(System.IO.Path.Combine(Folder, "settings.json"), JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true })); }
}
public static class PagePlanner
{
    public static IReadOnlyList<int> ParseRange(string input, int count)
    {
        if (string.IsNullOrWhiteSpace(input)) return Enumerable.Range(0, count).ToArray();
        var result = new List<int>(); var seen = new HashSet<int>();
        foreach (string token in input.Replace('，', ',').Split(','))
        {
            string[] ends = token.Trim().Split('-');
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out int first)) throw new FormatException("页码格式应为 1-3,5，不能包含空项。");
            int last = first;
            if (ends.Length == 2 && !int.TryParse(ends[1], out last)) throw new FormatException("页面范围不完整，例如 1-3。");
            if (first < 1 || last < first || last > count) throw new FormatException($"页码必须在 1–{count} 之间，且起始页不能大于结束页。");
            for (int i = first - 1; i < last; i++) if (seen.Add(i)) result.Add(i);
        }
        return result;
    }
    public static List<Sheet> Build(IEnumerable<PrintItem> items, PrintOptions options)
    {
        var pages = new List<PageRef>();
        foreach (var item in items.Where(x => x.Included))
        {
            if (!string.IsNullOrEmpty(item.Error)) throw new InvalidOperationException($"{item.Name}：{item.Error}");
            if (item.Copies is < 1 or > 999) throw new FormatException($"{item.Name}：份数应在 1–999 之间。");
            IReadOnlyList<int> range;
            try { range = ParseRange(item.Range, item.PageCount); } catch (Exception ex) { throw new FormatException($"{item.Name}：{ex.Message}"); }
            if ((long)pages.Count + (long)range.Count * item.Copies > 20000) throw new InvalidOperationException("本次任务超过 20,000 页，请分批处理。");
            for (int c = 0; c < item.Copies; c++) foreach (int i in range) pages.Add(new(item.Path, item.IsPdf, i, item.Rotation));
        }
        return pages.Chunk(options.PerSheet).Select(p => new Sheet(p)).ToList();
    }
}
