using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

public interface IAppUpdatePackageService
{
    Task<AppUpdateDownloadResult> DownloadInstallerAsync(AppReleaseInfo release, CancellationToken cancellationToken = default);

    void LaunchInstaller(string installerPath);
}
