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
        string defaultWorkDir, SessionManager sessions, ILogger<Engine> logger)
    {
        _projectName = projectName;
        _agent = agent;
        _platforms = platforms.ToList();
        _sessions = sessions;
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

        if (!msg.Content.StartsWith('/') && await TryHandlePendingUserQuestionTextAsync(platform, msg))
            return;

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
        var stateStart = await GetOrCreateStateAsync(msg.SessionKey, session);
        var state = stateStart.State;
        _logger.LogInformation("[{Platform}] Agent 会话就绪: {SessionId}", platform.Name, state.AgentSession.SessionId);

        if (!string.IsNullOrWhiteSpace(stateStart.ConnectionNotice))
            await platform.ReplyAsync(msg.ReplyContext, stateStart.ConnectionNotice, _cts.Token);

        // 启动 typing 指示器
        IDisposable? typing = null;
        if (platform is ITypingIndicator indicator)
            typing = indicator.StartTyping(msg.ReplyContext);

        try
        {
            // 发送消息到 Agent
            _logger.LogInformation("[{Platform}] 发送消息到 Agent: {Content}", platform.Name,
                msg.Content.Length > 50 ? msg.Content[..50] + "..." : msg.Content);
            try
            {
                await state.AgentSession.SendAsync(msg.Content, msg.Images, _cts.Token);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("管道已关闭") || ex.Message.Contains("会话已关闭") || ex.InnerException is IOException)
            {
                _logger.LogWarning("检测到管道已关闭，重建会话: {SessionKey}", msg.SessionKey);
                _states.TryRemove(msg.SessionKey, out _);
                session.AgentSessionId = null;
                var newStart = await GetOrCreateStateAsync(msg.SessionKey, session);
                state = newStart.State;
                if (!string.IsNullOrWhiteSpace(newStart.ConnectionNotice))
                    await platform.ReplyAsync(msg.ReplyContext, newStart.ConnectionNotice, _cts.Token);
                await state.AgentSession.SendAsync(msg.Content, msg.Images, _cts.Token);
            }
            _logger.LogInformation("[{Platform}] 消息已发送，开始等待 Agent 事件流...", platform.Name);

            // 处理事件流
            await ProcessEventsAsync(platform, msg.ReplyContext, state, _cts.Token);
            _logger.LogInformation("[{Platform}] 事件流处理完毕", platform.Name);
        }
        catch (Exception ex) when (state.StopRequested)
        {
            _logger.LogInformation(ex, "[{Platform}] 当前回复已被用户中断: {SessionKey}", platform.Name, msg.SessionKey);
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
        var allowThinkingMessages = ShouldEmitThinking(platform);

        // 流式预览句柄
        object? previewHandle = null;
        var lastPreviewAt = DateTimeOffset.MinValue;
        var updater = ShouldUseStreamingPreview(platform) ? platform as IMessageUpdater : null;

        // 将 textBuffer 中累积的文本发送出去并清空
        async Task FlushTextAsync()
        {
            if (textBuffer.Length == 0) return;
            var text = textBuffer.ToString();
            textBuffer.Clear();
            if (previewHandle is not null && updater is not null)
            {
                await updater.UpdateMessageAsync(previewHandle, Truncate(text, 2000), ct);
                previewHandle = null;
            }
            else
            {
                foreach (var chunk in SplitMessage(text, 4000))
                    await platform.ReplyAsync(replyContext, chunk, ct);
            }
        }

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
                        await FlushTextAsync();
                        if (allowThinkingMessages)
                        {
                            var thinkingPreview = Truncate(evt.Content, 300);
                            await platform.ReplyAsync(replyContext, $"💭 {thinkingPreview}", ct);
                        }
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
                        await FlushTextAsync();
                        var toolMsg = evt.ToolName switch
                        {
                            "TaskCreate" or "TodoWrite" => $"📋 **待办**: {evt.ToolInput}",
                            "TaskUpdate" => $"📋 **待办更新**: {evt.ToolInput}",
                            _ => string.IsNullOrEmpty(evt.ToolInput)
                                ? $"🔧 `{evt.ToolName}`"
                                : $"🔧 `{evt.ToolName}`: {Truncate(evt.ToolInput, 200)}",
                        };
                        await platform.ReplyAsync(replyContext, toolMsg, ct);
                        break;

                    case AgentEventType.PermissionRequest:
                        await HandlePermissionRequestAsync(platform, replyContext, state, evt, ct);
                        break;

                    case AgentEventType.UserQuestion:
                        await HandleUserQuestionAsync(platform, replyContext, state, evt, ct);
                        break;

                    case AgentEventType.Result:
                        var flushedText = textBuffer.Length > 0;
                        await FlushTextAsync();
                        var resultText = evt.Content;
                        if (previewHandle is not null && updater is not null)
                        {
                            var previewResult = string.IsNullOrWhiteSpace(resultText)
                                ? "" : resultText;
                            await updater.UpdateMessageAsync(previewHandle,
                                BuildCompletedStatusMessage(Truncate(previewResult, 4000), success: true), ct);
                            previewHandle = null;
                        }
                        else if (!flushedText && !string.IsNullOrWhiteSpace(resultText))
                        {
                            // textBuffer 没有被 flush 过（没有中间文本），直接发 result 内容
                            var finalText = BuildCompletedStatusMessage(Truncate(resultText, 4000), success: true);
                            foreach (var chunk in SplitMessage(finalText, 4000))
                                await platform.ReplyAsync(replyContext, chunk, ct);
                        }
                        else
                        {
                            // 正文已经通过 FlushTextAsync 发出，只发完成状态
                            await platform.ReplyAsync(replyContext,
                                BuildCompletedStatusMessage("", success: true), ct);
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
        if (state.StopRequested)
            return;

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
                UpdatedInput = evt.ToolInputRaw,
            }, ct);
            return;
        }

        var pending = new PendingPermission(evt.RequestId!);
        state.PendingPermission = pending;
        _logger.LogInformation("开始等待用户审批: requestId={RequestId}, tool={Tool}, input={Input}",
            evt.RequestId, evt.ToolName, evt.ToolInput);

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
        _logger.LogInformation("用户审批已返回: requestId={RequestId}, allow={Allow}, allowAll={AllowAll}",
            evt.RequestId, response.Allow, response.AllowAll);
        if (response.AllowAll)
            state.AutoAllowPermissions = true;

        await state.AgentSession.RespondPermissionAsync(evt.RequestId!, new PermissionResponse
        {
            Allow = response.Allow,
            AllowAll = response.AllowAll,
            UpdatedInput = response.Allow ? (response.UpdatedInput ?? evt.ToolInputRaw) : null,
            Message = response.Message,
        }, ct);
        state.PendingPermission = null;
    }

    private async Task HandleUserQuestionAsync(
        IPlatform platform, object replyContext, InteractiveState state, AgentEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId))
            throw new InvalidOperationException("AskUserQuestion 缺少 requestId。");
        if (evt.ToolInputRaw is null)
            throw new InvalidOperationException("AskUserQuestion 缺少原始输入。");
        if (evt.Questions.Count == 0)
            throw new InvalidOperationException("AskUserQuestion 缺少问题内容。");

        var answers = new Dictionary<int, string>();

        for (var i = 0; i < evt.Questions.Count; i++)
        {
            var pending = new PendingUserQuestion(evt.RequestId!, i, evt.Questions[i]);
            state.PendingUserQuestion = pending;

            _logger.LogInformation("开始等待用户回答问题: requestId={RequestId}, questionIndex={QuestionIndex}, prompt={Prompt}",
                evt.RequestId, i, evt.Questions[i].Question);

            if (platform is ICardSender cardSender)
            {
                var card = BuildUserQuestionCard(evt.RequestId!, evt.Questions[i], i, evt.Questions.Count);
                await cardSender.SendCardAsync(replyContext, card, ct);
            }
            else
            {
                await platform.ReplyAsync(replyContext, BuildUserQuestionText(evt.Questions[i], i, evt.Questions.Count), ct);
            }

            var answer = await pending.WaitAsync(ct);
            _logger.LogInformation("用户问题已回答: requestId={RequestId}, questionIndex={QuestionIndex}, answer={Answer}",
                evt.RequestId, i, answer);
            await platform.ReplyAsync(replyContext, $"✅ 已回答: {answer}", ct);
            answers[i] = answer;
        }

        var updatedInput = BuildAskQuestionUpdatedInput(evt.ToolInputRaw, answers);
        await state.AgentSession.RespondPermissionAsync(evt.RequestId!, new PermissionResponse
        {
            Allow = true,
            UpdatedInput = updatedInput,
        }, ct);
        state.PendingUserQuestion = null;
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

    private static Card BuildUserQuestionCard(string requestId, UserQuestion question, int questionIndex, int totalQuestions)
    {
        var header = string.IsNullOrWhiteSpace(question.Header) ? "补充信息" : question.Header;
        var title = totalQuestions > 1
            ? $"📝 {header} ({questionIndex + 1}/{totalQuestions})"
            : $"📝 {header}";

        // 把问题文本和选项描述合到一个 markdown 块
        var sb = new System.Text.StringBuilder();
        sb.Append(question.Question);

        if (question.Options.Count > 0)
        {
            var hasDescriptions = question.Options.Any(o => !string.IsNullOrWhiteSpace(o.Description));
            if (hasDescriptions)
            {
                sb.AppendLine();
                foreach (var (o, i) in question.Options.Select((o, i) => (o, i)))
                {
                    sb.Append(string.IsNullOrWhiteSpace(o.Description)
                        ? $"\n**{i + 1}.** {o.Label}"
                        : $"\n**{i + 1}.** {o.Label} — {o.Description}");
                }
            }

            sb.Append("\n_点击按钮或直接回复文字_");

            var buttons = question.Options
                .Select((option, index) => new CardButton(option.Label, $"ask:{requestId}:{questionIndex}:{index}")
                {
                    Style = index == 0 ? "primary" : "default",
                })
                .ToArray();

            return new Card
            {
                Title = title,
                Elements = [new CardMarkdown(sb.ToString()), new CardActions(buttons)],
            };
        }

        sb.Append("\n_请直接回复文字作答_");
        return new Card
        {
            Title = title,
            Elements = [new CardMarkdown(sb.ToString())],
        };
    }

    private static string BuildUserQuestionText(UserQuestion question, int questionIndex, int totalQuestions)
    {
        var sb = new System.Text.StringBuilder();
        var header = string.IsNullOrWhiteSpace(question.Header) ? "补充信息" : question.Header;
        sb.AppendLine(totalQuestions > 1
            ? $"📝 **{header}** ({questionIndex + 1}/{totalQuestions})"
            : $"📝 **{header}**");
        sb.Append(question.Question);

        if (question.Options.Count > 0)
        {
            foreach (var (option, i) in question.Options.Select((o, i) => (o, i)))
            {
                sb.Append($"\n  {i + 1}. {option.Label}");
                if (!string.IsNullOrWhiteSpace(option.Description))
                    sb.Append($" — {option.Description}");
            }
            sb.Append("\n回复序号或文字自由输入");
        }
        else
        {
            sb.Append("\n请直接回复文字作答");
        }

        return sb.ToString();
    }

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

    public bool ResolveUserQuestion(string sessionKey, string requestId, string answer)
    {
        if (!_states.TryGetValue(sessionKey, out var state))
        {
            _logger.LogWarning("收到问题回答但未找到会话状态: sessionKey={SessionKey}, requestId={RequestId}",
                sessionKey, requestId);
            return false;
        }

        if (state.PendingUserQuestion?.RequestId != requestId)
        {
            _logger.LogWarning("收到问题回答但未匹配到待处理问题: sessionKey={SessionKey}, requestId={RequestId}, pendingRequestId={PendingRequestId}",
                sessionKey, requestId, state.PendingUserQuestion?.RequestId);
            return false;
        }

        _logger.LogInformation("问题回答已匹配: sessionKey={SessionKey}, requestId={RequestId}, answer={Answer}",
            sessionKey, requestId, answer);
        return state.PendingUserQuestion.Resolve(answer);
    }

    public bool ResolveUserQuestionOption(string sessionKey, string requestId, int questionIndex, int optionIndex)
    {
        if (!_states.TryGetValue(sessionKey, out var state))
        {
            _logger.LogWarning("收到问题选项回答但未找到会话状态: sessionKey={SessionKey}, requestId={RequestId}",
                sessionKey, requestId);
            return false;
        }

        if (state.PendingUserQuestion?.RequestId != requestId || state.PendingUserQuestion.QuestionIndex != questionIndex)
        {
            _logger.LogWarning("收到问题选项回答但未匹配到待处理问题: sessionKey={SessionKey}, requestId={RequestId}, questionIndex={QuestionIndex}, pendingRequestId={PendingRequestId}, pendingQuestionIndex={PendingQuestionIndex}",
                sessionKey, requestId, questionIndex, state.PendingUserQuestion?.RequestId, state.PendingUserQuestion?.QuestionIndex);
            return false;
        }

        _logger.LogInformation("问题选项已匹配: sessionKey={SessionKey}, requestId={RequestId}, questionIndex={QuestionIndex}, optionIndex={OptionIndex}",
            sessionKey, requestId, questionIndex, optionIndex);
        return state.PendingUserQuestion.ResolveOptionIndex(optionIndex);
    }

    private static Dictionary<string, object?> BuildAskQuestionUpdatedInput(
        Dictionary<string, object?> originalInput,
        IReadOnlyDictionary<int, string> answers)
    {
        var updated = new Dictionary<string, object?>(originalInput);
        var answerMap = new Dictionary<string, object?>();
        foreach (var (index, answer) in answers.OrderBy(entry => entry.Key))
            answerMap[index.ToString()] = answer;
        updated["answers"] = answerMap;
        return updated;
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

    private async Task<bool> TryHandlePendingUserQuestionTextAsync(IPlatform platform, Message msg)
    {
        if (!_states.TryGetValue(msg.SessionKey, out var state) || state.PendingUserQuestion is null)
            return false;

        if (!state.PendingUserQuestion.Resolve(msg.Content.Trim()))
        {
            _logger.LogWarning("文本回答重复或过期: sessionKey={SessionKey}, content={Content}",
                msg.SessionKey, msg.Content);
            return true;
        }

        _logger.LogInformation("收到文本问题回答: sessionKey={SessionKey}, requestId={RequestId}, answer={Answer}",
            msg.SessionKey, state.PendingUserQuestion.RequestId, msg.Content);

        await platform.ReplyAsync(msg.ReplyContext, "✅ 已提交回答", _cts.Token);
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
            "stop" => await CmdStopAsync(platform, msg),
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

    /// <summary>/new [--project 路径] - 开始新会话（清空当前 AgentSessionId）。</summary>
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
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is "--project" or "-p" && i + 1 < args.Length)
                    workDir = args[++i];
            }

            if (workDir is not null)
            {
                workDir = ResolveProjectPath(workDir);
                if (!TryEnsureProjectDirectory(workDir, out var _, out var error))
                {
                    await platform.ReplyAsync(msg.ReplyContext,
                        $"无法创建目录: `{workDir}`\n{error}", _cts.Token);
                    return true;
                }
            }

            var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
            if (workDir is not null)
                session.ProjectKey = workDir;
            session.AgentSessionId = null; // 清空，下次消息时启动全新会话
            _sessions.Save();

            var projectDisplay = (session.ProjectKey ?? _defaultWorkDir) != _defaultWorkDir
                ? $" (目录: {session.ProjectKey})"
                : "";
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已开始新会话{projectDisplay}\n发送任意消息开始对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/stop - 中断当前正在运行的回复，但保留会话记录。</summary>
    private async Task<bool> CmdStopAsync(IPlatform platform, Message msg)
    {
        if (!_states.TryRemove(msg.SessionKey, out var state))
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前没有正在运行的回复。", _cts.Token);
            return true;
        }

        state.StopRequested = true;
        _logger.LogInformation("用户请求中断当前回复: sessionKey={SessionKey}", msg.SessionKey);

        await state.AgentSession.DisposeAsync();
        await platform.ReplyAsync(msg.ReplyContext, "⏹️ 已停止当前回复。直接发送新消息即可继续。", _cts.Token);
        return true;
    }

    /// <summary>/clear - 清除当前对话上下文（杀进程，保留会话，下次 resume 恢复）。</summary>
    private async Task<bool> CmdClearAsync(IPlatform platform, Message msg)
    {
        await DestroyStateAsync(msg.SessionKey);
        await platform.ReplyAsync(msg.ReplyContext, "🗑️ 已清除对话上下文。", _cts.Token);
        return true;
    }

    /// <summary>/continue - 继续当前工作目录最近一次 Claude Code 会话。</summary>
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

            var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
            session.AgentSessionId = "__continue__";
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

    /// <summary>/resume - 列出当前工作目录的所有 Claude Code 原生会话。</summary>
    private async Task<bool> CmdResumeAsync(IPlatform platform, Message msg)
    {
        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var nativeSessions = ClaudeNativeSession.GetSessions(workDir);

        if (nativeSessions.Count == 0)
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"当前目录 `{workDir}` 暂无历史会话记录。", _cts.Token);
            return true;
        }

        var currentId = session.AgentSessionId;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**会话列表**");
        sb.AppendLine();
        for (var i = 0; i < nativeSessions.Count; i++)
        {
            var s = nativeSessions[i];
            var marker = s.SessionId == currentId ? " ▶" : "  ";
            var lastActive = s.LastActive.ToString("MM-dd HH:mm");
            var summary = string.IsNullOrWhiteSpace(s.Summary) ? "(无摘要)" : s.Summary;
            sb.AppendLine($"{marker} **{i + 1}.** {lastActive}  {summary}");
        }
        sb.AppendLine();
        sb.AppendLine("使用 `/switch <序号>` 恢复指定会话");

        await platform.ReplyAsync(msg.ReplyContext, sb.ToString(), _cts.Token);
        return true;
    }

    /// <summary>/switch <序号> - 切换到指定的 Claude Code 原生会话。</summary>
    private async Task<bool> CmdSwitchAsync(IPlatform platform, Message msg, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 1)
        {
            await platform.ReplyAsync(msg.ReplyContext, "用法: `/switch <序号>`\n使用 `/resume` 查看可用会话。", _cts.Token);
            return true;
        }

        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var nativeSessions = ClaudeNativeSession.GetSessions(workDir);

        if (index > nativeSessions.Count)
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"序号 {index} 不存在，使用 `/resume` 查看可用会话。", _cts.Token);
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
            await DestroyStateAsync(msg.SessionKey);

            var target = nativeSessions[index - 1];
            session.AgentSessionId = target.SessionId;
            _sessions.Save();

            var lastActive = target.LastActive.ToString("MM-dd HH:mm");
            var summary = string.IsNullOrWhiteSpace(target.Summary) ? "(无摘要)" : target.Summary;
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换到会话 **{index}** ({lastActive})\n{summary}\n\n发送任意消息继续对话。", _cts.Token);
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

        var sid = GetDisplaySessionId(msg.SessionKey, session);
        var mode = _agent.Mode;
        ModeDisplayNames.TryGetValue(mode, out var modeDisplay);
        modeDisplay ??= mode;
        var projectKey = session.ProjectKey ?? _defaultWorkDir;
        var lastActive = session.LastActiveAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        var text = $"**会话ID**: `{sid}`\n" +
                   $"**工作目录**: `{projectKey}`\n" +
                   $"**权限模式**: {modeDisplay}\n" +
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
        if (!TryEnsureProjectDirectory(targetDir, out var createdDirectory, out var error))
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"无法创建目录: `{targetDir}`\n{error}", _cts.Token);
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

            var activeSession = _sessions.GetActive(msg.SessionKey)
                ?? _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
            activeSession.ProjectKey = targetDir;
            _sessions.Save();

            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换工作目录: `{targetDir}`{(createdDirectory ? "\n📁 已自动创建目录。" : "")}\n下次发消息时将在新目录启动。", _cts.Token);
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

                   `/new [--project 路径]` - 开始全新会话
                   `/stop` - 中断当前正在运行的回复
                   `/clear` - 清除当前对话上下文
                   `/continue` - 继续最近一次 Claude Code 会话
                   `/resume` - 列出当前目录的历史会话
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

    // ─── 会话状态管理 ────────────────────────────────────────────

    private async Task<StateStartResult> GetOrCreateStateAsync(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var existing) && existing.AgentSession is not null)
        {
            return new StateStartResult(existing, null);
        }

        _states.TryRemove(sessionKey, out _);

        // 工作目录直接取自 session.ProjectKey
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var connectionNotice = GetConnectionNotice(session.AgentSessionId);

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
        _sessions.Save();
        return new StateStartResult(state, connectionNotice);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private string ResolveProjectPath(string rawPath) => Path.IsPathRooted(rawPath)
        ? Path.GetFullPath(rawPath)
        : Path.GetFullPath(Path.Combine(_defaultWorkDir, rawPath));

    private static string GetConnectionNotice(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? "🎉 客户端已连接"
            : "🎉 客户端已恢复";

    private string GetDisplaySessionId(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var state))
            return state.AgentSession.SessionId;

        return session.AgentSessionId switch
        {
            "__continue__" => "(待恢复最近会话)",
            { Length: > 0 } sid => sid,
            _ => "(未启动)",
        };
    }

    private static bool TryEnsureProjectDirectory(string targetDir, out bool createdDirectory, out string? error)
    {
        createdDirectory = !Directory.Exists(targetDir);
        error = null;

        try
        {
            Directory.CreateDirectory(targetDir);
            return true;
        }
        catch (Exception ex)
        {
            createdDirectory = false;
            error = ex.Message;
            return false;
        }
    }

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

    private static bool ShouldEmitThinking(IPlatform platform) =>
        !string.Equals(platform.Name, "feishu", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseStreamingPreview(IPlatform platform) =>
        !string.Equals(platform.Name, "feishu", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }

    /// <summary>获取所有活跃会话的状态快照。</summary>
    public IReadOnlyList<SessionStatus> GetActiveStatuses()
    {
        var result = new List<SessionStatus>();
        foreach (var (sessionKey, _) in _sessionLocks)
        {
            var session = _sessions.GetActive(sessionKey);
            if (session is null) continue;

            var isProcessing = _sessionLocks.TryGetValue(sessionKey, out var sem) && sem.CurrentCount == 0;
            result.Add(new SessionStatus(
                sessionKey,
                session.FromName ?? session.From,
                session.Platform,
                session.ProjectKey,
                session.CreatedAt,
                session.LastActiveAt,
                isProcessing));
        }

        return result;
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

            // 清理所有 sessionLock
            foreach (var (_, semaphore) in _sessionLocks)
                semaphore.Dispose();
            _sessionLocks.Clear();
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
        public PendingUserQuestion? PendingUserQuestion { get; set; }
        public bool AutoAllowPermissions { get; set; }
        public bool StopRequested { get; set; }
    }

    private sealed record StateStartResult(InteractiveState State, string? ConnectionNotice);

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

    private sealed class PendingUserQuestion(string requestId, int questionIndex, UserQuestion question)
    {
        private readonly TaskCompletionSource<string> _tcs = new();
        public string RequestId { get; } = requestId;
        public int QuestionIndex { get; } = questionIndex;
        public UserQuestion Question { get; } = question;

        public bool Resolve(string answer)
        {
            if (Question.Options.Count > 0)
            {
                var matched = Question.Options.FirstOrDefault(option =>
                    string.Equals(option.Label, answer, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                    answer = matched.Label;
            }

            return _tcs.TrySetResult(answer);
        }

        public bool ResolveOptionIndex(int index)
        {
            if (index < 0 || index >= Question.Options.Count)
                return false;

            return _tcs.TrySetResult(Question.Options[index].Label);
        }

        public async Task<string> WaitAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
            return await _tcs.Task;
        }
    }
}
