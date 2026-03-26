namespace MinoLink.Core;

internal static class NativeSessionSummaryHelper
{
    private const int MaxLength = 80;

    public static string NormalizeCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (ShouldSkip(normalized))
            return string.Empty;

        var firstLine = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine) || ShouldSkip(firstLine))
            return string.Empty;

        return firstLine.Length <= MaxLength ? firstLine : firstLine[..MaxLength] + "...";
    }

    private static bool ShouldSkip(string text)
    {
        return text.StartsWith("# AGENTS.md instructions for ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<INSTRUCTIONS>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Request interrupted by user", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("The user doesn't want to proceed with this tool use", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AGENTS.md instructions for", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<INSTRUCTIONS>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<environment_context>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tool use was rejected", StringComparison.OrdinalIgnoreCase);
    }
}
