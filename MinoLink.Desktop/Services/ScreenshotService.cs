using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using MinoLink.Core.Interfaces;

namespace MinoLink.Desktop.Services;

public sealed class ScreenshotService : IScreenshotService
{
    public Task<string> CaptureActiveWindowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("无法获取当前活动窗口。");

        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("无法获取活动窗口尺寸。");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("活动窗口尺寸无效。");

        var dir = Path.Combine(AppContext.BaseDirectory, "data", "snapshots", DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"snap_{DateTime.Now:HHmmssfff}.png");

        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        }

        bitmap.Save(filePath, ImageFormat.Png);
        return Task.FromResult(filePath);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
