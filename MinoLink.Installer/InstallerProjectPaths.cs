using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace MinoLink.Installer;

public sealed class InstallerProjectPaths
{
    private const string ApplicationProjectName = "MinoLink.Desktop";
    private const string ApplicationExecutableName = "MinoLink.Desktop.exe";
    private const string ReleaseTargetFramework = "net8.0-windows10.0.19041";
    private const string ReleaseRuntimeIdentifier = "win-x64";

    private InstallerProjectPaths(
        string repositoryRoot,
        string applicationProjectPath,
        string publishDirectory)
    {
        RepositoryRoot = repositoryRoot;
        ApplicationProjectPath = applicationProjectPath;
        PublishDirectory = publishDirectory;
        PublishExecutablePath = Path.Combine(publishDirectory, ApplicationExecutableName);
    }

    public string RepositoryRoot { get; }

    public string ApplicationProjectPath { get; }

    public string PublishDirectory { get; }

    public string PublishExecutablePath { get; }

    public static InstallerProjectPaths FromRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new ArgumentException("仓库根目录不能为空。", nameof(repositoryRoot));
        }

        var normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        var applicationProjectPath = Path.Combine(normalizedRepositoryRoot, ApplicationProjectName, $"{ApplicationProjectName}.csproj");
        var publishDirectory = Path.Combine(
            normalizedRepositoryRoot,
            ApplicationProjectName,
            "bin",
            "Release",
            ReleaseTargetFramework,
            ReleaseRuntimeIdentifier,
            "publish");

        return new InstallerProjectPaths(normalizedRepositoryRoot, applicationProjectPath, publishDirectory);
    }

    public static string ReadApplicationIconRelativePath(string projectFilePath)
    {
        var iconElement = LoadProjectDocument(projectFilePath)
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "ApplicationIcon", StringComparison.Ordinal));

        var iconRelativePath = iconElement?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(iconRelativePath))
        {
            throw new InvalidOperationException($"未在项目文件中找到 ApplicationIcon：{projectFilePath}");
        }

        return iconRelativePath;
    }

    public static string ReadProductVersion(string projectFilePath)
    {
        var versionElement = LoadProjectDocument(projectFilePath)
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Version", StringComparison.Ordinal));

        var configuredVersion = versionElement?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(configuredVersion))
        {
            throw new InvalidOperationException($"未在项目文件中找到 Version：{projectFilePath}");
        }

        return configuredVersion;
    }

    public string GetPublishExecutablePath()
    {
        if (!File.Exists(PublishExecutablePath))
        {
            throw new FileNotFoundException(
                $"未找到已发布的 {ApplicationExecutableName}。请先执行 dotnet publish。发布目录：{PublishDirectory}",
                PublishExecutablePath);
        }

        return PublishExecutablePath;
    }

    private static XDocument LoadProjectDocument(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            throw new ArgumentException("项目文件路径不能为空。", nameof(projectFilePath));
        }

        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("未找到主程序项目文件。", projectFilePath);
        }

        return XDocument.Load(projectFilePath);
    }
}
