using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Core;

public sealed partial class Engine
{
    private static Message NormalizeFileCommand(Message msg)
    {
        var trimmed = msg.Content.TrimStart();
        if (!trimmed.StartsWith("/file", StringComparison.OrdinalIgnoreCase))
            return msg;

        var payload = trimmed.Length > 5 ? trimmed[5..].TrimStart() : string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            payload = "请生成用户需要的文件，并按要求返回文件路径。";

        return new Message
        {
            SessionKey = msg.SessionKey,
            From = msg.From,
            FromName = msg.FromName,
            Content = BuildFileOutputPrompt(payload),
            ExpectFileOutput = true,
            Attachments = msg.Attachments,
            ReplyContext = msg.ReplyContext,
            IsGroup = msg.IsGroup,
            ReceivedAt = msg.ReceivedAt,
        };
    }

    private static string BuildFileOutputPrompt(string payload)
    {
        var builder = new StringBuilder();
        builder.AppendLine(payload.Trim());
        builder.AppendLine();
        builder.AppendLine("补充要求：");
        builder.AppendLine("- 本轮需要产出文件。所有产物默认写到 output/ 目录；如需子目录，也必须放在 output/ 下。");
        builder.AppendLine("- 文件生成完成后，请在回复末尾输出一个固定区块。");
        builder.AppendLine("- 格式严格如下：");
        builder.AppendLine();
        builder.AppendLine("[FILES]");
        builder.AppendLine("output/example.ext");
        builder.AppendLine("output/subdir/");
        builder.AppendLine("[/FILES]");
        builder.AppendLine();
        builder.AppendLine("- 只填写真实已生成的文件路径。每行一个路径。");
        builder.AppendLine("- 不要在其他正文位置混杂文件路径说明。");
        return builder.ToString().TrimEnd();
    }

    private async Task SendFileOutputsAsync(IPlatform platform, object replyContext, string workDir, string resultText, CancellationToken ct)
    {
        var extraction = ExtractFileOutputPaths(workDir, resultText);
        if (extraction.ValidPaths.Count == 0)
        {
            await platform.ReplyAsync(replyContext, extraction.HasFilesBlock
                ? "⚠️ 检测到了 [FILES] 区块，但未找到可发送的有效文件路径。"
                : "⚠️ 未检测到有效的 [FILES] 区块，请在回复末尾按协议返回文件路径。", ct);
        }

        if (extraction.InvalidEntries.Count > 0)
        {
            var invalidSummary = string.Join("\n", extraction.InvalidEntries.Select(item => $"- {item}"));
            await platform.ReplyAsync(replyContext, $"⚠️ 以下文件未发送：\n{invalidSummary}", ct);
        }

        var sentFiles = 0;
        var sentImages = 0;
        foreach (var filePath in extraction.ValidPaths)
        {
            try
            {
                if (ImageExtensions.Contains(Path.GetExtension(filePath)))
                {
                    if (platform is IImageSender imageSender)
                    {
                        await imageSender.SendImageAsync(replyContext, filePath, ct);
                        sentImages++;
                    }
                    else
                    {
                        await platform.ReplyAsync(replyContext, $"当前平台 `{platform.Name}` 不支持发送图片：`{filePath}`", ct);
                    }
                }
                else
                {
                    if (platform is IFileSender fileSender)
                    {
                        await fileSender.SendFileAsync(replyContext, filePath, ct);
                        sentFiles++;
                    }
                    else
                    {
                        await platform.ReplyAsync(replyContext, $"当前平台 `{platform.Name}` 不支持发送文件：`{filePath}`", ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送 /file 产物失败: path={Path}", filePath);
                await platform.ReplyAsync(replyContext, $"发送文件失败：`{filePath}` - {ex.Message}", ct);
            }
        }

        if (sentFiles > 0 || sentImages > 0)
        {
            await platform.ReplyAsync(replyContext, $"📦 已发送 {sentFiles} 个文件，{sentImages} 张图片。", ct);
        }
    }

    private static FileOutputExtractionResult ExtractFileOutputPaths(string workDir, string resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return new FileOutputExtractionResult(false, [], []);

        var match = FilesBlockRegex.Match(resultText);
        if (!match.Success)
            return new FileOutputExtractionResult(false, [], []);

        var outputRoot = Path.GetFullPath(Path.Combine(workDir, "output"));

        var results = new List<string>();
        var invalidEntries = new List<string>();
        foreach (var rawLine in match.Groups["paths"].Value.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var candidate = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(workDir, trimmed));

            if (!IsPathInsideRoot(candidate, outputRoot))
            {
                invalidEntries.Add($"{trimmed} (必须位于 output/ 目录下)");
                continue;
            }

            if (Directory.Exists(candidate))
            {
                var directoryInfo = new DirectoryInfo(candidate);
                if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    invalidEntries.Add($"{trimmed} (不允许发送符号链接或 junction 目录)");
                    continue;
                }

                foreach (var child in EnumerateRegularFiles(candidate))
                {
                    var info = new FileInfo(child);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        invalidEntries.Add($"{Path.GetRelativePath(workDir, child)} (不允许发送符号链接文件)");
                        continue;
                    }

                    if (info.Length > 30L * 1024 * 1024)
                    {
                        invalidEntries.Add($"{Path.GetRelativePath(workDir, child)} (超过 30 MB)");
                        continue;
                    }

                    results.Add(Path.GetFullPath(child));
                }
                continue;
            }

            if (!File.Exists(candidate))
            {
                invalidEntries.Add($"{trimmed} (文件不存在)");
                continue;
            }

            var fileInfo = new FileInfo(candidate);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                invalidEntries.Add($"{trimmed} (不允许发送符号链接文件)");
                continue;
            }

            if (fileInfo.Length > 30L * 1024 * 1024)
            {
                invalidEntries.Add($"{trimmed} (超过 30 MB)");
                continue;
            }

            results.Add(candidate);
        }

        return new FileOutputExtractionResult(true, results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), invalidEntries);
    }

    private static IEnumerable<string> EnumerateRegularFiles(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        return Directory.EnumerateFiles(directoryPath, "*", options);
    }

    private static bool IsPathInsideRoot(string candidatePath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return relativePath == "."
               || (!Path.IsPathRooted(relativePath)
                   && !relativePath.Equals("..", StringComparison.Ordinal)
                   && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string RemoveFilesBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var stripped = FilesBlockRegex.Replace(text, string.Empty);
        stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
        return stripped.Trim();
    }

    private static string ComposeFullAgentOutput(string streamedText, string resultText)
    {
        var streamed = streamedText?.Trim() ?? string.Empty;
        var result = resultText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(streamed))
            return result;
        if (string.IsNullOrWhiteSpace(result))
            return streamed;
        if (string.Equals(streamed, result, StringComparison.Ordinal))
            return result;
        if (streamed.Contains(result, StringComparison.Ordinal))
            return streamed;
        if (result.Contains(streamed, StringComparison.Ordinal))
            return result;
        return streamed + "\n\n" + result;
    }
}
