namespace AgentOrchestrator.Models;

public sealed record OrchestratorRunResult(
    ExecutionReport Report,
    RunArtifacts Artifacts,
    string RequestSource,
    string RequestSourceMessage);
