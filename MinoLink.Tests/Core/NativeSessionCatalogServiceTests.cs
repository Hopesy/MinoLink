using MinoLink.Core;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Services;

namespace MinoLink.Tests.Core;

public sealed class NativeSessionCatalogServiceTests
{
    [Fact]
    public async Task GetProjectsAsync_ShouldCacheCombinedProjectsUntilForceRefresh()
    {
        var claude = new FakeNativeSessionProjectSource(
            "claudecode",
            [CreateProject("claudecode", "C:/Repos/A", "claude-1", new DateTime(2026, 4, 4, 9, 0, 0), string.Empty)]);
        var codex = new FakeNativeSessionProjectSource(
            "codex",
            [CreateProject("codex", "C:/Repos/B", "codex-1", new DateTime(2026, 4, 4, 10, 0, 0), string.Empty)]);
        var sut = new NativeSessionCatalogService([claude, codex]);

        var first = await sut.GetProjectsAsync();
        var second = await sut.GetProjectsAsync();

        Assert.Equal(new[] { "C:/Repos/B", "C:/Repos/A" }, first.Select(x => x.WorkDir).ToArray());
        Assert.Equal(1, claude.GetAllProjectsCalls);
        Assert.Equal(1, codex.GetAllProjectsCalls);
        Assert.Same(first, second);

        var refreshed = await sut.GetProjectsAsync(forceRefresh: true);

        Assert.Equal(2, claude.GetAllProjectsCalls);
        Assert.Equal(2, codex.GetAllProjectsCalls);
        Assert.NotSame(first, refreshed);
    }

    [Fact]
    public async Task GetProjectSessionsAsync_ShouldLoadDetailsFromMatchingProviderOnly()
    {
        var claude = new FakeNativeSessionProjectSource(
            "claudecode",
            [CreateProject("claudecode", "C:/Repos/A", "claude-1", new DateTime(2026, 4, 4, 9, 0, 0), string.Empty)],
            [new NativeSessionInfo("claude-1", "C:/Repos/A", new DateTime(2026, 4, 4, 9, 0, 0), "详细摘要")]);
        var codex = new FakeNativeSessionProjectSource(
            "codex",
            [CreateProject("codex", "C:/Repos/B", "codex-1", new DateTime(2026, 4, 4, 10, 0, 0), string.Empty)],
            [new NativeSessionInfo("codex-1", "C:/Repos/B", new DateTime(2026, 4, 4, 10, 0, 0), "codex 摘要")]);
        var sut = new NativeSessionCatalogService([claude, codex]);

        var sessions = await sut.GetProjectSessionsAsync("claudecode", "C:/Repos/A");

        var session = Assert.Single(sessions);
        Assert.Equal("详细摘要", session.Summary);
        Assert.Equal(1, claude.GetSessionsCalls);
        Assert.Equal(0, codex.GetSessionsCalls);
    }

    private static NativeProjectInfo CreateProject(string agentType, string workDir, string sessionId, DateTime lastActive, string summary)
    {
        return new NativeProjectInfo(workDir, $"{agentType}-{workDir}",
        [
            new NativeSessionInfo(sessionId, workDir, lastActive, summary),
        ]);
    }

    private sealed class FakeNativeSessionProjectSource(
        string agentType,
        IReadOnlyList<NativeProjectInfo> projects,
        IReadOnlyList<NativeSessionInfo>? sessions = null) : INativeSessionProjectSource
    {
        public int GetAllProjectsCalls { get; private set; }
        public int GetSessionsCalls { get; private set; }

        public string AgentType { get; } = agentType;

        public IReadOnlyList<NativeProjectInfo> GetAllProjects(bool includeSummaries)
        {
            GetAllProjectsCalls++;
            return projects;
        }

        public IReadOnlyList<NativeSessionInfo> GetSessions(string workDir)
        {
            GetSessionsCalls++;
            return sessions ?? [];
        }
    }
}
