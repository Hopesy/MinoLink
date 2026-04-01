namespace MinoLink.Core.TurnMerge;

/// <summary>
/// turn merge 时间窗口配置。
/// </summary>
public sealed class TurnMergeOptions
{
    public TimeSpan InitialMergeWindow { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RestartDebounceWindow { get; init; } = TimeSpan.FromSeconds(1);
}
