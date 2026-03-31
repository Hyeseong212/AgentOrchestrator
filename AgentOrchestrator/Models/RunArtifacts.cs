namespace AgentOrchestrator.Models;

public sealed record RunArtifacts(
    string RunDirectory,
    string TextReportPath,
    string JsonReportPath,
    string MainLogPath,
    IReadOnlyDictionary<string, string> SubLogPaths);
