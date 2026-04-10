namespace MinoLink.Desktop.Services;

public sealed class ReleaseUpdateOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    public string GitHubOwner { get; set; } = "Hopesy";

    public string GitHubRepo { get; set; } = "MinoLink";
}
