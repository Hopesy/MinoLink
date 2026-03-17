namespace MinoLink.Core.Models;

/// <summary>
/// 权限审批响应。
/// </summary>
public sealed class PermissionResponse
{
    /// <summary>允许 / 拒绝。</summary>
    public required bool Allow { get; init; }

    /// <summary>是否对后续同类请求也自动允许。</summary>
    public bool AllowAll { get; init; }
}
