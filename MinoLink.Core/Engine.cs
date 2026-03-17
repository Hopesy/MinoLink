using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core;

/// <summary>
/// 消息引擎：路由 Platform 消息到 Agent 会话，处理事件流。
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    private readonly string _projectName;
    private readonly IAgent _agent;
    private readonly List<IPlatform> _platforms;
    private readonly SessionManager _sessions = new();
    private readonly ILogger<Engine> _logger;
    private readonly CancellationTokenSource _cts = new();

    // sessionKey → 交互状态
    private readonly ConcurrentDictionary<string, InteractiveState> _states = new();
    // sessionKey → 锁（保证同一会话串行处理）
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public Engine(string projectName, IAgent agent, IEnumerable<IPlatform> platforms, ILogger<Engine> logger)
    {
        _projectName = projectName;
        _agent = agent;
        _platforms = platforms.ToList();
        _logger = logger;
    }

    /// <summary>启动所有平台，注册消息回调。</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var platform in _platforms)
        {
            try
            {
                await platform.StartAsync(HandleMessageAsync, ct);
                _logger.LogInformation("平台 {Platform} 已启动", platform.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平台 {Platform} 启动失败", platform.Name);
            }
        }
    }

    /// <summary>平台消息回调入口。</summary>
    private async Task HandleMessageAsync(IPlatform platform, Message msg)
    {
        _logger.LogInformation("[{Platform}] 收到消息: {SessionKey} from={From}", platform.Name, msg.SessionKey, msg.From);

        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        session.LastActiveAt = DateTimeOffset.UtcNow;

        // 获取该会话的锁
        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));

        if (!await sessionLock.WaitAsync(TimeSpan.Zero))
        {
            await platform.ReplyAsync(msg.ReplyContext, "上一条消息还在处理中，请稍候...", _cts.Token);
            return;
        }

        // 在后台处理，释放平台回调线程
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessMessageAsync(platform, msg, session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理消息异常: {SessionKey}", msg.SessionKey);
                try
                {
                    await platform.ReplyAsync(msg.ReplyContext, $"处理异常: {ex.Message}", _cts.Token);
                }
                catch { /* 回复失败则忽略 */ }
            }
            finally
            {
                sessionLock.Release();
            }
        }, _cts.Token);
    }

    /// <summary>消息处理核心逻辑。</summary>
    private async Task ProcessMessageAsync(IPlatform platform, Message msg, SessionRecord session)
    {
        _logger.LogInformation("[{Platform}] 创建/获取 Agent 会话: {SessionKey}", platform.Name, msg.SessionKey);
        var state = await GetOrCreateStateAsync(msg.SessionKey, session);
        _logger.LogInformation("[{Platform}] Agent 会话就绪: {SessionId}", platform.Name, state.AgentSession.SessionId);

        // 启动 typing 指示器
        IDisposable? typing = null;
        if (platform is ITypingIndicator indicator)
            typing = indicator.StartTyping(msg.ReplyContext);

        try
        {
            // 发送消息到 Agent
            _logger.LogInformation("[{Platform}] 发送消息到 Agent: {Content}", platform.Name,
                msg.Content.Length > 50 ? msg.Content[..50] + "..." : msg.Content);
            await state.AgentSession.SendAsync(msg.Content, msg.Images, _cts.Token);
            _logger.LogInformation("[{Platform}] 消息已发送，开始等待 Agent 事件流...", platform.Name);

            // 处理事件流
            await ProcessEventsAsync(platform, msg.ReplyContext, state, _cts.Token);
            _logger.LogInformation("[{Platform}] 事件流处理完毕", platform.Name);
        }
        finally
        {
            typing?.Dispose();
        }
    }

    /// <summary>处理 Agent 事件流。</summary>
    private async Task ProcessEventsAsync(IPlatform platform, object replyContext, InteractiveState state, CancellationToken ct)
    {
        var events = state.AgentSession.Events;
        var textBuffer = new System.Text.StringBuilder();

        // 流式预览句柄
        object? previewHandle = null;
        var lastPreviewAt = DateTimeOffset.MinValue;
        var updater = platform as IMessageUpdater;

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(TimeSpan.FromHours(2));

        while (await events.WaitToReadAsync(idleCts.Token))
        {
            while (events.TryRead(out var evt))
            {
                _logger.LogDebug("Agent 事件: {Type} tool={Tool}", evt.Type, evt.ToolName ?? "");
                // 每收到事件重置空闲计时器
                idleCts.CancelAfter(TimeSpan.FromHours(2));

                switch (evt.Type)
                {
                    case AgentEventType.Thinking:
                        // 冻结流式预览（保留当前内容，不删除）
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle, Truncate(textBuffer.ToString(), 2000), ct);
                            previewHandle = null; // detach，后续不再更新此消息
                        }
                        var thinkingPreview = Truncate(evt.Content, 300);
                        await platform.ReplyAsync(replyContext, $"💭 {thinkingPreview}", ct);
                        break;

                    case AgentEventType.Text:
                        textBuffer.Append(evt.Content);
                        // 流式预览：节流更新
                        if (updater is not null && (DateTimeOffset.UtcNow - lastPreviewAt).TotalMilliseconds > 1500)
                        {
                            var preview = Truncate(textBuffer.ToString(), 2000);
                            if (previewHandle is null)
                            {
                                previewHandle = await updater.SendPreviewAsync(replyContext, preview + " ▌", ct);
                            }
                            else
                            {
                                await updater.UpdateMessageAsync(previewHandle, preview + " ▌", ct);
                            }
                            lastPreviewAt = DateTimeOffset.UtcNow;
                        }
                        break;

                    case AgentEventType.ToolUse:
                        // 冻结流式预览（保留当前内容，不删除）
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle, Truncate(textBuffer.ToString(), 2000), ct);
                            previewHandle = null;
                        }
                        var toolMsg = $"🔧 `{evt.ToolName}`";
                        if (!string.IsNullOrEmpty(evt.ToolInput))
                            toolMsg += $": {Truncate(evt.ToolInput, 200)}";
                        await platform.ReplyAsync(replyContext, toolMsg, ct);
                        break;

                    case AgentEventType.PermissionRequest:
                        await HandlePermissionRequestAsync(platform, replyContext, state, evt, ct);
                        break;

                    case AgentEventType.Result:
                        // 在原预览消息上更新最终结果，不删除
                        var result = string.IsNullOrEmpty(evt.Content) ? textBuffer.ToString() : evt.Content;
                        if (previewHandle is not null && updater is not null)
                        {
                            if (!string.IsNullOrWhiteSpace(result))
                            {
                                // 更新预览为最终内容（去掉光标符号）
                                await updater.UpdateMessageAsync(previewHandle, Truncate(result, 4000), ct);
                            }
                            previewHandle = null;
                        }
                        else if (!string.IsNullOrWhiteSpace(result))
                        {
                            // 没有预览消息时，直接发送新消息
                            foreach (var chunk in SplitMessage(result, 4000))
                                await platform.ReplyAsync(replyContext, chunk, ct);
                        }
                        return;

                    case AgentEventType.Error:
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle, Truncate(textBuffer.ToString(), 2000), ct);
                            previewHandle = null;
                        }
                        await platform.ReplyAsync(replyContext, $"❌ {evt.Content}", ct);
                        return;
                }
            }
        }

        // Channel 关闭但没收到 Result 事件 → Agent 进程可能异常退出
        _logger.LogWarning("Agent 事件流已结束（未收到 Result 事件）");
        var pendingText = textBuffer.ToString();
        if (previewHandle is not null && updater is not null && !string.IsNullOrWhiteSpace(pendingText))
        {
            await updater.UpdateMessageAsync(previewHandle, Truncate(pendingText, 4000), ct);
        }
        else if (!string.IsNullOrWhiteSpace(pendingText))
        {
            foreach (var chunk in SplitMessage(pendingText, 4000))
                await platform.ReplyAsync(replyContext, chunk, ct);
        }
        else
        {
            await platform.ReplyAsync(replyContext, "⚠️ Agent 进程已退出，未返回结果。请检查 Claude CLI 日志。", ct);
        }
    }

    /// <summary>处理权限请求：发送审批卡片，阻塞等待用户响应。</summary>
    private async Task HandlePermissionRequestAsync(
        IPlatform platform, object replyContext, InteractiveState state, AgentEvent evt, CancellationToken ct)
    {
        var pending = new PendingPermission(evt.RequestId!);
        state.PendingPermission = pending;

        // 构建权限卡片
        if (platform is ICardSender cardSender)
        {
            var card = BuildPermissionCard(evt);
            await cardSender.SendCardAsync(replyContext, card, ct);
        }
        else
        {
            await platform.ReplyAsync(replyContext,
                $"🔒 权限请求: {evt.ToolName}\n输入 'allow' 允许, 'deny' 拒绝", ct);
        }

        // 阻塞等待用户响应
        var response = await pending.WaitAsync(ct);

        await state.AgentSession.RespondPermissionAsync(evt.RequestId!, response, ct);
        state.PendingPermission = null;
    }

    private static Card BuildPermissionCard(AgentEvent evt) => new()
    {
        Title = "🔒 权限请求",
        Elements =
        [
            new CardMarkdown($"**工具**: `{evt.ToolName}`\n**输入**: {Truncate(evt.ToolInput ?? "", 300)}"),
            new CardDivider(),
            new CardActions(
            [
                new CardButton("✅ 允许", $"perm:allow:{evt.RequestId}") { Style = "primary" },
                new CardButton("❌ 拒绝", $"perm:deny:{evt.RequestId}") { Style = "danger" },
                new CardButton("✅ 全部允许", $"perm:allow_all:{evt.RequestId}"),
            ]),
        ],
    };

    /// <summary>响应权限请求（由平台卡片回调触发）。</summary>
    public void ResolvePermission(string sessionKey, string requestId, PermissionResponse response)
    {
        if (_states.TryGetValue(sessionKey, out var state) && state.PendingPermission?.RequestId == requestId)
        {
            state.PendingPermission.Resolve(response);
        }
    }

    private async Task<InteractiveState> GetOrCreateStateAsync(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var existing) && existing.AgentSession is not null)
        {
            return existing;
        }

        var agentSession = await _agent.StartSessionAsync(session.AgentSessionId ?? "", _cts.Token);
        session.AgentSessionId = agentSession.SessionId;

        var state = new InteractiveState(agentSession);
        _states[sessionKey] = state;
        return state;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        foreach (var state in _states.Values)
            await state.AgentSession.DisposeAsync();

        foreach (var platform in _platforms)
            await platform.DisposeAsync();

        await _agent.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>单个会话的交互状态。</summary>
    private sealed class InteractiveState(IAgentSession agentSession)
    {
        public IAgentSession AgentSession { get; } = agentSession;
        public PendingPermission? PendingPermission { get; set; }
    }

    /// <summary>权限请求的阻塞等待器。</summary>
    private sealed class PendingPermission(string requestId)
    {
        private readonly TaskCompletionSource<PermissionResponse> _tcs = new();
        public string RequestId { get; } = requestId;

        public void Resolve(PermissionResponse response) => _tcs.TrySetResult(response);

        public async Task<PermissionResponse> WaitAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
            return await _tcs.Task;
        }
    }
}
