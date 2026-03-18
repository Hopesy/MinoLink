using System.Collections.Concurrent;
using MinoLink.Core.Models;

namespace MinoLink.Core;

/// <summary>
/// 管理所有活跃会话，线程安全。支持每个 sessionKey 拥有多个会话。
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, UserSessions> _users = new();

    /// <summary>获取当前活跃会话，若不存在则自动创建。</summary>
    public SessionRecord GetOrCreate(string sessionKey, string platform, string from, string? fromName = null)
    {
        var user = _users.GetOrAdd(sessionKey, _ => new UserSessions());
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
            }
            return user.Sessions[user.ActiveIndex];
        }
    }

    /// <summary>创建新会话并设为活跃。</summary>
    public SessionRecord CreateNew(string sessionKey, string platform, string from, string? name = null)
    {
        var user = _users.GetOrAdd(sessionKey, _ => new UserSessions());
        lock (user)
        {
            var record = new SessionRecord
            {
                SessionKey = sessionKey,
                Platform = platform,
                From = from,
                Name = name,
            };
            user.Sessions.Add(record);
            user.ActiveIndex = user.Sessions.Count - 1;
            return record;
        }
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
        lock (user)
        {
            var idx = oneBasedIndex - 1;
            if (idx < 0 || idx >= user.Sessions.Count) return null;
            user.ActiveIndex = idx;
            return user.Sessions[idx];
        }
    }

    /// <summary>删除当前活跃会话，自动切到上一个或下一个。返回被删除的会话。</summary>
    public SessionRecord? RemoveActive(string sessionKey)
    {
        if (!_users.TryGetValue(sessionKey, out var user)) return null;
        lock (user)
        {
            if (user.Sessions.Count == 0) return null;
            var removed = user.Sessions[user.ActiveIndex];
            user.Sessions.RemoveAt(user.ActiveIndex);
            if (user.Sessions.Count == 0)
                user.ActiveIndex = 0;
            else if (user.ActiveIndex >= user.Sessions.Count)
                user.ActiveIndex = user.Sessions.Count - 1;
            return removed;
        }
    }

    private sealed class UserSessions
    {
        public List<SessionRecord> Sessions { get; } = [];
        public int ActiveIndex { get; set; }
    }
}
