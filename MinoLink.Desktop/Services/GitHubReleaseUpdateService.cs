using System.Net.Http;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Core.Services;

namespace MinoLink.Desktop.Services;

public sealed class GitHubReleaseUpdateService(
    HttpClient httpClient,
    ReleaseUpdateOptions options,
    IAppVersionProvider versionProvider,
    GitHubReleaseUpdateResolver resolver) : IAppUpdateService
{
    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = versionProvider.Version;
        var requestPath = $"repos/{options.GitHubOwner}/{options.GitHubRepo}/releases?per_page=10";

        using var response = await httpClient.GetAsync(requestPath, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return AppUpdateCheckResult.Failed(currentVersion, $"检查更新失败：HTTP {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return resolver.ResolveLatestStable(json, currentVersion);
    }
}
