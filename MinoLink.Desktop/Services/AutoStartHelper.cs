using Microsoft.Win32;
using MinoLink.Core.Interfaces;

namespace MinoLink.Desktop.Services;

public sealed class AutoStartHelper : IAutoStartService
{
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MinoLink";

    /// <summary>当自启状态变化时触发，用于跨组件联动。</summary>
    public event Action? Changed;

    public bool IsAutoStartEnabled() => IsEnabled();

    public void SetAutoStart(bool enabled)
    {
        SetEnabled(enabled);
        Changed?.Invoke();
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
        return key?.GetValue(AppName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (key is null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? "";
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
