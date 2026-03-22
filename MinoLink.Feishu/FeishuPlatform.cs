using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeishuNetSdk;
using FeishuNetSdk.Extensions;
using FeishuNetSdk.Im;
using Microsoft.Extensions.Logging;
using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Feishu;

/// <summary>
/// 飞书平台适配器。
/// 通过 FeishuNetSdk WebSocket 长连接接收消息，通过 TenantApi 发送回复。
/// </summary>
public sealed class FeishuPlatform : IPlatform, ICardSender, IMessageUpdater, ITypingIndicator
{
    private readonly IFeishuTenantApi _api;
    private readonly FeishuPlatformOptions _options;
    private readonly ILogger<FeishuPlatform> _logger;
    private readonly ConcurrentDictionary<string, string> _userNameCache = new();

    /// <summary>Emoji reaction 表情类型，默认 "OnIt"。</summary>
    private readonly string _reactionEmoji = "OnIt";

    private Func<IPlatform, Message, Task>? _messageHandler;

    public string Name => "feishu";

    public FeishuPlatform(IFeishuTenantApi api, FeishuPlatformOptions options, ILogger<FeishuPlatform> logger)
    {
        _api = api;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(Func<IPlatform, Message, Task> messageHandler, CancellationToken ct)
    {
        _messageHandler = messageHandler;
        _logger.LogInformation("飞书平台已就绪 (WebSocket 由 FeishuNetSdk HostedService 管理)");
        return Task.CompletedTask;
    }

    /// <summary>由 <see cref="FeishuMessageHandler"/> 调用，将飞书消息转发到 Engine。</summary>
    internal async Task OnMessageReceivedAsync(string messageId, string chatId, string senderId, string senderName, string content, bool isGroup)
    {
        if (_messageHandler is null)
        {
            _logger.LogWarning("消息处理器未就绪（_messageHandler 为 null），消息被丢弃: sender={SenderId}", senderId);
            return;
        }

        var sessionKey = GetSessionKey(chatId, senderId, isGroup);

        var msg = new Message
        {
            SessionKey = sessionKey,
            From = senderId,
            FromName = senderName,
            Content = content,
            IsGroup = isGroup,
            ReplyContext = new FeishuReplyContext(messageId, chatId, senderId, sessionKey),
        };

        _logger.LogInformation("准备调用消息处理器: sessionKey={SessionKey}, content={Content}",
            sessionKey, content.Length > 30 ? content[..30] + "..." : content);

        try
        {
            await _messageHandler(this, msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息处理器执行异常: sessionKey={SessionKey}", sessionKey);
        }
    }

    internal string GetSessionKey(string chatId, string senderId, bool isGroup = false) => isGroup
        ? $"feishu:{senderId}:{chatId}"
        : $"feishu:{senderId}";

    // ─────────── 消息发送 ───────────

    public async Task ReplyAsync(object replyContext, string text, CancellationToken ct)
    {
        var ctx = (FeishuReplyContext)replyContext;
        var (msgType, content) = BuildReplyContent(text);
        var dto = new PostImV1MessagesBodyDto
        {
            ReceiveId = ctx.ChatId,
            MsgType = msgType,
            Content = content,
        };

        try
        {
            await _api.PostImV1MessagesAsync("chat_id", dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "飞书发送消息失败: chatId={ChatId}", ctx.ChatId);
        }
    }

    public async Task SendAsync(object replyContext, string text, CancellationToken ct) =>
        await ReplyAsync(replyContext, text, ct);

    public async Task SendCardAsync(object replyContext, Card card, CancellationToken ct)
    {
        var ctx = (FeishuReplyContext)replyContext;
        var cardDto = FeishuCardBuilder.BuildCard(card, ctx.SessionKey);

        var dto = new PostImV1MessagesBodyDto
        {
            ReceiveId = ctx.ChatId,
        }.SetContent(cardDto);

        try
        {
            _logger.LogInformation("发送飞书交互卡片: chatId={ChatId}, content={Content}", ctx.ChatId, dto.Content);
            await _api.PostImV1MessagesAsync("chat_id", dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "飞书发送卡片失败: chatId={ChatId}", ctx.ChatId);
        }
    }

    // ─────────── 流式预览 (Interactive Card) ───────────

    public async Task<object?> SendPreviewAsync(object replyContext, string text, CancellationToken ct)
    {
        var ctx = (FeishuReplyContext)replyContext;
        var cardJson = BuildMarkdownCardJson(text);
        var dto = new PostImV1MessagesBodyDto
        {
            ReceiveId = ctx.ChatId,
            MsgType = "interactive",
            Content = cardJson,
        };

        try
        {
            var result = await _api.PostImV1MessagesAsync("chat_id", dto);
            return result?.Data?.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "飞书发送预览失败");
            return null;
        }
    }

    public async Task UpdateMessageAsync(object messageHandle, string text, CancellationToken ct)
    {
        if (messageHandle is not string msgId) return;
        try
        {
            await _api.PatchImV1MessagesByMessageIdAsync(msgId, new PatchImV1MessagesByMessageIdBodyDto
            {
                Content = BuildMarkdownCardJson(text),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "飞书更新消息失败: {MsgId}", msgId);
        }
    }

    public async Task DeleteMessageAsync(object messageHandle, CancellationToken ct)
    {
        if (messageHandle is not string msgId) return;
        try
        {
            await _api.DeleteImV1MessagesByMessageIdAsync(msgId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "飞书删除消息失败: {MsgId}", msgId);
        }
    }

    // ─────────── Typing Indicator (Emoji Reaction) ───────────

    public IDisposable StartTyping(object replyContext)
    {
        var ctx = (FeishuReplyContext)replyContext;
        var reactionId = AddReaction(ctx.MessageId);
        return new ReactionDisposable(this, ctx.MessageId, reactionId);
    }

    private string? AddReaction(string messageId)
    {
        try
        {
            var dto = new PostImV1MessagesByMessageIdReactionsBodyDto
            {
                ReactionType = new PostImV1MessagesByMessageIdReactionsBodyDto.Emoji
                {
                    EmojiType = _reactionEmoji,
                },
            };
            var result = _api.PostImV1MessagesByMessageIdReactionsAsync(messageId, dto).GetAwaiter().GetResult();
            var rid = result?.Data?.ReactionId;
            _logger.LogDebug("添加 Reaction: msgId={MsgId}, reactionId={ReactionId}", messageId, rid);
            return rid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "添加 Reaction 失败: msgId={MsgId}", messageId);
            return null;
        }
    }

    private void RemoveReaction(string messageId, string? reactionId)
    {
        if (string.IsNullOrEmpty(reactionId)) return;
        try
        {
            _api.DeleteImV1MessagesByMessageIdReactionsByReactionIdAsync(messageId, reactionId).GetAwaiter().GetResult();
            _logger.LogDebug("移除 Reaction: msgId={MsgId}, reactionId={ReactionId}", messageId, reactionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "移除 Reaction 失败: msgId={MsgId}", messageId);
        }
    }

    private sealed class ReactionDisposable(FeishuPlatform platform, string messageId, string? reactionId) : IDisposable
    {
        public void Dispose() => platform.RemoveReaction(messageId, reactionId);
    }

    // ─────────── 消息格式构建 ───────────

    /// <summary>
    /// 根据内容自动选择消息格式：
    /// - 包含复杂 Markdown（代码块、表格）→ Interactive Card
    /// - 包含简单 Markdown（粗体、列表）→ Interactive Card
    /// - 纯文本 → text
    /// </summary>
    private static (string MsgType, string Content) BuildReplyContent(string text)
    {
        if (HasMarkdown(text))
            return ("interactive", BuildMarkdownCardJson(text));

        return ("text", JsonSerializer.Serialize(new { text }));
    }

    /// <summary>检测文本是否包含 Markdown 标记。</summary>
    private static bool HasMarkdown(string text) =>
        text.Contains("```") ||
        text.Contains("**") ||
        text.Contains("~~") ||
        text.Contains('`') ||
        text.Contains("\n-") ||
        text.Contains("\n*") ||
        text.Contains("\n#") ||
        text.Contains("\n1.") ||
        text.Contains("---");

    /// <summary>构建飞书 Interactive Card JSON (schema 2.0, markdown 元素)。</summary>
    private static string BuildMarkdownCardJson(string markdownContent)
    {
        var card = new
        {
            schema = "2.0",
            config = new { wide_screen_mode = true },
            body = new
            {
                elements = new object[]
                {
                    new { tag = "markdown", content = markdownContent },
                },
            },
        };
        return JsonSerializer.Serialize(card);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>飞书回复上下文。</summary>
internal sealed record FeishuReplyContext(string MessageId, string ChatId, string SenderId, string SessionKey);
