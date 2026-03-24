using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

public interface IAgentMessageEncoder
{
    string Encode(string content, IReadOnlyList<MessageAttachment>? attachments);
}
