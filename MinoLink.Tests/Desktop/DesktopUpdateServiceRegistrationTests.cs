namespace MinoLink.Tests.Desktop;

public sealed class DesktopUpdateServiceRegistrationTests
{
    [Fact]
    public void DesktopApp_ShouldRegisterGitHubReleaseUpdateService()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));

        Assert.Contains("AddHttpClient<IAppUpdateService, GitHubReleaseUpdateService>", source, StringComparison.Ordinal);
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
