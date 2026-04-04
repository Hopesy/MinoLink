using System.Text.Json;

namespace MinoLink.Core;

/// <summary>
/// 读取 Claude Code 原生的 ~/.claude/projects/ 会话列表。
/// </summary>
public static class ClaudeNativeSession
{
    /// <summary>
    /// 将工作目录路径转换为 Claude 的项目目录名称。
    /// </summary>
    public static string EncodeProjectDir(string workDir)
    {
        return workDir.Replace('\\', '/').Replace(':', '-').Replace('/', '-');
    }

    /// <summary>
    /// 将 Claude 项目目录名称还原为工作目录路径。
    /// </summary>
    public static string DecodeProjectDir(string encoded)
    {
        // C--Users-zhouh-Desktop-MinoLink → C:/Users/zhouh/Desktop/MinoLink
        if (encoded.Length >= 3 && encoded[1] == '-' && encoded[2] == '-')
        {
            var drive = encoded[0] + ":/";
            var rest = encoded[3..].Replace('-', '/');
            return drive + rest;
        }
        return encoded.Replace('-', '/');
    }

    /// <summary>
    /// 获取指定工作目录下的所有 Claude Code 原生会话，按最后修改时间倒序。
    /// </summary>
    public static List<NativeSessionInfo> GetSessions(string workDir, bool includeSummaries = true)
    {
        var projectDirName = EncodeProjectDir(workDir);
        var claudeProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", projectDirName);

        if (!Directory.Exists(claudeProjectsDir))
            return [];

        return LoadSessionsFromDir(claudeProjectsDir, workDir, includeSummaries);
    }

    /// <summary>
    /// 获取所有 Claude Code 项目及其会话，按最后活跃时间倒序。
    /// </summary>
    public static List<NativeProjectInfo> GetAllProjects(bool includeSummaries = true)
    {
        var claudeProjectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (!Directory.Exists(claudeProjectsRoot))
            return [];

        var results = new List<NativeProjectInfo>();
        foreach (var dir in Directory.GetDirectories(claudeProjectsRoot))
        {
            var encoded = Path.GetFileName(dir);
            var workDir = DecodeProjectDir(encoded);
            var sessions = LoadSessionsFromDir(dir, workDir, includeSummaries);
            if (sessions.Count == 0) continue;
            results.Add(new NativeProjectInfo(workDir, encoded, sessions));
        }

        results.Sort((a, b) => b.LastActive.CompareTo(a.LastActive));
        return results;
    }

    private static List<NativeSessionInfo> LoadSessionsFromDir(string dir, string workDir, bool includeSummaries)
    {
        var results = new List<NativeSessionInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            var mtime = File.GetLastWriteTime(file);
            var summary = includeSummaries ? ReadFirstUserMessage(file) : string.Empty;
            results.Add(new NativeSessionInfo(sessionId, workDir, mtime, summary));
        }
        results.Sort((a, b) => b.LastActive.CompareTo(a.LastActive));
        return results;
    }

    private static string ReadFirstUserMessage(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            using var sr = new StreamReader(fs);
            string? customTitle = null;
            string? firstMessage = null;

            while (sr.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                var entryType = typeEl.GetString();
                if (string.IsNullOrWhiteSpace(customTitle) &&
                    entryType == "custom-title" &&
                    root.TryGetProperty("customTitle", out var customTitleEl))
                {
                    customTitle = NativeSessionSummaryHelper.NormalizeCandidate(customTitleEl.GetString());
                    continue;
                }

                if (entryType != "user" || !string.IsNullOrWhiteSpace(firstMessage))
                    continue;

                if (!root.TryGetProperty("message", out var msgEl) ||
                    !msgEl.TryGetProperty("content", out var contentEl))
                    continue;

                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    firstMessage = NativeSessionSummaryHelper.NormalizeCandidate(contentEl.GetString());
                    continue;
                }

                if (contentEl.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in contentEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var itemTypeEl) || itemTypeEl.GetString() != "text" ||
                        !item.TryGetProperty("text", out var textEl))
                        continue;

                    firstMessage = NativeSessionSummaryHelper.NormalizeCandidate(textEl.GetString());
                    if (!string.IsNullOrWhiteSpace(firstMessage))
                        break;
                }
            }

            return !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle
                : firstMessage ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed record NativeSessionInfo(
    string SessionId,
    string WorkDir,
    DateTime LastActive,
    string Summary);

public sealed record NativeProjectInfo(
    string WorkDir,
    string EncodedDir,
    List<NativeSessionInfo> Sessions)
{
    public DateTime LastActive => Sessions.Count > 0 ? Sessions[0].LastActive : DateTime.MinValue;
}
