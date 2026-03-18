using MinoLink.Core.Interfaces;

namespace MinoLink.Core;

/// <summary>
/// Agent 工厂注册表。插件通过 <see cref="Register"/> 注册自己。
/// </summary>
public static class AgentRegistry
{
    private static readonly Dictionary<string, Func<AgentOptions, IAgent>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string name, Func<AgentOptions, IAgent> factory) =>
        _factories[name] = factory;

    public static IAgent Create(string name, AgentOptions options) =>
        _factories.TryGetValue(name, out var factory)
            ? factory(options)
            : throw new InvalidOperationException($"Unknown agent type: '{name}'. Registered: [{string.Join(", ", _factories.Keys)}]");

    public static IReadOnlyCollection<string> RegisteredNames => _factories.Keys;
}

/// <summary>
/// Platform 工厂注册表。插件通过 <see cref="Register"/> 注册自己。
/// </summary>
public static class PlatformRegistry
{
    private static readonly Dictionary<string, Func<PlatformOptions, IPlatform>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string name, Func<PlatformOptions, IPlatform> factory) =>
        _factories[name] = factory;

    public static IPlatform Create(string name, PlatformOptions options) =>
        _factories.TryGetValue(name, out var factory)
            ? factory(options)
            : throw new InvalidOperationException($"Unknown platform type: '{name}'. Registered: [{string.Join(", ", _factories.Keys)}]");

    public static IReadOnlyCollection<string> RegisteredNames => _factories.Keys;
}

/// <summary>Agent 创建选项（从配置反序列化）。</summary>
public sealed class AgentOptions
{
    public string? Model { get; init; }
    public string Mode { get; init; } = "default";
    public Dictionary<string, object> Extra { get; init; } = [];
}

/// <summary>Platform 创建选项（从配置反序列化）。</summary>
public sealed class PlatformOptions
{
    public Dictionary<string, string> Settings { get; init; } = [];

    public string Get(string key, string defaultValue = "") =>
        Settings.TryGetValue(key, out var value) ? value : defaultValue;

    public bool GetBool(string key, bool defaultValue = false) =>
        Settings.TryGetValue(key, out var value) && bool.TryParse(value, out var result) ? result : defaultValue;
}
