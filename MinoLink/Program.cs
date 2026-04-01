using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinoLink.ClaudeCode;
using MinoLink.Codex;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;
using MinoLink.Feishu;
using MinoLink.Logging;
using MinoLink.Services;
using MinoLink.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// 加载配置
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables("MINO_");

var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("FeishuNetSdk", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.FeishuNetSdk", LogLevel.Warning);
builder.Logging.AddFilter("WebApiClientCore", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider(logDirectory));

var config = builder.Configuration.GetSection("MinoLink").Get<MinoLinkConfig>()
    ?? throw new InvalidOperationException("配置缺失：请在 appsettings.json 中配置 MinoLink 节");

var defaultWorkDir = ProgramHelpers.ResolveDefaultWorkDir(config.Agent.WorkDir);

// 注册 ConfigService
var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
builder.Services.AddSingleton<IConfigService>(new ConfigService(configPath, config));

// 注册 Agent
builder.Services.AddSingleton<Func<string, IAgent>>(sp => agentType =>
{
    var options = new AgentOptions
    {
        Model = config.Agent.Model,
        Mode = config.Agent.Mode ?? "default",
    };

    return agentType.ToLowerInvariant() switch
    {
        "claudecode" or "claude" => new ClaudeCodeAgent(options, sp.GetRequiredService<ILogger<ClaudeCodeAgent>>()),
        "codex" => new CodexAgent(options, sp.GetRequiredService<ILogger<CodexAgent>>()),
        _ => throw new InvalidOperationException($"未知 Agent: {agentType}"),
    };
});

// 注册 SessionManager
var sessionStoragePath = Path.Combine(AppContext.BaseDirectory, "data", "sessions.json");
builder.Services.AddSingleton(new SessionManager(sessionStoragePath));

// 注册 Engine
builder.Services.AddSingleton<Engine>(sp =>
{
    var agentFactory = sp.GetRequiredService<Func<string, IAgent>>();
    var platforms = sp.GetServices<IPlatform>();
    var sessions = sp.GetRequiredService<SessionManager>();
    var logger = sp.GetRequiredService<ILogger<Engine>>();
    return new Engine(config.ProjectName ?? "default", agentFactory, platforms, defaultWorkDir, sessions, logger);
});

// 注册飞书平台
if (config.Feishu is { AppId: not null and not "" })
{
    var feishuOpts = new FeishuPlatformOptions
    {
        AppId = config.Feishu.AppId,
        AppSecret = config.Feishu.AppSecret ?? "",
        VerificationToken = config.Feishu.VerificationToken ?? "",
    };
    builder.Services.AddFeishuPlatform(feishuOpts);

    // 将 FeishuPlatform 同时注册为 IPlatform
    builder.Services.AddSingleton<IPlatform>(sp => sp.GetRequiredService<FeishuPlatform>());
}

// 注册 Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 注册 HostedService
builder.Services.AddHostedService<EngineHostedService>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

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

static class ProgramHelpers
{
    public static string ResolveDefaultWorkDir(string? configuredWorkDir)
    {
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(configuredWorkDir))
            return desktopDir;

        return Path.GetFullPath(configuredWorkDir);
    }
}
