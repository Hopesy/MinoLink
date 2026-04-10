using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using MinoLink.Core.Interfaces;

namespace MinoLink.Desktop.Services;

public sealed class AppVersionProvider : IAppVersionProvider
{
    private readonly Lazy<string> _version = new(ResolveVersion);

    public string Version => _version.Value;

    private static string ResolveVersion()
    {
        var assembly = typeof(App).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return NormalizeVersion(informationalVersion);

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
            return NormalizeVersion(fileVersion);

        var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        if (!string.IsNullOrWhiteSpace(productVersion))
            return NormalizeVersion(productVersion);

        var projectVersion = TryReadVersionFromProjectFile();
        if (!string.IsNullOrWhiteSpace(projectVersion))
            return NormalizeVersion(projectVersion);

        var assemblyVersion = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "0.0.0" : NormalizeVersion(assemblyVersion);
    }

    private static string? TryReadVersionFromProjectFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var projectFile = Path.Combine(directory.FullName, "MinoLink.Desktop", "MinoLink.Desktop.csproj");
            if (File.Exists(projectFile))
            {
                var document = XDocument.Load(projectFile);
                return document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value?.Trim();
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string NormalizeVersion(string version)
    {
        var sanitized = version.Trim();
        if (sanitized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            sanitized = sanitized[1..];

        sanitized = sanitized.Split('+', 2)[0];
        sanitized = sanitized.Split('-', 2)[0];

        var parts = sanitized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "0.0.0";

        return parts.Length <= 3 ? sanitized : string.Join('.', parts.Take(3));
    }
}
