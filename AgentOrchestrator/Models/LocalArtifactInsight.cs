namespace AgentOrchestrator.Models;

public sealed record LocalArtifactInsight(
    string FullPath,
    string FileName,
    string Kind,
    string Summary,
    bool RequiresDirectInspection = false);
