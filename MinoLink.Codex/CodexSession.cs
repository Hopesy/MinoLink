using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Codex;

public sealed class CodexSession : IAgentSession
{
    private readonly Channel<AgentEvent> _eventChannel = Channel.CreateBounded<AgentEvent>(64);
    private readonly ILogger _logger;
    private readonly string _workDir;
    private readonly string? _model;
    private readonly string _mode;
    private readonly IAgentMessageEncoder _messageEncoder;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _useContinue;
    private readonly TaskCompletionSource<string> _threadReady = new();
    private readonly HashSet<string> _announcedItems = [];
    private readonly Dictionary<string, int> _agentMessageLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentMessagePhases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new(StringComparer.OrdinalIgnoreCase);

    private Process? _process;
    private StreamWriter? _stdin;
    private string _sessionId;
    private string? _turnId;
    private int _requestId;

    public string SessionId => _sessionId;
    public ChannelReader<AgentEvent> Events => _eventChannel.Reader;

    internal CodexSession(string sessionId, string workDir, string? model, string mode, ILogger logger, IAgentMessageEncoder? messageEncoder = null, bool useContinue = false)
    {
        _sessionId = sessionId;
        _workDir = workDir;
        _model = model;
        _mode = mode;
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

        if (!string.IsNullOrWhiteSpace(_model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_model);
        }
        psi.ArgumentList.Add("--ask-for-approval");
        psi.ArgumentList.Add(_mode);
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("workspace-write");
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

        if (!string.IsNullOrWhiteSpace(_sessionId))
            await SendRequestAsync("thread/resume", new { threadId = _sessionId });
        else
            await SendRequestAsync("thread/start", new { model = _model ?? "gpt-5.4" });

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

    public async Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default)
    {
        if (!_pendingRequests.Remove(requestId, out var pending))
        {
            _logger.LogWarning("未找到待响应的 Codex 请求: requestId={RequestId}", requestId);
            return;
        }

        object result = pending.Kind switch
        {
            PendingRequestKind.Approval => response.AllowAll
                ? "acceptForSession"
                : response.Allow ? "accept" : "decline",
            PendingRequestKind.ToolCall => new
            {
                contentItems = BuildToolCallContentItems(response),
                success = response.Allow,
            },
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
                        await HandleApprovalRequestAsync(root, writer, method!);
                        break;
                    case "item/tool/call":
                        await HandleToolCallAsync(root, writer);
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
                await writer.WriteAsync(new AgentEvent
                {
                    Type = AgentEventType.ToolUse,
                    ToolName = item.TryGetProperty("tool", out var dynToolEl) ? dynToolEl.GetString() : "dynamicToolCall",
                    ToolInput = item.TryGetProperty("arguments", out var argsEl) ? Truncate(argsEl.GetRawText(), 200) : string.Empty,
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
        var text = item.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(text))
            return;

        if (item.TryGetProperty("phase", out var phaseEl))
            _agentMessagePhases[itemId] = phaseEl.GetString() ?? string.Empty;

        var emittedLength = _agentMessageLengths.TryGetValue(itemId, out var length) ? length : 0;
        if (text.Length <= emittedLength)
            return;

        var delta = text[emittedLength..];
        _agentMessageLengths[itemId] = text.Length;
        await writer.WriteAsync(new AgentEvent
        {
            Type = GetAgentMessageEventType(itemId),
            Content = delta,
        });
    }

    private async Task HandleAgentMessageDeltaAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("params", out var paramsEl))
            return;

        var itemId = paramsEl.TryGetProperty("itemId", out var itemIdEl) ? itemIdEl.GetString() : null;
        var delta = ExtractText(paramsEl);
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

        _pendingRequests[requestId] = new PendingRequest(PendingRequestKind.Approval, method, null);

        var (toolName, summary) = method switch
        {
            "item/commandExecution/requestApproval" => ("Bash", SummarizeApproval(paramsEl, "command", "cwd", "reason")),
            "item/fileChange/requestApproval" => ("Edit", SummarizeApproval(paramsEl, "grantRoot", "reason")),
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
        _pendingRequests[requestId] = new PendingRequest(PendingRequestKind.ToolCall, "item/tool/call", toolName);

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

        _pendingRequests.Remove(requestId);
        await SendResponseAsync(requestId, new
        {
            contentItems = new object[] { new { type = "text", text = $"Unsupported tool call: {toolName}" } },
            success = false,
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
            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Error, Content = "Codex 当前回合已中断。" });
            return;
        }

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
        await _stdin.WriteLineAsync(payload);
    }

    private async Task SendNotificationAsync(string method, object @params)
    {
        if (_stdin is null)
            return;

        var payload = JsonSerializer.Serialize(new { method, @params });
        _logger.LogInformation("→ codex stdin: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
        await _stdin.WriteLineAsync(payload);
    }

    private async Task SendResponseAsync(string requestId, object result)
    {
        if (_stdin is null)
            return;

        object id = long.TryParse(requestId, out var numericId) ? numericId : requestId;
        var payload = JsonSerializer.Serialize(new { id, result });
        _logger.LogInformation("→ codex 响应: {Json}", payload.Length > 200 ? payload[..200] + "..." : payload);
        await _stdin.WriteLineAsync(payload);
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

    private static IReadOnlyList<object> BuildToolCallContentItems(PermissionResponse response)
    {
        if (response.UpdatedInput is { Count: > 0 })
        {
            if (response.UpdatedInput.TryGetValue("answers", out var answers) && answers is not null)
                return [new { type = "text", text = JsonSerializer.Serialize(answers) }];

            return [new { type = "text", text = JsonSerializer.Serialize(response.UpdatedInput) }];
        }

        if (!string.IsNullOrWhiteSpace(response.Message))
            return [new { type = "text", text = response.Message }];

        return [new { type = "text", text = response.Allow ? "accepted" : "declined" }];
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

            questions.Add(new UserQuestion
            {
                Question = questionText,
                Header = questionEl.TryGetProperty("header", out var headerEl) ? headerEl.GetString() ?? string.Empty : string.Empty,
                MultiSelect = questionEl.TryGetProperty("multiSelect", out var multiEl) && multiEl.ValueKind is JsonValueKind.True or JsonValueKind.False && multiEl.GetBoolean(),
                Options = options,
            });
        }

        return questions;
    }

    private static string SummarizeAskUserQuestion(JsonElement arguments)
    {
        var questions = ParseUserQuestions(arguments);
        if (questions.Count == 0)
            return Truncate(arguments.GetRawText(), 200);

        var parts = new List<string>();
        foreach (var question in questions)
        {
            if (!string.IsNullOrWhiteSpace(question.Header))
                parts.Add($"[{question.Header}]");
            parts.Add(question.Question);
            if (question.Options.Count > 0)
                parts.Add("可选项: " + string.Join(" / ", question.Options.Select(x => x.Label)));
        }
        return string.Join("\n", parts);
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
        if (_stdin is not null)
        {
            try { _stdin.Close(); } catch { }
            _stdin = null;
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
        _cts.Dispose();
    }

    private sealed record PendingRequest(PendingRequestKind Kind, string Method, string? ToolName);

    private enum PendingRequestKind
    {
        Approval,
        ToolCall,
    }
}
