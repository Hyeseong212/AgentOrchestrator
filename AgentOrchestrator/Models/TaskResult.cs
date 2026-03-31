namespace AgentOrchestrator.Models;

public sealed record TaskResult(
    int TaskId,
    string TaskTitle,
    string AssignedAgent,
    string Status,
    string Summary,
    TimeSpan Duration);
