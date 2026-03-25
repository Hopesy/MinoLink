namespace MinoLink.Core.Models;

public sealed record SessionStatus(
    string SessionKey,
    string? UserName,
    string? Platform,
    string? AgentType,
    string? WorkDir,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    bool IsProcessing);
