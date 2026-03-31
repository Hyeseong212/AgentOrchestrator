namespace AgentOrchestrator.Models;

public sealed record TaskExecutionEvent(
    DateTimeOffset Timestamp,
    int TaskId,
    string AgentName,
    string State,
    int Attempt,
    string Message);
