using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core;

public sealed partial class Engine
{
    private async Task<bool> CmdGoalAsync(IPlatform platform, Message msg, string[] args)
    {
        if (!TryParseGoalCommand(args, out var command, out var error))
        {
            await platform.ReplyAsync(msg.ReplyContext, error!, _cts.Token);
            return true;
        }

        if (await ReplyIfTurnBusyAsync(platform, msg, "当前有消息正在处理，无法操作 goal。"))
            return true;

        var session = _sessions.GetOrCreate(msg.SessionKey, platform.Name, msg.From, msg.FromName);
        session.LastActiveAt = DateTimeOffset.UtcNow;
        _sessions.Save();

        var goalMessage = new Message
        {
            SessionKey = msg.SessionKey,
            From = msg.From,
            FromName = msg.FromName,
            Content = msg.Content,
            GoalCommand = command,
            ReplyContext = msg.ReplyContext,
            IsGroup = msg.IsGroup,
            ReceivedAt = msg.ReceivedAt,
        };

        if (!await _turnCoordinator.RunExclusiveAsync(platform, goalMessage, session, _cts.Token))
            await platform.ReplyAsync(msg.ReplyContext, "当前有消息正在处理，无法操作 goal。", _cts.Token);

        return true;
    }

    private static bool TryParseGoalCommand(string[] args, out AgentGoalCommand command, out string? error)
    {
        command = new AgentGoalCommand(AgentGoalAction.Get);
        error = null;

        if (args.Length == 0)
            return true;

        var first = args[0].Trim().ToLowerInvariant();
        if (IsGoalClearAlias(first))
        {
            if (args.Length > 1)
            {
                error = "用法: `/goal clear`";
                return false;
            }

            command = new AgentGoalCommand(AgentGoalAction.Clear);
            return true;
        }

        if (first is "pause" or "paused")
        {
            if (args.Length > 1)
            {
                error = "用法: `/goal pause`";
                return false;
            }

            command = new AgentGoalCommand(AgentGoalAction.Set, Status: "paused");
            return true;
        }

        if (first is "resume" or "active")
        {
            if (args.Length > 1)
            {
                error = "用法: `/goal resume`";
                return false;
            }

            command = new AgentGoalCommand(AgentGoalAction.Set, Status: "active");
            return true;
        }

        var objective = string.Join(' ', args).Trim();
        if (string.IsNullOrWhiteSpace(objective))
        {
            error = BuildGoalUsage();
            return false;
        }

        command = new AgentGoalCommand(AgentGoalAction.Set, objective);
        return true;
    }

    private static bool IsGoalClearAlias(string value) =>
        value is "clear" or "stop" or "off" or "reset" or "none" or "cancel";

    private static string BuildGoalUsage() =>
        """
        用法:
        `/goal` - 查看当前 goal
        `/goal <目标>` - 设置当前会话 goal
        `/goal clear` - 清除当前 goal
        `/goal pause` - 暂停当前 Codex goal
        `/goal resume` - 恢复当前 Codex goal
        """;

    private static string BuildGoalCommandReply(AgentGoalCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
            return result.Message!;

        if (result.Goal is null)
            return "当前没有 active goal。";

        return BuildGoalSummary(result.Goal);
    }

    private static string BuildGoalSummary(AgentGoal goal)
    {
        var budget = goal.TokenBudget is null
            ? goal.TokensUsed.ToString()
            : $"{goal.TokensUsed}/{goal.TokenBudget}";

        var elapsed = goal.TimeUsedSeconds > 0
            ? $"\n**耗时**: {FormatDuration(goal.TimeUsedSeconds)}"
            : string.Empty;

        return $"**Goal**: {goal.Objective}\n**状态**: {goal.Status}\n**Tokens**: {budget}{elapsed}";
    }

    private static string FormatDuration(long totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }
}
