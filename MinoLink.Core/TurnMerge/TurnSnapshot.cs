using MinoLink.Core.Models;

namespace MinoLink.Core.TurnMerge;

internal sealed record TurnSnapshot(
    string SessionKey,
    string From,
    string? FromName,
    object ReplyContext,
    bool IsGroup,
    int Revision,
    string PromptText,
    IReadOnlyList<MessageAttachment> Attachments);
