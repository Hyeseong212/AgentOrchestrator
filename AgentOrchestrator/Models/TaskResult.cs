namespace AgentOrchestrator.Models;

public sealed record TaskResult(
    int TaskId,
    string TaskTitle,
    string AssignedAgent,
    string Status,
    string Summary,
    int Attempts,
    string ExecutionMode,
    string? Model,
    int? TokensUsed,
    TimeSpan Duration);
