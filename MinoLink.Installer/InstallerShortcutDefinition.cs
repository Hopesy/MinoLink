namespace MinoLink.Installer;

public sealed class InstallerShortcutDefinition
{
    public InstallerShortcutDefinition(
        string name,
        string directory,
        string target,
        string workingDirectory)
    {
        Name = name;
        Directory = directory;
        Target = target;
        WorkingDirectory = workingDirectory;
    }

    public string Name { get; }

    public string Directory { get; }

    public string Target { get; }

    public string WorkingDirectory { get; }
}
