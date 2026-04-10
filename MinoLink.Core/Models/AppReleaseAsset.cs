namespace MinoLink.Core.Models;

public sealed record AppReleaseAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? ContentType);
