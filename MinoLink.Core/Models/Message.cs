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

    /// <summary>统一附件列表。</summary>
    public IReadOnlyList<MessageAttachment> Attachments { get; init; } = [];

    /// <summary>兼容旧调用的图片本地路径列表。</summary>
    public IReadOnlyList<string> Images =>
        Attachments.Where(x => x.Kind == MessageAttachmentKind.Image)
            .Select(x => x.LocalPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();

    /// <summary>兼容旧调用的文件本地路径列表。</summary>
    public IReadOnlyList<string> Files =>
        Attachments.Where(x => x.Kind == MessageAttachmentKind.File)
            .Select(x => x.LocalPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();

    /// <summary>平台特定的回复上下文（用于回复该消息）。</summary>
    public required object ReplyContext { get; init; }

    /// <summary>是否来自群聊。</summary>
    public bool IsGroup { get; init; }

    /// <summary>消息到达时间。</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum MessageAttachmentKind
{
    Image,
    File,
}

public sealed class MessageAttachment
{
    public required MessageAttachmentKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string LocalPath { get; init; } = string.Empty;
    public string RemoteKey { get; init; } = string.Empty;
    public string SourcePlatform { get; init; } = string.Empty;
    public string SourceMessageId { get; init; } = string.Empty;
}
