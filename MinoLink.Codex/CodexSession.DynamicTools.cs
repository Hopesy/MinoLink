using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MinoLink.Codex;

public sealed partial class CodexSession
{
    private async Task<ToolExecutionResult> ExecuteDynamicToolAsync(string? toolName, JsonElement arguments)
    {
        try
        {
            return toolName switch
            {
                "Read" => await ExecuteReadAsync(arguments),
                "Write" => await ExecuteWriteAsync(arguments),
                "Edit" => await ExecuteEditAsync(arguments),
                "Bash" => await ExecuteBashAsync(arguments),
                "Glob" => await ExecuteGlobAsync(arguments),
                "Grep" => await ExecuteGrepAsync(arguments),
                "TaskCreate" => ExecuteTaskCreate(arguments),
                "TaskUpdate" => ExecuteTaskUpdate(arguments),
                "TaskList" => ExecuteTaskList(),
                "TaskGet" => ExecuteTaskGet(arguments),
                "TaskOutput" => ExecuteTaskOutput(arguments),
                "TaskStop" => ExecuteTaskStop(arguments),
                "TodoWrite" => ExecuteTodoWrite(arguments),
                _ => new ToolExecutionResult(false, $"Unsupported tool call: {toolName}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 Codex dynamic tool 失败: {ToolName}", toolName);
            return new ToolExecutionResult(false, ex.Message);
        }
    }

    private Task<ToolExecutionResult> ExecuteReadAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var resolvedPath = ResolvePath(filePath);
        var fileInfo = new FileInfo(resolvedPath);
        if (!fileInfo.Exists)
            return Task.FromResult(new ToolExecutionResult(false, $"File not found: {filePath}"));

        var offset = Math.Max(1, GetOptionalInt(arguments, "offset") ?? 1);
        var limit = Math.Max(1, GetOptionalInt(arguments, "limit") ?? 2000);
        var lines = File.ReadAllLines(resolvedPath);
        if (lines.Length == 0)
            return Task.FromResult(new ToolExecutionResult(true, Text: string.Empty));

        var startIndex = Math.Min(offset - 1, lines.Length);
        var selected = lines.Skip(startIndex).Take(limit).ToArray();
        var builder = new StringBuilder();
        for (var i = 0; i < selected.Length; i++)
            builder.AppendLine($"{startIndex + i + 1,6}→{selected[i]}");

        return Task.FromResult(new ToolExecutionResult(true, Text: builder.ToString().TrimEnd()));
    }

    private Task<ToolExecutionResult> ExecuteWriteAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var content = GetOptionalString(arguments, "content") ?? string.Empty;
        var resolvedPath = ResolvePath(filePath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(resolvedPath, content, Encoding.UTF8);
        return Task.FromResult(new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
        {
            ["filePath"] = resolvedPath,
            ["bytesWritten"] = Encoding.UTF8.GetByteCount(content),
        }));
    }

    private Task<ToolExecutionResult> ExecuteEditAsync(JsonElement arguments)
    {
        var filePath = GetRequiredString(arguments, "file_path");
        var oldString = GetRequiredString(arguments, "old_string");
        var newString = GetRequiredString(arguments, "new_string");
        var replaceAll = GetOptionalBool(arguments, "replace_all") ?? false;
        var resolvedPath = ResolvePath(filePath);
        if (!File.Exists(resolvedPath))
            return Task.FromResult(new ToolExecutionResult(false, $"File not found: {filePath}"));

        var content = File.ReadAllText(resolvedPath);
        var matches = CountOccurrences(content, oldString);
        if (matches == 0)
            return Task.FromResult(new ToolExecutionResult(false, "old_string not found"));
        if (!replaceAll && matches > 1)
            return Task.FromResult(new ToolExecutionResult(false, "old_string is not unique"));

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);
        File.WriteAllText(resolvedPath, updated, Encoding.UTF8);

        var replaced = replaceAll ? matches : 1;
        return Task.FromResult(new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
        {
            ["filePath"] = resolvedPath,
            ["occurrences"] = replaced,
        }));
    }

    private async Task<ToolExecutionResult> ExecuteBashAsync(JsonElement arguments)
    {
        var command = GetRequiredString(arguments, "command");
        var timeout = GetOptionalInt(arguments, "timeout") ?? 120000;
        var runInBackground = GetOptionalBool(arguments, "run_in_background") ?? false;
        var description = GetOptionalString(arguments, "description") ?? "Run shell command";

        var psi = new ProcessStartInfo(GetBashShellPath())
        {
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        ApplyPreferredShellEnvironment(psi.Environment);

        var process = Process.Start(psi);
        if (process is null)
            return new ToolExecutionResult(false, "无法启动 bash 进程");

        if (runInBackground)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var state = new BackgroundTaskState(taskId, description, process);
            _backgroundTasks[taskId] = state;
            _ = CaptureBackgroundProcessAsync(state);
            return new ToolExecutionResult(true, Payload: new Dictionary<string, object?>
            {
                ["task_id"] = taskId,
                ["status"] = "running",
                ["description"] = description,
            });
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var text = BuildBashOutputText(stdout, stderr);

            return new ToolExecutionResult(process.ExitCode == 0, Message: process.ExitCode == 0 ? null : $"Command exited with code {process.ExitCode}", Text: text);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new ToolExecutionResult(false, $"Command timed out after {timeout}ms");
        }
        finally
        {
            process.Dispose();
        }
    }

    private Task<ToolExecutionResult> ExecuteGlobAsync(JsonElement arguments)
    {
        var pattern = GetRequiredString(arguments, "pattern");
        var basePath = ResolvePath(GetOptionalString(arguments, "path") ?? ".");
        if (!Directory.Exists(basePath))
            return Task.FromResult(new ToolExecutionResult(false, $"Directory not found: {basePath}"));

        var matches = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .Where(path => IsGlobMatch(Path.GetRelativePath(basePath, path), pattern) || IsGlobMatch(path, pattern))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        return Task.FromResult(new ToolExecutionResult(true, Text: string.Join("\n", matches)));
    }

    private Task<ToolExecutionResult> ExecuteGrepAsync(JsonElement arguments)
    {
        var pattern = GetRequiredString(arguments, "pattern");
        var basePath = ResolvePath(GetOptionalString(arguments, "path") ?? ".");
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
            return Task.FromResult(new ToolExecutionResult(false, $"Path not found: {basePath}"));

        var outputMode = GetOptionalString(arguments, "output_mode") ?? "files_with_matches";
        var glob = GetOptionalString(arguments, "glob");
        var ignoreCase = GetOptionalBool(arguments, "-i") ?? false;
        var headLimit = Math.Max(0, GetOptionalInt(arguments, "head_limit") ?? 0);
        var offset = Math.Max(0, GetOptionalInt(arguments, "offset") ?? 0);
        var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        var files = EnumerateSearchFiles(basePath, glob).ToArray();
        var fileMatches = new List<GrepFileMatch>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            var entries = new List<GrepLineMatch>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                    entries.Add(new GrepLineMatch(i + 1, lines[i]));
            }

            if (entries.Count > 0)
                fileMatches.Add(new GrepFileMatch(file, entries));
        }

        var text = outputMode switch
        {
            "count" => string.Join("\n", fileMatches
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)
                .Select(x => $"{x.Path}:{x.Lines.Count}")),
            "content" => string.Join("\n", fileMatches
                .SelectMany(x => x.Lines.Select(line => $"{x.Path}:{line.LineNumber}:{line.Text}"))
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)),
            _ => string.Join("\n", fileMatches
                .Select(x => x.Path)
                .Skip(offset)
                .Take(headLimit > 0 ? headLimit : int.MaxValue)),
        };

        return Task.FromResult(new ToolExecutionResult(true, Text: text));
    }

    private ToolExecutionResult ExecuteTaskCreate(JsonElement arguments)
    {
        var id = Interlocked.Increment(ref _taskSequence).ToString();
        var task = new TaskRecord(
            id,
            GetRequiredString(arguments, "subject"),
            GetRequiredString(arguments, "description"),
            GetOptionalString(arguments, "activeForm"),
            "pending",
            null,
            [],
            []);
        _tasks.Add(task);
        return new ToolExecutionResult(true, Payload: SerializeTask(task));
    }

    private ToolExecutionResult ExecuteTaskUpdate(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "taskId");
        var index = _tasks.FindIndex(x => x.Id == taskId);
        if (index < 0)
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        var current = _tasks[index];
        var updated = current with
        {
            Subject = GetOptionalString(arguments, "subject") ?? current.Subject,
            Description = GetOptionalString(arguments, "description") ?? current.Description,
            ActiveForm = GetOptionalString(arguments, "activeForm") ?? current.ActiveForm,
            Status = GetOptionalString(arguments, "status") ?? current.Status,
            Owner = GetOptionalString(arguments, "owner") ?? current.Owner,
            Blocks = MergeStringList(current.Blocks, GetOptionalStringArray(arguments, "addBlocks")),
            BlockedBy = MergeStringList(current.BlockedBy, GetOptionalStringArray(arguments, "addBlockedBy")),
        };
        _tasks[index] = updated;
        return new ToolExecutionResult(true, Payload: SerializeTask(updated));
    }

    private ToolExecutionResult ExecuteTaskList()
    {
        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(_tasks.Select(SerializeTaskSummary).ToArray()));
    }

    private ToolExecutionResult ExecuteTaskGet(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "taskId");
        var task = _tasks.FirstOrDefault(x => x.Id == taskId);
        return task is null
            ? new ToolExecutionResult(false, $"Task not found: {taskId}")
            : new ToolExecutionResult(true, Text: JsonSerializer.Serialize(SerializeTask(task)));
    }

    private ToolExecutionResult ExecuteTaskOutput(JsonElement arguments)
    {
        var taskId = GetRequiredString(arguments, "task_id");
        if (!_backgroundTasks.TryGetValue(taskId, out var state))
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(new
        {
            task_id = taskId,
            status = state.Status,
            stdout = state.Stdout.ToString(),
            stderr = state.Stderr.ToString(),
            exitCode = state.ExitCode,
        }));
    }

    private ToolExecutionResult ExecuteTaskStop(JsonElement arguments)
    {
        var taskId = GetOptionalString(arguments, "task_id") ?? GetOptionalString(arguments, "shell_id");
        if (string.IsNullOrWhiteSpace(taskId) || !_backgroundTasks.TryGetValue(taskId, out var state))
            return new ToolExecutionResult(false, $"Task not found: {taskId}");

        try
        {
            if (!state.Process.HasExited)
                state.Process.Kill(entireProcessTree: true);
            state.Status = "stopped";
            return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(new
            {
                task_id = taskId,
                status = state.Status,
            }));
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, ex.Message);
        }
    }

    private ToolExecutionResult ExecuteTodoWrite(JsonElement arguments)
    {
        var todos = GetTodoItems(arguments);
        _tasks.Clear();
        foreach (var todo in todos)
        {
            _tasks.Add(todo with { Id = string.IsNullOrWhiteSpace(todo.Id) ? Interlocked.Increment(ref _taskSequence).ToString() : todo.Id });
        }

        return new ToolExecutionResult(true, Text: JsonSerializer.Serialize(_tasks.Select(SerializeTaskSummary).ToArray()));
    }

    private async Task CaptureBackgroundProcessAsync(BackgroundTaskState state)
    {
        try
        {
            var stdoutTask = PumpReaderAsync(state.Process.StandardOutput, state.Stdout);
            var stderrTask = PumpReaderAsync(state.Process.StandardError, state.Stderr);
            await state.Process.WaitForExitAsync(_cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            state.ExitCode = state.Process.ExitCode;
            state.Status = state.Process.ExitCode == 0 ? "completed" : "failed";
        }
        catch (OperationCanceledException)
        {
            state.Status = "canceled";
        }
        catch (Exception ex)
        {
            state.Stderr.AppendLine(ex.Message);
            state.Status = "failed";
        }
    }

    private static async Task PumpReaderAsync(StreamReader reader, StringBuilder target)
    {
        while (await reader.ReadLineAsync() is { } line)
            target.AppendLine(line);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required field: {propertyName}");
        return value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result))
            return result;
        return null;
    }

    private static string[] GetOptionalStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_workDir, path));
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text.Remove(index, oldValue.Length).Insert(index, newValue);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateSearchFiles(string path, string? glob)
    {
        if (File.Exists(path))
        {
            if (string.IsNullOrWhiteSpace(glob) || IsGlobMatch(Path.GetFileName(path), glob))
                yield return path;
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (string.IsNullOrWhiteSpace(glob) || IsGlobMatch(Path.GetRelativePath(path, file), glob) || IsGlobMatch(Path.GetFileName(file), glob))
                yield return file;
        }
    }

    private static bool IsGlobMatch(string candidate, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^\\/]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(candidate.Replace('\\', '/'), regexPattern.Replace("\\/", "/"), RegexOptions.IgnoreCase);
    }

    private static string GetBashShellPath()
    {
        if (!OperatingSystem.IsWindows())
            return "/bin/bash";

        return GetPreferredGitBashPath() ?? "bash";
    }

    private static string BuildBashOutputText(string stdout, string stderr)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout))
            parts.Add(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            parts.Add(stderr.TrimEnd());
        return string.Join("\n", parts);
    }

    private static Dictionary<string, object?> SerializeTask(TaskRecord task) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = task.Id,
        ["subject"] = task.Subject,
        ["description"] = task.Description,
        ["activeForm"] = task.ActiveForm,
        ["status"] = task.Status,
        ["owner"] = task.Owner,
        ["blocks"] = task.Blocks.ToArray(),
        ["blockedBy"] = task.BlockedBy.ToArray(),
    };

    private static Dictionary<string, object?> SerializeTaskSummary(TaskRecord task) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = task.Id,
        ["subject"] = task.Subject,
        ["status"] = task.Status,
        ["owner"] = task.Owner,
        ["blockedBy"] = task.BlockedBy.ToArray(),
    };

    private static IReadOnlyList<string> MergeStringList(IReadOnlyList<string> current, IReadOnlyList<string> extra)
    {
        return current.Concat(extra)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<TaskRecord> GetTodoItems(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array)
            return [];

        var tasks = new List<TaskRecord>();
        foreach (var todo in todosEl.EnumerateArray())
        {
            tasks.Add(new TaskRecord(
                GetOptionalString(todo, "id") ?? string.Empty,
                GetOptionalString(todo, "content") ?? string.Empty,
                GetOptionalString(todo, "content") ?? string.Empty,
                null,
                NormalizeTodoStatus(GetOptionalString(todo, "status")),
                null,
                [],
                []));
        }
        return tasks;
    }

    private static string NormalizeTodoStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "in-progress" => "in_progress",
        "completed" => "completed",
        _ => "pending",
    };
}
