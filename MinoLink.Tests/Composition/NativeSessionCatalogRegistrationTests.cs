namespace MinoLink.Tests.Composition;

public sealed class NativeSessionCatalogRegistrationTests
{
    [Fact]
    public void WebProgram_ShouldRegisterNativeSessionCatalogService()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink", "Program.cs"));

        Assert.Contains("builder.Services.AddSingleton(new NativeSessionCatalogService(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopApp_ShouldRegisterNativeSessionCatalogService()
    {
        var source = File.ReadAllText(GetRepoPath("MinoLink.Desktop", "App.xaml.cs"));

        Assert.Contains("builder.Services.AddSingleton(new NativeSessionCatalogService(", source, StringComparison.Ordinal);
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
