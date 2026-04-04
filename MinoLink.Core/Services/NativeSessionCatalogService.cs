using MinoLink.Core.Interfaces;

namespace MinoLink.Core.Services;

public sealed class NativeSessionCatalogService(IEnumerable<INativeSessionProjectSource> sources)
{
    private readonly IReadOnlyList<INativeSessionProjectSource> _sources = sources.ToArray();
    private readonly object _syncRoot = new();
    private IReadOnlyList<NativeProjectCatalogItem>? _cachedProjects;
    private Task<IReadOnlyList<NativeProjectCatalogItem>>? _loadProjectsTask;

    public IReadOnlyList<NativeProjectCatalogItem> GetCachedProjects() =>
        _cachedProjects ?? [];

    public async Task<IReadOnlyList<NativeProjectCatalogItem>> GetProjectsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<NativeProjectCatalogItem>> loadTask;
        lock (_syncRoot)
        {
            if (!forceRefresh && _cachedProjects is not null)
                return _cachedProjects;

            if (!forceRefresh && _loadProjectsTask is not null)
            {
                loadTask = _loadProjectsTask;
            }
            else
            {
                loadTask = Task.Run(BuildProjectsSnapshot, cancellationToken);
                _loadProjectsTask = loadTask;
            }
        }

        try
        {
            var projects = await loadTask.ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (ReferenceEquals(_loadProjectsTask, loadTask))
                    _loadProjectsTask = null;

                _cachedProjects = projects;
                return _cachedProjects;
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_loadProjectsTask, loadTask))
                    _loadProjectsTask = null;
            }

            throw;
        }
    }

    public Task<IReadOnlyList<NativeSessionInfo>> GetProjectSessionsAsync(
        string agentType,
        string workDir,
        CancellationToken cancellationToken = default)
    {
        var source = _sources.FirstOrDefault(x =>
            string.Equals(x.AgentType, agentType, StringComparison.OrdinalIgnoreCase));

        if (source is null)
            return Task.FromResult<IReadOnlyList<NativeSessionInfo>>([]);

        return Task.Run(() =>
            (IReadOnlyList<NativeSessionInfo>)source.GetSessions(workDir)
                .OrderByDescending(x => x.LastActive)
                .ToList(), cancellationToken);
    }

    private IReadOnlyList<NativeProjectCatalogItem> BuildProjectsSnapshot()
    {
        return _sources
            .SelectMany(source => source.GetAllProjects(includeSummaries: false)
                .Select(project => new NativeProjectCatalogItem(
                    source.AgentType,
                    project.WorkDir,
                    project.EncodedDir,
                    project.Sessions.OrderByDescending(x => x.LastActive).ToList())))
            .OrderByDescending(project => project.LastActive)
            .ToList();
    }
}

public sealed record NativeProjectCatalogItem(
    string AgentType,
    string WorkDir,
    string EncodedDir,
    List<NativeSessionInfo> Sessions)
{
    public DateTime LastActive => Sessions.Count > 0 ? Sessions[0].LastActive : DateTime.MinValue;
}

public sealed class ClaudeNativeSessionProjectSource : INativeSessionProjectSource
{
    public string AgentType => "claudecode";

    public IReadOnlyList<NativeProjectInfo> GetAllProjects(bool includeSummaries) =>
        ClaudeNativeSession.GetAllProjects(includeSummaries);

    public IReadOnlyList<NativeSessionInfo> GetSessions(string workDir) =>
        ClaudeNativeSession.GetSessions(workDir);
}

public sealed class CodexNativeSessionProjectSource : INativeSessionProjectSource
{
    public string AgentType => "codex";

    public IReadOnlyList<NativeProjectInfo> GetAllProjects(bool includeSummaries) =>
        CodexNativeSession.GetAllProjects(includeSummaries);

    public IReadOnlyList<NativeSessionInfo> GetSessions(string workDir) =>
        CodexNativeSession.GetSessions(workDir);
}
