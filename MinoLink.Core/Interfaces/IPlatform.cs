using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

/// <summary>
/// 消息平台适配器。
/// </summary>
public interface IPlatform : IAsyncDisposable
{
    string Name { get; }

    /// <summary>启动平台，注册消息回调。</summary>
    Task StartAsync(Func<IPlatform, Message, Task> messageHandler, CancellationToken ct);

    /// <summary>回复某条消息。</summary>
    Task ReplyAsync(object replyContext, string text, CancellationToken ct);

    /// <summary>主动发送消息（无上下文）。</summary>
    Task SendAsync(object replyContext, string text, CancellationToken ct);
}

/// <summary>
/// 支持发送富文本卡片。
/// </summary>
public interface ICardSender
{
    Task SendCardAsync(object replyContext, Card card, CancellationToken ct);
}

/// <summary>
/// 支持更新已发送的消息（用于流式预览）。
/// </summary>
public interface IMessageUpdater
{
    /// <summary>发送初始预览消息，返回消息句柄。</summary>
    Task<object?> SendPreviewAsync(object replyContext, string text, CancellationToken ct);

    /// <summary>更新已发送消息的内容。</summary>
    Task UpdateMessageAsync(object messageHandle, string text, CancellationToken ct);

    /// <summary>删除消息。</summary>
    Task DeleteMessageAsync(object messageHandle, CancellationToken ct);
}

/// <summary>
/// 支持"正在输入"指示器。
/// </summary>
public interface ITypingIndicator
{
    /// <summary>开始显示输入状态，返回 IDisposable 以停止。</summary>
    IDisposable StartTyping(object replyContext);
}

/// <summary>
/// 卡片数据模型（简化版）。
/// </summary>
public sealed class Card
{
    public string? Title { get; init; }
    public IReadOnlyList<CardElement> Elements { get; init; } = [];
}

public abstract class CardElement;

public sealed class CardMarkdown(string content) : CardElement
{
    public string Content { get; } = content;
}

public sealed class CardDivider : CardElement;

public sealed class CardActions(IReadOnlyList<CardButton> buttons) : CardElement
{
    public IReadOnlyList<CardButton> Buttons { get; } = buttons;
}

public sealed class CardButton(string label, string value)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Style { get; init; } = "default";
}
