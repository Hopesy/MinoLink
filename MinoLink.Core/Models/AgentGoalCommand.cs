namespace MinoLink.Core.Models;

public enum AgentGoalAction
{
    Get,
    Set,
    Clear,
}

public sealed record AgentGoalCommand(
    AgentGoalAction Action,
    string? Objective = null,
    string Status = "active",
    long? TokenBudget = null);

public sealed record AgentGoal(
    string Objective,
    string Status,
    long TokensUsed,
    long? TokenBudget,
    long TimeUsedSeconds);

public sealed record AgentGoalCommandResult(
    bool Handled,
    bool StartsTurn,
    string? Message = null,
    AgentGoal? Goal = null)
{
    public static AgentGoalCommandResult Unsupported(string message) => new(false, false, message);

    public static AgentGoalCommandResult TurnStarted() => new(true, true);
}
