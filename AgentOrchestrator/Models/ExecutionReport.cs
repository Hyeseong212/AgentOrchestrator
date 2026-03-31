using System.Text;

namespace AgentOrchestrator.Models;

public sealed class ExecutionReport
{
    public required string RunId { get; init; }
    public required string ProjectName { get; init; }
    public required string Goal { get; init; }
    public required int SubAgentCount { get; init; }
    public required IReadOnlyList<AgentTask> PlannedTasks { get; init; }
    public required IReadOnlyList<TaskResult> Results { get; init; }
    public required IReadOnlyList<TaskExecutionEvent> ExecutionTimeline { get; init; }
    public required IReadOnlyList<string> NextSteps { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    public string ToConsoleText()
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Run Id: {RunId}");
        builder.AppendLine($"Project: {ProjectName}");
        builder.AppendLine($"Goal: {Goal}");
        builder.AppendLine($"Generated At: {GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Scaled Sub Agents: {SubAgentCount}");
        builder.AppendLine($"Estimated Difficulty: {GetOverallDifficulty()}");
        builder.AppendLine($"Estimated Duration: ~{PlannedTasks.Sum(task => task.EstimatedMinutes)} min");
        builder.AppendLine();
        builder.AppendLine("Planned Tasks");

        foreach (AgentTask task in PlannedTasks)
        {
            builder.AppendLine(
                $"- [{task.Priority}] #{task.Id} {task.Title} | " +
                $"Lane: {task.ExecutionLaneLabel} | Phase: {task.Phase} | ETA: ~{task.EstimatedMinutes} min");
            builder.AppendLine($"  {task.Description}");
        }

        builder.AppendLine();
        builder.AppendLine("Execution Results");

        foreach (TaskResult result in Results.OrderBy(result => result.TaskId))
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

        builder.AppendLine();
        builder.AppendLine("Execution Timeline");

        foreach (TaskExecutionEvent executionEvent in ExecutionTimeline.OrderBy(item => item.Timestamp))
        {
            builder.AppendLine(
                $"- {executionEvent.Timestamp:HH:mm:ss.fff} | Task #{executionEvent.TaskId} | " +
                $"{executionEvent.AgentName} | {executionEvent.State} | Attempt {executionEvent.Attempt}");
            builder.AppendLine($"  {executionEvent.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("Recommended Next Steps");

        foreach (string nextStep in NextSteps)
        {
            builder.AppendLine($"- {nextStep}");
        }

        return builder.ToString();
    }

    private string GetOverallDifficulty()
    {
        int maxComplexity = PlannedTasks.Count == 0 ? 1 : PlannedTasks.Max(task => task.Complexity);
        return maxComplexity >= 4 ? "High" : maxComplexity == 3 ? "Medium" : "Low";
    }
}
