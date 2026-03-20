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
    private readonly SessionManager _sessions;
    private readonly ILogger<Engine> _logger;
    private readonly CancellationTokenSource _cts = new();

    // 默认工作目录
    private readonly string _defaultWorkDir;

    // sessionKey → 交互状态
    private readonly ConcurrentDictionary<string, InteractiveState> _states = new();
    // sessionKey → 锁（保证同一会话串行处理）
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private int _disposeState;

    public Engine(string projectName, IAgent agent, IEnumerable<IPlatform> platforms,
        string defaultWorkDir, string sessionStoragePath, ILogger<Engine> logger)
    {
        _projectName = projectName;
        _agent = agent;
        _platforms = platforms.ToList();
        _sessions = new SessionManager(sessionStoragePath);
        _defaultWorkDir = Path.GetFullPath(defaultWorkDir);
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

        if (await TryHandlePendingPermissionTextAsync(platform, msg))
            return;

        // 命令拦截（在锁之前）
        if (msg.Content.StartsWith('/') && await TryHandleCommandAsync(platform, msg))
            return;

        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        if (session.ProjectKey is null)
        {
            session.ProjectKey = _defaultWorkDir;
            _sessions.Save();
        }
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
        var workDirNotice = EnsureSessionWorkDir(session);
        if (!string.IsNullOrWhiteSpace(workDirNotice))
            await platform.ReplyAsync(msg.ReplyContext, workDirNotice, _cts.Token);

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
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle, Truncate(textBuffer.ToString(), 2000), ct);
                            previewHandle = null;
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
                        var result = string.IsNullOrEmpty(evt.Content) ? textBuffer.ToString() : evt.Content;
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle,
                                BuildCompletedStatusMessage(Truncate(result, 4000), success: true), ct);
                            previewHandle = null;
                        }
                        else if (!string.IsNullOrWhiteSpace(result))
                        {
                            var finalText = BuildCompletedStatusMessage(Truncate(result, 4000), success: true);
                            foreach (var chunk in SplitMessage(finalText, 4000))
                                await platform.ReplyAsync(replyContext, chunk, ct);
                        }
                        return;

                    case AgentEventType.Error:
                        var errorText = string.IsNullOrWhiteSpace(evt.Content)
                            ? Truncate(textBuffer.ToString(), 2000)
                            : $"❌ {evt.Content}";
                        if (previewHandle is not null && updater is not null)
                        {
                            await updater.UpdateMessageAsync(previewHandle,
                                BuildCompletedStatusMessage(errorText, success: false), ct);
                            previewHandle = null;
                        }
                        else
                        {
                            await platform.ReplyAsync(replyContext,
                                BuildCompletedStatusMessage(errorText, success: false), ct);
                        }
                        return;
                }
            }
        }

        // Channel 关闭但没收到 Result 事件 → Agent 进程可能异常退出
        _logger.LogWarning("Agent 事件流已结束（未收到 Result 事件）");
        var pendingText = textBuffer.ToString();
        if (previewHandle is not null && updater is not null && !string.IsNullOrWhiteSpace(pendingText))
        {
            await updater.UpdateMessageAsync(previewHandle,
                BuildCompletedStatusMessage(Truncate(pendingText, 4000), success: false), ct);
        }
        else if (!string.IsNullOrWhiteSpace(pendingText))
        {
            foreach (var chunk in SplitMessage(BuildCompletedStatusMessage(pendingText, success: false), 4000))
                await platform.ReplyAsync(replyContext, chunk, ct);
        }
        else
        {
            await platform.ReplyAsync(replyContext,
                BuildCompletedStatusMessage("⚠️ Agent 进程已退出，未返回结果。请检查 Claude CLI 日志。", success: false), ct);
        }
    }

    /// <summary>处理权限请求：发送审批卡片，阻塞等待用户响应。</summary>
    private async Task HandlePermissionRequestAsync(
        IPlatform platform, object replyContext, InteractiveState state, AgentEvent evt, CancellationToken ct)
    {
        if (state.AutoAllowPermissions)
        {
            _logger.LogInformation("会话已开启自动批准权限，直接放行: {RequestId} tool={Tool}", evt.RequestId, evt.ToolName);
            await state.AgentSession.RespondPermissionAsync(evt.RequestId!, new PermissionResponse
            {
                Allow = true,
                AllowAll = true,
            }, ct);
            return;
        }

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
                $"🔒 权限请求: {evt.ToolName}\n输入 'allow' 允许, 'deny' 拒绝, 'allow all' 全部允许", ct);
        }

        // 阻塞等待用户响应
        var response = await pending.WaitAsync(ct);
        if (response.AllowAll)
            state.AutoAllowPermissions = true;

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
            new CardMarkdown("_如果按钮无响应，请直接回复：allow / deny / allow all_"),
            new CardActions(
            [
                new CardButton("✅ 允许", $"perm:allow:{evt.RequestId}") { Style = "primary" },
                new CardButton("❌ 拒绝", $"perm:deny:{evt.RequestId}") { Style = "danger" },
                new CardButton("✅ 全部允许", $"perm:allow_all:{evt.RequestId}"),
            ]),
        ],
    };

    /// <summary>响应权限请求（由平台卡片回调触发）。</summary>
    public bool ResolvePermission(string sessionKey, string requestId, PermissionResponse response)
    {
        if (!_states.TryGetValue(sessionKey, out var state))
        {
            _logger.LogWarning("收到权限回调但未找到会话状态: sessionKey={SessionKey}, requestId={RequestId}",
                sessionKey, requestId);
            return false;
        }

        if (state.PendingPermission?.RequestId != requestId)
        {
            _logger.LogWarning("收到权限回调但未匹配到待处理请求: sessionKey={SessionKey}, requestId={RequestId}, pendingRequestId={PendingRequestId}",
                sessionKey, requestId, state.PendingPermission?.RequestId);
            return false;
        }

        _logger.LogInformation("权限回调已匹配: sessionKey={SessionKey}, requestId={RequestId}, allow={Allow}, allowAll={AllowAll}",
            sessionKey, requestId, response.Allow, response.AllowAll);
        return state.PendingPermission.Resolve(response);
    }

    private string? EnsureSessionWorkDir(SessionRecord session)
    {
        var configuredWorkDir = session.ProjectKey ?? _defaultWorkDir;
        if (Directory.Exists(configuredWorkDir))
            return null;

        if (!Directory.Exists(_defaultWorkDir))
        {
            throw new InvalidOperationException(
                $"会话工作目录不存在，且默认工作目录也不存在: `{configuredWorkDir}` / `{_defaultWorkDir}`。请使用 /project 重新设置工作目录。");
        }

        var previousWorkDir = configuredWorkDir;
        session.ProjectKey = _defaultWorkDir;

        // 原工作目录已经失效，旧 session id 与 continue 语义都不再可信，强制从默认目录启动新会话。
        session.AgentSessionId = null;
        _sessions.Save();

        _logger.LogWarning("会话工作目录不存在，已回退到默认目录: missing={MissingWorkDir}, fallback={FallbackWorkDir}, sessionKey={SessionKey}",
            previousWorkDir, _defaultWorkDir, session.SessionKey);

        return $"⚠️ 原工作目录不存在，已切回默认目录：`{_defaultWorkDir}`";
    }

    private async Task<bool> TryHandlePendingPermissionTextAsync(IPlatform platform, Message msg)
    {
        if (!_states.TryGetValue(msg.SessionKey, out var state) || state.PendingPermission is null)
            return false;

        if (!TryParsePermissionText(msg.Content, out var response, out var resultText))
            return false;

        if (!state.PendingPermission.Resolve(response))
        {
            _logger.LogWarning("文本权限响应重复或过期: sessionKey={SessionKey}, content={Content}",
                msg.SessionKey, msg.Content);
            return true;
        }

        _logger.LogInformation("收到文本权限响应: sessionKey={SessionKey}, allow={Allow}, allowAll={AllowAll}",
            msg.SessionKey, response.Allow, response.AllowAll);

        await platform.ReplyAsync(msg.ReplyContext, resultText, _cts.Token);
        return true;
    }

    private static bool TryParsePermissionText(string? text, out PermissionResponse response, out string resultText)
    {
        response = new PermissionResponse { Allow = false };
        resultText = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = string.Join(' ',
            text.Trim().ToLowerInvariant()
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        switch (normalized)
        {
            case "allow":
            case "yes":
            case "y":
            case "允许":
                response = new PermissionResponse { Allow = true };
                resultText = "✅ 已允许";
                return true;

            case "deny":
            case "no":
            case "n":
            case "拒绝":
                response = new PermissionResponse { Allow = false };
                resultText = "❌ 已拒绝";
                return true;

            case "allow all":
            case "allowall":
            case "全部允许":
            case "允许所有":
                response = new PermissionResponse { Allow = true, AllowAll = true };
                resultText = "✅ 已全部允许";
                return true;

            default:
                return false;
        }
    }

    // ─── 命令系统 ───────────────────────────────────────────────

    private static readonly Dictionary<string, string> ModeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "default",
        ["yolo"] = "bypassPermissions",
        ["auto"] = "bypassPermissions",
        ["plan"] = "plan",
        ["acceptedits"] = "acceptEdits",
        ["accept-edits"] = "acceptEdits",
    };

    private static readonly Dictionary<string, string> ModeDisplayNames = new()
    {
        ["default"] = "默认 (每次操作需确认)",
        ["acceptEdits"] = "自动接受编辑",
        ["plan"] = "规划模式 (只读)",
        ["bypassPermissions"] = "自动批准 (yolo)",
    };

    /// <summary>尝试处理 / 命令，返回 true 表示已处理。</summary>
    private async Task<bool> TryHandleCommandAsync(IPlatform platform, Message msg)
    {
        var parts = msg.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLower().TrimStart('/');
        var args = parts[1..];

        return cmd switch
        {
            "new" => await CmdNewAsync(platform, msg, args),
            "clear" => await CmdClearAsync(platform, msg),
            "resume" => await CmdResumeAsync(platform, msg),
            "switch" => await CmdSwitchAsync(platform, msg, args),
            "continue" => await CmdContinueAsync(platform, msg),
            "current" => await CmdCurrentAsync(platform, msg),
            "mode" => await CmdModeAsync(platform, msg, args),
            "project" => await CmdProjectAsync(platform, msg, args),
            "help" => await CmdHelpAsync(platform, msg),
            _ => false, // 未知命令，透传给 Agent
        };
    }

    /// <summary>/new [名称] [--project 路径] - 创建新会话。</summary>
    private async Task<bool> CmdNewAsync(IPlatform platform, Message msg, string[] args)
    {
        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法创建新会话。", _cts.Token);
            return true;
        }

        try
        {
            await DestroyStateAsync(msg.SessionKey);

            // 解析 --project 选项
            string? workDir = null;
            var nameArgs = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is "--project" or "-p" && i + 1 < args.Length)
                {
                    workDir = args[++i];
                }
                else
                {
                    nameArgs.Add(args[i]);
                }
            }

            // 验证目录存在
            if (workDir is not null)
            {
                workDir = ResolveProjectPath(workDir);
                if (!Directory.Exists(workDir))
                {
                    await platform.ReplyAsync(msg.ReplyContext,
                        $"目录不存在: `{workDir}`", _cts.Token);
                    return true;
                }
            }

            var name = nameArgs.Count > 0 ? string.Join(' ', nameArgs) : null;
            var newSession = _sessions.CreateNew(msg.SessionKey, platform.Name, msg.From, name);
            newSession.ProjectKey = workDir ?? _defaultWorkDir;
            _sessions.Save();

            var displayName = name ?? $"会话 #{GetSessionIndex(msg.SessionKey)}";
            var projectDisplay = newSession.ProjectKey != _defaultWorkDir ? $" (目录: {newSession.ProjectKey})" : "";
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已创建新会话: **{displayName}**{projectDisplay}\n发送任意消息开始对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/clear - 清除当前会话（删除并新建空白会话）。</summary>
    private async Task<bool> CmdClearAsync(IPlatform platform, Message msg)
    {
        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法清除会话。", _cts.Token);
            return true;
        }

        try
        {
            var currentProjectKey = _sessions.GetActive(msg.SessionKey)?.ProjectKey ?? _defaultWorkDir;

            await DestroyStateAsync(msg.SessionKey);

            var removed = _sessions.RemoveActive(msg.SessionKey);
            // 自动创建一个新的空白会话
            var newSession = _sessions.CreateNew(msg.SessionKey, platform.Name, msg.From);
            newSession.ProjectKey = currentProjectKey;
            _sessions.Save();

            var removedName = removed?.Name ?? "当前会话";
            await platform.ReplyAsync(msg.ReplyContext,
                $"🗑️ 已清除 **{removedName}** 的对话历史。\n发送任意消息开始全新对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/continue - 继续当前工作目录最近一次 Claude Code 会话。</summary>
    private async Task<bool> CmdContinueAsync(IPlatform platform, Message msg)
    {
        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理。", _cts.Token);
            return true;
        }

        try
        {
            await DestroyStateAsync(msg.SessionKey);

            var previousProjectKey = _sessions.GetActive(msg.SessionKey)?.ProjectKey ?? _defaultWorkDir;

            // 创建一条特殊的 SessionRecord，标记为 continue 模式
            var session = _sessions.CreateNew(msg.SessionKey, platform.Name, msg.From, "continued");
            session.AgentSessionId = "__continue__"; // 特殊标记
            session.ProjectKey = previousProjectKey;
            _sessions.Save();

            await platform.ReplyAsync(msg.ReplyContext,
                "🔄 将继续最近一次 Claude Code 会话。\n发送任意消息继续对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/resume - 列出所有会话（对应 Claude Code 的 resume 概念）。</summary>
    private async Task<bool> CmdResumeAsync(IPlatform platform, Message msg)
    {
        var (sessions, activeIndex) = _sessions.GetAllSessions(msg.SessionKey);
        if (sessions.Count == 0)
        {
            await platform.ReplyAsync(msg.ReplyContext, "暂无会话记录。", _cts.Token);
            return true;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**会话列表**");
        sb.AppendLine();
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var marker = i == activeIndex ? " ▶" : "  ";
            var name = s.Name ?? $"会话 #{i + 1}";
            var created = s.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
            var lastActive = s.LastActiveAt.ToLocalTime().ToString("MM-dd HH:mm");
            sb.AppendLine($"{marker} **{i + 1}.** {name}  (创建: {created}, 活跃: {lastActive})");
        }
        sb.AppendLine();
        sb.AppendLine("使用 `/switch <序号>` 恢复指定会话");

        await platform.ReplyAsync(msg.ReplyContext, sb.ToString(), _cts.Token);
        return true;
    }

    /// <summary>/switch id - 切换会话。</summary>
    private async Task<bool> CmdSwitchAsync(IPlatform platform, Message msg, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var index))
        {
            await platform.ReplyAsync(msg.ReplyContext, "用法: `/switch <序号>`\n使用 `/resume` 查看可用会话。", _cts.Token);
            return true;
        }

        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法切换会话。", _cts.Token);
            return true;
        }

        try
        {
            var target = _sessions.SwitchTo(msg.SessionKey, index);
            if (target is null)
            {
                await platform.ReplyAsync(msg.ReplyContext, $"序号 {index} 不存在，使用 `/resume` 查看可用会话。", _cts.Token);
                return true;
            }

            _sessions.Save();
            await DestroyStateAsync(msg.SessionKey);

            var name = target.Name ?? $"会话 #{index}";
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换到: **{name}**\n发送任意消息继续对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/current - 查看当前会话信息。</summary>
    private async Task<bool> CmdCurrentAsync(IPlatform platform, Message msg)
    {
        var session = _sessions.GetActive(msg.SessionKey);
        if (session is null)
        {
            await platform.ReplyAsync(msg.ReplyContext, "暂无活跃会话。发送任意消息自动创建。", _cts.Token);
            return true;
        }

        var (_, activeIndex) = _sessions.GetAllSessions(msg.SessionKey);
        var name = session.Name ?? $"会话 #{activeIndex + 1}";
        var created = session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var lastActive = session.LastActiveAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var sid = session.AgentSessionId ?? "(未启动)";
        var mode = _agent.Mode;
        ModeDisplayNames.TryGetValue(mode, out var modeDisplay);
        modeDisplay ??= mode;
        var projectKey = session.ProjectKey ?? _defaultWorkDir;

        var text = $"**当前会话**: {name}\n" +
                   $"**会话ID**: `{sid}`\n" +
                   $"**工作目录**: `{projectKey}`\n" +
                   $"**权限模式**: {modeDisplay}\n" +
                   $"**创建时间**: {created}\n" +
                   $"**最后活跃**: {lastActive}";

        await platform.ReplyAsync(msg.ReplyContext, text, _cts.Token);
        return true;
    }

    /// <summary>/mode [模式] - 查看或切换权限模式。</summary>
    private async Task<bool> CmdModeAsync(IPlatform platform, Message msg, string[] args)
    {
        // 无参数：显示当前模式
        if (args.Length == 0)
        {
            var current = _agent.Mode;
            ModeDisplayNames.TryGetValue(current, out var display);
            display ??= current;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"**当前模式**: {display}");
            sb.AppendLine();
            sb.AppendLine("**可用模式**:");
            sb.AppendLine("  `default` - 默认 (每次操作需确认)");
            sb.AppendLine("  `acceptedits` - 自动接受编辑");
            sb.AppendLine("  `plan` - 规划模式 (只读)");
            sb.AppendLine("  `yolo` - 自动批准所有操作");
            sb.AppendLine();
            sb.AppendLine("使用 `/mode <模式名>` 切换");

            await platform.ReplyAsync(msg.ReplyContext, sb.ToString(), _cts.Token);
            return true;
        }

        // 有参数：切换模式
        var targetInput = args[0].ToLowerInvariant();
        if (!ModeMap.TryGetValue(targetInput, out var targetMode))
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"未知模式: `{args[0]}`\n可用: `default`, `acceptedits`, `plan`, `yolo`", _cts.Token);
            return true;
        }

        if (targetMode == _agent.Mode)
        {
            ModeDisplayNames.TryGetValue(targetMode, out var d);
            await platform.ReplyAsync(msg.ReplyContext, $"当前已经是 {d ?? targetMode} 模式。", _cts.Token);
            return true;
        }

        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法切换模式。", _cts.Token);
            return true;
        }

        try
        {
            _agent.SetMode(targetMode);
            await DestroyStateAsync(msg.SessionKey);

            ModeDisplayNames.TryGetValue(targetMode, out var displayName);
            displayName ??= targetMode;
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换到 **{displayName}** 模式\n下次发消息时将以 `--resume` 恢复会话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/project [路径] - 查看或切换工作目录。</summary>
    private async Task<bool> CmdProjectAsync(IPlatform platform, Message msg, string[] args)
    {
        // 无参数：显示当前工作目录
        if (args.Length == 0)
        {
            var session = _sessions.GetActive(msg.SessionKey);
            var currentDir = session?.ProjectKey ?? _defaultWorkDir;

            await platform.ReplyAsync(msg.ReplyContext,
                $"**当前工作目录**: `{currentDir}`\n默认: `{_defaultWorkDir}`\n\n使用 `/project <路径>` 切换", _cts.Token);
            return true;
        }

        // 有参数：切换到指定路径
        var targetDir = ResolveProjectPath(string.Join(' ', args));
        if (!Directory.Exists(targetDir))
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"目录不存在: `{targetDir}`", _cts.Token);
            return true;
        }

        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法切换目录。", _cts.Token);
            return true;
        }

        try
        {
            // 销毁当前进程（下次消息时在新目录启动）
            await DestroyStateAsync(msg.SessionKey);

            var activeSession = _sessions.GetActive(msg.SessionKey);
            if (activeSession is not null)
            {
                activeSession.ProjectKey = targetDir;
                _sessions.Save();
            }

            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换工作目录: `{targetDir}`\n下次发消息时将在新目录启动。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/help - 显示可用命令。</summary>
    private async Task<bool> CmdHelpAsync(IPlatform platform, Message msg)
    {
        var text = """
                   **MinoLink 命令**

                   `/new [名称] [--project 路径]` - 创建新会话
                   `/clear` - 清除当前会话的对话历史
                   `/continue` - 继续最近一次 Claude Code 会话
                   `/resume` - 列出所有会话
                   `/switch <序号>` - 切换到指定会话
                   `/current` - 查看当前会话信息
                   `/project [路径]` - 查看/切换工作目录
                   `/mode [模式]` - 查看/切换权限模式
                   `/help` - 显示此帮助

                   其他 `/` 开头的消息将直接转发给 Agent。
                   """;
        await platform.ReplyAsync(msg.ReplyContext, text, _cts.Token);
        return true;
    }

    /// <summary>销毁指定 sessionKey 的 InteractiveState（杀进程）。</summary>
    private async Task DestroyStateAsync(string sessionKey)
    {
        if (_states.TryRemove(sessionKey, out var state))
        {
            _logger.LogInformation("销毁会话状态: {SessionKey}", sessionKey);
            await state.AgentSession.DisposeAsync();
        }
    }

    /// <summary>获取当前活跃会话的序号（1-based）。</summary>
    private int GetSessionIndex(string sessionKey)
    {
        var (_, activeIndex) = _sessions.GetAllSessions(sessionKey);
        return activeIndex + 1;
    }

    // ─── 会话状态管理 ────────────────────────────────────────────

    private async Task<InteractiveState> GetOrCreateStateAsync(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var existing) && existing.AgentSession is not null)
        {
            return existing;
        }

        // 工作目录直接取自 session.ProjectKey
        var workDir = session.ProjectKey ?? _defaultWorkDir;

        IAgentSession agentSession;
        if (session.AgentSessionId == "__continue__")
        {
            // /continue 模式：使用 --continue 恢复最近会话
            agentSession = await _agent.ContinueSessionAsync(workDir, _cts.Token);
            session.AgentSessionId = agentSession.SessionId; // 拿到真实 session_id
        }
        else
        {
            agentSession = await _agent.StartSessionAsync(session.AgentSessionId ?? "", workDir, _cts.Token);
            session.AgentSessionId = agentSession.SessionId;
        }

        var state = new InteractiveState(agentSession);
        _states[sessionKey] = state;
        return state;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private string ResolveProjectPath(string rawPath) => Path.IsPathRooted(rawPath)
        ? Path.GetFullPath(rawPath)
        : Path.GetFullPath(Path.Combine(_defaultWorkDir, rawPath));

    private static string BuildCompletedStatusMessage(string content, bool success)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var status = success
            ? $"✅ 已完成 · {timestamp}"
            : $"❌ 处理结束 · {timestamp}";

        if (string.IsNullOrWhiteSpace(content))
            return status;

        return $"{content}\n\n---\n{status}";
    }

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        try
        {
            try
            {
                await _cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            foreach (var state in _states.Values)
                await state.AgentSession.DisposeAsync();

            foreach (var platform in _platforms)
                await platform.DisposeAsync();

            await _agent.DisposeAsync();
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>单个会话的交互状态。</summary>
    private sealed class InteractiveState(IAgentSession agentSession)
    {
        public IAgentSession AgentSession { get; } = agentSession;
        public PendingPermission? PendingPermission { get; set; }
        public bool AutoAllowPermissions { get; set; }
    }

    /// <summary>权限请求的阻塞等待器。</summary>
    private sealed class PendingPermission(string requestId)
    {
        private readonly TaskCompletionSource<PermissionResponse> _tcs = new();
        public string RequestId { get; } = requestId;

        public bool Resolve(PermissionResponse response) => _tcs.TrySetResult(response);

        public async Task<PermissionResponse> WaitAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
            return await _tcs.Task;
        }
    }
}
