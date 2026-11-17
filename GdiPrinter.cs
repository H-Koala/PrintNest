using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrintNest;
// Windows GDI printing avoids dependence on the XPS document writer and its lifecycle.
internal sealed class GdiPrinter : IDisposable
{
    private IntPtr dc;
    private bool started;
    public Rect PrintableArea { get; }
    public GdiPrinter(string printer, byte[] devMode)
    {
        var pin = GCHandle.Alloc(devMode, GCHandleType.Pinned);
        try { dc = CreateDC("WINSPOOL", printer, null, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接所选打印机。");
        double dx = GetDeviceCaps(dc, 88), dy = GetDeviceCaps(dc, 90);
        PrintableArea = new Rect(GetDeviceCaps(dc, 112) * 96 / dx, GetDeviceCaps(dc, 113) * 96 / dy, GetDeviceCaps(dc, 8) * 96 / dx, GetDeviceCaps(dc, 10) * 96 / dy);
    }
    public void Begin()
    {
        var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = "印巢 · 批量打印" };
        if (StartDoc(dc, ref info) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "打印任务未能启动，或保存对话框已取消。");
        started = true;
    }
    public void WritePage(BitmapSource source)
    {
        if (StartPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法开始打印页面。");
        Draw(dc, source, -GetDeviceCaps(dc, 112), -GetDeviceCaps(dc, 113), GetDeviceCaps(dc, 110), GetDeviceCaps(dc, 111));
        if (EndPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "提交打印页面失败。");
    }
    public void Complete()
    {
        if (EndDoc(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "提交打印任务失败。");
        started = false;
    }
    private static void Draw(IntPtr target, BitmapSource source, int x, int y, int width, int height)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        int stride = checked(bitmap.PixelWidth * 4); var pixels = new byte[checked(stride * bitmap.PixelHeight)]; bitmap.CopyPixels(pixels, stride, 0);
        var info = Header(bitmap.PixelWidth, bitmap.PixelHeight);
        SetStretchBltMode(target, 4); SetBrushOrgEx(target, 0, 0, IntPtr.Zero);
        int result = StretchDIBits(target, x, y, width, height, 0, 0, bitmap.PixelWidth, bitmap.PixelHeight, pixels, ref info, 0, 0x00CC0020);
        if (result == 0 || result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "打印机无法接收页面图像。");
    }
    private static BitmapInfo Header(int width, int height) => new() { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32, ImageSize = (uint)checked(width * height * 4) };
    public void Dispose() { if (dc == IntPtr.Zero) return; if (started) AbortDoc(dc); DeleteDC(dc); dc = IntPtr.Zero; }
    internal static bool TestBitmapTransfer()
    {
        IntPtr target = CreateCompatibleDC(IntPtr.Zero); if (target == IntPtr.Zero) return false;
        var info = Header(64, 64); IntPtr bitmap = CreateDIBSection(target, ref info, 0, out _, IntPtr.Zero, 0);
        IntPtr old = SelectObject(target, bitmap);
        try
        {
            var pixels = Enumerable.Repeat(0x006E760F, 16 * 16).ToArray();
            var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, pixels, 16 * 4);
            Draw(target, source, 0, 0, 64, 64);
            // DIB BGR value 0x006E760F has RGB=(110,118,15); GetPixel returns COLORREF 0x000F766E.
            return GetPixel(target, 32, 32) == 0x000F766E;
        }
        finally { SelectObject(target, old); DeleteObject(bitmap); DeleteDC(target); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DocInfo { public int Size; public string? Name; public string? Output; public string? DataType; public int Type; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width; public int Height; public ushort Planes; public ushort Bits; public uint Compression; public uint ImageSize; public int XPels; public int YPels; public uint ColorsUsed; public uint ColorsImportant; }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, byte[] bits, ref BitmapInfo info, uint usage, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
