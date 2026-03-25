using System.Text.Json;

namespace MinoLink.Core;

/// <summary>
/// 读取 Codex 原生的 ~/.codex/sessions/ 会话列表。
/// </summary>
public static class CodexNativeSession
{
    public static List<NativeSessionInfo> GetSessions(string workDir)
    {
        var normalizedWorkDir = NormalizeWorkDir(workDir);
        return LoadAllSessions()
            .Where(x => string.Equals(NormalizeWorkDir(x.WorkDir), normalizedWorkDir, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastActive)
            .ToList();
    }

    public static List<NativeProjectInfo> GetAllProjects()
    {
        return LoadAllSessions()
            .GroupBy(x => x.WorkDir, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NativeProjectInfo(g.Key, g.Key, g.OrderByDescending(x => x.LastActive).ToList()))
            .OrderByDescending(x => x.LastActive)
            .ToList();
    }

    private static List<NativeSessionInfo> LoadAllSessions()
    {
        var sessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");

        if (!Directory.Exists(sessionsRoot))
            return [];

        var results = new List<NativeSessionInfo>();
        foreach (var file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            if (!TryReadSessionMeta(file, out var sessionId, out var sessionWorkDir, out var lastActive))
                continue;

            var summary = ReadFirstUserMessage(file);
            results.Add(new NativeSessionInfo(sessionId, sessionWorkDir, lastActive, summary));
        }

        return results;
    }

    private static bool TryReadSessionMeta(string filePath, out string sessionId, out string workDir, out DateTime lastActive)
    {
        sessionId = string.Empty;
        workDir = string.Empty;
        lastActive = File.GetLastWriteTime(filePath);

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "session_meta")
                    continue;
                if (!root.TryGetProperty("payload", out var payload))
                    break;

                sessionId = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                workDir = payload.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() ?? string.Empty : string.Empty;
                if (payload.TryGetProperty("timestamp", out var tsEl) && DateTime.TryParse(tsEl.GetString(), out var ts))
                    lastActive = ts;
                return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(workDir);
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadFirstUserMessage(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                if (typeEl.GetString() == "event_msg" &&
                    root.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("type", out var payloadTypeEl) &&
                    payloadTypeEl.GetString() == "user_message" &&
                    payload.TryGetProperty("message", out var messageEl))
                {
                    var message = messageEl.GetString()?.Trim() ?? string.Empty;
                    return message.Length <= 80 ? message : message[..80] + "...";
                }

                if (typeEl.GetString() == "response_item" &&
                    root.TryGetProperty("payload", out var responsePayload) &&
                    responsePayload.TryGetProperty("role", out var roleEl) &&
                    roleEl.GetString() == "user" &&
                    responsePayload.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contentEl.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out var itemTypeEl) || itemTypeEl.GetString() != "input_text")
                            continue;
                        var text = item.TryGetProperty("text", out var textEl) ? textEl.GetString()?.Trim() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(text))
                            return text.Length <= 80 ? text : text[..80] + "...";
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string NormalizeWorkDir(string workDir) =>
        Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
