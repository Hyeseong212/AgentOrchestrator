using System.Text;

namespace AgentOrchestrator.Models;

public sealed class ExecutionReport
{
    public required string ProjectName { get; init; }
    public required string Goal { get; init; }
    public required int SubAgentCount { get; init; }
    public required IReadOnlyList<AgentTask> PlannedTasks { get; init; }
    public required IReadOnlyList<TaskResult> Results { get; init; }
    public required IReadOnlyList<string> NextSteps { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    public string ToConsoleText()
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Project: {ProjectName}");
        builder.AppendLine($"Goal: {Goal}");
        builder.AppendLine($"Generated At: {GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Scaled Sub Agents: {SubAgentCount}");
        builder.AppendLine();
        builder.AppendLine("Planned Tasks");

        foreach (AgentTask task in PlannedTasks)
        {
            builder.AppendLine($"- [{task.Priority}] #{task.Id} {task.Title}");
            builder.AppendLine($"  {task.Description}");
        }

        builder.AppendLine();
        builder.AppendLine("Execution Results");

        foreach (TaskResult result in Results.OrderBy(result => result.TaskId))
        {
            builder.AppendLine($"- #{result.TaskId} {result.TaskTitle}");
            builder.AppendLine($"  Agent: {result.AssignedAgent}");
            builder.AppendLine($"  Status: {result.Status}");
            builder.AppendLine($"  Duration: {result.Duration.TotalMilliseconds:N0} ms");
            builder.AppendLine($"  Summary: {result.Summary}");
        }

        builder.AppendLine();
        builder.AppendLine("Recommended Next Steps");

        foreach (string nextStep in NextSteps)
        {
            builder.AppendLine($"- {nextStep}");
        }

        return builder.ToString();
    }
}
