namespace MinoLink.Tests.Desktop;

public sealed class AutoStartHelperDebugGuardTests
{
    [Fact]
    public void AutoStartHelper_ShouldGuardRegistryWritesInDebugBuild()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "Services", "AutoStartHelper.cs"));

        Assert.Contains("#if DEBUG", source, StringComparison.Ordinal);
        Assert.Contains("public static bool CanPersistAutoStart", source, StringComparison.Ordinal);
        Assert.Contains("return false;", ExtractConditionalBlock(source, "#if DEBUG", "#else"), StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopApp_ShouldDisableAutoStartMenuWhenPersistenceIsBlocked()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));

        Assert.Contains("IsEnabled = AutoStartHelper.CanPersistAutoStart", source, StringComparison.Ordinal);
    }

    private static string ExtractConditionalBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到条件编译起始: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"未找到条件编译结束: {endMarker}");

        return source[start..end];
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
