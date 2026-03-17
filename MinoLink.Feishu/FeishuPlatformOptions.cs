namespace MinoLink.Feishu;

/// <summary>
/// 飞书平台配置选项。
/// </summary>
public sealed class FeishuPlatformOptions
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";

    /// <summary>允许的用户 open_id，逗号分隔；"*" 表示所有。</summary>
    public string AllowFrom { get; init; } = "*";

    /// <summary>收到消息时添加的 emoji 回复，"none" 禁用。</summary>
    public string ReactionEmoji { get; init; } = "OnIt";

    /// <summary>群聊无需 @bot 即响应所有消息。</summary>
    public bool GroupReplyAll { get; init; }

    /// <summary>群聊内所有用户共享同一 Agent 会话。</summary>
    public bool ShareSessionInChannel { get; init; }

    private HashSet<string>? _allowSet;

    public bool IsAllowed(string openId)
    {
        if (AllowFrom == "*") return true;
        _allowSet ??= new HashSet<string>(AllowFrom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return _allowSet.Contains(openId);
    }
}
