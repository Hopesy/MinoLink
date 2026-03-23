using System.Diagnostics;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MinoLink.ClaudeCode;

/// <summary>
/// Claude Code CLI Agent 适配器。
/// 通过启动 <c>claude</c> 子进程，使用 stdin/stdout JSON 流通信。
/// </summary>
public sealed class ClaudeCodeAgent : IAgent
{
    private readonly string? _model;
    private string _mode;
    private readonly ILogger<ClaudeCodeAgent> _logger;

    public string Name => "claudecode";
    public string Mode => _mode;

    public ClaudeCodeAgent(AgentOptions options, ILogger<ClaudeCodeAgent> logger)
    {
        _model = options.Model;
        _mode = NormalizeMode(options.Mode);
        _logger = logger;

        // 验证 claude CLI 可用（仅警告，不阻断启动）
        ValidateCliAvailable();
    }

    public async Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct)
    {
        var session = new ClaudeSession(sessionId, workDir, _model, _mode, _logger);
        await session.StartAsync(ct);
        return session;
    }

    public async Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct)
    {
        var session = new ClaudeSession("", workDir, _model, _mode, _logger, useContinue: true);
        await session.StartAsync(ct);
        return session;
    }

    public void SetMode(string mode)
    {
        _mode = NormalizeMode(mode);
        _logger.LogInformation("权限模式已切换: {Mode}", _mode);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ValidateCliAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("claude", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is not null && !proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _logger.LogWarning("Claude CLI --version 超时 5s，已强制终止");
                return;
            }
            _logger.LogInformation("Claude CLI 可用, mode={Mode}", _mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "找不到 'claude' CLI，请先安装 Claude Code: npm install -g @anthropic-ai/claude-code。" +
                "消息将无法处理，直到 claude CLI 可用。");
        }
    }

    private static string NormalizeMode(string mode) => mode.ToLowerInvariant() switch
    {
        "acceptedits" or "accept-edits" or "accept_edits" => "acceptEdits",
        "plan" => "plan",
        "bypasspermissions" or "bypass-permissions" or "yolo" or "auto" => "bypassPermissions",
        _ => "default",
    };

    /// <summary>注册到全局 Agent 注册表。</summary>
    public static void Register(ILoggerFactory loggerFactory) =>
        AgentRegistry.Register("claudecode", opts => new ClaudeCodeAgent(opts, loggerFactory.CreateLogger<ClaudeCodeAgent>()));
}
