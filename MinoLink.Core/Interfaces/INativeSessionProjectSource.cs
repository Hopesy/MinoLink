namespace MinoLink.Core.Interfaces;

public interface INativeSessionProjectSource
{
    string AgentType { get; }

    IReadOnlyList<NativeProjectInfo> GetAllProjects(bool includeSummaries);

    IReadOnlyList<NativeSessionInfo> GetSessions(string workDir);
}
