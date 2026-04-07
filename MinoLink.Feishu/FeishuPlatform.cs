using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public sealed class FeishuPlatform : IPlatform, ICardSender, IMessageUpdater, ITypingIndicator, IImageSender, IFileSender
{
    private readonly IFeishuTenantApi _api;
    private readonly FeishuPlatformOptions _options;
    private readonly ILogger<FeishuPlatform> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, string> _userNameCache = new();

    /// <summary>Emoji reaction 表情类型，默认 "OnIt"。</summary>
    private readonly string _reactionEmoji = "OnIt";

    private Func<IPlatform, Message, Task>? _messageHandler;

    public string Name => "feishu";

    public FeishuPlatform(IFeishuTenantApi api, FeishuPlatformOptions options, ILogger<FeishuPlatform> logger, IHttpClientFactory httpClientFactory)
    {
        _api = api;
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public Task StartAsync(Func<IPlatform, Message, Task> messageHandler, CancellationToken ct)
    {
        _messageHandler = messageHandler;
        _logger.LogInformation("飞书平台已就绪 (WebSocket 由 FeishuNetSdk HostedService 管理)");
        return Task.CompletedTask;
    }

    /// <summary>由 <see cref="FeishuMessageHandler"/> 调用，将飞书消息转发到 Engine。</summary>
    internal async Task OnMessageReceivedAsync(string messageId, string chatId, string senderId, string senderName, string content, bool isGroup, IReadOnlyList<MessageAttachment>? attachments = null)
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
            Attachments = attachments ?? [],
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
            _logger.LogInformation("飞书回复分片: chatId={ChatId}, length={Length}\n<<<CHUNK\n{Chunk}\nCHUNK>>>",
                ctx.ChatId, text?.Length ?? 0, text ?? string.Empty);
            _logger.LogInformation("飞书最终 markdown: chatId={ChatId}, length={Length}\n<<<FEISHU_MARKDOWN\n{Markdown}\nFEISHU_MARKDOWN>>>",
                ctx.ChatId, ExtractMarkdownContent(content)?.Length ?? 0, ExtractMarkdownContent(content) ?? string.Empty);
            await _api.PostImV1MessagesAsync("chat_id", dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "飞书发送消息失败: chatId={ChatId}", ctx.ChatId);
        }
    }

    public async Task SendAsync(object replyContext, string text, CancellationToken ct) =>
        await ReplyAsync(replyContext, text, ct);

    public async Task SendImageAsync(object replyContext, string filePath, CancellationToken ct)
    {
        var ctx = (FeishuReplyContext)replyContext;
        if (!File.Exists(filePath))
            throw new FileNotFoundException("图片文件不存在", filePath);

        var imageKey = await UploadImageAsync(filePath, ct);
        if (string.IsNullOrWhiteSpace(imageKey))
            throw new InvalidOperationException("上传飞书图片失败，未返回 image_key");

        var dto = new PostImV1MessagesBodyDto
        {
            ReceiveId = ctx.ChatId,
            MsgType = "image",
            Content = JsonSerializer.Serialize(new { image_key = imageKey }),
        };

        await _api.PostImV1MessagesAsync("chat_id", dto);
        _logger.LogInformation("发送飞书图片成功: chatId={ChatId}, path={Path}", ctx.ChatId, filePath);
    }

    public async Task SendFileAsync(object replyContext, string filePath, CancellationToken ct)
    {
        var ctx = (FeishuReplyContext)replyContext;
        if (!File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);

        var fileKey = await UploadFileAsync(filePath, ct);
        if (string.IsNullOrWhiteSpace(fileKey))
            throw new InvalidOperationException("上传飞书文件失败，未返回 file_key");

        var dto = new PostImV1MessagesBodyDto
        {
            ReceiveId = ctx.ChatId,
            MsgType = "file",
            Content = JsonSerializer.Serialize(new { file_key = fileKey }),
        };

        await _api.PostImV1MessagesAsync("chat_id", dto);
        _logger.LogInformation("发送飞书文件成功: chatId={ChatId}, path={Path}", ctx.ChatId, filePath);
    }

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

    private async Task<string?> UploadImageAsync(string filePath, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        using var tokenContent = JsonContent.Create(new
        {
            app_id = _options.AppId,
            app_secret = _options.AppSecret,
        });
        var tokenResponse = await client.PostAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", tokenContent, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("获取飞书 tenant_access_token 失败: status={StatusCode}", (int)tokenResponse.StatusCode);
            return null;
        }

        await using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(ct);
        using var tokenDoc = await JsonDocument.ParseAsync(tokenStream, cancellationToken: ct);
        if (!tokenDoc.RootElement.TryGetProperty("tenant_access_token", out var tokenEl))
            return null;

        var token = tokenEl.GetString();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("message"), "image_type");

        await using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageMimeType(filePath));
        form.Add(fileContent, "image", Path.GetFileName(filePath));
        request.Content = form;

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("上传飞书图片失败: path={Path}, status={StatusCode}", filePath, (int)response.StatusCode);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var responseDoc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);
        if (!responseDoc.RootElement.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("image_key", out var imageKeyEl))
        {
            return null;
        }

        return imageKeyEl.GetString();
    }

    private async Task<string?> UploadFileAsync(string filePath, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        using var tokenContent = JsonContent.Create(new
        {
            app_id = _options.AppId,
            app_secret = _options.AppSecret,
        });
        var tokenResponse = await client.PostAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", tokenContent, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("获取飞书 tenant_access_token 失败: status={StatusCode}", (int)tokenResponse.StatusCode);
            return null;
        }

        await using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(ct);
        using var tokenDoc = await JsonDocument.ParseAsync(tokenStream, cancellationToken: ct);
        if (!tokenDoc.RootElement.TryGetProperty("tenant_access_token", out var tokenEl))
            return null;

        var token = tokenEl.GetString();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/files");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = new MultipartFormDataContent();
        var fileName = Path.GetFileName(filePath);
        form.Add(new StringContent(GetFeishuFileType(filePath)), "file_type");
        form.Add(new StringContent(fileName), "file_name");

        await using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        request.Content = form;

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("上传飞书文件失败: path={Path}, status={StatusCode}", filePath, (int)response.StatusCode);
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var responseDoc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);
        if (!responseDoc.RootElement.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("file_key", out var fileKeyEl))
        {
            return null;
        }

        return fileKeyEl.GetString();
    }

    private static string GetFeishuFileType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".opus" => "opus",
            ".mp4" => "mp4",
            ".pdf" => "pdf",
            ".doc" or ".docx" => "doc",
            ".xls" or ".xlsx" => "xls",
            ".ppt" or ".pptx" => "ppt",
            _ => "stream",
        };
    }

    private static string GetImageMimeType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };
    }

    // ─────────── 消息格式构建 ───────────

    /// <summary>统一使用飞书 Interactive Card + markdown 渲染回复。</summary>
    private static (string MsgType, string Content) BuildReplyContent(string text) =>
        ("interactive", BuildMarkdownCardJson(text));

    /// <summary>构建飞书 Interactive Card JSON (schema 2.0, markdown 元素)。</summary>
    private static string BuildMarkdownCardJson(string markdownContent)
    {
        var normalizedContent = NormalizeFeishuMarkdown(markdownContent);
        var card = new
        {
            schema = "2.0",
            config = new { wide_screen_mode = true },
            body = new
            {
                elements = new object[]
                {
                    new { tag = "markdown", content = normalizedContent },
                },
            },
        };
        return JsonSerializer.Serialize(card);
    }

    private static string NormalizeFeishuMarkdown(string markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
            return string.Empty;

        var text = markdownContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var normalizedLines = new List<string>();
        var inCodeFence = false;

        foreach (var rawLine in text.Split('\n'))
        {
            foreach (var segment in SplitMergedMarkdownLine(rawLine))
            {
                var trimmedStart = segment.TrimStart();
                if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
                {
                    normalizedLines.Add(trimmedStart);
                    inCodeFence = !inCodeFence;
                    continue;
                }

                if (inCodeFence)
                {
                    normalizedLines.Add(segment);
                    continue;
                }

                normalizedLines.Add(NormalizeFeishuMarkdownLine(segment));
            }
        }

        return string.Join("\n", CompactBlankLines(normalizedLines)).Trim();
    }

    private static IEnumerable<string> CompactBlankLines(IEnumerable<string> lines)
    {
        var inCodeFence = false;
        var previousBlank = true;
        foreach (var line in lines)
        {
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                if (!previousBlank)
                    yield return string.Empty;

                yield return trimmedStart;
                inCodeFence = !inCodeFence;
                previousBlank = false;
                continue;
            }

            if (inCodeFence)
            {
                yield return line;
                previousBlank = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (previousBlank)
                    continue;

                yield return string.Empty;
                previousBlank = true;
                continue;
            }

            yield return line.TrimEnd();
            previousBlank = false;
        }
    }

    private static IEnumerable<string> SplitMergedMarkdownLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield return string.Empty;
            yield break;
        }

        var text = line;
        text = Regex.Replace(text, @"(?<=[^\n])(?=```)", "\n");
        text = Regex.Replace(text, @"(?<=[^\n])(?=#{1,6}\s*\S)", "\n");
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff：:])(?=\d+[\.)]\s*\S)", "\n");

        foreach (var segment in text.Split('\n'))
            yield return segment;
    }

    private static string NormalizeFeishuMarkdownLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var headingMatch = Regex.Match(line, @"^\s*#{1,6}\s*(.+?)\s*$");
        if (headingMatch.Success)
        {
            var headingText = headingMatch.Groups[1].Value.Trim();
            return string.IsNullOrEmpty(headingText) ? string.Empty : $"**{headingText}**";
        }

        return line.TrimEnd();
    }

    private static string? ExtractMarkdownContent(string cardJson)
    {
        if (string.IsNullOrWhiteSpace(cardJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(cardJson);
            if (!document.RootElement.TryGetProperty("body", out var body)
                || !body.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("tag", out var tag)
                    || !string.Equals(tag.GetString(), "markdown", StringComparison.Ordinal))
                    continue;

                if (element.TryGetProperty("content", out var content))
                    return content.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>飞书回复上下文。</summary>
internal sealed record FeishuReplyContext(string MessageId, string ChatId, string SenderId, string SessionKey);
