using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Tests.AgentRouting;

public sealed class FileOutputBehaviorTests
{
    [Fact]
    public async Task FileCommand_ShouldAppendFilesProtocolToAgentPrompt()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = "ok"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成 output/report.pdf");
        await context.WaitForAsync(() => context.AgentSession.SentMessages.Count == 1);

        var sent = context.AgentSession.SentMessages.Single();
        Assert.Contains("生成 output/report.pdf", sent.Content, StringComparison.Ordinal);
        Assert.Contains("[FILES]", sent.Content, StringComparison.Ordinal);
        Assert.Contains("[/FILES]", sent.Content, StringComparison.Ordinal);
        Assert.Contains("只填写真实已生成的文件路径", sent.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileCommand_ResultWithFilesBlock_ShouldSendImageAndFileAfterReply()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var outputDir = Path.Combine(context.WorkDir, "output");
        Directory.CreateDirectory(outputDir);
        var imagePath = Path.Combine(outputDir, "chart.png");
        var filePath = Path.Combine(outputDir, "report.pdf");
        await File.WriteAllTextAsync(imagePath, "fake-image");
        await File.WriteAllTextAsync(filePath, "fake-pdf");

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = $"已生成文件。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, imagePath)}\n{Path.GetRelativePath(context.WorkDir, filePath)}\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成报告和图表");
        await context.WaitForAsync(() => context.Platform.SentImages.Count == 1 && context.Platform.SentFiles.Count == 1);

        Assert.Contains(context.Platform.Replies, reply => reply.Contains("已生成文件", StringComparison.Ordinal));
        Assert.Contains(imagePath, context.Platform.SentImages);
        Assert.Contains(filePath, context.Platform.SentFiles);
    }


    [Fact]
    public async Task FileCommand_ShouldHideFilesBlockFromVisibleReply()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var outputDir = Path.Combine(context.WorkDir, "output");
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "report.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = $"报告已生成。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, filePath)}\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成报告");
        await context.WaitForAsync(() => context.Platform.SentFiles.Count == 1);

        Assert.DoesNotContain(context.Platform.Replies, reply => reply.Contains("[FILES]", StringComparison.Ordinal));
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("报告已生成", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileCommand_TextStreamWithFilesBlock_ShouldSendAfterResult()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var outputDir = Path.Combine(context.WorkDir, "output");
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "streamed.txt");
        await File.WriteAllTextAsync(filePath, "stream-body");

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Text,
            Content = $"流式正文。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, filePath)}\n[/FILES]"
        });
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = string.Empty
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成流式文件");
        await context.WaitForAsync(() => context.Platform.SentFiles.Count == 1);

        Assert.Contains(filePath, context.Platform.SentFiles);
        Assert.DoesNotContain(context.Platform.Replies, reply => reply.Contains("[FILES]", StringComparison.Ordinal));
    }



    [Fact]
    public async Task FileCommand_ShouldExplicitlyRequireOutputDirectory()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = "ok"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成月报");
        await context.WaitForAsync(() => context.AgentSession.SentMessages.Count == 1);

        var sent = context.AgentSession.SentMessages.Single().Content;
        Assert.Contains("output/", sent, StringComparison.Ordinal);
        Assert.Contains("所有产物默认写到 output/", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileCommand_DirectoryInFilesBlock_ShouldSendAllFilesUnderDirectory()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var dir = Path.Combine(context.WorkDir, "output", "bundle");
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "a.txt");
        var file2 = Path.Combine(dir, "b.png");
        await File.WriteAllTextAsync(file1, "a");
        await File.WriteAllTextAsync(file2, "b");

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = $"已生成。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, dir)}\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 批量导出");
        await context.WaitForAsync(() => context.Platform.SentFiles.Count == 1 && context.Platform.SentImages.Count == 1);

        Assert.Contains(file1, context.Platform.SentFiles);
        Assert.Contains(file2, context.Platform.SentImages);
    }

    [Fact]
    public async Task FileCommand_DuplicatePaths_ShouldSendOnceAndReplySummary()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var filePath = Path.Combine(context.WorkDir, "output", "dup.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "dup");

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = $"已生成。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, filePath)}\n{Path.GetRelativePath(context.WorkDir, filePath)}\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 重复路径");
        await context.WaitForAsync(() => context.Platform.SentFiles.Count == 1);

        Assert.Single(context.Platform.SentFiles);
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("已发送 1 个文件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileCommand_FileTooLarge_ShouldWarnBeforeSending()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        var filePath = Path.Combine(context.WorkDir, "output", "too-large.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(31L * 1024 * 1024);
        }

        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = $"已生成。\n\n[FILES]\n{Path.GetRelativePath(context.WorkDir, filePath)}\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 大文件");
        await context.WaitForAsync(() => context.Platform.Replies.Count >= 2);

        Assert.Empty(context.Platform.SentFiles);
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("超过 30 MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileCommand_ResultWithoutFilesBlock_ShouldWarnUser()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = "只返回说明，没有路径"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成但不返回路径");
        await context.WaitForAsync(() => context.Platform.Replies.Count >= 2);

        Assert.Contains(context.Platform.Replies, reply => reply.Contains("未检测到有效的 [FILES] 区块", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileCommand_InvalidFilePath_ShouldWarnAndSkipSending()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = "已生成。\n\n[FILES]\nC:/Windows/System32/not-allowed.txt\noutput/missing.txt\n[/FILES]"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成非法路径");
        await context.WaitForAsync(() => context.Platform.Replies.Count >= 2);

        Assert.Empty(context.Platform.SentFiles);
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("以下文件未发送", StringComparison.Ordinal));
        Assert.Contains(context.Platform.Replies, reply => reply.Contains("missing.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileCommand_ResultWithoutFilesBlock_ShouldNotSendAttachments()
    {
        await using var context = await FileTestEngineContext.CreateAsync();
        context.AgentSession.EnqueueEvent(new AgentEvent
        {
            Type = AgentEventType.Result,
            Content = "只返回说明，没有路径"
        });

        await context.Platform.SendMessageAsync(context.SessionKey, "/file 生成但不返回路径");
        await context.WaitForAsync(() => context.Platform.Replies.Count > 0);

        Assert.Empty(context.Platform.SentImages);
        Assert.Empty(context.Platform.SentFiles);
    }

    private sealed class FileTestEngineContext : IAsyncDisposable
    {
        private readonly string _rootDir;

        private FileTestEngineContext(string rootDir, string workDir, Engine engine, FileAwarePlatform platform, FakeAgent agent, FakeAgentSession agentSession)
        {
            _rootDir = rootDir;
            WorkDir = workDir;
            Engine = engine;
            Platform = platform;
            Agent = agent;
            AgentSession = agentSession;
        }

        public string SessionKey { get; } = "feishu:file-test-user";
        public string WorkDir { get; }
        public Engine Engine { get; }
        public FileAwarePlatform Platform { get; }
        public FakeAgent Agent { get; }
        public FakeAgentSession AgentSession { get; }

        public static async Task<FileTestEngineContext> CreateAsync()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "MinoLink.FileOutputTests", Guid.NewGuid().ToString("N"));
            var workDir = Path.Combine(rootDir, "workdir");
            Directory.CreateDirectory(workDir);
            var sessionStoragePath = Path.Combine(rootDir, "sessions.json");
            var sessionManager = new SessionManager(sessionStoragePath);
            var platform = new FileAwarePlatform();
            var agentSession = new FakeAgentSession("claude-file-test");
            var agent = new FakeAgent(agentSession);
            var engine = new Engine(
                "test-project",
                _ => agent,
                [platform],
                workDir,
                sessionManager,
                NullLogger<Engine>.Instance);

            await engine.StartAsync(CancellationToken.None);
            return new FileTestEngineContext(rootDir, workDir, engine, platform, agent, agentSession);
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
            await Agent.DisposeAsync();
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

    private sealed class FileAwarePlatform : IPlatform, IImageSender, IFileSender
    {
        private Func<IPlatform, Message, Task>? _messageHandler;
        public string Name => "feishu";
        public List<string> Replies { get; } = [];
        public List<string> SentImages { get; } = [];
        public List<string> SentFiles { get; } = [];

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

        public Task SendImageAsync(object replyContext, string filePath, CancellationToken ct)
        {
            lock (SentImages)
                SentImages.Add(filePath);
            return Task.CompletedTask;
        }

        public Task SendFileAsync(object replyContext, string filePath, CancellationToken ct)
        {
            lock (SentFiles)
                SentFiles.Add(filePath);
            return Task.CompletedTask;
        }

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

    private sealed class FakeAgent(FakeAgentSession session) : IAgent
    {
        public string Name => "claudecode";
        public string Mode => "default";
        public List<(string SessionId, string WorkDir)> StartCalls { get; } = [];

        public void SetMode(string mode) { }

        public Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct)
        {
            StartCalls.Add((sessionId, workDir));
            return Task.FromResult<IAgentSession>(session);
        }

        public Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct) =>
            Task.FromResult<IAgentSession>(session);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAgentSession(string sessionId) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public string SessionId { get; } = sessionId;
        public ChannelReader<AgentEvent> Events => _events.Reader;
        public List<SentMessage> SentMessages { get; } = [];

        public Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default)
        {
            SentMessages.Add(new SentMessage(content, attachments ?? []));
            return Task.CompletedTask;
        }

        public void EnqueueEvent(AgentEvent evt)
        {
            _events.Writer.TryWrite(evt);
            if (evt.Type is AgentEventType.Result or AgentEventType.Error)
                _events.Writer.TryComplete();
        }

        public Task<bool> InterruptAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ClearAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentMessage(string Content, IReadOnlyList<MessageAttachment> Attachments);
}
