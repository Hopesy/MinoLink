using System.Threading.Channels;
using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

/// <summary>
/// AI 编程 Agent 适配器。
/// </summary>
public interface IAgent : IAsyncDisposable
{
    string Name { get; }

    /// <summary>当前权限模式。</summary>
    string Mode { get; }

    /// <summary>运行时切换权限模式，下次启动会话生效。</summary>
    void SetMode(string mode);

    /// <summary>创建或恢复一个交互式会话。sessionId 为空则新建。</summary>
    Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct);

    /// <summary>继续当前工作目录最近一次会话（等价于 --continue）。</summary>
    Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct);
}

/// <summary>
/// 一个正在运行的 Agent 会话（双向通信）。
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>向 Agent 发送用户消息。</summary>
    Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default);

    /// <summary>优雅中断当前正在执行的 turn。返回 false 表示未确认中断，应由上层决定是否 fallback kill。</summary>
    Task<bool> InterruptAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>协议级清除对话上下文。返回 true 表示成功，false 表示不支持（上层应 fallback 杀进程重建）。</summary>
    Task<bool> ClearAsync(CancellationToken ct = default);

    /// <summary>回复权限请求。</summary>
    Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default);

    /// <summary>Agent 事件流（思考、文本、工具调用、权限请求、最终结果）。</summary>
    ChannelReader<AgentEvent> Events { get; }
}
