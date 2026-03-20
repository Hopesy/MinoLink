using System.Text.Json;
using FeishuNetSdk.CallbackEvents;
using FeishuNetSdk.Extensions;
using FeishuNetSdk.Im.Dtos;
using FeishuNetSdk.Services;
using Microsoft.Extensions.Logging;
using MinoLink.Core;
using MinoLink.Core.Models;

namespace MinoLink.Feishu;

/// <summary>
/// 飞书卡片点击回调处理器。
/// </summary>
public sealed class FeishuCardActionHandler(
    FeishuPlatform platform,
    Engine engine,
    ILogger<FeishuCardActionHandler> logger)
    : ICallbackHandler<CallbackV2Dto<CardActionTriggerEventBodyDto>, CardActionTriggerEventBodyDto, CardActionTriggerResponseDto>
{
    public Task<CardActionTriggerResponseDto> ExecuteAsync(CallbackV2Dto<CardActionTriggerEventBodyDto> input, CancellationToken ct)
    {
        var evt = input.Event;
        if (evt?.Action?.Value is null)
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "卡片动作无效"));

        var action = GetString(evt.Action.Value, "action");
        var sessionKey = GetString(evt.Action.Value, "session_key");
        sessionKey ??= ResolveSessionKey(evt);

        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(sessionKey))
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "缺少 action 或 session_key"));

        if (action.StartsWith("ask:", StringComparison.Ordinal))
            return Task.FromResult(HandleQuestionAction(action, sessionKey));

        if (!action.StartsWith("perm:", StringComparison.Ordinal))
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, "暂不支持此卡片操作"));

        var parts = action.Split(':', 3);
        if (parts.Length != 3)
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "权限动作格式错误"));

        var behavior = parts[1];
        var requestId = parts[2];
        var response = behavior switch
        {
            "allow" => new PermissionResponse { Allow = true },
            "deny" => new PermissionResponse { Allow = false },
            "allow_all" => new PermissionResponse { Allow = true, AllowAll = true },
            _ => null,
        };

        if (response is null)
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "未知权限动作"));

        logger.LogInformation("收到飞书卡片权限回调: action={Action}, sessionKey={SessionKey}, requestId={RequestId}",
            behavior, sessionKey, requestId);

        var resolved = engine.ResolvePermission(sessionKey, requestId, response);
        if (!resolved)
            return Task.FromResult(BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "未找到待处理的权限请求，请重新发起"));

        return Task.FromResult(BuildResolvedCard(behavior));
    }

    private string? ResolveSessionKey(CardActionTriggerEventBodyDto evt)
    {
        var chatId = evt.Context?.OpenChatId;
        var openId = evt.Operator?.OpenId;
        if (string.IsNullOrWhiteSpace(openId))
            return null;

        return string.IsNullOrWhiteSpace(chatId)
            ? platform.GetSessionKey(string.Empty, openId)
            : platform.GetSessionKey(chatId, openId, isGroup: true);
    }

    private static string? GetString(object value, string key)
    {
        if (value is IReadOnlyDictionary<string, object> dict && dict.TryGetValue(key, out var obj))
            return obj?.ToString();

        if (value is IReadOnlyDictionary<string, string> strDict && strDict.TryGetValue(key, out var str))
            return str;

        if (value is JsonElement el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var prop))
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();

        return null;
    }

    private static CardActionTriggerResponseDto BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType type, string content) => new()
    {
        Toast = new CardActionTriggerResponseDto.ToastSuffix
        {
            Type = type,
            Content = content,
        },
    };

    private static CardActionTriggerResponseDto BuildResolvedCard(string behavior)
    {
        var (title, template) = behavior switch
        {
            "allow" => ("已允许", "green"),
            "deny" => ("已拒绝", "red"),
            "allow_all" => ("已全部允许", "blue"),
            _ => ("权限请求已处理", "blue"),
        };

        return new CardActionTriggerResponseDto
        {
            Toast = new CardActionTriggerResponseDto.ToastSuffix
            {
                Type = CardActionTriggerResponseDto.ToastSuffix.ToastType.Success,
                Content = "权限请求已处理",
            },
        }.SetCard(new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Title = new HeaderTitleElement(title, null),
                Template = template,
            },
            Body = new ElementsCardV2Dto.BodySuffix(
            [
                new DivElement().SetText(new PlainTextElement("权限请求已处理", null, null, null, null)),
            ]),
        });
    }

    private CardActionTriggerResponseDto HandleQuestionAction(string action, string sessionKey)
    {
        var parts = action.Split(':', 4);
        if (parts.Length != 4 ||
            !int.TryParse(parts[2], out var questionIndex) ||
            !int.TryParse(parts[3], out var optionIndex))
            return BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "问题动作格式错误");

        var requestId = parts[1];
        logger.LogInformation("收到飞书卡片问题回调: sessionKey={SessionKey}, requestId={RequestId}, questionIndex={QuestionIndex}, optionIndex={OptionIndex}",
            sessionKey, requestId, questionIndex, optionIndex);

        if (!engine.ResolveUserQuestionOption(sessionKey, requestId, questionIndex, optionIndex))
            return BuildToast(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, "未找到待回答的问题，请重新发起");

        return new CardActionTriggerResponseDto
        {
            Toast = new CardActionTriggerResponseDto.ToastSuffix
            {
                Type = CardActionTriggerResponseDto.ToastSuffix.ToastType.Success,
                Content = "已提交回答",
            },
        };
    }
}
