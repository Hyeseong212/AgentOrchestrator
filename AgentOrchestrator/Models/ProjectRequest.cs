namespace AgentOrchestrator.Models;

public sealed record ProjectRequest(
    string Name,
    string Goal,
    IReadOnlyList<string> Deliverables);
