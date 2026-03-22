namespace MinoLink.Core.Interfaces;

public interface IAutoStartService
{
    bool IsAutoStartEnabled();
    void SetAutoStart(bool enabled);

    /// <summary>当自启状态变化时触发，用于跨组件联动。</summary>
    event Action? Changed;
}
