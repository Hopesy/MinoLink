using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FeishuNetSdk.Im.Events;
using FeishuNetSdk.Services;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Models;

namespace MinoLink.Feishu;

/// <summary>
/// 飞书消息接收事件处理器。
/// 由 FeishuNetSdk WebSocket 自动分发调用，桥接到 <see cref="FeishuPlatform"/>。
/// </summary>
public sealed class FeishuMessageHandler : IEventHandler<EventV2Dto<ImMessageReceiveV1EventBodyDto>, ImMessageReceiveV1EventBodyDto>
{
    private readonly FeishuPlatform _platform;
    private readonly ILogger<FeishuMessageHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FeishuPlatformOptions _options;

    public FeishuMessageHandler(
        FeishuPlatform platform,
        ILogger<FeishuMessageHandler> logger,
        IHttpClientFactory httpClientFactory,
        FeishuPlatformOptions options)
    {
        _platform = platform;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options;
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

        var content = ExtractTextContent(message.MessageType, message.Content);
        var attachments = await ExtractAttachmentsAsync(messageId, message.MessageType, message.Content, ct);
        if (string.IsNullOrWhiteSpace(content) && attachments.Count == 0)
        {
            _logger.LogDebug("忽略不支持的消息: type={Type}", message.MessageType);
            return;
        }

        _logger.LogInformation("飞书消息: sender={Sender}, chat={Chat}, type={Type}, content={Content}, attachments={AttachmentCount}",
            senderId, chatId, message.MessageType, content.Length > 50 ? content[..50] + "..." : content, attachments.Count);

        await _platform.OnMessageReceivedAsync(messageId, chatId, senderId, senderName, content, isGroup, attachments);
    }

    private async Task<IReadOnlyList<MessageAttachment>> ExtractAttachmentsAsync(string messageId, string? messageType, string? contentJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageType) || string.IsNullOrWhiteSpace(contentJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            return messageType.ToLowerInvariant() switch
            {
                "image" => await ExtractImageAttachmentsAsync(messageId, doc.RootElement, ct),
                "file" => await ExtractFileAttachmentsAsync(messageId, doc.RootElement, ct),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析飞书附件消息失败: messageId={MessageId}, type={Type}", messageId, messageType);
            return [];
        }
    }

    private async Task<IReadOnlyList<MessageAttachment>> ExtractImageAttachmentsAsync(string messageId, JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("image_key", out var imageKeyEl))
            return [];

        var imageKey = imageKeyEl.GetString();
        if (string.IsNullOrWhiteSpace(imageKey))
            return [];

        var downloaded = await DownloadImageResourceAsync(messageId, imageKey, ct);
        return downloaded is null ? [] : [downloaded];
    }

    private async Task<IReadOnlyList<MessageAttachment>> ExtractFileAttachmentsAsync(string messageId, JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("file_key", out var fileKeyEl))
            return [];

        var fileKey = fileKeyEl.GetString();
        if (string.IsNullOrWhiteSpace(fileKey))
            return [];

        var fileName = root.TryGetProperty("file_name", out var fileNameEl)
            ? fileNameEl.GetString() ?? string.Empty
            : string.Empty;
        var fileSize = root.TryGetProperty("file_size", out var fileSizeEl) && fileSizeEl.TryGetInt64(out var size)
            ? size
            : root.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var altSize)
                ? altSize
                : 0;

        var downloaded = await DownloadMessageResourceAsync(messageId, fileKey, "file", fileName, fileSize, MessageAttachmentKind.File, ct);
        return downloaded is null ? [] : [downloaded];
    }

    private Task<MessageAttachment?> DownloadImageResourceAsync(string messageId, string imageKey, CancellationToken ct)
    {
        return DownloadMessageResourceAsync(messageId, imageKey, "image", string.Empty, 0, MessageAttachmentKind.Image, ct);
    }

    private async Task<MessageAttachment?> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string fileName,
        long declaredSize,
        MessageAttachmentKind kind,
        CancellationToken ct)
    {
        try
        {
            var token = await GetTenantAccessTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
                return null;

            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"https://open.feishu.cn/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/resources/{Uri.EscapeDataString(fileKey)}?type={Uri.EscapeDataString(resourceType)}";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("下载飞书资源失败: messageId={MessageId}, fileKey={FileKey}, type={Type}, status={StatusCode}",
                    messageId, fileKey, resourceType, (int)response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = mediaType switch
                {
                    "application/pdf" => ".pdf",
                    "text/plain" => ".txt",
                    "application/zip" => ".zip",
                    _ => string.Empty,
                };
            }

            var safeName = string.IsNullOrWhiteSpace(fileName) ? fileKey + ext : fileName;
            foreach (var c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');

            var dir = GetAttachmentStorageDirectory(DateTime.Now);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"{messageId}_{safeName}");
            await using var fs = File.Create(filePath);
            await response.Content.CopyToAsync(fs, ct);
            var fileInfo = new FileInfo(filePath);
            _logger.LogInformation("飞书文件已保存: {Path}", filePath);
            return new MessageAttachment
            {
                Kind = kind,
                Name = safeName,
                MimeType = mediaType,
                SizeBytes = declaredSize > 0 ? declaredSize : response.Content.Headers.ContentLength ?? fileInfo.Length,
                LocalPath = filePath,
                RemoteKey = fileKey,
                SourcePlatform = "feishu",
                SourceMessageId = messageId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载飞书资源异常: messageId={MessageId}, fileKey={FileKey}, type={Type}", messageId, fileKey, resourceType);
            return null;
        }
    }

    private static string GetAttachmentStorageDirectory(DateTime date)
    {
        return Path.Combine(AppContext.BaseDirectory, "output", "feishu-files", date.ToString("yyyyMMdd"));
    }

    private async Task<string?> GetTenantAccessTokenAsync(CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var content = JsonContent.Create(new
            {
                app_id = _options.AppId,
                app_secret = _options.AppSecret,
            });
            var response = await client.PostAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("获取飞书 tenant_access_token 失败: status={StatusCode}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("tenant_access_token", out var tokenEl)
                ? tokenEl.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取飞书 tenant_access_token 异常");
            return null;
        }
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
