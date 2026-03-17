namespace MinoLink.Core.Models;

/// <summary>
/// 平台收到的用户消息。
/// </summary>
public sealed class Message
{
    /// <summary>会话标识，格式 "{platform}:{userId}:{chatId}"。</summary>
    public required string SessionKey { get; init; }

    /// <summary>发送者 ID（平台侧）。</summary>
    public required string From { get; init; }

    /// <summary>发送者显示名。</summary>
    public string? FromName { get; init; }

    /// <summary>文本内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>图片 URL 列表。</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>文件 URL 列表。</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>平台特定的回复上下文（用于回复该消息）。</summary>
    public object? ReplyContext { get; init; }

    /// <summary>是否来自群聊。</summary>
    public bool IsGroup { get; init; }

    /// <summary>消息到达时间。</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}
