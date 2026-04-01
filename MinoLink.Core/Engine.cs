using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Core.TurnMerge;

namespace MinoLink.Core;

/// <summary>
/// 消息引擎：路由 Platform 消息到 Agent 会话，处理事件流。
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    private const string ClaudeAgentType = "claudecode";
    private const string CodexAgentType = "codex";
    private static readonly TimeSpan AgentInterruptTimeout = TimeSpan.FromMilliseconds(800);

    private readonly record struct AgentDirectiveResult(bool Recognized, bool AgentChanged)
    {
        public static AgentDirectiveResult None => new(false, false);
    }

    private static class StartModes
    {
        public const string Continue = "continue";
        public const string Resume = "resume";
    }

    private readonly string _projectName;
    private readonly Func<string, IAgent> _agentFactory;
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPlatform> _platforms;
    private readonly SessionManager _sessions;
    private readonly ILogger<Engine> _logger;
    private readonly IScreenshotService? _screenshotService;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionTurnCoordinator _turnCoordinator;

    // 默认工作目录
    private readonly string _defaultWorkDir;

    // sessionKey → 交互状态
    private readonly ConcurrentDictionary<string, InteractiveState> _states = new();
    // sessionKey → 锁（保证同一会话串行处理）
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private int _disposeState;

    public Engine(string projectName, Func<string, IAgent> agentFactory, IEnumerable<IPlatform> platforms,
        string defaultWorkDir, SessionManager sessions, ILogger<Engine> logger, IScreenshotService? screenshotService = null,
        TurnMergeOptions? turnMergeOptions = null)
    {
        _projectName = projectName;
        _agentFactory = agentFactory;
        _platforms = platforms.ToList();
        _sessions = sessions;
        _defaultWorkDir = Path.GetFullPath(defaultWorkDir);
        _logger = logger;
        _screenshotService = screenshotService;
        _turnCoordinator = new SessionTurnCoordinator(
            turnMergeOptions ?? new TurnMergeOptions(),
            ExecuteTurnSnapshotAsync,
            InterruptActiveExecutionAsync,
            NullLogger<SessionTurnCoordinator>.Instance);
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
        var directiveResult = TryApplyAgentDirective(session, ref msg);
        if (directiveResult.AgentChanged)
            await DestroyStateAsync(msg.SessionKey);

        if (session.ProjectKey is null)
        {
            session.ProjectKey = _defaultWorkDir;
            _sessions.Save();
        }
        session.LastActiveAt = DateTimeOffset.UtcNow;

        await _turnCoordinator.EnqueueAsync(platform, msg, session, directiveResult.Recognized, _cts.Token);
    }

    /// <summary>消息处理核心逻辑。</summary>
    private async Task ProcessMessageAsync(IPlatform platform, Message msg, SessionRecord session, bool useSelectedAgentForStartup = false, CancellationToken executionCt = default)
    {
        var workDirNotice = EnsureSessionWorkDir(session);
        if (!string.IsNullOrWhiteSpace(workDirNotice))
            await platform.ReplyAsync(msg.ReplyContext, workDirNotice, _cts.Token);

        _logger.LogInformation("[{Platform}] 创建/获取 Agent 会话: {SessionKey}", platform.Name, msg.SessionKey);
        var stateStart = await GetOrCreateStateAsync(msg.SessionKey, session, useSelectedAgentForStartup);
        var state = stateStart.State;
        state.StopRequested = false;
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
                await state.AgentSession.SendAsync(msg.Content, msg.Attachments, executionCt);
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
                await state.AgentSession.SendAsync(msg.Content, msg.Attachments, executionCt);
            }
            _logger.LogInformation("[{Platform}] 消息已发送，开始等待 Agent 事件流...", platform.Name);

            // 处理事件流
            await ProcessEventsAsync(platform, msg.ReplyContext, state, executionCt);
            _logger.LogInformation("[{Platform}] 事件流处理完毕", platform.Name);
        }
        catch (OperationCanceledException) when (executionCt.IsCancellationRequested)
        {
            _logger.LogInformation("[{Platform}] 当前 turn 已取消: {SessionKey}", platform.Name, msg.SessionKey);
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
        var thinkingBuffer = new System.Text.StringBuilder();
        var allowThinkingMessages = ShouldEmitThinking();

        // 流式预览句柄
        object? previewHandle = null;
        var lastPreviewAt = DateTimeOffset.MinValue;
        var updater = ShouldUseStreamingPreview(platform) ? platform as IMessageUpdater : null;

        async Task FlushThinkingAsync()
        {
            if (thinkingBuffer.Length == 0)
                return;

            var thinking = thinkingBuffer.ToString().Trim();
            thinkingBuffer.Clear();
            if (!allowThinkingMessages || string.IsNullOrWhiteSpace(thinking))
                return;

            var thinkingPreview = Truncate(thinking, 300);
            foreach (var chunk in SplitMessage($"💭 {thinkingPreview}", 4000))
                await platform.ReplyAsync(replyContext, chunk, ct);
        }

        // 将 textBuffer 中累积的文本发送出去并清空
        async Task FlushTextAsync(bool normalizeFinalSummary = false)
        {
            if (textBuffer.Length == 0) return;
            var text = textBuffer.ToString();
            textBuffer.Clear();
            var originalText = text;
            if (normalizeFinalSummary && string.Equals(platform.Name, "feishu", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[feishu] Result 原始文本\n<<<RESULT_RAW\n{Text}\nRESULT_RAW>>>", originalText);
                text = NormalizeFinalReplyForFeishu(text);
                _logger.LogInformation("[feishu] Normalize 后文本\n<<<RESULT_NORMALIZED\n{Text}\nRESULT_NORMALIZED>>>", text);
            }

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
                        thinkingBuffer.Append(evt.Content);
                        if (ShouldFlushThinking(thinkingBuffer, evt.Content))
                            await FlushThinkingAsync();
                        break;

                    case AgentEventType.Text:
                        await FlushThinkingAsync();
                        AppendMarkdownChunk(textBuffer, evt.Content);
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
                        await FlushThinkingAsync();
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
                        await FlushThinkingAsync();
                        await FlushTextAsync();
                        await HandlePermissionRequestAsync(platform, replyContext, state, evt, ct);
                        break;

                    case AgentEventType.UserQuestion:
                        await FlushThinkingAsync();
                        await FlushTextAsync();
                        await HandleUserQuestionAsync(platform, replyContext, state, evt, ct);
                        break;

                    case AgentEventType.Result:
                        if (state.StopRequested)
                            return;
                        await FlushThinkingAsync();
                        var flushedText = textBuffer.Length > 0;
                        await FlushTextAsync(normalizeFinalSummary: true);
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
                        if (state.StopRequested)
                            return;
                        await FlushThinkingAsync();
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
        await FlushThinkingAsync();
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
                BuildCompletedStatusMessage("⚠️ Agent 进程已退出，未返回结果。请检查 Agent 日志。", success: false), ct);
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

        if (TryGetQuestionIds(originalInput, out var questionIds))
        {
            foreach (var (index, answer) in answers.OrderBy(entry => entry.Key))
            {
                var key = questionIds.TryGetValue(index, out var questionId)
                    ? questionId
                    : index.ToString();
                answerMap[key] = answer;
            }
        }
        else
        {
            foreach (var (index, answer) in answers.OrderBy(entry => entry.Key))
                answerMap[index.ToString()] = answer;
        }

        updated["answers"] = answerMap;
        return updated;
    }

    private static bool TryGetQuestionIds(Dictionary<string, object?> originalInput, out Dictionary<int, string> questionIds)
    {
        questionIds = new Dictionary<int, string>();
        if (!originalInput.TryGetValue("questions", out var questionsObj) || questionsObj is not JsonElement questionsEl || questionsEl.ValueKind != JsonValueKind.Array)
            return false;

        var index = 0;
        foreach (var questionEl in questionsEl.EnumerateArray())
        {
            if (questionEl.TryGetProperty("id", out var idEl) && !string.IsNullOrWhiteSpace(idEl.GetString()))
                questionIds[index] = idEl.GetString()!;
            index++;
        }

        return questionIds.Count > 0;
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

        var pendingQuestion = state.PendingUserQuestion;

        if (!pendingQuestion.Resolve(msg.Content.Trim()))
        {
            _logger.LogWarning("文本回答重复或过期: sessionKey={SessionKey}, content={Content}",
                msg.SessionKey, msg.Content);
            return true;
        }

        _logger.LogInformation("收到文本问题回答: sessionKey={SessionKey}, requestId={RequestId}, answer={Answer}",
            msg.SessionKey, pendingQuestion.RequestId, msg.Content);

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
            "close" => await CmdCloseAsync(platform, msg),
            "clear" => await CmdClearAsync(platform, msg),
            "resume" => await CmdResumeAsync(platform, msg),
            "switch" => await CmdSwitchAsync(platform, msg, args),
            "continue" => await CmdContinueAsync(platform, msg),
            "current" => await CmdCurrentAsync(platform, msg),
            "mode" => await CmdModeAsync(platform, msg, args),
            "project" => await CmdProjectAsync(platform, msg, args),
            "snap" => await CmdSnapAsync(platform, msg),
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
            session.AgentType = ClaudeAgentType;
            session.AgentSessionId = null;
            session.PendingStartMode = null;
            session.PendingResumeSessionId = null;
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
        var hadTurnRuntime = await _turnCoordinator.ResetAsync(msg.SessionKey);
        if (!_states.TryGetValue(msg.SessionKey, out var state))
        {
            if (hadTurnRuntime)
            {
                await platform.ReplyAsync(msg.ReplyContext, "⏹️ 已停止当前回复。直接发送新消息即可继续。", _cts.Token);
                return true;
            }

            await platform.ReplyAsync(msg.ReplyContext, "当前没有正在运行的回复。", _cts.Token);
            return true;
        }

        state.StopRequested = true;
        _logger.LogInformation("用户请求中断当前回复: sessionKey={SessionKey}", msg.SessionKey);

        await InterruptOrDestroyStateAsync(msg.SessionKey);
        await platform.ReplyAsync(msg.ReplyContext, "⏹️ 已停止当前回复。直接发送新消息即可继续。", _cts.Token);
        return true;
    }

    /// <summary>/clear - 清除当前对话上下文（优先协议级清除，不支持则杀进程重建）。</summary>
    private async Task<bool> CmdClearAsync(IPlatform platform, Message msg)
    {
        // 先中断 TurnCoordinator 中可能排队的消息
        await _turnCoordinator.ResetAsync(msg.SessionKey);

        if (_states.TryGetValue(msg.SessionKey, out var state))
        {
            try
            {
                if (await state.AgentSession.ClearAsync(_cts.Token))
                {
                    var session = _sessions.GetActive(msg.SessionKey);
                    if (session is not null)
                    {
                        session.AgentSessionId = state.AgentSession.SessionId;
                        _sessions.Save();
                    }

                    _logger.LogInformation("协议级清除上下文成功: sessionKey={SessionKey}", msg.SessionKey);
                    await platform.ReplyAsync(msg.ReplyContext, "🗑️ 已清除对话上下文（协议级）。", _cts.Token);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "协议级清除上下文失败，回退杀进程: sessionKey={SessionKey}", msg.SessionKey);
            }
        }

        // fallback：杀进程
        await DestroyInteractiveStateAsync(msg.SessionKey);
        await platform.ReplyAsync(msg.ReplyContext, "🗑️ 已清除对话上下文。", _cts.Token);
        return true;
    }

    /// <summary>/close - 关闭 Agent 终端进程并清除会话记录，下次发消息将启动全新会话。</summary>
    private async Task<bool> CmdCloseAsync(IPlatform platform, Message msg)
    {
        await _turnCoordinator.ResetAsync(msg.SessionKey);
        await DestroyInteractiveStateAsync(msg.SessionKey);

        var session = _sessions.GetActive(msg.SessionKey);
        if (session is not null)
        {
            session.AgentSessionId = null;
            session.PendingStartMode = null;
            session.PendingResumeSessionId = null;
            _sessions.Save();
        }

        await platform.ReplyAsync(msg.ReplyContext,
            "🔌 已关闭 Agent 终端进程并清除会话。\n下次发送消息将启动全新会话。", _cts.Token);
        return true;
    }

    /// <summary>/continue - 继续当前工作目录最近一次 Agent 会话。</summary>
    /// <summary>/continue - 继续当前工作目录最近一次 Agent 会话。</summary>
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
            session.AgentSessionId = null;
            session.PendingStartMode = StartModes.Continue;
            session.PendingResumeSessionId = null;
            _sessions.Save();

            var agentDisplay = GetAgentDisplayName(session.AgentType);
            await platform.ReplyAsync(msg.ReplyContext,
                $"🔄 将继续最近一次 {agentDisplay} 会话。\n发送任意消息继续对话。", _cts.Token);
        }
        finally
        {
            sessionLock.Release();
        }
        return true;
    }

    /// <summary>/resume - 列出当前工作目录的所有原生会话。</summary>
    private async Task<bool> CmdResumeAsync(IPlatform platform, Message msg)
    {
        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var nativeSessions = GetNativeSessions(session.AgentType, workDir);

        if (nativeSessions.Count == 0)
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"当前目录 `{workDir}` 暂无历史会话记录。", _cts.Token);
            return true;
        }

        var currentId = session.PendingResumeSessionId ?? session.AgentSessionId;
        var currentIdInList = currentId is not null && nativeSessions.Any(s => s.SessionId == currentId);
        var sb = new System.Text.StringBuilder();
        var agentDisplay = GetAgentDisplayName(session.AgentType);
        sb.AppendLine($"**{agentDisplay} 会话列表**");
        sb.AppendLine();
        for (var i = 0; i < nativeSessions.Count; i++)
        {
            var s = nativeSessions[i];
            var marker = currentIdInList && s.SessionId == currentId ? " ▶" : "  ";
            var lastActive = s.LastActive.ToString("MM-dd HH:mm");
            var summary = string.IsNullOrWhiteSpace(s.Summary) ? "(无摘要)" : s.Summary;
            sb.AppendLine($"{marker} **{i + 1}.** {lastActive}  {summary}");
        }

        if (!currentIdInList && currentId is not null)
            sb.AppendLine($"\n ▶ 当前活跃会话: `{currentId[..Math.Min(12, currentId.Length)]}…` (新建，尚无历史记录)");

        sb.AppendLine();
        sb.AppendLine("使用 `/switch <序号>` 恢复指定会话");

        await platform.ReplyAsync(msg.ReplyContext, sb.ToString(), _cts.Token);
        return true;
    }

    /// <summary>/switch <序号> - 切换到指定的原生会话。</summary>
    private async Task<bool> CmdSwitchAsync(IPlatform platform, Message msg, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 1)
        {
            await platform.ReplyAsync(msg.ReplyContext, "用法: `/switch <序号>`\n使用 `/resume` 查看可用会话。", _cts.Token);
            return true;
        }

        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var nativeSessions = GetNativeSessions(session.AgentType, workDir);

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
            session.AgentSessionId = null;
            session.PendingStartMode = StartModes.Resume;
            session.PendingResumeSessionId = target.SessionId;
            _sessions.Save();

            var lastActive = target.LastActive.ToString("MM-dd HH:mm");
            var summary = string.IsNullOrWhiteSpace(target.Summary) ? "(无摘要)" : target.Summary;
            var agentDisplay = GetAgentDisplayName(session.AgentType);
            await platform.ReplyAsync(msg.ReplyContext,
                $"✅ 已切换到 {agentDisplay} 会话 **{index}** ({lastActive})\n{summary}\n\n发送任意消息继续对话。", _cts.Token);
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
        var mode = GetAgent(session).Mode;
        var modeLabel = GetModeFieldLabel(session.AgentType);
        var modeDisplay = GetModeDisplayName(session.AgentType, mode);
        var projectKey = session.ProjectKey ?? _defaultWorkDir;
        var lastActive = session.LastActiveAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        var agentDisplay = GetAgentDisplayName(session.AgentType);

        var sandboxDisplay = GetSandboxDisplayName(session.AgentType, mode);
        var text = $"**会话ID**: `{sid}`\n" +
                   $"**Agent**: {agentDisplay}\n" +
                   $"**工作目录**: `{projectKey}`\n" +
                   $"**{modeLabel}**: {modeDisplay}\n" +
                   (string.IsNullOrWhiteSpace(sandboxDisplay) ? string.Empty : $"**Sandbox**: {sandboxDisplay}\n") +
                   $"**最后活跃**: {lastActive}";

        await platform.ReplyAsync(msg.ReplyContext, text, _cts.Token);
        return true;
    }

    /// <summary>/mode [模式] - 查看或切换权限模式。</summary>
    private async Task<bool> CmdModeAsync(IPlatform platform, Message msg, string[] args)
    {
        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        var agent = GetAgent(session);

        // 无参数：显示当前模式
        if (args.Length == 0)
        {
            var current = agent.Mode;
            var display = GetModeDisplayName(session.AgentType, current);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"**当前模式**: {display}");
            sb.AppendLine();
            sb.AppendLine("**可用模式**:");
            AppendModeOptions(sb, session.AgentType);
            sb.AppendLine();
            sb.AppendLine("使用 `/mode <模式名>` 切换");

            await platform.ReplyAsync(msg.ReplyContext, sb.ToString(), _cts.Token);
            return true;
        }

        var targetInput = args[0].ToLowerInvariant();
        if (!ModeMap.TryGetValue(targetInput, out var targetMode))
        {
            await platform.ReplyAsync(msg.ReplyContext,
                $"未知模式: `{args[0]}`\n可用: `default`, `acceptedits`, `plan`, `yolo`", _cts.Token);
            return true;
        }

        var effectiveCurrentMode = GetEffectiveModeForAgent(session.AgentType, agent.Mode);
        var effectiveTargetMode = GetEffectiveModeForAgent(session.AgentType, targetMode);
        if (string.Equals(effectiveTargetMode, effectiveCurrentMode, StringComparison.OrdinalIgnoreCase))
        {
            var d = GetModeDisplayName(session.AgentType, effectiveCurrentMode);
            await platform.ReplyAsync(msg.ReplyContext, $"当前已经是 {d} 模式。", _cts.Token);
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
            agent.SetMode(targetMode);
            await DestroyStateAsync(msg.SessionKey);

            var actualMode = agent.Mode;
            var displayName = GetModeDisplayName(session.AgentType, actualMode);
            var sandboxDisplay = GetSandboxDisplayName(session.AgentType, actualMode);
            await platform.ReplyAsync(msg.ReplyContext,
                string.IsNullOrWhiteSpace(sandboxDisplay)
                    ? $"✅ 已切换到 **{displayName}** 模式\n下次发消息时将按当前 Agent 设置启动。"
                    : $"✅ 已切换到 **{displayName}** 模式\nSandbox: `{sandboxDisplay}`\n下次发消息时将按当前 Agent 设置启动。",
                _cts.Token);
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

    /// <summary>/snap - 截取当前活动窗口并发送到当前会话。</summary>
    private async Task<bool> CmdSnapAsync(IPlatform platform, Message msg)
    {
        if (_screenshotService is null)
        {
            await platform.ReplyAsync(msg.ReplyContext, "当前环境未启用截图服务。", _cts.Token);
            return true;
        }

        if (platform is not IImageSender imageSender)
        {
            await platform.ReplyAsync(msg.ReplyContext, $"当前平台 `{platform.Name}` 不支持发送图片。", _cts.Token);
            return true;
        }

        try
        {
            var imagePath = await _screenshotService.CaptureActiveWindowAsync(_cts.Token);
            await imageSender.SendImageAsync(msg.ReplyContext, imagePath, _cts.Token);
            await platform.ReplyAsync(msg.ReplyContext, $"✅ 截图已发送: `{imagePath}`", _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "截屏发送失败: sessionKey={SessionKey}", msg.SessionKey);
            await platform.ReplyAsync(msg.ReplyContext, $"截屏发送失败: {ex.Message}", _cts.Token);
        }

        return true;
    }

    /// <summary>/help - 显示可用命令。</summary>
    private async Task<bool> CmdHelpAsync(IPlatform platform, Message msg)
    {
        var text = """
                   **MinoLink 命令**

                   `/new [--project 路径]` - 开始全新会话
                   `/stop` - 中断当前回复（协议中断，保留会话）
                   `/close` - 关闭 Agent 终端进程（彻底销毁）
                   `/clear` - 清除当前对话上下文
                   `/continue` - 继续最近一次会话
                   `/resume` - 列出当前目录的历史会话
                   `/switch <序号>` - 切换到指定会话
                   `/current` - 查看当前会话信息
                   `/project [路径]` - 查看/切换工作目录
                   `/snap` - 截取当前活动窗口并发送
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
        await _turnCoordinator.ResetAsync(sessionKey);
        await DestroyInteractiveStateAsync(sessionKey);
    }

    private async Task DestroyInteractiveStateAsync(string sessionKey)
    {
        if (_states.TryRemove(sessionKey, out var state))
        {
            state.StopRequested = true;
            _logger.LogInformation("销毁会话状态: {SessionKey}", sessionKey);
            await state.AgentSession.DisposeAsync();
        }
    }

    private async Task InterruptOrDestroyStateAsync(string sessionKey)
    {
        if (!_states.TryGetValue(sessionKey, out var state))
            return;

        state.StopRequested = true;

        try
        {
            if (await state.AgentSession.InterruptAsync(AgentInterruptTimeout, _cts.Token))
            {
                _logger.LogInformation("Agent 协议中断成功: sessionKey={SessionKey}", sessionKey);
                return;
            }

            _logger.LogWarning("Agent 协议中断未确认，回退销毁会话: sessionKey={SessionKey}", sessionKey);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent 协议中断异常，回退销毁会话: sessionKey={SessionKey}", sessionKey);
        }

        await DestroyInteractiveStateAsync(sessionKey);
    }

    private Task InterruptActiveExecutionAsync(string sessionKey) => InterruptOrDestroyStateAsync(sessionKey);

    private Task ExecuteTurnSnapshotAsync(TurnExecutionRequest request, CancellationToken ct)
    {
        var message = new Message
        {
            SessionKey = request.Snapshot.SessionKey,
            From = request.Snapshot.From,
            FromName = request.Snapshot.FromName,
            Content = request.Snapshot.PromptText,
            Attachments = request.Snapshot.Attachments,
            ReplyContext = request.Snapshot.ReplyContext,
            IsGroup = request.Snapshot.IsGroup,
        };

        return ProcessMessageAsync(request.Platform, message, request.Session, request.UseSelectedAgentForStartup, ct);
    }

    // ─── 会话状态管理 ────────────────────────────────────────────

    private async Task<StateStartResult> GetOrCreateStateAsync(string sessionKey, SessionRecord session, bool useSelectedAgentForStartup = false)
    {
        if (_states.TryGetValue(sessionKey, out var existing) && existing.AgentSession is not null)
        {
            return new StateStartResult(existing, null);
        }

        _states.TryRemove(sessionKey, out _);

        // 工作目录直接取自 session.ProjectKey
        var workDir = session.ProjectKey ?? _defaultWorkDir;
        var connectionNotice = GetConnectionNotice(session.PendingStartMode);

        var startupAgentType = ResolveAgentTypeForStartup(session, useSelectedAgentForStartup);
        session.AgentType = startupAgentType;

        IAgentSession agentSession;
        if (string.Equals(session.PendingStartMode, StartModes.Continue, StringComparison.Ordinal))
        {
            agentSession = await GetAgent(startupAgentType).ContinueSessionAsync(workDir, _cts.Token);
        }
        else
        {
            var resumeSessionId = string.Equals(session.PendingStartMode, StartModes.Resume, StringComparison.Ordinal)
                ? session.PendingResumeSessionId ?? string.Empty
                : string.Empty;
            agentSession = await GetAgent(startupAgentType).StartSessionAsync(resumeSessionId, workDir, _cts.Token);
        }

        session.AgentSessionId = agentSession.SessionId;
        session.PendingStartMode = null;
        session.PendingResumeSessionId = null;
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

    private AgentDirectiveResult TryApplyAgentDirective(SessionRecord session, ref Message msg)
    {
        var content = msg.Content.TrimStart();
        if (!content.StartsWith('#'))
            return AgentDirectiveResult.None;

        var firstSpace = content.IndexOf(' ');
        var directive = (firstSpace >= 0 ? content[..firstSpace] : content).Trim();
        var remaining = firstSpace >= 0 ? content[(firstSpace + 1)..].TrimStart() : string.Empty;

        var targetAgent = directive.ToLowerInvariant() switch
        {
            "#claude" => ClaudeAgentType,
            "#codex" => CodexAgentType,
            _ => null,
        };

        if (targetAgent is null)
            return AgentDirectiveResult.None;

        if (string.IsNullOrWhiteSpace(remaining) && string.IsNullOrWhiteSpace(session.AgentSessionId))
            remaining = targetAgent == CodexAgentType ? "开始一个新的 Codex 会话。" : "开始一个新的 Claude 会话。";

        var changed = !string.Equals(session.AgentType, targetAgent, StringComparison.OrdinalIgnoreCase);
        session.AgentType = targetAgent;
        session.AgentSessionId = null;
        session.PendingStartMode = null;
        session.PendingResumeSessionId = null;
        _sessions.Save();

        msg = new Message
        {
            SessionKey = msg.SessionKey,
            From = msg.From,
            FromName = msg.FromName,
            Content = remaining,
            Attachments = msg.Attachments,
            ReplyContext = msg.ReplyContext,
            IsGroup = msg.IsGroup,
            ReceivedAt = msg.ReceivedAt,
        };

        return new AgentDirectiveResult(true, changed);
    }

    private IAgent GetAgent(string agentType)
    {
        agentType = NormalizeAgentType(agentType);
        if (_agents.TryGetValue(agentType, out var existing))
            return existing;

        var created = _agentFactory(agentType);
        _agents[agentType] = created;
        return created;
    }

    private IAgent GetAgent(SessionRecord session) => GetAgent(session.AgentType);

    private static List<NativeSessionInfo> GetNativeSessions(string agentType, string workDir)
    {
        return agentType switch
        {
            CodexAgentType => CodexNativeSession.GetSessions(workDir),
            _ => ClaudeNativeSession.GetSessions(workDir),
        };
    }

    private static string GetConnectionNotice(string? pendingStartMode) =>
        IsRecoveryStartMode(pendingStartMode) ? "🎉 客户端已恢复" : "🎉 客户端已连接";

    private static string NormalizeAgentType(string? agentType) =>
        string.Equals(agentType, CodexAgentType, StringComparison.OrdinalIgnoreCase)
            ? CodexAgentType
            : ClaudeAgentType;

    private static bool IsRecoveryStartMode(string? pendingStartMode) =>
        string.Equals(pendingStartMode, StartModes.Continue, StringComparison.Ordinal) ||
        string.Equals(pendingStartMode, StartModes.Resume, StringComparison.Ordinal);

    private static string ResolveAgentTypeForStartup(SessionRecord session, bool useSelectedAgentForStartup)
    {
        var selectedAgentType = NormalizeAgentType(session.AgentType);
        if (IsRecoveryStartMode(session.PendingStartMode))
            return selectedAgentType;

        return useSelectedAgentForStartup
            ? selectedAgentType
            : ClaudeAgentType;
    }

    private static string GetAgentDisplayName(string? agentType) =>
        NormalizeAgentType(agentType) == CodexAgentType ? "Codex" : "Claude";

    private static bool IsCodexAgent(string? agentType) =>
        NormalizeAgentType(agentType) == CodexAgentType;

    private static string GetModeFieldLabel(string? agentType) =>
        IsCodexAgent(agentType) ? "审批模式" : "权限模式";

    private static string GetModeDisplayName(string? agentType, string mode)
    {
        if (TryGetCodexModePresentation(agentType, mode, out var displayName, out _))
            return displayName;

        ModeDisplayNames.TryGetValue(mode, out var display);
        return display ?? mode;
    }

    private static string GetEffectiveModeForAgent(string? agentType, string mode)
    {
        if (!IsCodexAgent(agentType))
            return mode;

        return NormalizeCodexMode(mode);
    }

    private static void AppendModeOptions(System.Text.StringBuilder sb, string? agentType)
    {
        var options = IsCodexAgent(agentType)
            ? new[]
            {
                "  `default` - 映射到 `on-request + workspace-write`",
                "  `acceptedits` - 当前等价于 `default`，同样映射到 `on-request + workspace-write`",
                "  `plan` - 映射到 `untrusted + read-only`",
                "  `yolo` - 映射到 `never + danger-full-access`",
            }
            : new[]
            {
                "  `default` - 默认 (每次操作需确认)",
                "  `acceptedits` - 自动接受编辑",
                "  `plan` - 规划模式 (只读)",
                "  `yolo` - 自动批准所有操作",
            };

        foreach (var option in options)
            sb.AppendLine(option);
    }

    private static string GetSandboxDisplayName(string? agentType, string mode)
    {
        if (TryGetCodexModePresentation(agentType, mode, out _, out var sandboxDisplay))
            return sandboxDisplay;

        return string.Empty;
    }

    private static string NormalizeCodexMode(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "acceptedits" or "accept-edits" or "accept_edits" or "default" or "on-request" => "on-request",
            "plan" or "untrusted" => "untrusted",
            "bypasspermissions" or "bypass-permissions" or "yolo" or "auto" or "never" => "never",
            _ => mode,
        };

    private static bool TryGetCodexModePresentation(string? agentType, string mode, out string displayName, out string sandboxDisplay)
    {
        if (!IsCodexAgent(agentType))
        {
            displayName = string.Empty;
            sandboxDisplay = string.Empty;
            return false;
        }

        var effectiveMode = NormalizeCodexMode(mode);
        displayName = effectiveMode switch
        {
            "on-request" => "on-request (按需审批)",
            "untrusted" => "untrusted (只读规划)",
            "never" => "never (自动批准)",
            _ => effectiveMode,
        };

        sandboxDisplay = effectiveMode switch
        {
            "untrusted" => "read-only",
            "never" => "danger-full-access",
            _ => "workspace-write",
        };

        return true;
    }

    private string GetDisplaySessionId(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var state))
            return state.AgentSession.SessionId;

        return session.PendingStartMode switch
        {
            StartModes.Continue => "(待恢复最近会话)",
            StartModes.Resume when !string.IsNullOrWhiteSpace(session.PendingResumeSessionId) => session.PendingResumeSessionId!,
            _ => session.AgentSessionId switch
            {
                { Length: > 0 } sid => sid,
                _ => "(未启动)",
            },
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

    private static bool ShouldEmitThinking() => true;

    private static bool ShouldFlushThinking(System.Text.StringBuilder thinkingBuffer, string latestChunk)
    {
        if (thinkingBuffer.Length >= 160)
            return true;

        if (string.IsNullOrWhiteSpace(latestChunk))
            return false;

        var trimmed = latestChunk.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        var lastChar = trimmed[^1];
        return lastChar is '。' or '！' or '？' or '.' or '!' or '?' or '\n';
    }

    private static bool ShouldUseStreamingPreview(IPlatform platform) =>
        !string.Equals(platform.Name, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFinalReplyForFeishu(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(?<=[^\n])(?=```)", "\n");

        var lines = new List<string>();
        var inCodeFence = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (!inCodeFence)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*#{1,6}\s*$"))
                    continue;

                line = System.Text.RegularExpressions.Regex.Replace(line, @"^(#{1,6})(?=\S)", "$1 ");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^-(?=\S)", "- ");
            }

            lines.Add(line);
            if (CountCodeFenceMarkers(line) % 2 == 1)
                inCodeFence = !inCodeFence;
        }

        var compactedLines = CompactFinalReplyBlankLines(lines).ToList();
        var promptCollapsedLines = CollapsePromptAnswerBlankLines(compactedLines);
        return string.Join("\n", promptCollapsedLines).Trim();
    }

    private static IEnumerable<string> CompactFinalReplyBlankLines(IEnumerable<string> lines)
    {
        var inCodeFence = false;
        var previousBlank = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (!inCodeFence && string.IsNullOrWhiteSpace(line))
            {
                if (previousBlank)
                    continue;

                previousBlank = true;
                yield return string.Empty;
                continue;
            }

            previousBlank = false;
            yield return line;

            if (CountCodeFenceMarkers(line) % 2 == 1)
                inCodeFence = !inCodeFence;
        }
    }

    private static IEnumerable<string> CollapsePromptAnswerBlankLines(IReadOnlyList<string> lines)
    {
        var inCodeFence = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                yield return line;
                inCodeFence = !inCodeFence;
                continue;
            }

            if (!inCodeFence
                && string.IsNullOrWhiteSpace(line)
                && i > 0
                && i + 1 < lines.Count
                && EndsWithPromptLeadIn(lines[i - 1])
                && IsStandaloneShortAnswer(lines[i + 1]))
            {
                continue;
            }

            yield return line;
        }
    }

    private static bool EndsWithPromptLeadIn(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith('：') || trimmed.EndsWith(':');
    }

    private static bool IsStandaloneShortAnswer(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 20)
            return false;

        if (trimmed.StartsWith("```", StringComparison.Ordinal)
            || trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.)]\s"))
        {
            return false;
        }

        return !trimmed.Contains('\n')
               && !trimmed.Contains('：')
               && !trimmed.Contains(':');
    }

    private static void AppendMarkdownChunk(System.Text.StringBuilder buffer, string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        var normalizedChunk = TrimOverlappingPrefix(buffer, chunk);
        if (string.IsNullOrEmpty(normalizedChunk))
            return;

        if (buffer.Length > 0 && NeedsMarkdownSeparator(buffer, normalizedChunk))
            buffer.Append('\n');

        buffer.Append(normalizedChunk);
    }

    private static bool NeedsMarkdownSeparator(System.Text.StringBuilder buffer, string chunk)
    {
        var previous = GetTrailingText(buffer);
        var current = chunk.TrimStart();
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            return false;

        return EndsWithMarkdownBoundary(previous) && StartsWithMarkdownBoundary(current);
    }

    private static string TrimOverlappingPrefix(System.Text.StringBuilder buffer, string chunk)
    {
        var trailing = GetTrailingText(buffer, 256);
        if (string.IsNullOrEmpty(trailing) || string.IsNullOrEmpty(chunk))
            return chunk;

        var maxOverlap = Math.Min(trailing.Length, chunk.Length);
        for (var overlap = maxOverlap; overlap >= 8; overlap--)
        {
            if (!trailing.EndsWith(chunk[..overlap], StringComparison.Ordinal))
                continue;

            return chunk[overlap..];
        }

        return chunk;
    }

    private static string GetTrailingText(System.Text.StringBuilder buffer, int maxLength = 64)
    {
        var length = Math.Min(buffer.Length, maxLength);
        return length <= 0 ? string.Empty : buffer.ToString(buffer.Length - length, length).TrimEnd();
    }

    private static bool EndsWithMarkdownBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            return true;

        var lastChar = trimmed[^1];
        return lastChar is ':' or '：' or '\n';
    }

    private static bool StartsWithMarkdownBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            return true;
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
            return true;
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            return true;

        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.)]\s*") ||
               trimmed.StartsWith("产物：", StringComparison.Ordinal) ||
               trimmed.StartsWith("关键结构", StringComparison.Ordinal) ||
               trimmed.StartsWith("构建结果", StringComparison.Ordinal) ||
               trimmed.StartsWith("并且存在", StringComparison.Ordinal) ||
               trimmed.StartsWith("如果你", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var remaining = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var inCodeFence = false;

        while (remaining.Length > 0)
        {
            var prefix = inCodeFence ? "```\n" : string.Empty;
            var reserveForClosingFence = inCodeFence ? 4 : 0;
            var budget = Math.Max(1, maxLength - prefix.Length - reserveForClosingFence);

            var take = remaining.Length <= budget
                ? remaining.Length
                : FindSplitPosition(remaining, budget);

            var body = remaining[..take].TrimEnd();
            remaining = take >= remaining.Length
                ? string.Empty
                : remaining[take..].TrimStart('\n');

            var endInCodeFence = inCodeFence ^ (CountCodeFenceMarkers(body) % 2 == 1);
            var suffix = endInCodeFence ? "\n```" : string.Empty;
            yield return prefix + body + suffix;
            inCodeFence = endInCodeFence;
        }
    }

    private static int FindSplitPosition(string text, int budget)
    {
        foreach (var separator in new[] { "\n\n", "\n", " ", "，", "。" })
        {
            var index = text.LastIndexOf(separator, budget, StringComparison.Ordinal);
            if (index >= Math.Max(1, budget / 2))
                return index + separator.Length;
        }

        return budget;
    }

    private static int CountCodeFenceMarkers(string text)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf("```", startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += 3;
        }

        return count;
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
                session.AgentType,
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

            foreach (var agent in _agents.Values)
                await agent.DisposeAsync();

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
