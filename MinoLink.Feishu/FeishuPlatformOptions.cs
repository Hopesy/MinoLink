namespace MinoLink.Feishu;

/// <summary>
/// 飞书平台配置选项。
/// </summary>
public sealed class FeishuPlatformOptions
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string VerificationToken { get; init; } = "";

    /// <summary>收到消息时添加的 emoji 回复，"none" 禁用。</summary>
    public string ReactionEmoji { get; init; } = "OnIt";
}
