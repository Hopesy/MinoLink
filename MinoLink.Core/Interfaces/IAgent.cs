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

    /// <summary>创建一个新的交互式会话。</summary>
    Task<IAgentSession> StartSessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// 一个正在运行的 Agent 会话（双向通信）。
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>向 Agent 发送用户消息。</summary>
    Task SendAsync(string content, IReadOnlyList<string>? images = null, CancellationToken ct = default);

    /// <summary>回复权限请求。</summary>
    Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default);

    /// <summary>Agent 事件流（思考、文本、工具调用、权限请求、最终结果）。</summary>
    ChannelReader<AgentEvent> Events { get; }
}
