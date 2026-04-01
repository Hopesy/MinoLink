using FeishuNetSdk;
using FeishuNetSdk.CallbackEvents;
using FeishuNetSdk.Im.Events;
using FeishuNetSdk.Services;
using FeishuNetSdk.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace MinoLink.Feishu;

/// <summary>
/// DI 注册扩展方法。
/// </summary>
public static class FeishuServiceExtensions
{
    /// <summary>
    /// 注册飞书平台所需的所有服务。
    /// </summary>
    public static IServiceCollection AddFeishuPlatform(this IServiceCollection services, FeishuPlatformOptions options)
    {
        // 注册飞书 SDK + WebSocket 长连接
        services.AddFeishuNetSdk(sdkOpts =>
        {
            sdkOpts.AppId = options.AppId;
            sdkOpts.AppSecret = options.AppSecret;
            sdkOpts.VerificationToken = options.VerificationToken;
            sdkOpts.EnableLogging = false;
        }).AddFeishuWebSocket();

        services.AddHttpClient();

        // 注册平台和事件处理器
        services.AddSingleton(options);
        services.AddSingleton<FeishuPlatform>();
        services.AddScoped<FeishuMessageHandler>();
        services.AddScoped<FeishuCardActionHandler>();

        // 将 FeishuMessageHandler 注册为 FeishuNetSdk 的事件处理器
        services.AddScoped<IEventHandler<EventV2Dto<ImMessageReceiveV1EventBodyDto>,
            ImMessageReceiveV1EventBodyDto>, FeishuMessageHandler>();
        services.AddScoped<ICallbackHandler<CallbackV2Dto<CardActionTriggerEventBodyDto>,
            CardActionTriggerEventBodyDto, CardActionTriggerResponseDto>, FeishuCardActionHandler>();

        return services;
    }
}
