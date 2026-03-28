using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Tests.AgentRouting;

public sealed class AgentRoutingBehaviorTests
{
    [Fact]
    public async Task NewSession_NormalMessage_ShouldStartClaudeByDefault()
    {
        await using var context = await TestEngineContext.CreateAsync();

        await context.Platform.SendMessageAsync(context.SessionKey, "hello");

        await context.WaitForAsync(() => context.ClaudeAgent.StartCalls.Count == 1);

        Assert.Single(context.ClaudeAgent.StartCalls);
        Assert.Empty(context.CodexAgent.StartCalls);
    }

    [Fact]
    public async Task ExplicitCodexDirective_ShouldStartCodex()
    {
        await using var context = await TestEngineContext.CreateAsync();

        await context.Platform.SendMessageAsync(context.SessionKey, "#codex hello");

        await context.WaitForAsync(() => context.CodexAgent.StartCalls.Count == 1);

        Assert.Single(context.CodexAgent.StartCalls);
        Assert.Empty(context.ClaudeAgent.StartCalls);
    }

    [Fact]
    public async Task PersistedCodexSelection_WithoutRecoveryCommand_ShouldStillDefaultToClaude()
    {
        await using var context = await TestEngineContext.CreateAsync();
        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "codex";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();

        await context.Platform.SendMessageAsync(context.SessionKey, "plain message");

        await context.WaitForAsync(() => context.ClaudeAgent.StartCalls.Count == 1);

        Assert.Single(context.ClaudeAgent.StartCalls);
        Assert.Empty(context.CodexAgent.StartCalls);
    }

    [Fact]
    public async Task ClaudePlainStartup_WithPersistedSessionId_ShouldStartFreshInsteadOfResume()
    {
        await using var context = await TestEngineContext.CreateAsync();
        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "claudecode";
        session.AgentSessionId = "claude-old-session";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();

        await context.Platform.SendMessageAsync(context.SessionKey, "plain claude message");

        await context.WaitForAsync(() => context.ClaudeAgent.StartCalls.Count == 1);

        Assert.Equal(string.Empty, context.ClaudeAgent.StartCalls[0].RequestedSessionId);
    }

    [Fact]
    public async Task ContinueCommand_AfterExplicitCodexSelection_ShouldRestoreCodex()
    {
        await using var context = await TestEngineContext.CreateAsync();
        context.CreateCodexNativeSession("codex-continue-1", "codex summary");

        await context.Platform.SendMessageAsync(context.SessionKey, "#codex hello");
        await context.WaitForAsync(() => context.CodexAgent.StartCalls.Count == 1);

        await context.Platform.SendMessageAsync(context.SessionKey, "/continue");
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("Codex", StringComparison.Ordinal));

        await context.Platform.SendMessageAsync(context.SessionKey, "continue please");
        await context.WaitForAsync(() => context.CodexAgent.ContinueCalls.Count == 1);

        Assert.Single(context.CodexAgent.ContinueCalls);
        Assert.Empty(context.ClaudeAgent.ContinueCalls);
    }

    [Fact]
    public async Task ResumeCommand_ShouldShowCurrentAgentInHistoryTitle()
    {
        await using var context = await TestEngineContext.CreateAsync();
        context.CreateCodexNativeSession("codex-resume-1", "codex resume summary");

        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "codex";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();

        await context.Platform.SendMessageAsync(context.SessionKey, "/resume");

        Assert.Contains(context.Platform.Replies, reply => reply.Contains("Codex 会话列表", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchCommand_ForClaude_ShouldResumeSpecifiedHistoryOnNextMessage()
    {
        await using var context = await TestEngineContext.CreateAsync();
        context.CreateClaudeNativeSession("claude-switch-1", "claude switch summary");

        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "claudecode";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();

        await context.Platform.SendMessageAsync(context.SessionKey, "/switch 1");
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("Claude 会话", StringComparison.Ordinal));

        await context.Platform.SendMessageAsync(context.SessionKey, "resume switched claude");
        await context.WaitForAsync(() => context.ClaudeAgent.StartCalls.Count == 1);

        Assert.Equal("claude-switch-1", context.ClaudeAgent.StartCalls[0].RequestedSessionId);
    }

    [Fact]
    public async Task ModeCommand_ForCodex_ShouldShowMappedActualSemantics()
    {
        await using var context = await TestEngineContext.CreateAsync();
        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "codex";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();

        await context.Platform.SendMessageAsync(context.SessionKey, "/mode");

        Assert.Contains(context.Platform.Replies, reply =>
            reply.Contains("`default` - 映射到 `on-request + workspace-write`", StringComparison.Ordinal) &&
            reply.Contains("`acceptedits` - 当前等价于 `default`", StringComparison.Ordinal) &&
            reply.Contains("`plan` - 映射到 `untrusted + read-only`", StringComparison.Ordinal) &&
            reply.Contains("`yolo` - 映射到 `never + danger-full-access`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurrentCommand_ForCodex_ShouldShowActualApprovalModeAndSandbox()
    {
        await using var context = await TestEngineContext.CreateAsync();
        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "codex";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();
        context.CodexAgent.SetMode("plan");

        await context.Platform.SendMessageAsync(context.SessionKey, "/current");

        Assert.Contains(context.Platform.Replies, reply =>
            reply.Contains("**审批模式**: untrusted (只读规划)", StringComparison.Ordinal) &&
            reply.Contains("**Sandbox**: read-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModeCommand_AcceptEditsForCodex_ShouldBeTreatedAsEquivalentToDefault()
    {
        await using var context = await TestEngineContext.CreateAsync();
        var session = context.SessionManager.GetOrCreate(context.SessionKey, context.Platform.Name, "user", "User");
        session.AgentType = "codex";
        session.ProjectKey = context.WorkDir;
        context.SessionManager.Save();
        context.CodexAgent.SetMode("default");

        await context.Platform.SendMessageAsync(context.SessionKey, "/mode acceptedits");

        Assert.Contains(context.Platform.Replies, reply =>
            reply.Contains("当前已经是", StringComparison.Ordinal) &&
            reply.Contains("on-request", StringComparison.Ordinal));
    }

    private sealed class TestEngineContext : IAsyncDisposable
    {
        private readonly string _rootDir;
        private readonly List<string> _cleanupPaths = [];

        private TestEngineContext(
            string rootDir,
            string workDir,
            string sessionStoragePath,
            SessionManager sessionManager,
            TestPlatform platform,
            FakeAgent claudeAgent,
            FakeAgent codexAgent,
            Engine engine)
        {
            _rootDir = rootDir;
            WorkDir = workDir;
            SessionStoragePath = sessionStoragePath;
            SessionManager = sessionManager;
            Platform = platform;
            ClaudeAgent = claudeAgent;
            CodexAgent = codexAgent;
            Engine = engine;
        }

        public string SessionKey { get; } = "feishu:test-user";
        public string WorkDir { get; }
        public string SessionStoragePath { get; }
        public SessionManager SessionManager { get; }
        public TestPlatform Platform { get; }
        public FakeAgent ClaudeAgent { get; }
        public FakeAgent CodexAgent { get; }
        public Engine Engine { get; }

        public static async Task<TestEngineContext> CreateAsync()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "MinoLink.AgentRoutingTests", Guid.NewGuid().ToString("N"));
            var workDir = Path.Combine(rootDir, "workdir");
            var dataDir = Path.Combine(rootDir, "data");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(dataDir);

            var sessionStoragePath = Path.Combine(dataDir, "sessions.json");
            var sessionManager = new SessionManager(sessionStoragePath);
            var platform = new TestPlatform();
            var claudeAgent = new FakeAgent("claudecode");
            var codexAgent = new FakeAgent("codex");

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
                NullLogger<Engine>.Instance);

            await engine.StartAsync(CancellationToken.None);

            return new TestEngineContext(rootDir, workDir, sessionStoragePath, sessionManager, platform, claudeAgent, codexAgent, engine);
        }

        public void CreateCodexNativeSession(string sessionId, string summary)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions",
                "minolink-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{sessionId}.jsonl");
            var lines = new[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new
                    {
                        id = sessionId,
                        cwd = WorkDir,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    type = "event_msg",
                    payload = new
                    {
                        type = "user_message",
                        message = summary,
                    },
                }),
            };

            File.WriteAllLines(filePath, lines);
            _cleanupPaths.Add(dir);
        }

        public void CreateClaudeNativeSession(string sessionId, string summary)
        {
            var encoded = ClaudeNativeSession.EncodeProjectDir(WorkDir);
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "projects",
                encoded);

            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{sessionId}.jsonl");
            var lines = new[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new
                    {
                        content = summary,
                    },
                }),
            };

            File.WriteAllLines(filePath, lines);
            _cleanupPaths.Add(dir);
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

            foreach (var path in _cleanupPaths)
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }

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

        public async Task SendMessageAsync(string sessionKey, string content)
        {
            if (_messageHandler is null)
                throw new InvalidOperationException("平台尚未启动。");

            await _messageHandler(this, new Message
            {
                SessionKey = sessionKey,
                From = "user",
                FromName = "User",
                Content = content,
                ReplyContext = new object(),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAgent(string name) : IAgent
    {
        public string Name => name;
        public string Mode { get; private set; } = name == "codex" ? "on-request" : "default";
        public List<StartCall> StartCalls { get; } = [];
        public List<string> ContinueCalls { get; } = [];

        public void SetMode(string mode) => Mode = name == "codex"
            ? mode.ToLowerInvariant() switch
            {
                "acceptedits" or "accept-edits" or "accept_edits" or "default" => "on-request",
                "plan" => "untrusted",
                "bypasspermissions" or "bypass-permissions" or "yolo" or "auto" or "never" => "never",
                _ => mode,
            }
            : mode;

        public Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct)
        {
            var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
                ? $"{name}-start-{StartCalls.Count + 1}"
                : sessionId;
            StartCalls.Add(new StartCall(sessionId ?? string.Empty, resolvedSessionId, workDir));
            return Task.FromResult<IAgentSession>(new FakeAgentSession(resolvedSessionId, name));
        }

        public Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct)
        {
            ContinueCalls.Add(workDir);
            return Task.FromResult<IAgentSession>(new FakeAgentSession($"{name}-continue-{ContinueCalls.Count}", name));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public sealed record StartCall(string RequestedSessionId, string ResolvedSessionId, string WorkDir);
    }

    private sealed class FakeAgentSession(string sessionId, string agentName) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        private bool _sent;

        public string SessionId { get; } = sessionId;
        public ChannelReader<AgentEvent> Events => _events.Reader;

        public Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default)
        {
            if (_sent)
                throw new InvalidOperationException("该测试会话仅支持一次发送。");

            _sent = true;
            _events.Writer.TryWrite(new AgentEvent
            {
                Type = AgentEventType.Result,
                Content = $"{agentName}:{content}",
            });
            _events.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
