using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Core.Services;
using WpfApplication = System.Windows.Application;

namespace MinoLink.Desktop.Services;

public sealed class AppUpdatePackageService(IHttpClientFactory httpClientFactory) : IAppUpdatePackageService
{
    public async Task<AppUpdateDownloadResult> DownloadInstallerAsync(AppReleaseInfo release, CancellationToken cancellationToken = default)
    {
        var asset = AppUpdatePackageResolver.SelectInstallerAsset(release);
        if (asset is null)
            return AppUpdateDownloadResult.Failed("当前版本未提供可安装的 MSI 包。");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDirectory = AppUpdatePackageResolver.GetCacheDirectory(localAppData, release.Version);
        Directory.CreateDirectory(cacheDirectory);

        var installerPath = Path.Combine(cacheDirectory, asset.Name);
        if (File.Exists(installerPath))
        {
            var info = new FileInfo(installerPath);
            if (info.Length > 0 && (asset.Size <= 0 || info.Length == asset.Size))
                return AppUpdateDownloadResult.Success(installerPath, asset);
        }

        var tempPath = installerPath + ".download";
        using var httpClient = httpClientFactory.CreateClient("MinoLink.UpdatePackages");
        using var response = await httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return AppUpdateDownloadResult.Failed($"下载更新失败：HTTP {(int)response.StatusCode}");

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        File.Move(tempPath, installerPath, true);
        return AppUpdateDownloadResult.Success(installerPath, asset);
    }

    public void LaunchInstaller(string installerPath)
    {
        EnsureValidInstallerPath(installerPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
        });
    }

    public void LaunchInstallerAndShutdown(string installerPath)
    {
        EnsureValidInstallerPath(installerPath);
        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath))
            throw new InvalidOperationException("无法确定当前应用路径，无法自动重启新版本。");

        var currentProcessId = Environment.ProcessId;
        StartUpdateOrchestrator(installerPath, appPath, currentProcessId);

        var app = WpfApplication.Current;
        if (app is null)
            return;

        _ = app.Dispatcher.BeginInvoke(() => app.Shutdown());
    }

    private static void EnsureValidInstallerPath(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) ||
            !installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(installerPath))
        {
            throw new InvalidOperationException("更新安装包无效，无法启动安装器。");
        }
    }

    private static void StartUpdateOrchestrator(string installerPath, string appPath, int currentProcessId)
    {
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "MinoLink", "updater");
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, $"apply-update-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ps1");

        var script = $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $pidToWait = {{currentProcessId}}
        $msiPath = {{ToPowerShellSingleQuoted(installerPath)}}
        $appPath = {{ToPowerShellSingleQuoted(appPath)}}

        while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
            Start-Sleep -Milliseconds 250
        }

        $process = Start-Process -FilePath "msiexec.exe" -ArgumentList @("/i", $msiPath) -Wait -PassThru
        if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
            Start-Process -FilePath $appPath
        }

        Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
        """;
        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        var orchestrator = Process.Start(startInfo);
        if (orchestrator is null)
            throw new InvalidOperationException("无法启动更新编排进程。");
    }

    private static string ToPowerShellSingleQuoted(string value) => $"'{value.Replace("'", "''")}'";
}
