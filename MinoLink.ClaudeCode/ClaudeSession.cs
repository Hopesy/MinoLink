using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using Microsoft.Extensions.Logging;

namespace MinoLink.ClaudeCode;

/// <summary>
/// 管理一个 Claude Code CLI 子进程的会话。
/// 通过 stdin 写入 stream-json 消息，从 stdout 读取 stream-json 事件。
/// </summary>
public sealed class ClaudeSession : IAgentSession
{
    private readonly Channel<AgentEvent> _eventChannel = Channel.CreateBounded<AgentEvent>(64);
    private readonly ILogger _logger;
    private readonly string _workDir;
    private readonly string? _model;
    private readonly string _mode;
    private readonly CancellationTokenSource _cts = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private string _sessionId;
    private readonly bool _useContinue;

    public string SessionId => _sessionId;
    public ChannelReader<AgentEvent> Events => _eventChannel.Reader;

    public ClaudeSession(string sessionId, string workDir, string? model, string mode, ILogger logger, bool useContinue = false)
    {
        _sessionId = sessionId;
        _workDir = workDir;
        _model = model;
        _mode = mode;
        _logger = logger;
        _useContinue = useContinue;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var args = BuildArgs();
        _logger.LogInformation("启动 claude 进程: claude {Args}", string.Join(" ", args));

        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = _workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,   // stderr 不重定向，直接显示在窗口中便于排查
            UseShellExecute = false,
            CreateNoWindow = false,           // 显示 claude 进程窗口
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // 过滤 CLAUDECODE 环境变量，避免被检测为嵌套会话
        var env = psi.Environment;
        env.Remove("CLAUDECODE");
        ApplyPreferredShellEnvironment(env);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 claude 进程");
        _logger.LogInformation("claude 进程已启动: PID={Pid}", _process.Id);
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;

        // 后台读取 stdout 事件流
        _ = Task.Run(() => ReadLoopAsync(_process.StandardOutput), _cts.Token);

        return Task.CompletedTask;
    }

    public async Task SendAsync(string content, IReadOnlyList<string>? images = null, CancellationToken ct = default)
    {
        if (_stdin is null) throw new InvalidOperationException("会话未启动");

        var msg = new
        {
            type = "user",
            message = new { role = "user", content },
        };

        var json = JsonSerializer.Serialize(msg);
        _logger.LogInformation("→ stdin: {Json}", json.Length > 200 ? json[..200] + "..." : json);
        await _stdin.WriteLineAsync(json);
    }

    public async Task RespondPermissionAsync(string requestId, PermissionResponse response, CancellationToken ct = default)
    {
        if (_stdin is null) return;

        object permResponse = response.Allow
            ? new { behavior = "allow", updatedInput = response.UpdatedInput ?? new Dictionary<string, object?>() }
            : new { behavior = "deny", message = response.Message ?? "The user denied this tool use. Stop and wait for the user's instructions." };

        var msg = new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = permResponse,
            },
        };

        var json = JsonSerializer.Serialize(msg);
        _logger.LogInformation("→ 权限响应: {RequestId} allow={Allow} allowAll={AllowAll}",
            requestId, response.Allow, response.AllowAll);
        await _stdin.WriteLineAsync(json);
    }

    private async Task ReadLoopAsync(StreamReader stdout)
    {
        var writer = _eventChannel.Writer;
        _logger.LogInformation("ReadLoop 启动，等待 claude stdout...");
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    _logger.LogWarning("claude stdout 已关闭 (进程退出), exitCode={ExitCode}",
                        _process?.HasExited == true ? _process.ExitCode.ToString() : "unknown");
                    break;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;

                _logger.LogDebug("← stdout: {Line}", line.Length > 300 ? line[..300] + "..." : line);

                try
                {
                    var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var eventType = root.GetProperty("type").GetString();
                    _logger.LogInformation("claude 事件: {Type}", eventType);

                    switch (eventType)
                    {
                        case "system":
                            HandleSystem(root);
                            break;
                        case "assistant":
                            await HandleAssistantAsync(root, writer);
                            break;
                        case "result":
                            await HandleResultAsync(root, writer);
                            break;
                        case "control_request":
                            await HandleControlRequestAsync(root, writer);
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug("非 JSON 行: {Line} ({Error})", line[..Math.Min(100, line.Length)], ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 claude stdout 异常");
            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Error, Content = ex.Message });
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private void HandleSystem(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid))
        {
            var id = sid.GetString();
            if (!string.IsNullOrEmpty(id))
            {
                _sessionId = id;
                _logger.LogDebug("会话 ID 更新: {SessionId}", id);
            }
        }
    }

    private async Task HandleAssistantAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var contentArr)) return;

        foreach (var item in contentArr.EnumerateArray())
        {
            var contentType = item.GetProperty("type").GetString();
            switch (contentType)
            {
                case "thinking":
                    if (item.TryGetProperty("thinking", out var thinking))
                    {
                        var text = thinking.GetString();
                        if (!string.IsNullOrEmpty(text))
                            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Thinking, Content = text });
                    }
                    break;

                case "text":
                    if (item.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                            await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Text, Content = text });
                    }
                    break;

                case "tool_use":
                    var toolName = item.GetProperty("name").GetString() ?? "unknown";
                    if (toolName == "AskUserQuestion") continue;

                    var inputSummary = SummarizeToolInput(toolName, item);
                    await writer.WriteAsync(new AgentEvent
                    {
                        Type = AgentEventType.ToolUse,
                        ToolName = toolName,
                        ToolInput = inputSummary,
                    });
                    break;
            }
        }
    }

    private async Task HandleResultAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var content = root.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "";

        if (root.TryGetProperty("session_id", out var sid))
        {
            var id = sid.GetString();
            if (!string.IsNullOrEmpty(id)) _sessionId = id;
        }

        await writer.WriteAsync(new AgentEvent { Type = AgentEventType.Result, Content = content });
    }

    private async Task HandleControlRequestAsync(JsonElement root, ChannelWriter<AgentEvent> writer)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        if (!root.TryGetProperty("request", out var request)) return;

        var subtype = request.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool") return;

        var toolName = request.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var inputSummary = SummarizeToolInput(toolName, request);
        _logger.LogInformation("收到工具权限请求: requestId={RequestId}, tool={Tool}, input={Input}",
            requestId, toolName, inputSummary);

        if (toolName == "AskUserQuestion")
        {
            await writer.WriteAsync(new AgentEvent
            {
                Type = AgentEventType.UserQuestion,
                RequestId = requestId,
                ToolName = toolName,
                ToolInput = inputSummary,
                ToolInputRaw = DeserializeInput(request),
                Questions = ParseUserQuestions(request),
            });
            return;
        }

        // bypassPermissions 模式自动批准
        if (_mode == "bypassPermissions")
        {
            _logger.LogDebug("自动批准权限: {Tool}", toolName);
            await RespondPermissionAsync(requestId, new PermissionResponse { Allow = true });
            return;
        }

        await writer.WriteAsync(new AgentEvent
        {
            Type = AgentEventType.PermissionRequest,
            RequestId = requestId,
            ToolName = toolName,
            ToolInput = inputSummary,
            ToolInputRaw = DeserializeInput(request),
        });
    }

    private static string SummarizeToolInput(string toolName, JsonElement element)
    {
        if (!element.TryGetProperty("input", out var input)) return "";

        return toolName switch
        {
            "Read" or "Edit" or "Write" =>
                input.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "",
            "Bash" =>
                input.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "",
            "AskUserQuestion" =>
                ExtractQuestionSummary(input),
            "Grep" or "Glob" =>
                input.TryGetProperty("pattern", out var pat) ? pat.GetString() ?? "" : "",
            _ => input.GetRawText().Length > 200 ? input.GetRawText()[..200] + "..." : input.GetRawText(),
        };
    }

    private static Dictionary<string, object?>? DeserializeInput(JsonElement element)
    {
        if (!element.TryGetProperty("input", out var input))
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(input.GetRawText());
    }

    private static IReadOnlyList<UserQuestion> ParseUserQuestions(JsonElement element)
    {
        if (!element.TryGetProperty("input", out var input) ||
            !input.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<UserQuestion>();
        foreach (var question in questions.EnumerateArray())
        {
            var text = question.TryGetProperty("question", out var questionText)
                ? questionText.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var options = new List<UserQuestionOption>();
            if (question.TryGetProperty("options", out var optionElements) && optionElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionElements.EnumerateArray())
                {
                    var label = option.TryGetProperty("label", out var labelElement)
                        ? labelElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    var description = option.TryGetProperty("description", out var descriptionElement)
                        ? descriptionElement.GetString() ?? string.Empty
                        : string.Empty;

                    options.Add(new UserQuestionOption
                    {
                        Label = label,
                        Description = description,
                    });
                }
            }

            result.Add(new UserQuestion
            {
                Question = text!,
                Header = question.TryGetProperty("header", out var headerElement)
                    ? headerElement.GetString() ?? string.Empty
                    : string.Empty,
                Options = options,
                MultiSelect = question.TryGetProperty("multiSelect", out var multiSelectElement) &&
                              multiSelectElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                              multiSelectElement.GetBoolean(),
            });
        }

        return result;
    }

    private static string ExtractQuestionSummary(JsonElement input)
    {
        if (input.TryGetProperty("question", out var singleQuestion))
            return singleQuestion.GetString() ?? "请补充信息";

        if (!input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return input.GetRawText();

        var parts = new List<string>();
        foreach (var question in questions.EnumerateArray())
        {
            var header = question.TryGetProperty("header", out var headerElement)
                ? headerElement.GetString()
                : null;
            var text = question.TryGetProperty("question", out var questionElement)
                ? questionElement.GetString()
                : null;
            var labels = ExtractOptionLabels(question);

            if (!string.IsNullOrWhiteSpace(header))
                parts.Add($"[{header}]");
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text!);
            if (labels.Count > 0)
                parts.Add("可选项: " + string.Join(" / ", labels));
        }

        return parts.Count > 0 ? string.Join("\n", parts) : input.GetRawText();
    }

    private static IReadOnlyList<string> ExtractOptionLabels(JsonElement input)
    {
        if (!input.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return [];

        var labels = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.String)
            {
                var value = option.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    labels.Add(value);
                continue;
            }

            if (option.ValueKind == JsonValueKind.Object &&
                option.TryGetProperty("label", out var labelElement) &&
                !string.IsNullOrWhiteSpace(labelElement.GetString()))
            {
                labels.Add(labelElement.GetString()!);
            }
        }

        return labels;
    }

    private List<string> BuildArgs()
    {
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--verbose",
            "--input-format", "stream-json",
            "--permission-prompt-tool", "stdio",
        };

        if (_mode is not ("" or "default"))
            args.AddRange(["--permission-mode", _mode]);

        if (_useContinue)
            args.Add("--continue");
        else if (!string.IsNullOrEmpty(_sessionId))
            args.AddRange(["--resume", _sessionId]);

        if (!string.IsNullOrEmpty(_model))
            args.AddRange(["--model", _model]);

        return args;
    }

    private void ApplyPreferredShellEnvironment(IDictionary<string, string?> env)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var gitBashPath = GetPreferredGitBashPath();
        if (gitBashPath is null)
        {
            _logger.LogWarning("未找到 Git Bash，Claude 的 Bash 工具将继续使用系统默认 bash。");
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

        _logger.LogInformation("已为 Claude 子进程优先配置 Git Bash: {BashPath}", gitBashPath);
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

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_stdin is not null)
        {
            try { _stdin.Close(); } catch { /* ignore */ }
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch { /* ignore */ }
        }

        _process?.Dispose();
        _cts.Dispose();
    }
}
