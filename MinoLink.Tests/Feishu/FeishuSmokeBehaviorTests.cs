using System.Reflection;
using System.Threading.Channels;
using FeishuNetSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Core.TurnMerge;
using MinoLink.Feishu;

namespace MinoLink.Tests.Feishu;

public sealed class FeishuSmokeBehaviorTests
{
    [Fact]
    public async Task FeishuInboundMessages_WithinMergeWindow_ShouldMergeIntoSingleAgentTurn()
    {
        await using var context = await TestFeishuContext.CreateAsync();

        await context.SendInboundAsync("msg-1", "chat-1", "user-1", "User", "帮我看下这个报错");
        await Task.Delay(80);
        await context.SendInboundAsync("msg-2", "chat-1", "user-1", "User", "是在 docker 里");

        await context.WaitForAsync(() => context.Agent.SendCalls.Count == 1, timeoutMs: 4000);

        var send = Assert.Single(context.Agent.SendCalls);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            send.Content);
    }

    [Fact]
    public async Task FeishuInboundRunningTurn_WithSupplement_ShouldInterruptAndRerun()
    {
        await using var context = await TestFeishuContext.CreateAsync(blockFirstSendUntilCancelled: true);

        await context.SendInboundAsync("msg-1", "chat-1", "user-1", "User", "帮我看下这个报错");
        await context.WaitForAsync(() => context.Agent.SendCalls.Count == 1, timeoutMs: 4000);

        await context.SendInboundAsync("msg-2", "chat-1", "user-1", "User", "是在 docker 里");

        await context.WaitForAsync(() => context.Agent.InterruptCallCount == 1, timeoutMs: 4000);
        await context.WaitForAsync(() => context.Agent.CancelledCalls.Count == 1, timeoutMs: 4000);
        await context.WaitForAsync(() => context.Agent.SendCalls.Count == 2, timeoutMs: 5000);

        Assert.Equal(0, context.Agent.SessionDisposeCount);
        Assert.Equal(
            "用户当前请求：\n帮我看下这个报错\n\n补充信息：\n- 是在 docker 里",
            context.Agent.SendCalls[1].Content);
    }

    private sealed class TestFeishuContext : IAsyncDisposable
    {
        private static readonly MethodInfo OnMessageReceivedMethod =
            typeof(FeishuPlatform).GetMethod("OnMessageReceivedAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 FeishuPlatform.OnMessageReceivedAsync。");

        private readonly string _rootDir;

        private TestFeishuContext(
            string rootDir,
            Engine engine,
            FeishuPlatform platform,
            RecordingAgent agent)
        {
            _rootDir = rootDir;
            Engine = engine;
            Platform = platform;
            Agent = agent;
        }

        public Engine Engine { get; }

        public FeishuPlatform Platform { get; }

        public RecordingAgent Agent { get; }

        public static async Task<TestFeishuContext> CreateAsync(bool blockFirstSendUntilCancelled = false)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "MinoLink.FeishuSmoke", Guid.NewGuid().ToString("N"));
            var workDir = Path.Combine(rootDir, "workdir");
            var dataDir = Path.Combine(rootDir, "data");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(dataDir);

            var api = new Mock<IFeishuTenantApi>(MockBehavior.Loose);
            var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Loose);
            var platform = new FeishuPlatform(
                api.Object,
                new FeishuPlatformOptions
                {
                    AppId = "smoke-app",
                    AppSecret = "smoke-secret",
                    VerificationToken = "smoke-token",
                },
                NullLogger<FeishuPlatform>.Instance,
                httpClientFactory.Object);

            var sessionManager = new SessionManager(Path.Combine(dataDir, "sessions.json"));
            var agent = new RecordingAgent(blockFirstSendUntilCancelled);

            var engine = new Engine(
                "test-project",
                _ => agent,
                [platform],
                workDir,
                sessionManager,
                NullLogger<Engine>.Instance,
                turnMergeOptions: new TurnMergeOptions
                {
                    InitialMergeWindow = TimeSpan.FromMilliseconds(200),
                    RestartDebounceWindow = TimeSpan.FromMilliseconds(120),
                });

            await engine.StartAsync(CancellationToken.None);
            return new TestFeishuContext(rootDir, engine, platform, agent);
        }

        public async Task SendInboundAsync(string messageId, string chatId, string senderId, string senderName, string content)
        {
            var task = (Task?)OnMessageReceivedMethod.Invoke(
                Platform,
                [messageId, chatId, senderId, senderName, content, false, null]);

            if (task is null)
                throw new InvalidOperationException("飞书入站反射调用失败。");

            await task;
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

    private sealed class RecordingAgent(bool blockFirstSendUntilCancelled) : IAgent
    {
        private int _sendSequence;

        public string Name => "claudecode";

        public string Mode => "default";

        public List<SendCall> SendCalls { get; } = [];

        public List<string> CancelledCalls { get; } = [];

        public int InterruptCallCount { get; private set; }

        public int SessionDisposeCount { get; private set; }

        public void SetMode(string mode)
        {
        }

        public Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(new RecordingSession(this, blockFirstSendUntilCancelled));

        public Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(new RecordingSession(this, blockFirstSendUntilCancelled));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public int NextSendSequence() => Interlocked.Increment(ref _sendSequence);

        public void RecordInterrupt() => InterruptCallCount++;

        public void RecordDispose() => SessionDisposeCount++;
    }

    private sealed class RecordingSession(RecordingAgent owner, bool blockFirstSendUntilCancelled) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public string SessionId { get; } = "claude-feishu-smoke";

        public ChannelReader<AgentEvent> Events => _events.Reader;

        public async Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default)
        {
            owner.SendCalls.Add(new SendCall(content, attachments?.Count ?? 0));
            var sequence = owner.NextSendSequence();

            if (blockFirstSendUntilCancelled && sequence == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    owner.CancelledCalls.Add(content);
                    throw;
                }
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
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            owner.RecordDispose();
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SendCall(string Content, int AttachmentCount);
}
