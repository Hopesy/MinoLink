namespace MinoLink.Core.Models;

/// <summary>
/// 会话记录条目。
/// </summary>
public sealed class SessionRecord
{
    public required string SessionKey { get; init; }
    public string AgentType { get; set; } = "claudecode";
    public string? AgentSessionId { get; set; }
    public string? Name { get; set; }
    public string? From { get; init; }
    public string? FromName { get; init; }
    public string? Platform { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ProjectKey { get; set; }
}
