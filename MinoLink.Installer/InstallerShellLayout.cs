namespace MinoLink.Installer;

public static class InstallerShellLayout
{
    public const string ApplicationExecutableTarget = @"[INSTALLDIR]MinoLink.Desktop.exe";
    public const string DesktopDirectory = @"%Desktop%";
    public const string StartMenuDirectory = @"%ProgramMenu%\MinoLink";
    public const string WorkingDirectory = "[INSTALLDIR]";

    public static InstallerShortcutDefinition[] CreateShortcuts()
    {
        return
        [
            new InstallerShortcutDefinition("MinoLink", StartMenuDirectory, ApplicationExecutableTarget, WorkingDirectory),
            new InstallerShortcutDefinition("MinoLink", DesktopDirectory, ApplicationExecutableTarget, WorkingDirectory)
        ];
    }
}
