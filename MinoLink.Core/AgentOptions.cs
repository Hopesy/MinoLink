namespace MinoLink.Core;

/// <summary>Agent 创建选项（从配置反序列化）。</summary>
public sealed class AgentOptions
{
    public string? Model { get; init; }
    public string Mode { get; init; } = "default";
    public Dictionary<string, object> Extra { get; init; } = [];
}
