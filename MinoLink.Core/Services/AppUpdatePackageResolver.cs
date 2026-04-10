using MinoLink.Core.Models;

namespace MinoLink.Core.Services;

public static class AppUpdatePackageResolver
{
    public static AppReleaseAsset? SelectInstallerAsset(AppReleaseInfo release)
    {
        var installerAssets = release.Assets
            .Where(asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return installerAssets
            .FirstOrDefault(asset => asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            ?? installerAssets.FirstOrDefault();
    }

    public static string GetCacheDirectory(string localAppDataPath, string version) =>
        Path.Combine(localAppDataPath, "MinoLink", "updates", version);
}
