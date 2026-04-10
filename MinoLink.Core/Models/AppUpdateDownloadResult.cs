namespace MinoLink.Core.Models;

public sealed record AppUpdateDownloadResult(
    string? InstallerPath,
    AppReleaseAsset? Asset,
    string? ErrorMessage)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(InstallerPath) && string.IsNullOrWhiteSpace(ErrorMessage);

    public static AppUpdateDownloadResult Success(string installerPath, AppReleaseAsset asset) =>
        new(installerPath, asset, null);

    public static AppUpdateDownloadResult Failed(string errorMessage) =>
        new(null, null, errorMessage);
}
