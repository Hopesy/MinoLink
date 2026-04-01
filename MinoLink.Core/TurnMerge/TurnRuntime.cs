using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core.TurnMerge;

internal sealed class TurnRuntime(string sessionKey)
{
    public string SessionKey { get; } = sessionKey;

    public object SyncRoot { get; } = new();

    public TurnRuntimeState State { get; set; }

    public TurnAggregate? Aggregate { get; set; }

    public IPlatform? Platform { get; set; }

    public SessionRecord? Session { get; set; }

    public bool UseSelectedAgentForStartup { get; set; }

    public CancellationTokenSource? WindowCts { get; set; }

    public CancellationTokenSource? ExecutionCts { get; set; }

    public void Reset()
    {
        WindowCts?.Cancel();
        WindowCts?.Dispose();
        WindowCts = null;
        ExecutionCts?.Cancel();
        ExecutionCts?.Dispose();
        ExecutionCts = null;
        Aggregate = null;
        Platform = null;
        Session = null;
        UseSelectedAgentForStartup = false;
        State = TurnRuntimeState.Idle;
    }
}
