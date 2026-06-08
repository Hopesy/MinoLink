using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core;

public sealed partial class Engine
{
    private string ResolveProjectPath(string rawPath) => Path.IsPathRooted(rawPath)
        ? Path.GetFullPath(rawPath)
        : Path.GetFullPath(Path.Combine(_defaultWorkDir, rawPath));

    private AgentDirectiveResult TryApplyAgentDirective(SessionRecord session, ref Message msg)
    {
        var content = msg.Content.TrimStart();
        if (!content.StartsWith('#'))
            return AgentDirectiveResult.None;

        var firstSpace = content.IndexOf(' ');
        var directive = (firstSpace >= 0 ? content[..firstSpace] : content).Trim();
        var remaining = firstSpace >= 0 ? content[(firstSpace + 1)..].TrimStart() : string.Empty;

        var targetAgent = directive.ToLowerInvariant() switch
        {
            "#claude" => ClaudeAgentType,
            "#codex" => CodexAgentType,
            _ => null,
        };

        if (targetAgent is null)
            return AgentDirectiveResult.None;

        if (string.IsNullOrWhiteSpace(remaining) && string.IsNullOrWhiteSpace(session.AgentSessionId))
            remaining = targetAgent == CodexAgentType ? "开始一个新的 Codex 会话。" : "开始一个新的 Claude 会话。";

        var changed = !string.Equals(session.AgentType, targetAgent, StringComparison.OrdinalIgnoreCase);
        session.AgentType = targetAgent;
        session.AgentSessionId = null;
        session.PendingStartMode = null;
        session.PendingResumeSessionId = null;
        _sessions.Save();

        msg = new Message
        {
            SessionKey = msg.SessionKey,
            From = msg.From,
            FromName = msg.FromName,
            Content = remaining,
            Attachments = msg.Attachments,
            ReplyContext = msg.ReplyContext,
            IsGroup = msg.IsGroup,
            ReceivedAt = msg.ReceivedAt,
        };

        return new AgentDirectiveResult(changed);
    }

    private IAgent GetAgent(string agentType)
    {
        agentType = NormalizeAgentType(agentType);
        if (_agents.TryGetValue(agentType, out var existing))
            return existing;

        var created = _agentFactory(agentType);
        _agents[agentType] = created;
        return created;
    }

    private IAgent GetAgent(SessionRecord session) => GetAgent(session.AgentType);

    private static List<NativeSessionInfo> GetNativeSessions(string agentType, string workDir)
    {
        return agentType switch
        {
            CodexAgentType => CodexNativeSession.GetSessions(workDir),
            _ => ClaudeNativeSession.GetSessions(workDir),
        };
    }

    private static string GetConnectionNotice(string? pendingStartMode) =>
        IsRecoveryStartMode(pendingStartMode) ? "🎉 客户端已恢复" : "🎉 客户端已连接";

    private static string NormalizeAgentType(string? agentType) =>
        string.Equals(agentType, CodexAgentType, StringComparison.OrdinalIgnoreCase)
            ? CodexAgentType
            : ClaudeAgentType;

    private static bool IsRecoveryStartMode(string? pendingStartMode) =>
        string.Equals(pendingStartMode, StartModes.Continue, StringComparison.Ordinal) ||
        string.Equals(pendingStartMode, StartModes.Resume, StringComparison.Ordinal);

    private static string ResolveAgentTypeForStartup(SessionRecord session)
    {
        return NormalizeAgentType(session.AgentType);
    }

    private static string GetAgentDisplayName(string? agentType) =>
        NormalizeAgentType(agentType) == CodexAgentType ? "Codex" : "Claude";

    private static bool IsCodexAgent(string? agentType) =>
        NormalizeAgentType(agentType) == CodexAgentType;

    private static string GetModeFieldLabel(string? agentType) =>
        IsCodexAgent(agentType) ? "审批模式" : "权限模式";

    private static string GetModeDisplayName(string? agentType, string mode)
    {
        if (TryGetCodexModePresentation(agentType, mode, out var displayName, out _))
            return displayName;

        ModeDisplayNames.TryGetValue(mode, out var display);
        return display ?? mode;
    }

    private static string GetEffectiveModeForAgent(string? agentType, string mode)
    {
        if (!IsCodexAgent(agentType))
            return mode;

        return NormalizeCodexMode(mode);
    }

    private static void AppendModeOptions(System.Text.StringBuilder sb, string? agentType)
    {
        var options = IsCodexAgent(agentType)
            ? new[]
            {
                "  `default` - 映射到 `on-request + workspace-write`",
                "  `acceptedits` - 当前等价于 `default`，同样映射到 `on-request + workspace-write`",
                "  `plan` - 映射到 `untrusted + read-only`",
                "  `yolo` - 映射到 `never + danger-full-access`",
            }
            : new[]
            {
                "  `default` - 默认 (每次操作需确认)",
                "  `acceptedits` - 自动接受编辑",
                "  `plan` - 规划模式 (只读)",
                "  `yolo` - 自动批准所有操作",
            };

        foreach (var option in options)
            sb.AppendLine(option);
    }

    private static string GetSandboxDisplayName(string? agentType, string mode)
    {
        if (TryGetCodexModePresentation(agentType, mode, out _, out var sandboxDisplay))
            return sandboxDisplay;

        return string.Empty;
    }

    private static string NormalizeCodexMode(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "acceptedits" or "accept-edits" or "accept_edits" or "default" or "on-request" => "on-request",
            "plan" or "untrusted" => "untrusted",
            "bypasspermissions" or "bypass-permissions" or "yolo" or "auto" or "never" => "never",
            _ => mode,
        };

    private static bool TryGetCodexModePresentation(string? agentType, string mode, out string displayName, out string sandboxDisplay)
    {
        if (!IsCodexAgent(agentType))
        {
            displayName = string.Empty;
            sandboxDisplay = string.Empty;
            return false;
        }

        var effectiveMode = NormalizeCodexMode(mode);
        displayName = effectiveMode switch
        {
            "on-request" => "on-request (按需审批)",
            "untrusted" => "untrusted (只读规划)",
            "never" => "never (自动批准)",
            _ => effectiveMode,
        };

        sandboxDisplay = effectiveMode switch
        {
            "untrusted" => "read-only",
            "never" => "danger-full-access",
            _ => "workspace-write",
        };

        return true;
    }

    private string GetDisplaySessionId(string sessionKey, SessionRecord session)
    {
        if (_states.TryGetValue(sessionKey, out var state))
            return state.AgentSession.SessionId;

        return session.PendingStartMode switch
        {
            StartModes.Continue => "(待恢复最近会话)",
            StartModes.Resume when !string.IsNullOrWhiteSpace(session.PendingResumeSessionId) => session.PendingResumeSessionId!,
            _ => session.AgentSessionId switch
            {
                { Length: > 0 } sid => sid,
                _ => "(未启动)",
            },
        };
    }

    private static bool TryEnsureProjectDirectory(string targetDir, out bool createdDirectory, out string? error)
    {
        createdDirectory = !Directory.Exists(targetDir);
        error = null;

        try
        {
            Directory.CreateDirectory(targetDir);
            return true;
        }
        catch (Exception ex)
        {
            createdDirectory = false;
            error = ex.Message;
            return false;
        }
    }
}
