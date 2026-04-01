using MinoLink.Core.Models;

namespace MinoLink.Core.TurnMerge;

internal sealed class TurnAggregate
{
    private readonly List<string> _textParts = [];
    private readonly List<MessageAttachment> _attachments = [];

    public TurnAggregate(Message firstMessage)
    {
        SessionKey = firstMessage.SessionKey;
        From = firstMessage.From;
        FromName = firstMessage.FromName;
        ReplyContext = firstMessage.ReplyContext;
        IsGroup = firstMessage.IsGroup;
        FirstMessageAt = firstMessage.ReceivedAt;
        LastMessageAt = firstMessage.ReceivedAt;
        Revision = 1;
        AppendCore(firstMessage);
    }

    public string SessionKey { get; }

    public string From { get; }

    public string? FromName { get; }

    public object ReplyContext { get; private set; }

    public bool IsGroup { get; }

    public DateTimeOffset FirstMessageAt { get; }

    public DateTimeOffset LastMessageAt { get; private set; }

    public int Revision { get; private set; }

    public void AppendMessage(Message message)
    {
        AppendCore(message);
        ReplyContext = message.ReplyContext;
        LastMessageAt = message.ReceivedAt;
        Revision++;
    }

    public TurnSnapshot CreateSnapshot()
    {
        return new TurnSnapshot(
            SessionKey,
            From,
            FromName,
            ReplyContext,
            IsGroup,
            Revision,
            BuildPromptText(),
            _attachments.ToArray());
    }

    private void AppendCore(Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
            _textParts.Add(message.Content.Trim());

        if (message.Attachments.Count > 0)
            _attachments.AddRange(message.Attachments);
    }

    private string BuildPromptText()
    {
        var lines = new List<string>();

        if (_textParts.Count > 0)
        {
            lines.Add("用户当前请求：");
            lines.Add(_textParts[0]);
        }

        if (_textParts.Count > 1)
        {
            lines.Add(string.Empty);
            lines.Add("补充信息：");
            foreach (var text in _textParts.Skip(1))
                lines.Add($"- {text}");
        }

        if (_attachments.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);

            lines.Add("附件：");
            foreach (var attachment in _attachments)
            {
                var name = string.IsNullOrWhiteSpace(attachment.Name)
                    ? attachment.LocalPath
                    : attachment.Name;
                lines.Add($"- {name}");
            }
        }

        return string.Join('\n', lines).Trim();
    }
}
