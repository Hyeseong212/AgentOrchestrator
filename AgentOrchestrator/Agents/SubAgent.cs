using AgentOrchestrator.Models;

namespace AgentOrchestrator.Agents;

public sealed class SubAgent
{
    public SubAgent(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public async Task<TaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        await Task.Delay(TimeSpan.FromMilliseconds(150 + (task.Complexity * 110)), cancellationToken);

        var summary =
            $"{Name} completed '{task.Title}' by turning the request into an actionable slice " +
            $"with a {task.Priority.ToLowerInvariant()}-priority implementation plan.";

        return new TaskResult(
            TaskId: task.Id,
            TaskTitle: task.Title,
            AssignedAgent: Name,
            Status: "Completed",
            Summary: summary,
            Duration: DateTimeOffset.UtcNow - startedAt);
    }
}
