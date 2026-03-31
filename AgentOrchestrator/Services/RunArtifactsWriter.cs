using System.Text;
using System.Text.Json;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class RunArtifactsWriter
{
    private const int MaxProjectSlugLength = 40;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<RunArtifacts> WriteAsync(
        string projectRoot,
        ExecutionReport report,
        CancellationToken cancellationToken = default)
    {
        string projectSlug = SanitizeSegment(report.ProjectName);
        string projectDirectoryName = string.IsNullOrWhiteSpace(projectSlug)
            ? "untitled-project"
            : projectSlug;
        string projectDirectory = Path.Combine(projectRoot, "runs", projectDirectoryName);
        string runDirectory = Path.Combine(projectDirectory, report.RunId);
        string mainDirectory = Path.Combine(runDirectory, "main");
        string subDirectory = Path.Combine(runDirectory, "sub");

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(mainDirectory);

        string textReportPath = Path.Combine(runDirectory, "report.txt");
        string jsonReportPath = Path.Combine(runDirectory, "report.json");
        string mainLogPath = Path.Combine(mainDirectory, "main.log");

        Dictionary<string, string> subLogPaths = await WriteSubLogsAsync(subDirectory, report, cancellationToken);
        string reportText = BuildReportText(report, mainLogPath, subLogPaths);

        await File.WriteAllTextAsync(textReportPath, reportText, cancellationToken);
        await File.WriteAllTextAsync(mainLogPath, BuildMainLog(report, subLogPaths), cancellationToken);

        await using FileStream jsonStream = File.Create(jsonReportPath);
        await JsonSerializer.SerializeAsync(jsonStream, report, JsonOptions, cancellationToken);

        return new RunArtifacts(runDirectory, textReportPath, jsonReportPath, mainLogPath, subLogPaths);
    }

    private static string BuildReportText(
        ExecutionReport report,
        string mainLogPath,
        IReadOnlyDictionary<string, string> subLogPaths)
    {
        var builder = new StringBuilder();
        builder.Append(report.ToConsoleText());
        builder.AppendLine();
        builder.AppendLine("Generated Logs");
        builder.AppendLine($"- Main: {mainLogPath}");

        foreach ((string agentName, string logPath) in subLogPaths.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {agentName}: {logPath}");
        }

        return builder.ToString();
    }

    private static string BuildMainLog(
        ExecutionReport report,
        IReadOnlyDictionary<string, string> subLogPaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Run Id: {report.RunId}");
        builder.AppendLine($"Project: {report.ProjectName}");
        builder.AppendLine($"Goal: {report.Goal}");
        builder.AppendLine($"Generated At: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Sub Agents: {report.SubAgentCount}");
        builder.AppendLine();
        builder.AppendLine("Planned Tasks");

        foreach (AgentTask task in report.PlannedTasks.OrderBy(task => task.Id))
        {
            builder.AppendLine(
                $"- #{task.Id} [{task.Priority}] {task.Title} | " +
                $"{task.ExecutionLaneLabel} | Phase {task.Phase} | ETA ~{task.EstimatedMinutes} min");
            builder.AppendLine($"  {task.Description}");
        }

        builder.AppendLine();
        builder.AppendLine("Execution Timeline");

        foreach (TaskExecutionEvent executionEvent in report.ExecutionTimeline.OrderBy(item => item.Timestamp))
        {
            builder.AppendLine(
                $"{executionEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} | " +
                $"{executionEvent.AgentName} | {executionEvent.State} | " +
                $"Task #{executionEvent.TaskId} | Attempt {executionEvent.Attempt}");
            builder.AppendLine($"  {executionEvent.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("Task Results");

        foreach (TaskResult result in report.Results.OrderBy(result => result.TaskId))
        {
            builder.AppendLine($"- #{result.TaskId} {result.TaskTitle}");
            builder.AppendLine($"  Agent: {result.AssignedAgent}");
            builder.AppendLine($"  Status: {result.Status}");
            builder.AppendLine($"  Attempts: {result.Attempts}");
            builder.AppendLine($"  Mode: {result.ExecutionMode}");
            builder.AppendLine($"  Model: {result.Model ?? "n/a"}");
            builder.AppendLine($"  Tokens: {result.TokensUsed?.ToString("N0") ?? "n/a"}");
            builder.AppendLine($"  Duration: {result.Duration.TotalMilliseconds:N0} ms");
            builder.AppendLine($"  Summary: {result.Summary}");
        }

        if (subLogPaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Sub Logs");

            foreach ((string agentName, string logPath) in subLogPaths.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {agentName}: {logPath}");
            }
        }

        return builder.ToString();
    }

    private static async Task<Dictionary<string, string>> WriteSubLogsAsync(
        string subDirectory,
        ExecutionReport report,
        CancellationToken cancellationToken)
    {
        string[] subAgentNames = report.ExecutionTimeline
            .Select(item => item.AgentName)
            .Concat(report.Results.Select(item => item.AssignedAgent))
            .Where(name => name.StartsWith("sub-agent-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var subLogPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (subAgentNames.Length == 0)
        {
            return subLogPaths;
        }

        Directory.CreateDirectory(subDirectory);

        foreach (string agentName in subAgentNames)
        {
            string fileName = $"{SanitizeSegment(agentName)}.log";
            string logPath = Path.Combine(subDirectory, fileName);
            string logText = BuildSubAgentLog(report, agentName);

            await File.WriteAllTextAsync(logPath, logText, cancellationToken);
            subLogPaths[agentName] = logPath;
        }

        return subLogPaths;
    }

    private static string BuildSubAgentLog(ExecutionReport report, string agentName)
    {
        TaskExecutionEvent[] events = report.ExecutionTimeline
            .Where(item => string.Equals(item.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Timestamp)
            .ToArray();
        TaskResult[] results = report.Results
            .Where(item => string.Equals(item.AssignedAgent, agentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.TaskId)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"Run Id: {report.RunId}");
        builder.AppendLine($"Project: {report.ProjectName}");
        builder.AppendLine($"Agent: {agentName}");
        builder.AppendLine($"Generated At: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("Timeline");

        foreach (TaskExecutionEvent executionEvent in events)
        {
            builder.AppendLine(
                $"{executionEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} | " +
                $"{executionEvent.State} | Task #{executionEvent.TaskId} | Attempt {executionEvent.Attempt}");
            builder.AppendLine($"  {executionEvent.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("Results");

        foreach (TaskResult result in results)
        {
            builder.AppendLine($"- #{result.TaskId} {result.TaskTitle}");
            builder.AppendLine($"  Status: {result.Status}");
            builder.AppendLine($"  Attempts: {result.Attempts}");
            builder.AppendLine($"  Mode: {result.ExecutionMode}");
            builder.AppendLine($"  Model: {result.Model ?? "n/a"}");
            builder.AppendLine($"  Tokens: {result.TokensUsed?.ToString("N0") ?? "n/a"}");
            builder.AppendLine($"  Duration: {result.Duration.TotalMilliseconds:N0} ms");
            builder.AppendLine($"  Summary: {result.Summary}");
        }

        return builder.ToString();
    }

    private static string SanitizeSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();

        string sanitized = new string(value
            .Trim()
            .Select(character => invalidChars.Contains(character) ? '-' : character)
            .ToArray())
            .Replace(' ', '-');

        sanitized = sanitized
            .Trim('.', ' ', '-')
            .Replace("...", "-")
            .Replace("..", "-");

        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        if (sanitized.Length > MaxProjectSlugLength)
        {
            sanitized = sanitized[..MaxProjectSlugLength].TrimEnd('.', ' ', '-');
        }

        return sanitized;
    }
}
