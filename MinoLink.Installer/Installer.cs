using System;
using System.Linq;
using System.Xml.Linq;
using IO = System.IO;
using Microsoft.Deployment.WindowsInstaller;
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
                Dirs = BuildDirectories(paths),
                Actions = BuildInstallerActions(),
                GenericItems = BuildGenericItems()
            };

            project.Include(WixExtension.Util);
            project.WixSourceGenerated += NormalizePerUserComponentKeyPaths;
            project.ControlPanelInfo.Manufacturer = Manufacturer;
            project.ControlPanelInfo.ProductIcon = productIconPath;
            project.ControlPanelInfo.InstallLocation = "[INSTALLDIR]";
            project.ControlPanelInfo.NoModify = true;
            project.ControlPanelInfo.NoRepair = false;
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

    private static WixSharp.Action[] BuildInstallerActions()
    {
        return
        [
            new CustomActionRef("WixCloseApplications", When.After, Step.InstallInitialize, new Condition("VersionNT > 400")),
            BuildCleanupInstallDirectoryAction(),
        ];
    }

    private static IGenericEntity[] BuildGenericItems()
    {
        return
        [
            new CloseApplication(InstallerProjectPaths.ApplicationExecutableName)
            {
                Description = "MinoLink is running and will be closed before setup continues.",
                Property = "MINOLINK_RUNNING",
                CloseMessage = true,
                EndSessionMessage = true,
                RebootPrompt = false,
                Timeout = 5,
                TerminateProcess = 1,
            },
        ];
    }

    private static void NormalizePerUserComponentKeyPaths(XDocument document)
    {
        var ns = document.Root?.Name.Namespace
            ?? throw new InvalidOperationException("无法解析 WiX 文档命名空间。");

        foreach (var component in document.Descendants(ns + "Component"))
        {
            var componentId = component.Attribute("Id")?.Value;
            if (string.IsNullOrWhiteSpace(componentId))
            {
                continue;
            }

            var keyPathValue = component.Elements(ns + "RegistryKey")
                .Where(key => string.Equals(key.Attribute("Root")?.Value, "HKCU", StringComparison.OrdinalIgnoreCase))
                .SelectMany(key => key.Elements(ns + "RegistryValue"), (key, value) => new { key, value })
                .FirstOrDefault(entry => string.Equals(entry.value.Attribute("KeyPath")?.Value, "yes", StringComparison.OrdinalIgnoreCase));

            if (keyPathValue is null)
            {
                continue;
            }

            keyPathValue.key.SetAttributeValue("Key", @"Software\MinoLink\Installer\Components");
            keyPathValue.value.SetAttributeValue("Name", componentId);
            keyPathValue.value.SetAttributeValue("Type", "string");
            keyPathValue.value.SetAttributeValue("Value", "0");
        }
    }

    private static ManagedAction BuildCleanupInstallDirectoryAction()
    {
        var action = new ManagedAction(
            InstallerCustomActions.CleanupInstallDirectory,
            Return.check,
            When.After,
            Step.InstallFinalize,
            new Condition("REMOVE=\"ALL\""))
        {
            Execute = Execute.immediate,
            Impersonate = true,
        };

        return action;
    }
}

public static class InstallerCustomActions
{
    [CustomAction]
    public static ActionResult CleanupInstallDirectory(Session session)
    {
        try
        {
            var remove = session["REMOVE"];
            if (!string.Equals(remove, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                session.Log("CleanupInstallDirectory skipped because REMOVE is not ALL.");
                return ActionResult.Success;
            }

            var installDir = session["INSTALLDIR"];
            if (string.IsNullOrWhiteSpace(installDir))
            {
                session.Log("CleanupInstallDirectory skipped because INSTALLDIR is empty.");
                return ActionResult.Success;
            }

            var targetPath = IO.Path.GetFullPath(installDir);
            if (!IO.Directory.Exists(targetPath))
            {
                session.Log($"CleanupInstallDirectory skipped because directory does not exist: {targetPath}");
                return ActionResult.Success;
            }

            session.Log($"CleanupInstallDirectory removing: {targetPath}");
            DeleteShortcutArtifacts(session);
            DeleteDirectoryTree(targetPath);

            if (IO.Directory.Exists(targetPath))
            {
                throw new InvalidOperationException($"安装目录仍然存在，删除失败：{targetPath}");
            }

            session.Log("CleanupInstallDirectory completed successfully.");
            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("CleanupInstallDirectory failed: " + ex);
            return ActionResult.Failure;
        }
    }

    private static void DeleteDirectoryTree(string directoryPath)
    {
        foreach (var filePath in IO.Directory.GetFiles(directoryPath, "*", IO.SearchOption.AllDirectories))
        {
            var attributes = IO.File.GetAttributes(filePath);
            if ((attributes & IO.FileAttributes.ReadOnly) == IO.FileAttributes.ReadOnly)
            {
                IO.File.SetAttributes(filePath, attributes & ~IO.FileAttributes.ReadOnly);
            }

            IO.File.Delete(filePath);
        }

        foreach (var childDirectory in IO.Directory.GetDirectories(directoryPath, "*", IO.SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            IO.Directory.Delete(childDirectory, false);
        }

        IO.Directory.Delete(directoryPath, false);
    }

    private static void DeleteShortcutArtifacts(Session session)
    {
        var desktopFolder = session["DesktopFolder"];
        if (!string.IsNullOrWhiteSpace(desktopFolder))
        {
            DeleteFileIfExists(IO.Path.Combine(desktopFolder, "MinoLink.lnk"), session);
        }

        var programMenuFolder = session["ProgramMenuFolder"];
        if (!string.IsNullOrWhiteSpace(programMenuFolder))
        {
            var startMenuDirectory = IO.Path.Combine(programMenuFolder, "MinoLink");
            DeleteFileIfExists(IO.Path.Combine(startMenuDirectory, "MinoLink.lnk"), session);

            if (IO.Directory.Exists(startMenuDirectory) && !IO.Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
            {
                session.Log($"CleanupInstallDirectory removing empty shortcut directory: {startMenuDirectory}");
                IO.Directory.Delete(startMenuDirectory, false);
            }
        }
    }

    private static void DeleteFileIfExists(string filePath, Session session)
    {
        if (!IO.File.Exists(filePath))
        {
            return;
        }

        var attributes = IO.File.GetAttributes(filePath);
        if ((attributes & IO.FileAttributes.ReadOnly) == IO.FileAttributes.ReadOnly)
        {
            IO.File.SetAttributes(filePath, attributes & ~IO.FileAttributes.ReadOnly);
        }

        session.Log($"CleanupInstallDirectory removing file: {filePath}");
        IO.File.Delete(filePath);
    }
}
