using MinoLink.Core.Services;

namespace MinoLink.Tests.Core;

public sealed class GitHubReleaseUpdateResolverTests
{
    [Fact]
    public void ResolveLatestStable_ShouldIgnoreDraftAndPrereleaseEntries()
    {
        var resolver = new GitHubReleaseUpdateResolver();

        var result = resolver.ResolveLatestStable(BuildReleaseFeedJson(), "1.0.3");

        Assert.True(result.IsUpdateAvailable);
        Assert.NotNull(result.LatestRelease);
        Assert.Equal("1.0.4", result.LatestRelease!.Version);
        Assert.Equal("MinoLink-1.0.4-win-x64.msi", Assert.Single(result.LatestRelease.Assets).Name);
    }

    [Fact]
    public void ResolveLatestStable_ShouldReportUpToDateWhenCurrentVersionMatchesLatestStable()
    {
        var resolver = new GitHubReleaseUpdateResolver();

        var result = resolver.ResolveLatestStable(BuildReleaseFeedJson(), "1.0.4");

        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.LatestRelease);
        Assert.Equal("1.0.4", result.LatestRelease!.Version);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ResolveLatestStable_ShouldReturnErrorWhenNoStableReleaseExists()
    {
        var resolver = new GitHubReleaseUpdateResolver();
        const string json = """
            [
              { "tag_name": "v1.0.5-beta.1", "name": "beta", "draft": false, "prerelease": true, "html_url": "https://example.test/beta", "body": "beta only", "published_at": "2026-04-10T08:00:00Z", "assets": [] }
            ]
            """;

        var result = resolver.ResolveLatestStable(json, "1.0.4");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestRelease);
        Assert.Equal("未找到可用的正式版本。", result.ErrorMessage);
    }

    private static string BuildReleaseFeedJson() => """
        [
          {
            "tag_name": "v1.0.5-beta.1",
            "name": "MinoLink 1.0.5 beta",
            "draft": false,
            "prerelease": true,
            "html_url": "https://example.test/releases/v1.0.5-beta.1",
            "body": "beta",
            "published_at": "2026-04-10T09:00:00Z",
            "assets": []
          },
          {
            "tag_name": "v1.0.4",
            "name": "MinoLink 1.0.4",
            "draft": false,
            "prerelease": false,
            "html_url": "https://example.test/releases/v1.0.4",
            "body": "正式版说明",
            "published_at": "2026-04-10T10:00:00Z",
            "assets": [
              {
                "name": "MinoLink-1.0.4-win-x64.msi",
                "browser_download_url": "https://example.test/releases/download/v1.0.4/MinoLink-1.0.4-win-x64.msi",
                "size": 12345678,
                "content_type": "application/x-msi"
              }
            ]
          },
          {
            "tag_name": "v1.0.6",
            "name": "draft",
            "draft": true,
            "prerelease": false,
            "html_url": "https://example.test/releases/v1.0.6",
            "body": "draft",
            "published_at": "2026-04-10T11:00:00Z",
            "assets": []
          }
        ]
        """;
}
