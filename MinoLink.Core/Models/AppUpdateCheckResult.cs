namespace MinoLink.Core.Models;

public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    AppReleaseInfo? LatestRelease,
    bool IsUpdateAvailable,
    string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);

    public static AppUpdateCheckResult Success(string currentVersion, AppReleaseInfo latestRelease, bool isUpdateAvailable) =>
        new(currentVersion, latestRelease, isUpdateAvailable, null);

    public static AppUpdateCheckResult Failed(string currentVersion, string errorMessage) =>
        new(currentVersion, null, false, errorMessage);
}
