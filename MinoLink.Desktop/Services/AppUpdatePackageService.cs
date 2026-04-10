using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        if (string.IsNullOrWhiteSpace(installerPath) ||
            !installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(installerPath))
        {
            throw new InvalidOperationException("更新安装包无效，无法启动安装器。");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
        });
    }

    public void LaunchInstallerAndShutdown(string installerPath)
    {
        LaunchInstaller(installerPath);

        var app = WpfApplication.Current;
        if (app is null)
            return;

        _ = app.Dispatcher.BeginInvoke(() => app.Shutdown());
    }
}
