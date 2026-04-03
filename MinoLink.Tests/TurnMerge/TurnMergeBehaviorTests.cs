using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Core.TurnMerge;

namespace MinoLink.Tests.TurnMerge;

public sealed class TurnMergeBehaviorTests
{
    [Fact]
    public async Task FirstPlainMessage_ShouldBufferBeforeSendingToAgent()
    {
        await using var context = await TestEngineContext.CreateAsync();

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");

        await Task.Delay(300);
        Assert.Empty(context.ClaudeAgent.SendCalls);

        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1, timeoutMs: 4000);

        var send = Assert.Single(context.ClaudeAgent.SendCalls);
        Assert.Equal("用户当前请求：\n帮我看下这个报错", send.Content);
    }

    [Fact]
    public async Task SupplementaryMessages_WithinMergeWindow_ShouldBeMergedIntoSingleTurn()
    {
        await using var context = await TestEngineContext.CreateAsync();

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await Task.Delay(500);
        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");

        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1, timeoutMs: 4000);

        var send = Assert.Single(context.ClaudeAgent.SendCalls);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            send.Content);
    }

    [Fact]
    public async Task MergedTurnPrompt_ShouldKeepLatestSupplementStructure()
    {
        await using var context = await TestEngineContext.CreateAsync();

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await Task.Delay(300);
        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");
        await Task.Delay(300);
        await context.Platform.SendMessageAsync(context.SessionKey, "日志在附件里");

        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1, timeoutMs: 4500);

        var send = Assert.Single(context.ClaudeAgent.SendCalls);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里\n- 日志在附件里",
            send.Content);
    }

    [Fact]
    public async Task RunningTurn_WhenSupplementArrives_ShouldCancelCurrentExecutionAndRerunWithMergedPrompt()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            blockFirstSendUntilCancelled: true);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");

        await context.WaitForAsync(() => context.ClaudeAgent.InterruptCallCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.CancelledCalls.Count == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 2, timeoutMs: 4000);

        Assert.Equal(0, context.ClaudeAgent.SessionDisposeCount);
        Assert.Equal("用户当前请求：\n帮我看下这个报错", context.ClaudeAgent.SendCalls[0].Content);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            context.ClaudeAgent.SendCalls[1].Content);
    }

    [Fact]
    public async Task RunningTurn_WithMultipleSupplements_ShouldDebounceIntoSingleRerun()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(180),
            },
            blockFirstSendUntilCancelled: true);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");
        await Task.Delay(60);
        await context.Platform.SendMessageAsync(context.SessionKey, "日志在附件里");

        await context.WaitForAsync(() => context.ClaudeAgent.InterruptCallCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.CancelledCalls.Count == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 2, timeoutMs: 4000);
        await Task.Delay(300);

        Assert.Equal(2, context.ClaudeAgent.SendCalls.Count);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里\n- 日志在附件里",
            context.ClaudeAgent.SendCalls[1].Content);
    }

    [Fact]
    public async Task RunningTurn_WhenSupplementArrives_ShouldKeepCurrentSessionAfterInterrupt()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            blockFirstSendUntilCancelled: true);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");

        await context.WaitForAsync(() => context.ClaudeAgent.InterruptCallCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SessionDisposeCount == 0, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 2, timeoutMs: 4000);

        Assert.Equal(0, context.ClaudeAgent.SessionDisposeCount);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            context.ClaudeAgent.SendCalls[1].Content);
    }

    [Fact]
    public async Task RunningTurn_WhenProtocolInterruptFails_ShouldFallbackToDisposeBeforeRerun()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            blockFirstSendUntilCancelled: true,
            interruptSucceeds: false);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "是在 docker 里");

        await context.WaitForAsync(() => context.ClaudeAgent.InterruptCallCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SessionDisposeCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 2, timeoutMs: 4000);

        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            context.ClaudeAgent.SendCalls[1].Content);
    }

    [Fact]
    public async Task StopCommand_DuringBuffering_ShouldClearPendingTurnBeforeExecutionStarts()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(500),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            });

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await Task.Delay(100);
        await context.Platform.SendMessageAsync(context.SessionKey, "/stop");
        await Task.Delay(700);

        Assert.Empty(context.ClaudeAgent.SendCalls);
    }

    [Fact]
    public async Task StopCommand_DuringRunningTurn_ShouldInterruptWithoutDisposingSession()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(80),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            blockFirstSendUntilCancelled: true);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1, timeoutMs: 3000);

        await context.Platform.SendMessageAsync(context.SessionKey, "/stop");

        await context.WaitForAsync(() => context.ClaudeAgent.InterruptCallCount == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.CancelledCalls.Count == 1, timeoutMs: 3000);
        await context.WaitForAsync(() => context.ClaudeAgent.SessionDisposeCount == 0, timeoutMs: 3000);

        Assert.Contains(context.Platform.Replies, reply => reply.Contains("已停止当前回复"));
        Assert.Equal(0, context.ClaudeAgent.SessionDisposeCount);

        await context.Platform.SendMessageAsync(context.SessionKey, "继续分析");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 2, timeoutMs: 3000);
        Assert.Equal("用户当前请求：\n继续分析", context.ClaudeAgent.SendCalls[1].Content);
    }

    [Fact]
    public async Task PendingPermissionReply_ShouldBypassTurnMergeAndRespondToCurrentSession()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(120),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            scriptMode: AgentScriptMode.PermissionRequestOnFirstSend);

        await context.Platform.SendMessageAsync(context.SessionKey, "执行危险命令");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);
        await context.WaitForAsync(() => context.ClaudeAgent.PermissionRequests.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "allow");
        await context.WaitForAsync(() => context.ClaudeAgent.PermissionResponses.Count == 1, timeoutMs: 3000);

        Assert.Single(context.ClaudeAgent.SendCalls);
        Assert.True(context.ClaudeAgent.PermissionResponses[0].Allow);
    }

    [Fact]
    public async Task PendingUserQuestionReply_ShouldBypassTurnMergeAndUseCurrentPromptFlow()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(120),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            },
            scriptMode: AgentScriptMode.UserQuestionOnFirstSend);

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我继续");
        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1);
        await context.WaitForAsync(() => context.ClaudeAgent.UserQuestions.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "docker");
        await context.WaitForAsync(() => context.ClaudeAgent.PermissionResponses.Count == 1, timeoutMs: 3000);

        Assert.Single(context.ClaudeAgent.SendCalls);
    }

    [Fact]
    public async Task TextAndAttachments_ShouldMergeIntoSameTurnSnapshot()
    {
        await using var context = await TestEngineContext.CreateAsync(
            options: new TurnMergeOptions
            {
                InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
            });

        await context.Platform.SendMessageAsync(context.SessionKey, "帮我看下这个报错");
        await Task.Delay(50);
        await context.Platform.SendMessageAsync(
            context.SessionKey,
            "日志在附件里",
            [
                new MessageAttachment
                {
                    Kind = MessageAttachmentKind.File,
                    Name = "error.log",
                    LocalPath = "C:\\temp\\error.log",
                    SourcePlatform = "feishu",
                    SourceMessageId = "msg-1",
                }
            ]);

        await context.WaitForAsync(() => context.ClaudeAgent.SendCalls.Count == 1, timeoutMs: 3000);

        var send = Assert.Single(context.ClaudeAgent.SendCalls);
        Assert.Equal(1, send.AttachmentCount);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 日志在附件里\n\n附件：\n- error.log",
            send.Content);
    }

    private sealed class TestEngineContext : IAsyncDisposable
    {
        private readonly string _rootDir;

        private TestEngineContext(
            string rootDir,
            string workDir,
            SessionManager sessionManager,
            TestPlatform platform,
            RecordingAgent claudeAgent,
            RecordingAgent codexAgent,
            Engine engine)
        {
            _rootDir = rootDir;
            WorkDir = workDir;
            SessionManager = sessionManager;
            Platform = platform;
            ClaudeAgent = claudeAgent;
            CodexAgent = codexAgent;
            Engine = engine;
        }

        public string SessionKey { get; } = "feishu:test-user";
        public string WorkDir { get; }
        public SessionManager SessionManager { get; }
        public TestPlatform Platform { get; }
        public RecordingAgent ClaudeAgent { get; }
        public RecordingAgent CodexAgent { get; }
        public Engine Engine { get; }

        public static async Task<TestEngineContext> CreateAsync(
            TurnMergeOptions? options = null,
            bool blockFirstSendUntilCancelled = false,
            AgentScriptMode scriptMode = AgentScriptMode.ResultImmediately,
            bool interruptSucceeds = true)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "MinoLink.TurnMergeTests", Guid.NewGuid().ToString("N"));
            var workDir = Path.Combine(rootDir, "workdir");
            var dataDir = Path.Combine(rootDir, "data");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(dataDir);

            var sessionStoragePath = Path.Combine(dataDir, "sessions.json");
            var sessionManager = new SessionManager(sessionStoragePath);
            var platform = new TestPlatform();
            var claudeAgent = new RecordingAgent("claudecode", blockFirstSendUntilCancelled, scriptMode, interruptSucceeds);
            var codexAgent = new RecordingAgent("codex");

            var engine = new Engine(
                "test-project",
                agentType => agentType switch
                {
                    "codex" => codexAgent,
                    _ => claudeAgent,
                },
                [platform],
                workDir,
                sessionManager,
                NullLogger<Engine>.Instance,
                turnMergeOptions: options);

            await engine.StartAsync(CancellationToken.None);

            return new TestEngineContext(rootDir, workDir, sessionManager, platform, claudeAgent, codexAgent, engine);
        }

        public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var start = DateTime.UtcNow;
            while (!condition())
            {
                if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                    throw new TimeoutException("等待测试条件超时。");

                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await ClaudeAgent.DisposeAsync();
            await CodexAgent.DisposeAsync();
            TryDeleteDirectory(_rootDir);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class TestPlatform : IPlatform
    {
        private Func<IPlatform, Message, Task>? _messageHandler;
        public string Name => "feishu";
        public List<string> Replies { get; } = [];

        public Task StartAsync(Func<IPlatform, Message, Task> messageHandler, CancellationToken ct)
        {
            _messageHandler = messageHandler;
            return Task.CompletedTask;
        }

        public Task ReplyAsync(object replyContext, string text, CancellationToken ct)
        {
            lock (Replies)
                Replies.Add(text);
            return Task.CompletedTask;
        }

        public Task SendAsync(object replyContext, string text, CancellationToken ct) => ReplyAsync(replyContext, text, ct);

        public async Task SendMessageAsync(string sessionKey, string content, IReadOnlyList<MessageAttachment>? attachments = null)
        {
            if (_messageHandler is null)
                throw new InvalidOperationException("平台尚未启动。");

            await _messageHandler(this, new Message
            {
                SessionKey = sessionKey,
                From = "user",
                FromName = "User",
                Content = content,
                Attachments = attachments ?? [],
                ReplyContext = new object(),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAgent(
        string name,
        bool blockFirstSendUntilCancelled = false,
        AgentScriptMode scriptMode = AgentScriptMode.ResultImmediately,
        bool interruptSucceeds = true) : IAgent
    {
        private int _sendSequence;

        public string Name => name;
        public string Mode => "default";
        public List<SendCall> SendCalls { get; } = [];
        public List<string> CancelledCalls { get; } = [];
        public List<string> PermissionRequests { get; } = [];
        public List<string> UserQuestions { get; } = [];
        public List<PermissionResponse> PermissionResponses { get; } = [];
        public int InterruptCallCount { get; private set; }
        public int SessionDisposeCount { get; private set; }

        public void SetMode(string mode)
        {
        }

        public Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(new RecordingAgentSession(this, name, blockFirstSendUntilCancelled, scriptMode, interruptSucceeds));

        public Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(new RecordingAgentSession(this, name, blockFirstSendUntilCancelled, scriptMode, interruptSucceeds));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public int NextSendSequence() => Interlocked.Increment(ref _sendSequence);

        public void RecordInterrupt() => InterruptCallCount++;

        public void RecordSessionDispose() => SessionDisposeCount++;
    }

    private sealed class RecordingAgentSession(
        RecordingAgent owner,
        string agentName,
        bool blockFirstSendUntilCancelled,
        AgentScriptMode scriptMode,
        bool interruptSucceeds) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        private readonly TaskCompletionSource<PermissionResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _activeSendCts;

        public string SessionId { get; } = $"{agentName}-session";
        public ChannelReader<AgentEvent> Events => _events.Reader;

        public async Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default)
        {
            owner.SendCalls.Add(new SendCall(content, attachments?.Count ?? 0));
            var sequence = owner.NextSendSequence();

            if (blockFirstSendUntilCancelled && sequence == 1)
            {
                _activeSendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _activeSendCts.Token);
                }
                catch (OperationCanceledException)
                {
                    owner.CancelledCalls.Add(content);
                    throw;
                }
                finally
                {
                    _activeSendCts.Dispose();
                    _activeSendCts = null;
                }
            }

            if (sequence == 1 && scriptMode == AgentScriptMode.PermissionRequestOnFirstSend)
            {
                owner.PermissionRequests.Add(content);
                _ = Task.Run(async () =>
                {
                    _events.Writer.TryWrite(new AgentEvent
                    {
                        Type = AgentEventType.PermissionRequest,
                        RequestId = "perm-1",
                        ToolName = "ReadFile",
                        ToolInput = content,
                        ToolInputRaw = new Dictionary<string, object?> { ["prompt"] = content },
                    });
                    var response = await _responseTcs.Task;
                    owner.PermissionResponses.Add(response);
                    _events.Writer.TryWrite(new AgentEvent
                    {
                        Type = AgentEventType.Result,
                        Content = $"permission:{response.Allow}",
                    });
                    _events.Writer.TryComplete();
                });
                return;
            }

            if (sequence == 1 && scriptMode == AgentScriptMode.UserQuestionOnFirstSend)
            {
                owner.UserQuestions.Add(content);
                _ = Task.Run(async () =>
                {
                    _events.Writer.TryWrite(new AgentEvent
                    {
                        Type = AgentEventType.UserQuestion,
                        RequestId = "ask-1",
                        ToolInputRaw = new Dictionary<string, object?>(),
                        Questions =
                        [
                            new UserQuestion
                            {
                                Header = "补充信息",
                                Question = "运行环境是什么？",
                            }
                        ],
                    });
                    var response = await _responseTcs.Task;
                    owner.PermissionResponses.Add(response);
                    _events.Writer.TryWrite(new AgentEvent
                    {
                        Type = AgentEventType.Result,
                        Content = "question:answered",
                    });
                    _events.Writer.TryComplete();
                });
                return;
            }

            _events.Writer.TryWrite(new AgentEvent
            {
                Type = AgentEventType.Result,
                Content = $"done:{content}",
            });
            _events.Writer.TryComplete();
        }

        public Task<bool> InterruptAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            owner.RecordInterrupt();
            if (interruptSucceeds)
                _activeSendCts?.Cancel();
            return Task.FromResult(interruptSucceeds);
        }

        public Task<bool> ClearAsync(CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default)
        {
            _responseTcs.TrySetResult(response);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.RecordSessionDispose();
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SendCall(string Content, int AttachmentCount);

    private enum AgentScriptMode
    {
        ResultImmediately,
        PermissionRequestOnFirstSend,
        UserQuestionOnFirstSend,
    }
}
