using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core.TurnMerge;

internal sealed class SessionTurnCoordinator(
    TurnMergeOptions options,
    Func<TurnExecutionRequest, CancellationToken, Task> executeAsync,
    Func<string, Task> interruptAsync,
    ILogger<SessionTurnCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, TurnRuntime> _runtimes = new();

    public async Task EnqueueAsync(IPlatform platform, Message msg, SessionRecord session, CancellationToken ct)
    {
        var runtime = _runtimes.GetOrAdd(msg.SessionKey, static key => new TurnRuntime(key));
        CancellationToken delayToken;
        TimeSpan delay;
        var interruptCurrentExecution = false;

        lock (runtime.SyncRoot)
        {
            runtime.Platform = platform;
            runtime.Session = session;
            if (runtime.State is TurnRuntimeState.Idle || runtime.Aggregate is null)
            {
                runtime.Aggregate = new TurnAggregate(msg);
                runtime.State = TurnRuntimeState.Buffering;
                delay = options.InitialMergeWindow;
                logger.LogInformation("TurnCreated: sessionKey={SessionKey}", msg.SessionKey);
            }
            else if (runtime.State is TurnRuntimeState.Buffering)
            {
                runtime.Aggregate.AppendMessage(msg);
                delay = options.InitialMergeWindow;
                logger.LogInformation("TurnMerged: sessionKey={SessionKey}, revision={Revision}",
                    msg.SessionKey, runtime.Aggregate.Revision);
            }
            else if (runtime.State is TurnRuntimeState.Running)
            {
                runtime.Aggregate.AppendMessage(msg);
                runtime.State = TurnRuntimeState.RestartPending;
                runtime.ExecutionCts?.Cancel();
                delay = options.RestartDebounceWindow;
                interruptCurrentExecution = true;
                logger.LogInformation("TurnCancelledForMerge: sessionKey={SessionKey}, revision={Revision}",
                    msg.SessionKey, runtime.Aggregate.Revision);
            }
            else if (runtime.State is TurnRuntimeState.RestartPending)
            {
                runtime.Aggregate.AppendMessage(msg);
                delay = options.RestartDebounceWindow;
                logger.LogInformation("TurnMergedWhileRestartPending: sessionKey={SessionKey}, revision={Revision}",
                    msg.SessionKey, runtime.Aggregate.Revision);
            }
            else
            {
                logger.LogInformation("当前 turn 已进入 {State}，暂未启用运行中重算: sessionKey={SessionKey}",
                    runtime.State, msg.SessionKey);
                return;
            }

            runtime.WindowCts?.Cancel();
            runtime.WindowCts?.Dispose();
            runtime.WindowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayToken = runtime.WindowCts.Token;
        }

        if (interruptCurrentExecution)
            await interruptAsync(msg.SessionKey);

        _ = Task.Run(() => FlushTurnWindowAsync(runtime, delay, delayToken, ct), CancellationToken.None);
    }

    public Task<bool> RunExclusiveAsync(IPlatform platform, Message msg, SessionRecord session, CancellationToken ct)
    {
        var runtime = _runtimes.GetOrAdd(msg.SessionKey, static key => new TurnRuntime(key));
        TurnExecutionRequest? request;
        CancellationTokenSource? executionCts;
        long executionGeneration;

        lock (runtime.SyncRoot)
        {
            if (runtime.State is not TurnRuntimeState.Idle || runtime.Aggregate is not null)
                return Task.FromResult(false);

            runtime.Platform = platform;
            runtime.Session = session;
            runtime.Aggregate = new TurnAggregate(msg);
            var snapshot = runtime.Aggregate.CreateSnapshot();
            request = new TurnExecutionRequest(platform, session, snapshot);
            runtime.State = TurnRuntimeState.Running;
            executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runtime.ExecutionCts = executionCts;
            executionGeneration = ++runtime.ExecutionGeneration;

            logger.LogInformation("TurnExclusiveExecutionStarted: sessionKey={SessionKey}, revision={Revision}",
                runtime.SessionKey, snapshot.Revision);
        }

        try
        {
            _ = Task.Run(() => ExecuteExclusiveAsync(runtime, request, executionCts, executionGeneration), CancellationToken.None);
            return Task.FromResult(true);
        }
        catch
        {
            executionCts.Dispose();
            throw;
        }
    }

    public Task<bool> ResetAsync(string sessionKey)
    {
        if (_runtimes.TryRemove(sessionKey, out var runtime))
        {
            lock (runtime.SyncRoot)
                runtime.Reset();
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public bool IsBusy(string sessionKey)
    {
        if (!_runtimes.TryGetValue(sessionKey, out var runtime))
            return false;

        lock (runtime.SyncRoot)
        {
            return runtime.State is TurnRuntimeState.Buffering
                or TurnRuntimeState.Running
                or TurnRuntimeState.RestartPending;
        }
    }

    private async Task FlushTurnWindowAsync(TurnRuntime runtime, TimeSpan delay, CancellationToken delayToken, CancellationToken executionParentToken)
    {
        try
        {
            await Task.Delay(delay, delayToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TurnExecutionRequest? request = null;
        CancellationTokenSource? executionCts = null;
        long executionGeneration = 0;

        lock (runtime.SyncRoot)
        {
            if (delayToken.IsCancellationRequested ||
                (runtime.State != TurnRuntimeState.Buffering && runtime.State != TurnRuntimeState.RestartPending) ||
                runtime.Aggregate is null ||
                runtime.WindowCts is null ||
                runtime.WindowCts.Token != delayToken ||
                runtime.Platform is null ||
                runtime.Session is null)
            {
                return;
            }

            var snapshot = runtime.Aggregate.CreateSnapshot();
            request = new TurnExecutionRequest(runtime.Platform, runtime.Session, snapshot);
            runtime.State = TurnRuntimeState.Running;
            runtime.WindowCts.Dispose();
            runtime.WindowCts = null;
            executionCts = CancellationTokenSource.CreateLinkedTokenSource(executionParentToken);
            runtime.ExecutionCts = executionCts;
            executionGeneration = ++runtime.ExecutionGeneration;

            logger.LogInformation("TurnExecutionStarted: sessionKey={SessionKey}, revision={Revision}",
                runtime.SessionKey, snapshot.Revision);
        }

        try
        {
            await executeAsync(request, executionCts!.Token);
        }
        finally
        {
            var disposeExecutionCts = executionCts;
            var staleExecution = false;
            CancellationToken restartToken = default;
            var scheduleRestart = false;
            lock (runtime.SyncRoot)
            {
                if (runtime.ExecutionGeneration != executionGeneration)
                {
                    logger.LogInformation(
                        "TurnStaleExecutionCompleted: sessionKey={SessionKey}, generation={Generation}, currentGeneration={CurrentGeneration}",
                        runtime.SessionKey,
                        executionGeneration,
                        runtime.ExecutionGeneration);
                    staleExecution = true;
                }
                else if (ReferenceEquals(runtime.ExecutionCts, executionCts))
                {
                    runtime.ExecutionCts = null;
                }

                if (!staleExecution && runtime.State == TurnRuntimeState.RestartPending)
                {
                    runtime.WindowCts?.Cancel();
                    runtime.WindowCts?.Dispose();
                    runtime.WindowCts = CancellationTokenSource.CreateLinkedTokenSource(executionParentToken);
                    restartToken = runtime.WindowCts.Token;
                    scheduleRestart = true;
                    logger.LogInformation("TurnRestartScheduled: sessionKey={SessionKey}", runtime.SessionKey);
                }
                else if (!staleExecution)
                {
                    runtime.Reset();
                    _runtimes.TryRemove(runtime.SessionKey, out _);
                    logger.LogInformation("TurnCompleted: sessionKey={SessionKey}", runtime.SessionKey);
                }
            }

            disposeExecutionCts?.Dispose();

            if (scheduleRestart)
                _ = Task.Run(() => FlushTurnWindowAsync(runtime, options.RestartDebounceWindow, restartToken, executionParentToken), CancellationToken.None);
        }
    }

    private async Task ExecuteExclusiveAsync(
        TurnRuntime runtime,
        TurnExecutionRequest request,
        CancellationTokenSource executionCts,
        long executionGeneration)
    {
        try
        {
            await executeAsync(request, executionCts.Token);
        }
        finally
        {
            lock (runtime.SyncRoot)
            {
                if (runtime.ExecutionGeneration == executionGeneration)
                {
                    runtime.Reset();
                    _runtimes.TryRemove(runtime.SessionKey, out _);
                    logger.LogInformation("TurnExclusiveCompleted: sessionKey={SessionKey}", runtime.SessionKey);
                }
            }

            executionCts.Dispose();
        }
    }
}

internal sealed record TurnExecutionRequest(
    IPlatform Platform,
    SessionRecord Session,
    TurnSnapshot Snapshot);
