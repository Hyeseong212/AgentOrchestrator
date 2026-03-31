namespace AgentOrchestrator.Models;

public sealed record RunHistoryEntry(
    string RunId,
    string ProjectName,
    DateTimeOffset GeneratedAt,
    string TextReportPath,
    string JsonReportPath);
