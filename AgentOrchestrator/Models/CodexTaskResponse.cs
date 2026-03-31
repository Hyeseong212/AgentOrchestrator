namespace AgentOrchestrator.Models;

public sealed record CodexTaskResponse(
    string Message,
    string? Model,
    int? TokensUsed,
    string? RawDiagnostic);
