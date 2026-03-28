using System.Collections.Concurrent;
using System.Text.Json;
using MinoLink.Core.Models;

namespace MinoLink.Core;

/// <summary>
/// 轻量会话管理器：只记录每个用户的当前工作目录（ProjectKey）与 Agent 类型。
/// 多会话列表由各 Agent 的原生会话目录管理，不在此处维护。
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionRecord> _records = new();
    private readonly string _storageFilePath;
    private readonly object _ioLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SessionManager(string storageFilePath)
    {
        _storageFilePath = storageFilePath;
        Load();
    }

    /// <summary>获取当前会话记录，若不存在则自动创建。</summary>
    public SessionRecord GetOrCreate(string sessionKey, string platform, string from, string? fromName = null)
    {
        var created = false;
        var record = _records.GetOrAdd(sessionKey, _ =>
        {
            created = true;
            return new SessionRecord
            {
                SessionKey = sessionKey,
                Platform = platform,
                From = from,
                FromName = fromName,
            };
        });

        if (created)
            Save();

        return record;
    }

    /// <summary>获取当前活跃会话（不创建）。</summary>
    public SessionRecord? GetActive(string sessionKey)
    {
        _records.TryGetValue(sessionKey, out var record);
        return record;
    }

    /// <summary>持久化当前状态到磁盘。</summary>
    public void Save()
    {
        lock (_ioLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_storageFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var data = new StorageModel
                {
                    Records = new Dictionary<string, StorageRecord>(
                        _records.Select(kv => new KeyValuePair<string, StorageRecord>(
                            kv.Key,
                            new StorageRecord
                            {
                                AgentType = kv.Value.AgentType,
                                ProjectKey = kv.Value.ProjectKey,
                                PendingStartMode = kv.Value.PendingStartMode,
                                PendingResumeSessionId = kv.Value.PendingResumeSessionId,
                                Platform = kv.Value.Platform,
                                From = kv.Value.From,
                                FromName = kv.Value.FromName,
                                LastActiveAt = kv.Value.LastActiveAt,
                            })))
                };
                File.WriteAllText(_storageFilePath, JsonSerializer.Serialize(data, JsonOptions));
            }
            catch { /* 持久化失败不影响运行 */ }
        }
    }

    private void Load()
    {
        if (!File.Exists(_storageFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_storageFilePath);

            // 尝试新格式
            var data = JsonSerializer.Deserialize<StorageModel>(json, JsonOptions);
            if (data?.Records is { Count: > 0 })
            {
                foreach (var (key, sr) in data.Records)
                {
                    _records[key] = new SessionRecord
                    {
                        SessionKey = key,
                        AgentType = string.IsNullOrWhiteSpace(sr.AgentType) ? "claudecode" : sr.AgentType,
                        ProjectKey = sr.ProjectKey,
                        PendingStartMode = sr.PendingStartMode,
                        PendingResumeSessionId = sr.PendingResumeSessionId,
                        Platform = sr.Platform,
                        From = sr.From ?? key,
                        FromName = sr.FromName,
                        LastActiveAt = sr.LastActiveAt,
                    };
                }
                return;
            }

            // 兼容旧格式（Users 数组）
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Users", out var users)) return;

            foreach (var user in users.EnumerateObject())
            {
                string? projectKey = null, platform = null, from = null, fromName = null;
                var agentType = "claudecode";
                var lastActive = DateTimeOffset.UtcNow;

                if (user.Value.TryGetProperty("Sessions", out var sessions) &&
                    sessions.ValueKind == JsonValueKind.Array)
                {
                    var arr = sessions.EnumerateArray().ToList();
                    int activeIndex = 0;
                    if (user.Value.TryGetProperty("ActiveIndex", out var ai))
                        activeIndex = Math.Max(0, Math.Min(ai.GetInt32(), arr.Count - 1));

                    if (arr.Count > 0)
                    {
                        var s = arr[activeIndex];
                        if (s.TryGetProperty("ProjectKey", out var pk)) projectKey = pk.GetString();
                        if (s.TryGetProperty("Platform", out var pl)) platform = pl.GetString();
                        if (s.TryGetProperty("From", out var fr)) from = fr.GetString();
                        if (s.TryGetProperty("FromName", out var fn)) fromName = fn.GetString();
                        if (s.TryGetProperty("LastActiveAt", out var la) &&
                            DateTimeOffset.TryParse(la.GetString(), out var laDto))
                            lastActive = laDto;
                    }
                }

                _records[user.Name] = new SessionRecord
                {
                    SessionKey = user.Name,
                    AgentType = agentType,
                    ProjectKey = projectKey,
                    Platform = platform,
                    From = from ?? user.Name,
                    FromName = fromName,
                    LastActiveAt = lastActive,
                };
            }
        }
        catch { /* 加载失败则从空状态启动 */ }
    }

    // ─── 存储模型 ────────────────────────────────────────────────

    private sealed class StorageModel
    {
        public Dictionary<string, StorageRecord> Records { get; set; } = new();
    }

    private sealed class StorageRecord
    {
        public string? AgentType { get; set; }
        public string? ProjectKey { get; set; }
        public string? PendingStartMode { get; set; }
        public string? PendingResumeSessionId { get; set; }
        public string? Platform { get; set; }
        public string? From { get; set; }
        public string? FromName { get; set; }
        public DateTimeOffset LastActiveAt { get; set; }
    }
}
