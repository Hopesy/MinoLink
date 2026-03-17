using System.Text.Json;
using FeishuNetSdk.Im.Events;
using FeishuNetSdk.Services;
using Microsoft.Extensions.Logging;

namespace MinoLink.Feishu;

/// <summary>
/// 飞书消息接收事件处理器。
/// 由 FeishuNetSdk WebSocket 自动分发调用，桥接到 <see cref="FeishuPlatform"/>。
/// </summary>
public sealed class FeishuMessageHandler : IEventHandler<EventV2Dto<ImMessageReceiveV1EventBodyDto>, ImMessageReceiveV1EventBodyDto>
{
    private readonly FeishuPlatform _platform;
    private readonly ILogger<FeishuMessageHandler> _logger;

    public FeishuMessageHandler(FeishuPlatform platform, ILogger<FeishuMessageHandler> logger)
    {
        _platform = platform;
        _logger = logger;
    }

    public async Task ExecuteAsync(EventV2Dto<ImMessageReceiveV1EventBodyDto> input, CancellationToken ct)
    {
        var body = input.Event;
        if (body is null) return;

        var message = body.Message;
        if (message is null) return;

        var senderId = body.Sender?.SenderId?.OpenId ?? "";
        var senderName = body.Sender?.SenderId?.OpenId ?? "unknown";
        var chatId = message.ChatId ?? "";
        var messageId = message.MessageId ?? "";
        var isGroup = message.ChatType == "group";

        // 解析消息内容
        var content = ExtractTextContent(message.MessageType, message.Content);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("忽略非文本消息: type={Type}", message.MessageType);
            return;
        }

        _logger.LogInformation("飞书消息: sender={Sender}, chat={Chat}, content={Content}",
            senderId, chatId, content.Length > 50 ? content[..50] + "..." : content);

        await _platform.OnMessageReceivedAsync(messageId, chatId, senderId, senderName, content, isGroup);
    }

    private static string ExtractTextContent(string? messageType, string? contentJson)
    {
        if (string.IsNullOrEmpty(contentJson)) return "";

        try
        {
            var doc = JsonDocument.Parse(contentJson);
            return messageType switch
            {
                "text" => doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                _ => "",
            };
        }
        catch
        {
            return "";
        }
    }
}
