namespace MinoLink.Core.Models;

/// <summary>
/// Agent 产生的事件类型。
/// </summary>
public enum AgentEventType
{
    /// <summary>正在思考。</summary>
    Thinking,

    /// <summary>文本输出片段。</summary>
    Text,

    /// <summary>工具调用。</summary>
    ToolUse,

    /// <summary>权限请求（需要用户审批）。</summary>
    PermissionRequest,

    /// <summary>最终结果。</summary>
    Result,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>
/// Agent 会话产生的单个事件。
/// </summary>
public sealed class AgentEvent
{
    public required AgentEventType Type { get; init; }

    /// <summary>事件文本内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>工具名称（ToolUse / PermissionRequest 时有值）。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具输入参数的简要描述。</summary>
    public string? ToolInput { get; init; }

    /// <summary>权限请求 ID（PermissionRequest 时有值）。</summary>
    public string? RequestId { get; init; }
}
