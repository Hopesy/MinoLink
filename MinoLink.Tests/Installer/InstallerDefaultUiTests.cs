namespace MinoLink.Tests.Installer;

public sealed class InstallerDefaultUiTests
{
    [Fact]
    public void InstallerProgram_ShouldKeepDefaultInstallerUiButRestoreControlPanelIcon()
    {
        var source = LoadRepoFile("MinoLink.Installer", "Installer.cs");

        Assert.Contains("UI = WUI.WixUI_InstallDir", source, StringComparison.Ordinal);
        Assert.Contains("ControlPanelInfo.ProductIcon", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Banner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dialog", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerProgram_ShouldIncludeRuntimeSuffixInMsiFileName()
    {
        var source = LoadRepoFile("MinoLink.Installer", "Installer.cs");

        Assert.Contains("project.OutFileName = $\"{ProductName}-{project.Version}-win-x64\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerProjectPaths_ShouldReadApplicationIconForControlPanelEntry()
    {
        var source = LoadRepoFile("MinoLink.Installer", "InstallerProjectPaths.cs");

        Assert.Contains("ReadApplicationIconRelativePath", source, StringComparison.Ordinal);
        Assert.Contains("ApplicationIcon", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerProjectFile_ShouldPublishDesktopWithSingleMsbuildNode()
    {
        var source = LoadRepoFile("MinoLink.Installer", "MinoLink.Installer.csproj");

        Assert.Contains("dotnet publish", source, StringComparison.Ordinal);
        Assert.Contains("-m:1", source, StringComparison.Ordinal);
    }

    private static string LoadRepoFile(params string[] segments)
    {
        var path = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(path))
        {
            if (File.Exists(Path.Combine(path, "MinoLink.slnx")))
                return File.ReadAllText(Path.Combine([path, .. segments]));

            path = Path.GetDirectoryName(path)!;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录。");
    }
}
