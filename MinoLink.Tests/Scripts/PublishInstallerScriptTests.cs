namespace MinoLink.Tests.Scripts;

public class PublishInstallerScriptTests
{
    [Fact]
    public void PublishInstallerScript_ShouldExistAndCoverBuildTagPushAndReleaseFlow()
    {
        var scriptPath = GetRepoFilePath("scripts", "publish-installer.ps1");

        Assert.True(File.Exists(scriptPath), $"未找到脚本: {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("MinoLink.Desktop\\MinoLink.Desktop.csproj", script);
        Assert.Contains("MinoLink.Installer\\MinoLink.Installer.csproj", script);
        Assert.Contains("MinoLink.Installer\\output", script);
        Assert.Contains("Invoke-Checked -FilePath \"dotnet\"", script);
        Assert.Contains("Invoke-Checked -FilePath \"git\"", script);
        Assert.Contains("@(\"push\", \"origin\", \"master\")", script);
        Assert.Contains("Invoke-Checked -FilePath \"gh\"", script);
        Assert.Contains("gh\" -ArgumentList @(\"release\", \"create\"", script);
    }

    [Fact]
    public void Readme_ShouldDocumentLocalPublishInstallerScript()
    {
        var readmePath = GetRepoFilePath("README.md");
        var readme = File.ReadAllText(readmePath);

        Assert.Contains("publish-installer.ps1", readme);
        Assert.Contains("一键", readme);
        Assert.Contains("GitHub Release", readme);
    }

    private static string GetRepoFilePath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(new[] { AppContext.BaseDirectory }.Concat(segments).ToArray());
    }
}
