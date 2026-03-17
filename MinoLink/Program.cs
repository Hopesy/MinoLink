using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinoLink.ClaudeCode;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Feishu;

var builder = Host.CreateApplicationBuilder(args);

// 加载配置
builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddEnvironmentVariables("MINO_");

var config = builder.Configuration.GetSection("MinoLink").Get<MinoLinkConfig>()
    ?? throw new InvalidOperationException("配置缺失：请在 appsettings.json 中配置 MinoLink 节");

// 注册 Agent
builder.Services.AddSingleton<IAgent>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ClaudeCodeAgent>>();
    return new ClaudeCodeAgent(new AgentOptions
    {
        WorkDir = config.Agent.WorkDir,
        Model = config.Agent.Model,
        Mode = config.Agent.Mode ?? "default",
    }, logger);
});

// 注册飞书平台
if (config.Feishu is { AppId: not null and not "" })
{
    var feishuOpts = new FeishuPlatformOptions
    {
        AppId = config.Feishu.AppId,
        AppSecret = config.Feishu.AppSecret ?? "",
        AllowFrom = config.Feishu.AllowFrom ?? "*",
    };
    builder.Services.AddFeishuPlatform(feishuOpts);

    // 将 FeishuPlatform 同时注册为 IPlatform
    builder.Services.AddSingleton<IPlatform>(sp => sp.GetRequiredService<FeishuPlatform>());
}

// 注册 Engine
builder.Services.AddSingleton<Engine>(sp =>
{
    var agent = sp.GetRequiredService<IAgent>();
    var platforms = sp.GetServices<IPlatform>();
    var logger = sp.GetRequiredService<ILogger<Engine>>();
    return new Engine(config.ProjectName ?? "default", agent, platforms, logger);
});

// 注册 HostedService
builder.Services.AddHostedService<EngineHostedService>();

var host = builder.Build();
host.Run();

// ── HostedService ─────────────────────────────────────────────

sealed class EngineHostedService(Engine engine, ILogger<EngineHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("MinoLink 正在启动...");
        await engine.StartAsync(ct);
        logger.LogInformation("MinoLink 已启动");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        logger.LogInformation("MinoLink 正在关闭...");
        await engine.DisposeAsync();
        logger.LogInformation("MinoLink 已关闭");
    }
}

// ── 配置模型 ──────────────────────────────────────────────────

sealed class MinoLinkConfig
{
    public string? ProjectName { get; init; }
    public AgentConfig Agent { get; init; } = new();
    public FeishuConfig? Feishu { get; init; }
}

sealed class AgentConfig
{
    public string Type { get; init; } = "claudecode";
    public string WorkDir { get; init; } = ".";
    public string? Model { get; init; }
    public string? Mode { get; init; }
}

sealed class FeishuConfig
{
    public string? AppId { get; init; }
    public string? AppSecret { get; init; }
    public string? AllowFrom { get; init; }
}
