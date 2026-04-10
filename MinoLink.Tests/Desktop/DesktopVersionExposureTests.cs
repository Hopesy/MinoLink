using System.Text.RegularExpressions;

namespace MinoLink.Tests.Desktop;

public sealed class DesktopVersionExposureTests
{
    [Fact]
    public void DesktopProject_ShouldKeepSingleVersionSourceInDesktopCsproj()
    {
        var projectFile = LoadRepoFile("MinoLink.Desktop", "MinoLink.Desktop.csproj");

        Assert.Matches(new Regex(@"<Version>\d+\.\d+\.\d+</Version>", RegexOptions.Multiline), projectFile);
    }

    [Fact]
    public void DesktopApp_ShouldRegisterAppVersionProvider()
    {
        var source = LoadRepoFile("MinoLink.Desktop", "App.xaml.cs");

        Assert.Contains("AddSingleton<IAppVersionProvider, AppVersionProvider>()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopVersionProvider_ShouldReadMultipleVersionSources()
    {
        var source = LoadRepoFile("MinoLink.Desktop", "Services", "AppVersionProvider.cs");

        Assert.Contains("AssemblyInformationalVersionAttribute", source, StringComparison.Ordinal);
        Assert.Contains("FileVersionInfo", source, StringComparison.Ordinal);
        Assert.Contains("TryReadVersionFromProjectFile", source, StringComparison.Ordinal);
        Assert.Contains("typeof(App).Assembly", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopShell_ShouldRenderCurrentVersionBadge()
    {
        var layout = LoadRepoFile("MinoLink.Web", "Components", "Layout", "MainLayout.razor");

        Assert.Contains("@inject IAppVersionProvider AppVersionProvider", layout, StringComparison.Ordinal);
        Assert.Contains("nav-version", layout, StringComparison.Ordinal);
        Assert.Contains("@AppVersionProvider.Version", layout, StringComparison.Ordinal);
    }

    private static string LoadRepoFile(params string[] segments) =>
        File.ReadAllText(GetRepoPath(segments));

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
