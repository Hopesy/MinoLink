namespace MinoLink.Core.Models;

public sealed class MinoLinkConfig
{
    public string? ProjectName { get; set; }
    public AgentConfig Agent { get; set; } = new();
    public FeishuConfig? Feishu { get; set; }
}

public sealed class AgentConfig
{
    public string Type { get; set; } = "claudecode";
    public string WorkDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string? Model { get; set; }
    public string? Mode { get; set; }
}

public sealed class FeishuConfig
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string? VerificationToken { get; set; }
    public string? AllowFrom { get; set; }
}
