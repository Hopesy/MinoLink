namespace MinoLink.Tests.Desktop;

public sealed class DesktopExitLifecycleTests
{
    [Fact]
    public void DesktopApp_ShouldReleaseMutexOnlyWhenOwned()
    {
        var source = LoadDesktopAppSource();

        Assert.Contains("private bool _ownsSingleInstanceMutex;", source, StringComparison.Ordinal);
        Assert.Contains("if (_ownsSingleInstanceMutex)", source, StringComparison.Ordinal);
        Assert.Contains("SingleInstanceMutex.ReleaseMutex();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopApp_ShouldHideTrayIconBeforeDispose()
    {
        var source = LoadDesktopAppSource();

        Assert.Contains("_notifyIcon.Visible = false;", source, StringComparison.Ordinal);
        Assert.Contains("_notifyIcon.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopApp_ShouldDeferRestartUntilExit()
    {
        var source = LoadDesktopAppSource();

        Assert.Contains("private bool _restartRequested;", source, StringComparison.Ordinal);
        Assert.Contains("private string? _restartProcessPath;", source, StringComparison.Ordinal);
        Assert.Contains("_restartRequested = true;", source, StringComparison.Ordinal);
        Assert.Contains("_restartProcessPath = processPath;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start(new ProcessStartInfo", ExtractMethod(source, "private void RestartApplication()"), StringComparison.Ordinal);
        Assert.Contains("StartRestartProcessIfRequested();", source, StringComparison.Ordinal);
    }

    private static string LoadDesktopAppSource() =>
        File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法签名: {signature}");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"未找到方法体起始: {signature}");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            if (source[i] == '}') depth--;
            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"未找到方法体结束: {signature}");
    }

    private static string GetRepoPath(params string[] segments)
    {
        var path = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(path))
        {
            if (File.Exists(Path.Combine(path, "MinoLink.slnx")))
                return Path.Combine([path, .. segments]);

            path = Path.GetDirectoryName(path)!;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录。");
    }
}
