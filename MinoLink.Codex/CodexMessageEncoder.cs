using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Codex;

internal sealed class CodexMessageEncoder : IAgentMessageEncoder
{
    public string Encode(string content, IReadOnlyList<MessageAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return content;

        var lines = new List<string>();
        foreach (var attachment in attachments)
        {
            var kind = attachment.Kind switch
            {
                MessageAttachmentKind.Image => "图片",
                MessageAttachmentKind.File => "文件",
                _ => "附件",
            };

            var path = string.IsNullOrWhiteSpace(attachment.LocalPath) ? "(无本地路径)" : attachment.LocalPath;
            var name = string.IsNullOrWhiteSpace(attachment.Name) ? Path.GetFileName(path) : attachment.Name;
            lines.Add($"- {kind}: {name} | {path}");
        }

        var attachmentBlock = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(content)
            ? $"请结合以下附件内容进行处理：\n{attachmentBlock}"
            : $"{content}\n\n附加附件：\n{attachmentBlock}";
    }
}
