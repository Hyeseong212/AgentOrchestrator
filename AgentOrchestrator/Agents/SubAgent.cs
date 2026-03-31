using AgentOrchestrator.Models;
using AgentOrchestrator.Services;

namespace AgentOrchestrator.Agents;

public sealed class SubAgent
{
    private readonly int _maxRetries;
    private readonly CodexCliRunner? _codexCliRunner;

    public SubAgent(string name, int maxRetries, CodexCliRunner? codexCliRunner = null)
    {
        Name = name;
        _maxRetries = maxRetries;
        _codexCliRunner = codexCliRunner;
    }

    public SubAgent(string name)
    {
        Name = name;
        _maxRetries = 2;
    }

    public string Name { get; }

    public async Task<TaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(task, 1, cancellationToken);
    }

    public async Task<TaskResult> ExecuteAsync(AgentTask task, int attempt, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        if (_codexCliRunner is not null)
        {
            try
            {
                CodexTaskResponse response = await _codexCliRunner.ExecuteTaskAsync(Name, task, attempt, cancellationToken);

                return new TaskResult(
                    TaskId: task.Id,
                    TaskTitle: task.Title,
                    AssignedAgent: Name,
                    Status: "Completed",
                    Summary: response.Message,
                    Attempts: attempt,
                    ExecutionMode: "CodexCli",
                    Model: response.Model,
                    TokensUsed: response.TokensUsed,
                    Duration: DateTimeOffset.UtcNow - startedAt);
            }
            catch
            {
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(150 + (task.Complexity * 110)), cancellationToken);

        string summary =
            $"{Name} completed '{task.Title}' by turning the request into an actionable slice " +
            $"with a {task.Priority.ToLowerInvariant()}-priority implementation plan on attempt {attempt}.";

        return new TaskResult(
            TaskId: task.Id,
            TaskTitle: task.Title,
            AssignedAgent: Name,
            Status: "Completed",
            Summary: summary,
            Attempts: attempt,
            ExecutionMode: "Simulated",
            Model: null,
            TokensUsed: null,
            Duration: DateTimeOffset.UtcNow - startedAt);
    }

    public bool ShouldRetry(AgentTask task, int attempt)
    {
        bool simulatedTransientFailure = task.Complexity >= 4 && attempt == 1 && task.Id % 2 == 1;
        return simulatedTransientFailure && attempt <= _maxRetries;
    }
}
