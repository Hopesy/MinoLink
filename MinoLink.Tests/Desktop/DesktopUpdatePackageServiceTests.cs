namespace MinoLink.Tests.Desktop;

public sealed class DesktopUpdatePackageServiceTests
{
    [Fact]
    public void DesktopApp_ShouldRegisterUpdatePackageService()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));

        Assert.Contains("AddSingleton<IAppUpdatePackageService, AppUpdatePackageService>()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePackageService_ShouldLaunchOnlyValidMsiFiles()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "Services", "AppUpdatePackageService.cs"));

        Assert.Contains("EndsWith(\".msi\"", source, StringComparison.Ordinal);
        Assert.Contains("Process.Start(new ProcessStartInfo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePackageService_ShouldSupportAutoUpdateHandoffAndShutdown()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "Services", "AppUpdatePackageService.cs"));

        Assert.Contains("LaunchInstallerAndShutdown", source, StringComparison.Ordinal);
        Assert.Contains("StartUpdateOrchestrator(installerPath, appPath, currentProcessId);", source, StringComparison.Ordinal);
        Assert.Contains("msiexec.exe", source, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $appPath", source, StringComparison.Ordinal);
        Assert.Contains("app.Shutdown()", source, StringComparison.Ordinal);
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
