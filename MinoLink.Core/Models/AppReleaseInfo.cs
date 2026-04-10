namespace MinoLink.Core.Models;

public sealed record AppReleaseInfo(
    string Version,
    string TagName,
    string Title,
    string Notes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<AppReleaseAsset> Assets);
