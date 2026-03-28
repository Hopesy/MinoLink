using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MinoLink.Core;
using MinoLink.Core.Interfaces;

namespace MinoLink.Codex;

public sealed class CodexAgent : IAgent
{
    private readonly string? _model;
    private string _mode;
    private string _sandboxMode;
    private readonly ILogger<CodexAgent> _logger;

    public string Name => "codex";
    public string Mode => _mode;

    public CodexAgent(AgentOptions options, ILogger<CodexAgent> logger)
    {
        _model = options.Model;
        _mode = NormalizeMode(options.Mode);
        _sandboxMode = ResolveSandboxMode(_mode);
        _logger = logger;
        ValidateCliAvailable();
    }

    public async Task<IAgentSession> StartSessionAsync(string sessionId, string workDir, CancellationToken ct)
    {
        var session = new CodexSession(sessionId, workDir, _model, _mode, _sandboxMode, _logger);
        await session.StartAsync(ct);
        return session;
    }

    public async Task<IAgentSession> ContinueSessionAsync(string workDir, CancellationToken ct)
    {
        var latestSessionId = FindLatestSessionId(workDir);
        _logger.LogInformation("Codex continue: workDir={WorkDir}, latestSessionId={SessionId}", workDir, latestSessionId ?? "<none>");
        var session = new CodexSession(latestSessionId ?? string.Empty, workDir, _model, _mode, _sandboxMode, _logger, useContinue: true);
        await session.StartAsync(ct);
        return session;
    }

    public void SetMode(string mode)
    {
        _mode = NormalizeMode(mode);
        _sandboxMode = ResolveSandboxMode(_mode);
        _logger.LogInformation("Codex 权限模式已切换: {Mode}, sandbox={Sandbox}", _mode, _sandboxMode);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ValidateCliAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(ResolveCodexPath(), "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is not null && !proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _logger.LogWarning("Codex CLI --version 超时 5s，已强制终止");
                return;
            }
            _logger.LogInformation("Codex CLI 可用, mode={Mode}", _mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "找不到 'codex' CLI，Codex 消息将无法处理。");
        }
    }

    internal static string ResolveCodexPath()
    {
        if (!OperatingSystem.IsWindows())
            return "codex";

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            var cmdPath = Path.Combine(dir, "codex.cmd");
            if (File.Exists(cmdPath)) return cmdPath;
            var exePath = Path.Combine(dir, "codex.exe");
            if (File.Exists(exePath)) return exePath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var npmCmd = Path.Combine(appData, "npm", "codex.cmd");
            if (File.Exists(npmCmd)) return npmCmd;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var npmGlobal = Path.Combine(userProfile, ".npm-global", "codex.cmd");
            if (File.Exists(npmGlobal)) return npmGlobal;

            var roamingNpm = Path.Combine(userProfile, "AppData", "Roaming", "npm", "codex.cmd");
            if (File.Exists(roamingNpm)) return roamingNpm;
        }

        return "codex";
    }

    private static string? FindLatestSessionId(string workDir)
    {
        return CodexNativeSession.GetSessions(workDir).FirstOrDefault()?.SessionId;
    }

    private static string NormalizeMode(string mode) => mode.ToLowerInvariant() switch
    {
        "acceptedits" or "accept-edits" or "accept_edits" => "on-request",
        "plan" => "untrusted",
        "bypasspermissions" or "bypass-permissions" or "yolo" or "auto" => "never",
        _ => "on-request",
    };

    private static string ResolveSandboxMode(string mode) => mode switch
    {
        "untrusted" => "read-only",
        "never" => "danger-full-access",
        _ => "workspace-write",
    };
}
