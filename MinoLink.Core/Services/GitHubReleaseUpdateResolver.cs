using System.Text.Json;
using MinoLink.Core.Models;

namespace MinoLink.Core.Services;

public sealed class GitHubReleaseUpdateResolver
{
    public AppUpdateCheckResult ResolveLatestStable(string releaseFeedJson, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(releaseFeedJson))
            return AppUpdateCheckResult.Failed(currentVersion, "未找到可用的正式版本。");

        using var document = JsonDocument.Parse(releaseFeedJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return AppUpdateCheckResult.Failed(currentVersion, "更新源返回了无效数据。");

        AppReleaseInfo? latestRelease = null;
        Version? latestVersion = null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (IsDraftOrPrerelease(release))
                continue;

            var tagName = GetString(release, "tag_name");
            if (!TryParseVersion(tagName, out var releaseVersion))
                continue;

            if (latestVersion is not null && releaseVersion <= latestVersion)
                continue;

            latestVersion = releaseVersion;
            latestRelease = MapRelease(release, tagName);
        }

        if (latestRelease is null || latestVersion is null)
            return AppUpdateCheckResult.Failed(currentVersion, "未找到可用的正式版本。");

        var isUpdateAvailable = TryParseVersion(currentVersion, out var current)
            ? latestVersion > current
            : !string.Equals(NormalizeVersion(currentVersion), latestRelease.Version, StringComparison.OrdinalIgnoreCase);

        return AppUpdateCheckResult.Success(currentVersion, latestRelease, isUpdateAvailable);
    }

    public static bool TryParseVersion(string? value, out Version version) =>
        Version.TryParse(NormalizeVersion(value), out version!);

    public static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0.0.0";

        var sanitized = value.Trim();
        if (sanitized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            sanitized = sanitized[1..];

        sanitized = sanitized.Split('+', 2)[0];
        sanitized = sanitized.Split('-', 2)[0];
        return sanitized;
    }

    private static bool IsDraftOrPrerelease(JsonElement release) =>
        GetBoolean(release, "draft") || GetBoolean(release, "prerelease");

    private static AppReleaseInfo MapRelease(JsonElement release, string tagName)
    {
        var assets = new List<AppReleaseAsset>();
        if (release.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var downloadUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                assets.Add(new AppReleaseAsset(
                    GetString(asset, "name"),
                    downloadUrl,
                    GetInt64(asset, "size"),
                    GetNullableString(asset, "content_type")));
            }
        }

        return new AppReleaseInfo(
            NormalizeVersion(tagName),
            tagName,
            GetString(release, "name", NormalizeVersion(tagName)),
            GetString(release, "body"),
            GetString(release, "html_url"),
            GetNullableDateTimeOffset(release, "published_at"),
            assets);
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static long GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetNullableString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string GetString(JsonElement element, string propertyName, string defaultValue = "") =>
        GetNullableString(element, propertyName) ?? defaultValue;

    private static string? GetNullableString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
