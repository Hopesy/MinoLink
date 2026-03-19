using System.Collections.Concurrent;
using System.Text.Json;
using MinoLink.Core.Models;

namespace MinoLink.Core;

/// <summary>
/// 管理所有活跃会话，线程安全。支持每个 sessionKey 拥有多个会话。
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, UserSessions> _users = new();
    private readonly string _storageFilePath;
    private readonly object _ioLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public SessionManager(string storageFilePath)
    {
        _storageFilePath = storageFilePath;
        Load();
    }

    /// <summary>获取当前活跃会话，若不存在则自动创建。</summary>
    public SessionRecord GetOrCreate(string sessionKey, string platform, string from, string? fromName = null)
    {
        var user = _users.GetOrAdd(sessionKey, _ => new UserSessions());
        var created = false;
        SessionRecord record;

        lock (user)
        {
            if (user.Sessions.Count == 0)
            {
                user.Sessions.Add(new SessionRecord
                {
                    SessionKey = sessionKey,
                    Platform = platform,
                    From = from,
                    FromName = fromName,
                });
                created = true;
            }
            record = user.Sessions[user.ActiveIndex];
        }

        if (created)
            Save();

        return record;
    }

    /// <summary>创建新会话并设为活跃。</summary>
    public SessionRecord CreateNew(string sessionKey, string platform, string from, string? name = null)
    {
        var user = _users.GetOrAdd(sessionKey, _ => new UserSessions());
        SessionRecord record;

        lock (user)
        {
            record = new SessionRecord
            {
                SessionKey = sessionKey,
                Platform = platform,
                From = from,
                Name = name,
            };
            user.Sessions.Add(record);
            user.ActiveIndex = user.Sessions.Count - 1;
        }

        Save();
        return record;
    }

    /// <summary>获取当前活跃会话。</summary>
    public SessionRecord? GetActive(string sessionKey)
    {
        if (!_users.TryGetValue(sessionKey, out var user)) return null;
        lock (user)
        {
            return user.Sessions.Count > 0 ? user.Sessions[user.ActiveIndex] : null;
        }
    }

    /// <summary>获取某用户的所有会话和当前活跃索引。</summary>
    public (IReadOnlyList<SessionRecord> Sessions, int ActiveIndex) GetAllSessions(string sessionKey)
    {
        if (!_users.TryGetValue(sessionKey, out var user))
            return (Array.Empty<SessionRecord>(), 0);
        lock (user)
        {
            return (user.Sessions.ToList(), user.ActiveIndex);
        }
    }

    /// <summary>切换活跃会话（1-based index）。</summary>
    public SessionRecord? SwitchTo(string sessionKey, int oneBasedIndex)
    {
        if (!_users.TryGetValue(sessionKey, out var user)) return null;
        SessionRecord? record = null;

        lock (user)
        {
            var idx = oneBasedIndex - 1;
            if (idx < 0 || idx >= user.Sessions.Count) return null;
            user.ActiveIndex = idx;
            record = user.Sessions[idx];
        }

        Save();
        return record;
    }

    /// <summary>删除当前活跃会话，自动切到上一个或下一个。返回被删除的会话。</summary>
    public SessionRecord? RemoveActive(string sessionKey)
    {
        if (!_users.TryGetValue(sessionKey, out var user)) return null;
        SessionRecord? removed;

        lock (user)
        {
            if (user.Sessions.Count == 0) return null;
            removed = user.Sessions[user.ActiveIndex];
            user.Sessions.RemoveAt(user.ActiveIndex);
            if (user.Sessions.Count == 0)
                user.ActiveIndex = 0;
            else if (user.ActiveIndex >= user.Sessions.Count)
                user.ActiveIndex = user.Sessions.Count - 1;
        }

        Save();
        return removed;
    }

    /// <summary>持久化当前会话状态。</summary>
    public void Save()
    {
        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var snapshot = CreateSnapshot();
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_storageFilePath, json);
        }
    }

    private PersistedState CreateSnapshot()
    {
        var state = new PersistedState();

        foreach (var (sessionKey, user) in _users)
        {
            lock (user)
            {
                state.Users[sessionKey] = new PersistedUserSessions
                {
                    ActiveIndex = user.Sessions.Count == 0
                        ? 0
                        : Math.Clamp(user.ActiveIndex, 0, user.Sessions.Count - 1),
                    Sessions = user.Sessions.Select(CloneForPersistence).ToList(),
                };
            }
        }

        return state;
    }

    private void Load()
    {
        if (!File.Exists(_storageFilePath)) return;

        try
        {
            var json = File.ReadAllText(_storageFilePath);
            var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
            if (state?.Users is null) return;

            foreach (var (sessionKey, persisted) in state.Users)
            {
                if (persisted.Sessions.Count == 0) continue;

                var user = new UserSessions
                {
                    ActiveIndex = Math.Clamp(persisted.ActiveIndex, 0, persisted.Sessions.Count - 1),
                };
                user.Sessions.AddRange(persisted.Sessions.Select(CloneForRuntime));
                _users[sessionKey] = user;
            }
        }
        catch
        {
            // 持久化文件损坏时忽略，回退为内存空状态
        }
    }

    private static SessionRecord CloneForPersistence(SessionRecord record) => new()
    {
        SessionKey = record.SessionKey,
        Name = record.Name,
        From = record.From,
        FromName = record.FromName,
        Platform = record.Platform,
        CreatedAt = record.CreatedAt,
        LastActiveAt = record.LastActiveAt,
        ProjectKey = record.ProjectKey,
    };

    private static SessionRecord CloneForRuntime(SessionRecord record) => new()
    {
        SessionKey = record.SessionKey,
        Name = record.Name,
        From = record.From,
        FromName = record.FromName,
        Platform = record.Platform,
        CreatedAt = record.CreatedAt,
        LastActiveAt = record.LastActiveAt,
        ProjectKey = record.ProjectKey,
    };

    private sealed class UserSessions
    {
        public List<SessionRecord> Sessions { get; } = [];
        public int ActiveIndex { get; set; }
    }

    private sealed class PersistedState
    {
        public Dictionary<string, PersistedUserSessions> Users { get; init; } = [];
    }

    private sealed class PersistedUserSessions
    {
        public List<SessionRecord> Sessions { get; init; } = [];
        public int ActiveIndex { get; init; }
    }
}
