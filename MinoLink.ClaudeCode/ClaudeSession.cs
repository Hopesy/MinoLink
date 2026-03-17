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

    public string SessionId => _sessionId;
    public ChannelReader<AgentEvent> Events => _eventChannel.Reader;

    public ClaudeSession(string sessionId, string workDir, string? model, string mode, ILogger logger)
    {
        _sessionId = sessionId;
        _workDir = workDir;
        _model = model;
        _mode = mode;
        _logger = logger;
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
            ? new { behavior = "allow", updatedInput = new { } }
            : new { behavior = "deny", message = "The user denied this tool use. Stop and wait for the user's instructions." };

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
        _logger.LogInformation("→ 权限响应: {RequestId} allow={Allow}", requestId, response.Allow);
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
            "Grep" or "Glob" =>
                input.TryGetProperty("pattern", out var pat) ? pat.GetString() ?? "" : "",
            _ => input.GetRawText().Length > 200 ? input.GetRawText()[..200] + "..." : input.GetRawText(),
        };
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

        if (!string.IsNullOrEmpty(_sessionId))
            args.AddRange(["--resume", _sessionId]);

        if (!string.IsNullOrEmpty(_model))
            args.AddRange(["--model", _model]);

        return args;
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
