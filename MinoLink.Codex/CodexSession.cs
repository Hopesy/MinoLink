using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Codex;

public sealed class CodexSession : IAgentSession
{
    private const string DefaultModelName = "gpt-5.4";

    private readonly Channel<AgentEvent> _eventChannel = Channel.CreateBounded<AgentEvent>(64);
    private readonly ILogger _logger;
    private readonly string _workDir;
    private readonly string? _model;
    private readonly string _mode;
    private readonly string _sandboxMode;
    private readonly IAgentMessageEncoder _messageEncoder;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _useContinue;
    private readonly TaskCompletionSource<string> _threadReady = new();
    private readonly SemaphoreSlim _stdinWriteLock = new(1, 1);
    private readonly HashSet<string> _announcedItems = [];
    private readonly Dictionary<string, int> _agentMessageLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentMessagePhases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BackgroundTaskState> _backgroundTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingRpcAcks = new();
    private readonly List<TaskRecord> _tasks = [];
    private int _taskSequence;

    private Process? _process;
    private StreamWriter? _stdin;
    private string _sessionId;
    private string? _turnId;
    private int _requestId;

    public string SessionId => _sessionId;
    public ChannelReader<AgentEvent> Events => _eventChannel.Reader;

    internal CodexSession(string sessionId, string workDir, string? model, string mode, string sandboxMode, ILogger logger, IAgentMessageEncoder? messageEncoder = null, bool useContinue = false)
    {
        _sessionId = sessionId;
        _workDir = workDir;
        _model = model;
        _mode = mode;
        _sandboxMode = sandboxMode;
        _logger = logger;
        _messageEncoder = messageEncoder ?? new CodexMessageEncoder();
        _useContinue = useContinue;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var codexPath = CodexAgent.ResolveCodexPath();
        _logger.LogInformation("启动 codex 进程: {Path}", codexPath);

        var psi = new ProcessStartInfo(codexPath)
        {
            WorkingDirectory = _workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        var env = psi.Environment;
        ApplyPreferredShellEnvironment(env);

        if (!string.IsNullOrWhiteSpace(_model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_model);
        }
        psi.ArgumentList.Add("--ask-for-approval");
        psi.ArgumentList.Add(_mode);
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add(_sandboxMode);
        psi.ArgumentList.Add("app-server");

        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 codex 进程");
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;

        _ = Task.Run(() => ReadLoopAsync(_process.StandardOutput), _cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync(_cts.Token) is { } err)
                    _logger.LogDebug("codex stderr: {Line}", err);
            }
            catch { }
        }, _cts.Token);

        await SendRequestAsync("initialize", new
        {
            clientInfo = new
            {
                name = "minolink",
                title = "MinoLink",
                version = "1.0.0"
            }
        });
        await SendNotificationAsync("initialized", new { });

        if (_useContinue)
        {
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                _logger.LogInformation("Codex 继续最近一次会话: {SessionId}", _sessionId);
                await SendRequestAsync("thread/resume", new { threadId = _sessionId });
            }
            else
            {
                _logger.LogInformation("Codex 未找到可继续的最近会话，启动新会话");
                await SendRequestAsync("thread/start", new { model = _model ?? DefaultModelName });
            }
        }
        else if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            _logger.LogInformation("Codex 恢复指定会话: {SessionId}", _sessionId);
            await SendRequestAsync("thread/resume", new { threadId = _sessionId });
        }
        else
        {
            await SendRequestAsync("thread/start", new { model = _model ?? DefaultModelName });
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        await _threadReady.Task.WaitAsync(timeoutCts.Token);
    }

    public async Task SendAsync(string content, IReadOnlyList<MessageAttachment>? attachments = null, CancellationToken ct = default)
    {
        if (_stdin is null) throw new InvalidOperationException("会话未启动");
        if (_cts.IsCancellationRequested) throw new InvalidOperationException("会话已关闭");

        var inputItems = BuildInputItems(content, attachments);
        await SendRequestAsync("turn/start", new
        {
            threadId = _sessionId,
            input = inputItems,
        });
    }

    public async Task<bool> ClearAsync(CancellationToken ct = default)
    {
        if (_stdin is null || _cts.IsCancellationRequested)
            return false;

        try
        {
            _logger.LogInformation("→ Codex thread/start (协议级清除上下文), 旧 threadId={OldThreadId}", _sessionId);
            _turnId = null;
            _announcedItems.Clear();
            _agentMessageLengths.Clear();
            _agentMessagePhases.Clear();

            await SendRequestAsync("thread/start", new { model = _model ?? DefaultModelName });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            // _threadReady 只能 set 一次，新线程的 threadId 通过 HandleThreadStarted/HandleRpcResult 更新 _sessionId
            // 等待 stdout 确认新 threadId
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            var oldSessionId = _sessionId;
            while (_sessionId == oldSessionId && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100, timeoutCts.Token);

            var success = _sessionId != oldSessionId;
            _logger.LogInformation("Codex thread/start {Result}: newThreadId={NewThreadId}",
                success ? "成功" : "超时", _sessionId);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex thread/start 失败");
            return false;
        }
    }

    public async Task<bool> InterruptAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_stdin is null || _cts.IsCancellationRequested)
            return false;
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_turnId))
            return false;

        try
        {
            var ack = await SendRequestWithAckAsync("turn/interrupt", new
            {
                threadId = _sessionId,
                turnId = _turnId,
            }, ct);

            return await ack.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Codex turn/interrupt 超时: threadId={ThreadId}, turnId={TurnId}", _sessionId, _turnId);
            return false;
        }
    }

    public async Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default)
    {
        if (!_pendingRequests.Remove(requestId, out var pending))
        {
            _logger.LogWarning("未找到待响应的 Codex 请求: requestId={RequestId}", requestId);
            return;
        }

        object result = pending.Kind switch
        {
            PendingRequestKind.Approval => BuildApprovalResponse(pending, response),
            PendingRequestKind.ToolCall => BuildDynamicToolCallResponse(pending, response),
            PendingRequestKind.UserInput => BuildToolRequestUserInputResponse(pending, response),
            _ => new { success = response.Allow }
        };

        await SendResponseAsync(requestId, result);
    }

    private async Task ReadLoopAsync(StreamReader stdout)
    {
        var writer = _eventChannel.Writer;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(_cts.Token);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _logger.LogDebug("codex stdout: {Line}", line.Length > 400 ? line[..400] + "..." : line);

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                HandleRpcResult(root);

                if (!root.TryGetProperty("method", out var methodEl))
                    continue;

                var method = methodEl.GetString();
                switch (method)
                {
                    case "thread/started":
                        HandleThreadStarted(root);
                        break;
                    case "turn/started":
                        HandleTurnStarted(root);
                        break;
                    case "item/started":
                        await HandleItemStartedAsync(root, writer);
                        break;
                    case "item/completed":
                        await HandleItemCompletedAsync(root, writer);
                        break;
                    case "item/agentMessage/delta":
                        await HandleAgentMessageDeltaAsync(root, writer);
                        break;
                    case "item/commandExecution/requestApproval":
                    case "item/fileChange/requestApproval":
                    case "item/permissions/requestApproval":
                        await HandleApprovalRequestAsync(root, writer, method!);
                        break;
                    case "item/tool/call":
                        await HandleToolCallAsync(root, writer);
                        break;
                    case "item/tool/requestUserInput":
                        await HandleToolRequestUserInputAsync(root, writer);
                        break;
                    case "turn/completed":
                        await HandleTurnCompletedAsync(root, writer);
                        break;
                    case "error":
                        await HandleErrorAsync(root, writer);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 codex stdout 异常");
            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Error, Content = ex.Message });
        }
        finally
        {
            _threadReady.TrySetCanceled();
            writer.TryComplete();
        }
    }

    private void HandleRpcResult(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idEl) && TryGetNumericId(idEl, out var rpcId))
        {
            if (_pendingRpcAcks.TryRemove(rpcId, out var ack))
            {
                var ok = !root.TryGetProperty("error", out _);
                ack.TrySetResult(ok);
            }
        }

        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("thread", out var thread) &&
            thread.TryGetProperty("id", out var threadIdEl))
        {
            var threadId = threadIdEl.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _sessionId = threadId;
                _threadReady.TrySetResult(threadId);
            }
        }

        if (root.TryGetProperty("result", out result) &&
            result.TryGetProperty("turn", out var turn) &&
            turn.TryGetProperty("id", out var turnIdEl))
        {
            _turnId = turnIdEl.GetString();
        }
    }

    private void HandleThreadStarted(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl) ||
            !paramsEl.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("id", out var idEl))
            return;

        var threadId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        _sessionId = threadId;
        _threadReady.TrySetResult(threadId);
    }

    private void HandleTurnStarted(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsEl) ||
            !paramsEl.TryGetProperty("turn", out var turn) ||
            !turn.TryGetProperty("id", out var idEl))
            return;

        _turnId = idEl.GetString();
    }

    private async Task HandleItemStartedAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!TryGetItem(root, out var item))
            return;

        var itemType = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(itemType) || string.IsNullOrWhiteSpace(itemId) || !_announcedItems.Add(itemId))
            return;

        switch (itemType)
        {
            case "agentMessage":
                _agentMessagePhases[itemId] = item.TryGetProperty("phase", out var phaseEl)
                    ? phaseEl.GetString() ?? string.Empty
                    : string.Empty;
                break;
            case "commandExecution":
                await writer.WriteAsync(new AgentEvent
                {
                    Type = AgentEventType.ToolUse,
                    ToolName = "Bash",
                    ToolInput = SummarizeCommandExecution(item),
                });
                break;
            case "fileChange":
                await writer.WriteAsync(new AgentEvent
                {
                    Type = AgentEventType.ToolUse,
                    ToolName = "Edit",
                    ToolInput = SummarizeFileChange(item),
                });
                break;
            case "mcpToolCall":
                await writer.WriteAsync(new AgentEvent
                {
                    Type = AgentEventType.ToolUse,
                    ToolName = item.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() : "mcpToolCall",
                    ToolInput = SummarizeMcpToolCall(item),
                });
                break;
            case "dynamicToolCall":
                var dynamicToolName = item.TryGetProperty("tool", out var dynToolEl) ? dynToolEl.GetString() : "dynamicToolCall";
                await writer.WriteAsync(new AgentEvent
                {
                    Type = AgentEventType.ToolUse,
                    ToolName = dynamicToolName,
                    ToolInput = SummarizeDynamicToolCall(dynamicToolName, item),
                });
                break;
        }
    }

    private async Task HandleItemCompletedAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!TryGetItem(root, out var item))
            return;

        var itemType = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        if (itemType != "agentMessage")
            return;

        var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        if (item.TryGetProperty("phase", out var phaseEl))
            _agentMessagePhases[itemId] = phaseEl.GetString() ?? string.Empty;

        if (_agentMessageLengths.ContainsKey(itemId))
            return;

        var text = item.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        _agentMessageLengths[itemId] = text.Length;
        await writer.WriteAsync(new AgentEvent
        {
            Type = GetAgentMessageEventType(itemId),
            Content = text,
        });
    }

    private async Task HandleAgentMessageDeltaAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("params", out var paramsEl))
            return;

        var itemId = paramsEl.TryGetProperty("itemId", out var itemIdEl) ? itemIdEl.GetString() : null;
        var delta = ExtractAgentMessageDeltaText(paramsEl);
        if (string.IsNullOrWhiteSpace(delta))
            return;

        if (!string.IsNullOrWhiteSpace(itemId))
            _agentMessageLengths[itemId] = (_agentMessageLengths.TryGetValue(itemId, out var current) ? current : 0) + delta.Length;

        await writer.WriteAsync(new AgentEvent
        {
            Type = GetAgentMessageEventType(itemId),
            Content = delta,
        });
    }

    private async Task HandleApprovalRequestAsync(JsonElement root, ChannelWriter<AgentEvent> writer, string method)
    {
        var requestId = ExtractRequestId(root);
        if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("params", out var paramsEl))
            return;

        _pendingRequests[requestId] = new PendingRequest(PendingRequestKind.Approval, method, null, DeserializeObject(paramsEl));

        var (toolName, summary) = method switch
        {
            "item/commandExecution/requestApproval" => ("Bash", SummarizeApproval(paramsEl, "command", "cwd", "reason")),
            "item/fileChange/requestApproval" => ("Edit", SummarizeApproval(paramsEl, "grantRoot", "reason")),
            "item/permissions/requestApproval" => (ResolveApprovalToolName(paramsEl), SummarizePermissionApproval(paramsEl)),
            _ => ("Approval", Truncate(paramsEl.GetRawText(), 200)),
        };

        await writer.WriteAsync(new AgentEvent
        {
            Type = AgentEventType.PermissionRequest,
            RequestId = requestId,
            ToolName = toolName,
            ToolInput = summary,
            ToolInputRaw = DeserializeObject(paramsEl),
        });
    }

    private async Task HandleToolCallAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var requestId = ExtractRequestId(root);
        if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("params", out var paramsEl))
            return;

        var toolName = paramsEl.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() ?? string.Empty : string.Empty;
        var arguments = paramsEl.TryGetProperty("arguments", out var argsEl) ? argsEl : default;
        _pendingRequests[requestId] = new PendingRequest(PendingRequestKind.ToolCall, "item/tool/call", toolName, DeserializeObject(arguments));

        if (string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase))
        {
            var questions = ParseUserQuestions(arguments);
            await writer.WriteAsync(new AgentEvent
            {
                Type = AgentEventType.UserQuestion,
                RequestId = requestId,
                ToolName = toolName,
                ToolInput = SummarizeAskUserQuestion(arguments),
                ToolInputRaw = DeserializeObject(arguments),
                Questions = questions,
            });
            return;
        }

        await writer.WriteAsync(new AgentEvent
        {
            Type = AgentEventType.ToolUse,
            ToolName = toolName,
            ToolInput = SummarizeToolArguments(toolName, arguments),
        });

        _pendingRequests.Remove(requestId);

        var execution = await ExecuteDynamicToolAsync(toolName, arguments);
        await SendResponseAsync(requestId, BuildDynamicToolCallResponse(toolName, execution.Success, execution.Message, execution.Payload, execution.Text));
    }

    private async Task HandleToolRequestUserInputAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var requestId = ExtractRequestId(root);
        if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("params", out var paramsEl))
            return;

        var payload = DeserializeObject(paramsEl);
        _pendingRequests[requestId] = new PendingRequest(PendingRequestKind.UserInput, "item/tool/requestUserInput", "AskUserQuestion", payload);

        var questions = ParseToolRequestUserInputQuestions(paramsEl);
        if (questions.Count == 0)
        {
            _pendingRequests.Remove(requestId);
            await SendResponseAsync(requestId, BuildToolRequestUserInputResponse(null, new PermissionResponse
            {
                Allow = false,
                Message = "Missing request_user_input questions.",
            }));
            return;
        }

        await writer.WriteAsync(new AgentEvent
        {
            Type = AgentEventType.UserQuestion,
            RequestId = requestId,
            ToolName = "AskUserQuestion",
            ToolInput = SummarizeToolRequestUserInput(paramsEl),
            ToolInputRaw = payload,
            Questions = questions,
        });
    }

    private async Task HandleTurnCompletedAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("params", out var paramsEl) || !paramsEl.TryGetProperty("turn", out var turn))
        {
            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Result, Content = string.Empty });
            return;
        }

        _turnId = turn.TryGetProperty("id", out var turnIdEl) ? turnIdEl.GetString() : _turnId;
        var status = turn.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? string.Empty : string.Empty;

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var message = turn.TryGetProperty("error", out var errorEl)
                ? ExtractText(errorEl)
                : "Codex 处理失败。";
            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Error, Content = message });
            return;
        }

        if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase))
        {
            _turnId = null;
            _logger.LogInformation("Codex turn 已被中断（TurnMerge 正常行为），不写入事件流");
            return;
        }

        _turnId = null;
        await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Result, Content = string.Empty });
    }

    private async Task HandleErrorAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("params", out var paramsEl) || !paramsEl.TryGetProperty("error", out var errorEl))
            return;

        var willRetry = paramsEl.TryGetProperty("willRetry", out var retryEl) && retryEl.ValueKind is JsonValueKind.True or JsonValueKind.False && retryEl.GetBoolean();
        var message = ExtractText(errorEl);
        if (string.IsNullOrWhiteSpace(message))
            message = "Codex 发生未知错误。";

        if (willRetry)
        {
            _logger.LogWarning("Codex 临时错误，等待重试: {Message}", message);
            return;
        }

        await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Error, Content = message });
    }

    private async Task SendRequestAsync(string method, object @params)
    {
        if (_stdin is null)
            return;

        var id = Interlocked.Increment(ref _requestId);
        var payload = JsonSerializer.Serialize(new { id, method, @params });
        _logger.LogInformation("→ codex stdin: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
        await WriteLineAsync(payload, CancellationToken.None);
    }

    private async Task SendNotificationAsync(string method, object @params)
    {
        if (_stdin is null)
            return;

        var payload = JsonSerializer.Serialize(new { method, @params });
        _logger.LogInformation("→ codex stdin: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
        await WriteLineAsync(payload, CancellationToken.None);
    }

    private async Task SendResponseAsync(string requestId, object result)
    {
        if (_stdin is null)
            return;

        object id = long.TryParse(requestId, out var numericId) ? numericId : requestId;
        var payload = JsonSerializer.Serialize(new { id, result });
        _logger.LogInformation("→ codex 响应: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
        await WriteLineAsync(payload, CancellationToken.None);
    }

    private object[] BuildInputItems(string content, IReadOnlyList<MessageAttachment>? attachments)
    {
        var inputItems = new List<object>();
        var nonImageAttachments = attachments?
            .Where(x => x.Kind != MessageAttachmentKind.Image || string.IsNullOrWhiteSpace(x.LocalPath) || !File.Exists(x.LocalPath))
            .ToArray();

        var encodedText = _messageEncoder.Encode(content, nonImageAttachments);
        if (!string.IsNullOrWhiteSpace(encodedText))
            inputItems.Add(new { type = "text", text = encodedText });

        if (attachments is { Count: > 0 })
        {
            foreach (var attachment in attachments)
            {
                if (attachment.Kind != MessageAttachmentKind.Image)
                    continue;
                if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath))
                    continue;

                inputItems.Add(new { type = "localImage", path = attachment.LocalPath });
            }
        }

        if (inputItems.Count == 0)
            inputItems.Add(new { type = "text", text = string.Empty });

        return inputItems.ToArray();
    }

    private void ApplyPreferredShellEnvironment(IDictionary<string, string?> env)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var gitBashPath = GetPreferredGitBashPath();
        if (gitBashPath is null)
        {
            _logger.LogWarning("未找到 Git Bash，Codex 的 Bash 工具将继续使用系统默认 bash。");
            return;
        }

        var bashDirectory = Path.GetDirectoryName(gitBashPath);
        if (string.IsNullOrWhiteSpace(bashDirectory))
            return;

        var pathEntries = (env["PATH"] ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !string.Equals(
                entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                bashDirectory,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        pathEntries.Insert(0, bashDirectory);
        var gitBinDirectory = Path.GetDirectoryName(bashDirectory);
        if (!string.IsNullOrWhiteSpace(gitBinDirectory) &&
            !pathEntries.Any(entry => string.Equals(
                entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                gitBinDirectory,
                StringComparison.OrdinalIgnoreCase)))
        {
            pathEntries.Insert(0, gitBinDirectory);
        }

        env["PATH"] = string.Join(Path.PathSeparator, pathEntries);
        env["SHELL"] = gitBashPath;
        env["BASH"] = gitBashPath;
        env["MSYSTEM"] = "MINGW64";
        env["CHERE_INVOKING"] = "1";

        _logger.LogInformation("已为 Codex 子进程优先配置 Git Bash: {BashPath}", gitBashPath);
    }

    private static string? GetPreferredGitBashPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var candidate in new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool TryGetItem(JsonElement root, out JsonElement item)
    {
        item = default;
        return root.TryGetProperty("params", out var paramsEl) && paramsEl.TryGetProperty("item", out item);
    }

    private static string ExtractRequestId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl))
            return string.Empty;

        return idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(),
            JsonValueKind.String => idEl.GetString() ?? string.Empty,
            _ => idEl.GetRawText(),
        };
    }

    private AgentEventType GetAgentMessageEventType(string? itemId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) &&
            _agentMessagePhases.TryGetValue(itemId, out var phase) &&
            string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return AgentEventType.Thinking;
        }

        return AgentEventType.Text;
    }

    private static string SummarizeCommandExecution(JsonElement item)
    {
        var command = item.TryGetProperty("command", out var cmdEl)
            ? cmdEl.ValueKind == JsonValueKind.Array
                ? string.Join(' ', cmdEl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                : cmdEl.GetString() ?? string.Empty
            : string.Empty;
        var cwd = item.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(cwd) ? command : $"{command} @ {cwd}";
    }

    private static string SummarizeFileChange(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changesEl) || changesEl.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var paths = changesEl.EnumerateArray()
            .Select(change => change.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(5)
            .ToArray();
        return string.Join(", ", paths!);
    }

    private static string SummarizeMcpToolCall(JsonElement item)
    {
        var server = item.TryGetProperty("server", out var serverEl) ? serverEl.GetString() ?? string.Empty : string.Empty;
        var tool = item.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(server) ? tool : $"{server}/{tool}";
    }

    private static string SummarizeDynamicToolCall(string? toolName, JsonElement item)
    {
        if (!item.TryGetProperty("arguments", out var arguments))
            return string.Empty;

        return SummarizeToolArguments(toolName, arguments);
    }

    private static string SummarizeToolArguments(string? toolName, JsonElement arguments)
    {
        return toolName switch
        {
            "Read" or "Edit" or "Write" => arguments.TryGetProperty("file_path", out var filePath)
                ? filePath.GetString() ?? string.Empty
                : string.Empty,
            "Bash" => arguments.TryGetProperty("command", out var command)
                ? command.GetString() ?? string.Empty
                : string.Empty,
            "AskUserQuestion" => SummarizeAskUserQuestion(arguments),
            "Grep" or "Glob" => arguments.TryGetProperty("pattern", out var pattern)
                ? pattern.GetString() ?? string.Empty
                : string.Empty,
            "TaskCreate" or "TodoWrite" => SummarizeTaskCreate(arguments),
            "TaskUpdate" => SummarizeTaskUpdate(arguments),
            _ => Truncate(arguments.GetRawText(), 200),
        };
    }

    private static string SummarizeTaskCreate(JsonElement arguments)
    {
        if (arguments.TryGetProperty("subject", out var subject))
            return subject.GetString() ?? string.Empty;

        if (arguments.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (var todo in todos.EnumerateArray())
            {
                var content = todo.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
                var status = todo.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var icon = status switch
                {
                    "completed" => "✅",
                    "in_progress" or "in-progress" => "🔄",
                    _ => "⬜",
                };

                if (!string.IsNullOrWhiteSpace(content))
                    items.Add($"{icon} {content}");
            }

            return items.Count > 0 ? string.Join("\n", items) : string.Empty;
        }

        return Truncate(arguments.GetRawText(), 200);
    }

    private static string SummarizeTaskUpdate(JsonElement arguments)
    {
        var taskId = arguments.TryGetProperty("taskId", out var taskIdEl) ? taskIdEl.GetString() ?? string.Empty : string.Empty;
        var status = arguments.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? string.Empty : string.Empty;
        var statusIcon = status switch
        {
            "completed" => "✅",
            "in_progress" => "🔄",
            "pending" => "⏳",
            _ => string.Empty,
        };
        var subject = arguments.TryGetProperty("subject", out var subjectEl) ? subjectEl.GetString() : null;
        return subject is not null
            ? $"{statusIcon} #{taskId} → {subject}"
            : $"{statusIcon} #{taskId} → {status}";
    }

    private static string SummarizeApproval(JsonElement paramsEl, params string[] fieldNames)
    {
        var parts = new List<string>();
        foreach (var fieldName in fieldNames)
        {
            if (!paramsEl.TryGetProperty(fieldName, out var fieldEl))
                continue;

            var text = fieldEl.ValueKind switch
            {
                JsonValueKind.String => fieldEl.GetString(),
                _ => fieldEl.GetRawText(),
            };
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add($"{fieldName}={text}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : Truncate(paramsEl.GetRawText(), 200);
    }

    private static string ResolveApprovalToolName(JsonElement paramsEl)
    {
        foreach (var fieldName in new[] { "toolName", "tool", "kind", "permissionKind" })
        {
            if (!paramsEl.TryGetProperty(fieldName, out var fieldEl))
                continue;

            var value = fieldEl.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (paramsEl.TryGetProperty("command", out _))
            return "Bash";
        if (paramsEl.TryGetProperty("changes", out _) || paramsEl.TryGetProperty("grantRoot", out _))
            return "Edit";

        return "Approval";
    }

    private static string SummarizePermissionApproval(JsonElement paramsEl)
    {
        var summary = SummarizeApproval(
            paramsEl,
            "toolName",
            "tool",
            "kind",
            "permissionKind",
            "command",
            "cwd",
            "path",
            "grantRoot",
            "reason");

        return string.IsNullOrWhiteSpace(summary)
            ? Truncate(paramsEl.GetRawText(), 200)
            : summary;
    }

    private async Task<ToolExecutionResult> ExecuteDynamicToolAsync(string? toolName, JsonElement arguments)
    {
        try
        {
            return toolName switch
            {
                "Read" => await ExecuteReadAsync(arguments),
                "Write" => await ExecuteWriteAsync(arguments),
                "Edit" => await ExecuteEditAsync(arguments),
                "Bash" => await ExecuteBashAsync(arguments),
                "Glob" => await ExecuteGlobAsync(arguments),
                "Grep" => await ExecuteGrepAsync(arguments),
                "TaskCreate" => ExecuteTaskCreate(arguments),
                "TaskUpdate" => ExecuteTaskUpdate(arguments),
                "TaskList" => ExecuteTaskList(),
                "TaskGet" => ExecuteTaskGet(arguments),
                "TaskOutput" => ExecuteTaskOutput(arguments),
                "TaskStop" => ExecuteTaskStop(arguments),
                "TodoWrite" => ExecuteTodoWrite(arguments),
                _ => new ToolExecutionResult(false, $"Unsupported tool call: {toolName}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 Codex dynamic tool 失败: {ToolName}", toolName);
            return new ToolExecutionResult(false, ex.Message);
        }
    }

    private Task<ToolExecutionResult> ExecuteReadAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var resolvedPath = ResolvePath(filePath);
        var fileInfo = new FileInfo(resolvedPath);
        if (!fileInfo.Exists)
            return Task.FromResult(new ToolExecutionResult(false, $"File not found: {filePath}"));

        var offset = Math.Max(1, GetOptionalInt(arguments, "offset") ?? 1);
        var limit = Math.Max(1, GetOptionalInt(arguments, "limit") ?? 2000);
        var lines = File.ReadAllLines(resolvedPath);
        if (lines.Length == 0)
            return Task.FromResult(new ToolExecutionResult(true, Text: string.Empty));

        var startIndex = Math.Min(offset - 1, lines.Length);
        var selected = lines.Skip(startIndex).Take(limit).ToArray();
        var builder = new StringBuilder();
        for (var i = 0; i < selected.Length; i++)
            builder.AppendLine($"{startIndex + i + 1,6}→{selected[i]}");

        return Task.FromResult(new ToolExecutionResult(true, Text: builder.ToString().TrimEnd()));
    }

    private Task<ToolExecutionResult> ExecuteWriteAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var content = GetOptionalString(arguments, "content") ?? string.Empty;
        var resolvedPath = ResolvePath(filePath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(resolvedPath, content, Encoding.UTF8);
        return Task.FromResult(new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
        {
            ["filePath"] = resolvedPath,
            ["bytesWritten"] = Encoding.UTF8.GetByteCount(content),
        }));
    }

    private Task<ToolExecutionResult> ExecuteEditAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var oldString = GetRequiredString(arguments, "old_string");
        var newString = GetRequiredString(arguments, "new_string");
        var replaceAll = GetOptionalBool(arguments, "replace_all") ?? false;
        var resolvedPath = ResolvePath(filePath);
        if (!File.Exists(resolvedPath))
            return Task.FromResult(new ToolExecutionResult(false, $"File not found: {filePath}"));

        var content = File.ReadAllText(resolvedPath);
        var matches = CountOccurrences(content, oldString);
        if (matches == 0)
            return Task.FromResult(new ToolExecutionResult(false, "old_string not found"));
        if (!replaceAll && matches > 1)
            return Task.FromResult(new ToolExecutionResult(false, "old_string is not unique"));

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);
        File.WriteAllText(resolvedPath, updated, Encoding.UTF8);

        var replaced = replaceAll ? matches : 1;
        return Task.FromResult(new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
        {
            ["filePath"] = resolvedPath,
            ["occurrences"] = replaced,
        }));
    }

    private async Task<ToolExecutionResult> ExecuteBashAsync(JsonElement arguments)
    {
        var command = GetRequiredString(arguments, "command");
        var timeout = GetOptionalInt(arguments, "timeout") ?? 120000;
        var runInBackground = GetOptionalBool(arguments, "run_in_background") ?? false;
        var description = GetOptionalString(arguments, "description") ?? "Run shell command";

        var psi = new ProcessStartInfo(GetBashShellPath())
        {
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        ApplyPreferredShellEnvironment(psi.Environment);

        var process = Process.Start(psi);
        if (process is null)
            return new ToolExecutionResult(false, "无法启动 bash 进程");

        if (runInBackground)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var state = new BackgroundTaskState(taskId, description, process);
            _backgroundTasks[taskId] = state;
            _ = CaptureBackgroundProcessAsync(state);
            return new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
            {
                ["task_id"] = taskId,
                ["status"] = "running",
                ["description"] = description,
            });
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var text = BuildBashOutputText(stdout, stderr);

            return new ToolExecutionResult(process.ExitCode == 0, Message: process.ExitCode == 0 ? null : $"Command exited with code {process.ExitCode}", Text: text);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new ToolExecutionResult(false, $"Command timed out after {timeout}ms");
        }
        finally
        {
            process.Dispose();
        }
    }

    private Task<ToolExecutionResult> ExecuteGlobAsync(JsonElement arguments)
    {
        var pattern = GetRequiredString(arguments, "pattern");
        var basePath = ResolvePath(GetOptionalString(arguments, "path") ?? ".");
        if (!Directory.Exists(basePath))
            return Task.FromResult(new ToolExecutionResult(false, $"Directory not found: {basePath}"));

        var matches = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .Where(path => IsGlobMatch(Path.GetRelativePath(basePath, path), pattern) || IsGlobMatch(path, pattern))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        return Task.FromResult(new ToolExecutionResult(true, Text: string.Join("\n", matches)));
    }

    private Task<ToolExecutionResult> ExecuteGrepAsync(JsonElement arguments)
    {
        var pattern = GetRequiredString(arguments, "pattern");
        var basePath = ResolvePath(GetOptionalString(arguments, "path") ?? ".");
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
            return Task.FromResult(new ToolExecutionResult(false, $"Path not found: {basePath}"));

        var outputMode = GetOptionalString(arguments, "output_mode") ?? "files_with_matches";
        var glob = GetOptionalString(arguments, "glob");
        var ignoreCase = GetOptionalBool(arguments, "-i") ?? false;
        var headLimit = Math.Max(0, GetOptionalInt(arguments, "head_limit") ?? 0);
        var offset = Math.Max(0, GetOptionalInt(arguments, "offset") ?? 0);
        var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        var files = EnumerateSearchFiles(basePath, glob).ToArray();
        var fileMatches = new List<GrepFileMatch>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            var entries = new List<GrepLineMatch>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                    entries.Add(new GrepLineMatch(i + 1, lines[i]));
            }

            if (entries.Count > 0)
                fileMatches.Add(new GrepFileMatch(file, entries));
        }

        var text = outputMode switch
        {
            "count" => string.Join("\n", fileMatches
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)
                .Select(x => $"{x.Path}:{x.Lines.Count}")),
            "content" => string.Join("\n", fileMatches
                .SelectMany(x => x.Lines.Select(line => $"{x.Path}:{line.LineNumber}:{line.Text}"))
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)),
            _ => string.Join("\n", fileMatches
                .Select(x => x.Path)
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)),
        };

        return Task.FromResult(new ToolExecutionResult(true, Text: text));
    }

    private ToolExecutionResult ExecuteTaskCreate(JsonElement arguments)
    {
        var id = Interlocked.Increment(ref _taskSequence).ToString();
        var task = new TaskRecord(
            id,
            GetRequiredString(arguments, "subject"),
            GetRequiredString(arguments, "description"),
            GetOptionalString(arguments, "activeForm"),
            "pending",
            null,
            [],
            []);
        _tasks.Add(task);
        return new ToolExecutionResult(true, Payload: SerializeTask(task));
    }

    private ToolExecutionResult ExecuteTaskUpdate(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "taskId");
        var index = _tasks.FindIndex(x => x.Id == taskId);
        if (index < 0)
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        var current = _tasks[index];
        var updated = current with
        {
            Subject = GetOptionalString(arguments, "subject") ?? current.Subject,
            Description = GetOptionalString(arguments, "description") ?? current.Description,
            ActiveForm = GetOptionalString(arguments, "activeForm") ?? current.ActiveForm,
            Status = GetOptionalString(arguments, "status") ?? current.Status,
            Owner = GetOptionalString(arguments, "owner") ?? current.Owner,
            Blocks = MergeStringList(current.Blocks, GetOptionalStringArray(arguments, "addBlocks")),
            BlockedBy = MergeStringList(current.BlockedBy, GetOptionalStringArray(arguments, "addBlockedBy")),
        };
        _tasks[index] = updated;
        return new ToolExecutionResult(true, Payload: SerializeTask(updated));
    }

    private ToolExecutionResult ExecuteTaskList()
    {
        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(_tasks.Select(SerializeTaskSummary).ToArray()));
    }

    private ToolExecutionResult ExecuteTaskGet(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "taskId");
        var task = _tasks.FirstOrDefault(x => x.Id == taskId);
        return task is null
            ? new ToolExecutionResult(false, $"Task not found: {taskId}")
            : new ToolExecutionResult(true, Text: JsonSerializer.Serialize(SerializeTask(task)));
    }

    private ToolExecutionResult ExecuteTaskOutput(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "task_id");
        if (!_backgroundTasks.TryGetValue(taskId, out var state))
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(new
        {
            task_id = taskId,
            status = state.Status,
            stdout = state.Stdout.ToString(),
            stderr = state.Stderr.ToString(),
            exitCode = state.ExitCode,
        }));
    }

    private ToolExecutionResult ExecuteTaskStop(JsonElement arguments)
    {
        var taskId = GetOptionalString(arguments, "task_id") ?? GetOptionalString(arguments, "shell_id");
        if (string.IsNullOrWhiteSpace(taskId) || !_backgroundTasks.TryGetValue(taskId, out var state))
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        try
        {
            if (!state.Process.HasExited)
                state.Process.Kill(entireProcessTree: true);
            state.Status = "stopped";
            return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(new
            {
                task_id = taskId,
                status = state.Status,
            }));
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, ex.Message);
        }
    }

    private ToolExecutionResult ExecuteTodoWrite(JsonElement arguments)
    {
        var todos = GetTodoItems(arguments);
        _tasks.Clear();
        foreach (var todo in todos)
        {
            _tasks.Add(todo with { Id = string.IsNullOrWhiteSpace(todo.Id) ? Interlocked.Increment(ref _taskSequence).ToString() : todo.Id });
        }

        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(_tasks.Select(SerializeTaskSummary).ToArray()));
    }

    private async Task CaptureBackgroundProcessAsync(BackgroundTaskState state)
    {
        try
        {
            var stdoutTask = PumpReaderAsync(state.Process.StandardOutput, state.Stdout);
            var stderrTask = PumpReaderAsync(state.Process.StandardError, state.Stderr);
            await state.Process.WaitForExitAsync(_cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            state.ExitCode = state.Process.ExitCode;
            state.Status = state.Process.ExitCode == 0 ? "completed" : "failed";
        }
        catch (OperationCanceledException)
        {
            state.Status = "canceled";
        }
        catch (Exception ex)
        {
            state.Stderr.AppendLine(ex.Message);
            state.Status = "failed";
        }
    }

    private static async Task PumpReaderAsync(StreamReader reader, StringBuilder target)
    {
        while (await reader.ReadLineAsync() is { } line)
            target.AppendLine(line);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required field: {propertyName}");
        return value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result))
            return result;
        return null;
    }

    private static string[] GetOptionalStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_workDir, path));
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text.Remove(index, oldValue.Length).Insert(index, newValue);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateSearchFiles(string path, string? glob)
    {
        if (File.Exists(path))
        {
            if (string.IsNullOrWhiteSpace(glob) || IsGlobMatch(Path.GetFileName(path), glob))
                yield return path;
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (string.IsNullOrWhiteSpace(glob) || IsGlobMatch(Path.GetRelativePath(path, file), glob) || IsGlobMatch(Path.GetFileName(file), glob))
                yield return file;
        }
    }

    private static bool IsGlobMatch(string candidate, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^\\/]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(candidate.Replace('\\', '/'), regexPattern.Replace("\\/", "/"), RegexOptions.IgnoreCase);
    }

    private static string GetBashShellPath()
    {
        if (!OperatingSystem.IsWindows())
            return "/bin/bash";

        return GetPreferredGitBashPath() ?? "bash";
    }

    private static string BuildBashOutputText(string stdout, string stderr)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout))
            parts.Add(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            parts.Add(stderr.TrimEnd());
        return string.Join("\n", parts);
    }

    private static Dictionary<string, object?> SerializeTask(TaskRecord task) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = task.Id,
        ["subject"] = task.Subject,
        ["description"] = task.Description,
        ["activeForm"] = task.ActiveForm,
        ["status"] = task.Status,
        ["owner"] = task.Owner,
        ["blocks"] = task.Blocks.ToArray(),
        ["blockedBy"] = task.BlockedBy.ToArray(),
    };

    private static Dictionary<string, object?> SerializeTaskSummary(TaskRecord task) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = task.Id,
        ["subject"] = task.Subject,
        ["status"] = task.Status,
        ["owner"] = task.Owner,
        ["blockedBy"] = task.BlockedBy.ToArray(),
    };

    private static IReadOnlyList<string> MergeStringList(IReadOnlyList<string> current, IReadOnlyList<string> extra)
    {
        return current.Concat(extra)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<TaskRecord> GetTodoItems(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array)
            return [];

        var tasks = new List<TaskRecord>();
        foreach (var todo in todosEl.EnumerateArray())
        {
            tasks.Add(new TaskRecord(
                GetOptionalString(todo, "id") ?? string.Empty,
                GetOptionalString(todo, "content") ?? string.Empty,
                GetOptionalString(todo, "content") ?? string.Empty,
                null,
                NormalizeTodoStatus(GetOptionalString(todo, "status")),
                null,
                [],
                []));
        }
        return tasks;
    }

    private static string NormalizeTodoStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "in-progress" => "in_progress",
        "completed" => "completed",
        _ => "pending",
    };

    private static object BuildApprovalResponse(PendingRequest pending, PermissionResponse response)
    {
        return pending.Method switch
        {
            "item/permissions/requestApproval" => BuildPermissionsApprovalResponse(pending, response),
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => BuildDecisionResponse(response),
            _ => BuildDecisionResponse(response)
        };
    }

    private static object BuildDecisionResponse(PermissionResponse response) => new
    {
        decision = response.AllowAll
            ? "acceptForSession"
            : response.Allow ? "accept" : "decline"
    };

    private static object BuildPermissionsApprovalResponse(PendingRequest pending, PermissionResponse response)
    {
        var requestedPermissions = ExtractRequestedPermissions(pending.Payload);
        var grantedPermissions = response.Allow
            ? BuildGrantedPermissions(requestedPermissions, response)
            : new Dictionary<string, object?>();

        return new
        {
            permissions = grantedPermissions,
            scope = response.AllowAll ? "session" : "turn"
        };
    }

    private static Dictionary<string, object?> ExtractRequestedPermissions(Dictionary<string, object?>? payload)
    {
        if (payload is not null &&
            payload.TryGetValue("permissions", out var permissionsObj) &&
            permissionsObj is JsonElement permissionsEl &&
            permissionsEl.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(permissionsEl.GetRawText()) ?? new Dictionary<string, object?>();
        }

        return new Dictionary<string, object?>();
    }

    private static Dictionary<string, object?> BuildGrantedPermissions(Dictionary<string, object?> requestedPermissions, PermissionResponse response)
    {
        if (response.UpdatedInput is { Count: > 0 } &&
            TryExtractGrantedPermissions(response.UpdatedInput, out var explicitPermissions))
        {
            return explicitPermissions;
        }

        return requestedPermissions;
    }

    private static bool TryExtractGrantedPermissions(Dictionary<string, object?> updatedInput, out Dictionary<string, object?> permissions)
    {
        permissions = new Dictionary<string, object?>();

        if (updatedInput.TryGetValue("permissions", out var permissionsObj))
        {
            permissions = permissionsObj switch
            {
                JsonElement permissionsEl when permissionsEl.ValueKind == JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(permissionsEl.GetRawText()) ?? new Dictionary<string, object?>(),
                Dictionary<string, object?> permissionMap => new Dictionary<string, object?>(permissionMap, StringComparer.OrdinalIgnoreCase),
                _ => new Dictionary<string, object?>()
            };

            return permissions.Count > 0;
        }

        return false;
    }

    private static object BuildDynamicToolCallResponse(PendingRequest? pending, PermissionResponse response)
    {
        return BuildDynamicToolCallResponse(
            pending?.ToolName,
            response.Allow,
            response.Message,
            response.UpdatedInput,
            text: null);
    }

    private static object BuildDynamicToolCallResponse(string? toolName, bool allow, string? message = null, Dictionary<string, object?>? updatedInput = null, string? text = null)
    {
        return new
        {
            contentItems = BuildDynamicToolCallContentItems(toolName, allow, message, updatedInput, text),
            success = allow,
        };
    }

    private static IReadOnlyList<object> BuildDynamicToolCallContentItems(string? toolName, bool allow, string? message, Dictionary<string, object?>? updatedInput, string? text)
    {
        if (text is not null)
            return [new { type = "inputText", text }];

        if (updatedInput is { Count: > 0 })
        {
            if (string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase) &&
                TryExtractLegacyAnswerMap(updatedInput, out var answersJson))
            {
                return [new { type = "inputText", text = answersJson }];
            }

            return [new { type = "inputText", text = JsonSerializer.Serialize(updatedInput) }];
        }

        if (!string.IsNullOrWhiteSpace(message))
            return [new { type = "inputText", text = message }];

        return [];
    }

    private static object BuildToolRequestUserInputResponse(PendingRequest? pending, PermissionResponse response)
    {
        var answers = BuildToolRequestUserInputAnswers(pending?.Payload, response);
        return new { answers };
    }

    private static Dictionary<string, object> BuildToolRequestUserInputAnswers(Dictionary<string, object?>? payload, PermissionResponse response)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (payload is null ||
            !payload.TryGetValue("questions", out var questionsObj) ||
            questionsObj is not JsonElement questionsEl ||
            questionsEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var explicitAnswers = ExtractToolRequestUserInputAnswers(response.UpdatedInput);
        foreach (var questionEl in questionsEl.EnumerateArray())
        {
            var questionId = questionEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(questionId))
                continue;

            var answer = explicitAnswers.TryGetValue(questionId, out var explicitAnswer)
                ? explicitAnswer
                : response.Message ?? string.Empty;

            result[questionId] = new { answers = new[] { answer } };
        }

        return result;
    }

    private static Dictionary<string, string> ExtractToolRequestUserInputAnswers(Dictionary<string, object?>? updatedInput)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (updatedInput is null)
            return result;

        if (updatedInput.TryGetValue("answers", out var answersObj) && answersObj is Dictionary<string, object?> answersMap)
        {
            foreach (var (questionId, value) in answersMap)
            {
                if (value is null)
                    continue;

                result[questionId] = value.ToString() ?? string.Empty;
            }

            return result;
        }

        foreach (var (key, value) in updatedInput)
        {
            if (value is null)
                continue;

            result[key] = value.ToString() ?? string.Empty;
        }

        return result;
    }

    private static bool TryExtractLegacyAnswerMap(Dictionary<string, object?> updatedInput, out string answersJson)
    {
        answersJson = string.Empty;
        if (!updatedInput.TryGetValue("answers", out var answersObj) || answersObj is null)
            return false;

        answersJson = answersObj switch
        {
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(answersObj),
        };
        return !string.IsNullOrWhiteSpace(answersJson);
    }

    private static Dictionary<string, object?>? DeserializeObject(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText());
    }

    private static IReadOnlyList<UserQuestion> ParseUserQuestions(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return [];

        if (!arguments.TryGetProperty("questions", out var questionsEl) || questionsEl.ValueKind != JsonValueKind.Array)
        {
            if (arguments.TryGetProperty("question", out var singleQuestionEl) && !string.IsNullOrWhiteSpace(singleQuestionEl.GetString()))
            {
                return [new UserQuestion { Question = singleQuestionEl.GetString() ?? string.Empty }];
            }
            return [];
        }

        return ParseQuestionList(questionsEl, includeIds: false);
    }

    private static IReadOnlyList<UserQuestion> ParseToolRequestUserInputQuestions(JsonElement paramsEl)
    {
        if (!paramsEl.TryGetProperty("questions", out var questionsEl) || questionsEl.ValueKind != JsonValueKind.Array)
            return [];

        return ParseQuestionList(questionsEl, includeIds: true);
    }

    private static IReadOnlyList<UserQuestion> ParseQuestionList(JsonElement questionsEl, bool includeIds)
    {
        var questions = new List<UserQuestion>();
        foreach (var questionEl in questionsEl.EnumerateArray())
        {
            var questionText = questionEl.TryGetProperty("question", out var textEl) ? textEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(questionText))
                continue;

            var options = new List<UserQuestionOption>();
            if (questionEl.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var optionEl in optionsEl.EnumerateArray())
                {
                    var label = optionEl.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    options.Add(new UserQuestionOption
                    {
                        Label = label,
                        Description = optionEl.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? string.Empty : string.Empty,
                    });
                }
            }

            var header = questionEl.TryGetProperty("header", out var headerEl) ? headerEl.GetString() ?? string.Empty : string.Empty;
            if (includeIds && questionEl.TryGetProperty("id", out var idEl) && !string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                var id = idEl.GetString()!;
                header = string.IsNullOrWhiteSpace(header) ? $"id={id}" : $"{header} · id={id}";
            }

            questions.Add(new UserQuestion
            {
                Question = questionText,
                Header = header,
                MultiSelect = questionEl.TryGetProperty("multiSelect", out var multiEl) && multiEl.ValueKind is JsonValueKind.True or JsonValueKind.False && multiEl.GetBoolean(),
                Options = options,
            });
        }

        return questions;
    }

    private static string SummarizeToolRequestUserInput(JsonElement paramsEl)
    {
        var questions = ParseToolRequestUserInputQuestions(paramsEl);
        if (questions.Count == 0)
            return Truncate(paramsEl.GetRawText(), 200);

        return SummarizeQuestions(questions);
    }

    private static string SummarizeAskUserQuestion(JsonElement arguments)
    {
        var questions = ParseUserQuestions(arguments);
        if (questions.Count == 0)
            return Truncate(arguments.GetRawText(), 200);

        return SummarizeQuestions(questions);
    }

    private static string SummarizeQuestions(IEnumerable<UserQuestion> questions)
    {
        return string.Join("\n", questions.Select(question =>
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(question.Header))
                parts.Add($"[{question.Header}]");
            parts.Add(question.Question);
            if (question.Options.Count > 0)
                parts.Add("可选项: " + string.Join(" / ", question.Options.Select(x => x.Label)));
            return string.Join("\n", parts);
        }));
    }

    private static string ExtractAgentMessageDeltaText(JsonElement paramsEl)
    {
        foreach (var key in new[] { "delta", "text" })
        {
            if (!paramsEl.TryGetProperty(key, out var value))
                continue;

            var text = ExtractAgentMessageDeltaValue(value);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string ExtractAgentMessageDeltaValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => JoinDeltaSegments(element.EnumerateArray().Select(ExtractAgentMessageDeltaTextItem)),
            JsonValueKind.Object => ExtractAgentMessageDeltaTextItem(element),
            _ => string.Empty,
        };
    }

    private static string ExtractAgentMessageDeltaTextItem(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeEl)
            ? typeEl.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(type) && !IsPlainTextDeltaType(type))
            return string.Empty;

        foreach (var key in new[] { "text", "delta" })
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;

            var nested = ExtractAgentMessageDeltaValue(value);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return string.Empty;
    }

    private static bool IsPlainTextDeltaType(string type) =>
        type.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("output_text", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("input_text", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("text_delta", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("output_text_delta", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("input_text_delta", StringComparison.OrdinalIgnoreCase);

    private static string JoinDeltaSegments(IEnumerable<string> segments)
    {
        var builder = new StringBuilder();
        string? previous = null;

        foreach (var rawSegment in segments)
        {
            if (string.IsNullOrEmpty(rawSegment))
                continue;

            if (builder.Length > 0 && NeedsSeparator(previous, rawSegment))
                builder.Append('\n');

            builder.Append(rawSegment);
            previous = rawSegment;
        }

        return builder.ToString();
    }

    private static bool NeedsSeparator(string? previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            return false;

        return EndsWithMarkdownBoundary(previous) || StartsWithMarkdownBoundary(current);
    }

    private static bool EndsWithMarkdownBoundary(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            return true;

        var lastChar = trimmed[^1];
        return lastChar is ':' or '：' or '\n';
    }

    private static bool StartsWithMarkdownBoundary(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            return true;
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
            return true;
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            return true;

        return Regex.IsMatch(trimmed, @"^\d+[\)\.]\s*") ||
               trimmed.StartsWith("产物：", StringComparison.Ordinal) ||
               trimmed.StartsWith("关键结构", StringComparison.Ordinal) ||
               trimmed.StartsWith("构建结果", StringComparison.Ordinal) ||
               trimmed.StartsWith("并且存在", StringComparison.Ordinal) ||
               trimmed.StartsWith("如果你", StringComparison.Ordinal);
    }

    private static string ExtractText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Object => ExtractTextFromObject(element),
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(ExtractText)),
            _ => string.Empty,
        };
    }

    private static string ExtractTextFromObject(JsonElement element)
    {
        foreach (var key in new[] { "delta", "text", "message", "content", "output", "chunk", "additionalDetails", "reason" })
        {
            if (element.TryGetProperty(key, out var value))
            {
                var text = ExtractText(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var text = ExtractText(property.Value);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var ack in _pendingRpcAcks.Values)
            ack.TrySetResult(false);
        _pendingRpcAcks.Clear();
        if (_stdin is not null)
        {
            try { _stdin.Close(); } catch { }
            _stdin = null;
        }

        foreach (var backgroundTask in _backgroundTasks.Values)
        {
            try
            {
                if (!backgroundTask.Process.HasExited)
                    backgroundTask.Process.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                backgroundTask.Process.Dispose();
            }
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch { }
        }

        _process?.Dispose();
        _stdinWriteLock.Dispose();
        _cts.Dispose();
    }

    private async Task<Task<bool>> SendRequestWithAckAsync(string method, object @params, CancellationToken ct)
    {
        if (_stdin is null)
            throw new InvalidOperationException("会话未启动");

        var id = Interlocked.Increment(ref _requestId);
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRpcAcks.TryAdd(id, ack))
            throw new InvalidOperationException($"Codex RPC 请求 ID 冲突: {id}");

        try
        {
            var payload = JsonSerializer.Serialize(new { id, method, @params });
            _logger.LogInformation("→ codex stdin: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
            await WriteLineAsync(payload, ct);
            return ack.Task;
        }
        catch
        {
            _pendingRpcAcks.TryRemove(id, out _);
            throw;
        }
    }

    private async Task WriteLineAsync(string payload, CancellationToken ct)
    {
        if (_stdin is null)
            throw new InvalidOperationException("会话未启动");

        await _stdinWriteLock.WaitAsync(ct);
        try
        {
            if (_stdin is null)
                throw new InvalidOperationException("会话未启动");

            await _stdin.WriteLineAsync(payload);
        }
        finally
        {
            _stdinWriteLock.Release();
        }
    }

    private static bool TryGetNumericId(JsonElement idElement, out int id)
    {
        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out id))
            return true;

        if (idElement.ValueKind == JsonValueKind.String &&
            int.TryParse(idElement.GetString(), out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    private sealed record ToolExecutionResult(bool Success, string? Message = null, Dictionary<string, object?>? Payload = null, string? Text = null);

    private sealed class BackgroundTaskState(string taskId, string description, Process process)
    {
        public string TaskId { get; } = taskId;
        public string Description { get; } = description;
        public Process Process { get; } = process;
        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();
        public string Status { get; set; } = "running";
        public int? ExitCode { get; set; }
    }

    private sealed record GrepLineMatch(int LineNumber, string Text);

    private sealed record GrepFileMatch(string Path, IReadOnlyList<GrepLineMatch> Lines);

    private sealed record TaskRecord(
        string Id,
        string Subject,
        string Description,
        string? ActiveForm,
        string Status,
        string? Owner,
        IReadOnlyList<string> Blocks,
        IReadOnlyList<string> BlockedBy);

    private sealed record PendingRequest(PendingRequestKind Kind, string Method, string? ToolName, Dictionary<string, object?>? Payload = null);

    private enum PendingRequestKind
    {
        Approval,
        ToolCall,
        UserInput,
    }
}
