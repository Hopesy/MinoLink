using System;
using System.Linq;
using IO = System.IO;
using WixSharp;

namespace MinoLink.Installer;

internal static class Program
{
    private const string ProductName = "MinoLink";
    private const string Manufacturer = "MinoLink";
    private static readonly Guid UpgradeCode = new("A1C4E7F0-3B6D-4A8E-9F2C-5D1E0B7A3C6F");

    private static int Main()
    {
        try
        {
            var installerProjectDirectory = IO.Directory.GetCurrentDirectory();
            var repositoryRoot = IO.Path.GetFullPath(IO.Path.Combine(installerProjectDirectory, ".."));
            var paths = InstallerProjectPaths.FromRepositoryRoot(repositoryRoot);

            var publishExecutablePath = paths.GetPublishExecutablePath();
            var productVersion = ParseProductVersion(paths.ApplicationProjectPath);
            var productIconPath = ResolveProductIconPath(paths.ApplicationProjectPath);

            var project = new Project
            {
                OutDir = "output",
                Name = ProductName,
                Platform = Platform.x64,
                UI = WUI.WixUI_InstallDir,
                MajorUpgrade = MajorUpgrade.Default,
                GUID = UpgradeCode,
                Version = productVersion,
                InstallScope = InstallScope.perUser,
                Dirs = BuildDirectories(paths)
            };

            project.ControlPanelInfo.Manufacturer = Manufacturer;
            project.ControlPanelInfo.ProductIcon = productIconPath;
            project.ControlPanelInfo.InstallLocation = "[INSTALLDIR]";
            project.ControlPanelInfo.NoModify = true;
            project.ControlPanelInfo.NoRepair = true;
            project.OutFileName = $"{ProductName}-{project.Version}-win-x64";

            Console.WriteLine($"主程序发布目录: {paths.PublishDirectory}");
            Console.WriteLine($"主程序入口: {publishExecutablePath}");
            Console.WriteLine("正在构建 MinoLink 安装包...");

            var msiPath = project.BuildMsi();
            if (string.IsNullOrWhiteSpace(msiPath) || !IO.File.Exists(msiPath))
            {
                Console.Error.WriteLine("MSI 构建失败：未生成安装包文件。");
                return 1;
            }

            Console.WriteLine($"安装包构建完成: {msiPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"安装包构建失败: {ex.Message}");
            return 1;
        }
    }

    private static Version ParseProductVersion(string applicationProjectPath)
    {
        var versionText = InstallerProjectPaths.ReadProductVersion(applicationProjectPath);
        if (!Version.TryParse(versionText, out var version))
        {
            throw new InvalidOperationException($"无法将主程序 Version 解析为 MSI 版本号：{versionText}");
        }

        return version;
    }

    private static string ResolveProductIconPath(string applicationProjectPath)
    {
        var iconRelativePath = InstallerProjectPaths.ReadApplicationIconRelativePath(applicationProjectPath);
        var applicationProjectDirectory = IO.Path.GetDirectoryName(applicationProjectPath)
            ?? throw new InvalidOperationException($"无法解析主程序项目目录：{applicationProjectPath}");
        var iconPath = IO.Path.GetFullPath(IO.Path.Combine(applicationProjectDirectory, iconRelativePath));

        if (!IO.File.Exists(iconPath))
        {
            throw new IO.FileNotFoundException("未找到控制面板图标文件。", iconPath);
        }

        return iconPath;
    }

    private static Dir[] BuildDirectories(InstallerProjectPaths paths)
    {
        var installDirectory = new InstallDir(
            @"%LocalAppDataFolder%\Programs\MinoLink",
            new Files(IO.Path.Combine(paths.PublishDirectory, "*.*")));

        var shortcutDirectories = InstallerShellLayout.CreateShortcuts()
            .Select(shortcut => new Dir(
                shortcut.Directory,
                new ExeFileShortcut(shortcut.Name, shortcut.Target, string.Empty)
                {
                    WorkingDirectory = shortcut.WorkingDirectory
                }))
            .ToArray();

        return [installDirectory, .. shortcutDirectories];
    }
}
