using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using MinoLink.ClaudeCode;
using MinoLink.Core.Models;

namespace MinoLink.Tests.ClaudeCode;

public sealed class ClaudeSessionInterruptBehaviorTests
{
    [Fact]
    public async Task InterruptConfirmed_WhenResidualEmptySuccessResultArrives_ShouldSuppressOldCompletion()
    {
        await using var session = CreateSession();
        RegisterPendingInterrupt(session, "req-1");

        await RunReadLoopAsync(session,
            """
            {"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}
            """,
            """
            {"type":"result","subtype":"success","is_error":false,"result":"","stop_reason":null,"session_id":"session-1"}
            """);

        var events = await ReadAllEventsAsync(session.Events);
        Assert.Empty(events);
    }

    [Fact]
    public async Task InterruptConfirmed_WhenNewAssistantOutputArrives_ShouldClearSuppressionAndKeepFinalResult()
    {
        await using var session = CreateSession();
        RegisterPendingInterrupt(session, "req-1");

        await RunReadLoopAsync(session,
            """
            {"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}
            """,
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"继续处理你的请求"}]}}
            """,
            """
            {"type":"result","subtype":"success","is_error":false,"result":"最终结果","stop_reason":null,"session_id":"session-1"}
            """);

        var events = await ReadAllEventsAsync(session.Events);
        Assert.Collection(events,
            evt =>
            {
                Assert.Equal(AgentEventType.Text, evt.Type);
                Assert.Equal("继续处理你的请求", evt.Content);
            },
            evt =>
            {
                Assert.Equal(AgentEventType.Result, evt.Type);
                Assert.Equal("最终结果", evt.Content);
            });
    }

    [Fact]
    public async Task InterruptConfirmed_WhenResidualToolUseAbortTailArrivesBeforeNextSystem_ShouldSuppressIt()
    {
        await using var session = CreateSession();
        RegisterPendingInterrupt(session, "req-1");

        await RunReadLoopAsync(session,
            """
            {"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}
            """,
            """
            {"type":"user","session_id":"session-1","parent_tool_use_id":"tool-1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"Permission prompt was aborted.","is_error":true}]}}
            """,
            """
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"","stop_reason":"tool_use","session_id":"session-1","errors":["Permission prompt was aborted."]}
            """);

        var events = await ReadAllEventsAsync(session.Events);
        Assert.Empty(events);
    }

    [Fact]
    public async Task InterruptConfirmed_WhenResidualToolUseAbortTailThenNextTurnStarts_ShouldOnlyEmitNewTurnEvents()
    {
        await using var session = CreateSession();
        RegisterPendingInterrupt(session, "req-1");

        await RunReadLoopAsync(session,
            """
            {"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}
            """,
            """
            {"type":"user","session_id":"session-1","parent_tool_use_id":"tool-1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"Permission prompt was aborted.","is_error":true}]}}
            """,
            """
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"","stop_reason":"tool_use","session_id":"session-1","errors":["Permission prompt was aborted."]}
            """,
            """
            {"type":"system","session_id":"session-1"}
            """,
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"继续执行新任务"}]}}
            """,
            """
            {"type":"result","subtype":"success","is_error":false,"result":"新任务完成","stop_reason":null,"session_id":"session-1"}
            """);

        var events = await ReadAllEventsAsync(session.Events);
        Assert.Collection(events,
            evt =>
            {
                Assert.Equal(AgentEventType.Text, evt.Type);
                Assert.Equal("继续执行新任务", evt.Content);
            },
            evt =>
            {
                Assert.Equal(AgentEventType.Result, evt.Type);
                Assert.Equal("新任务完成", evt.Content);
            });
    }

    private static ClaudeSession CreateSession()
    {
        return (ClaudeSession)Activator.CreateInstance(
            typeof(ClaudeSession),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                "",
                Environment.CurrentDirectory,
                null,
                "default",
                NullLogger.Instance,
                null,
                false,
            ],
            culture: null)!;
    }

    private static void RegisterPendingInterrupt(ClaudeSession session, string requestId)
    {
        var field = typeof(ClaudeSession).GetField("_pendingInterrupts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pendingInterrupts = (ConcurrentDictionary<string, TaskCompletionSource<bool>>)field.GetValue(session)!;
        pendingInterrupts[requestId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task RunReadLoopAsync(ClaudeSession session, params string[] lines)
    {
        var payload = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var method = typeof(ClaudeSession).GetMethod("ReadLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(session, [reader])!;
    }

    private static async Task<List<AgentEvent>> ReadAllEventsAsync(ChannelReader<AgentEvent> reader)
    {
        var events = new List<AgentEvent>();
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var evt))
                events.Add(evt);
        }

        return events;
    }
}
