using MinoLink.Core.Models;
using MinoLink.Core.Services;

namespace MinoLink.Tests.Core;

public sealed class AppUpdatePackageResolverTests
{
    [Fact]
    public void SelectInstallerAsset_ShouldPreferWinX64Msi()
    {
        var release = new AppReleaseInfo(
            "1.0.4",
            "v1.0.4",
            "MinoLink 1.0.4",
            "notes",
            "https://example.test/release",
            DateTimeOffset.UtcNow,
            [
                new AppReleaseAsset("MinoLink-1.0.4.zip", "https://example.test/file.zip", 1, "application/zip"),
                new AppReleaseAsset("MinoLink-1.0.4-win-arm64.msi", "https://example.test/arm64.msi", 2, "application/x-msi"),
                new AppReleaseAsset("MinoLink-1.0.4-win-x64.msi", "https://example.test/winx64.msi", 3, "application/x-msi")
            ]);

        var asset = AppUpdatePackageResolver.SelectInstallerAsset(release);

        Assert.NotNull(asset);
        Assert.Equal("MinoLink-1.0.4-win-x64.msi", asset!.Name);
    }

    [Fact]
    public void GetCacheDirectory_ShouldUseLocalAppDataScopedUpdateFolder()
    {
        var path = AppUpdatePackageResolver.GetCacheDirectory(
            @"C:\Users\zhouh\AppData\Local",
            "1.0.4");

        Assert.Equal(
            @"C:\Users\zhouh\AppData\Local\MinoLink\updates\1.0.4",
            path);
    }
}
