using System.Collections.Concurrent;
using MinoLink.Core.Models;

namespace MinoLink.Core;

/// <summary>
/// 管理所有活跃会话，线程安全。
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();

    public SessionRecord GetOrCreate(string sessionKey, string platform, string from, string? fromName = null)
    {
        return _sessions.GetOrAdd(sessionKey, _ => new SessionRecord
        {
            SessionKey = sessionKey,
            Platform = platform,
            From = from,
            FromName = fromName,
        });
    }

    public SessionRecord? Get(string sessionKey) =>
        _sessions.TryGetValue(sessionKey, out var session) ? session : null;

    public bool Remove(string sessionKey) =>
        _sessions.TryRemove(sessionKey, out _);

    public IReadOnlyCollection<SessionRecord> GetAll() => _sessions.Values.ToList();
}
