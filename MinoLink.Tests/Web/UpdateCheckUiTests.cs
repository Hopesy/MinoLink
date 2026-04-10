namespace MinoLink.Tests.Web;

public sealed class UpdateCheckUiTests
{
    [Fact]
    public void ConfigPage_ShouldNotContainUpdateUi()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Web", "Components", "Pages", "Config.razor"));

        Assert.DoesNotContain("版本与更新", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckForUpdatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAppUpdateService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAppUpdatePackageService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainLayout_ShouldExposeVisibleCheckUpdateEntry()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Web", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("nav-update-link", source, StringComparison.Ordinal);
        Assert.Contains("检查更新", source, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OpenUpdateModal\"", source, StringComparison.Ordinal);
        Assert.Contains("M8 1v6m0 0l3-3m-3 3L5 4M3 9v3a2 2 0 002 2h6a2 2 0 002-2V9", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainLayout_ShouldUsePowerIconForAutoStartInsteadOfDownloadGlyph()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Web", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("M8 1.5v5", source, StringComparison.Ordinal);
        Assert.Contains("A5.5 5.5 0 103.02 3.18", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@if (_autoStart)\r\n                    {\r\n                        <svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"none\"><path d=\"M13.3 4L6 11.3 2.7 8\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainLayout_ShouldRenderUpdateModalInsteadOfNavigatingToConfigPage()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Web", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("_showUpdateModal", source, StringComparison.Ordinal);
        Assert.Contains("update-modal-backdrop", source, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("config#update-panel", source, StringComparison.Ordinal);
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
