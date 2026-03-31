namespace AgentOrchestrator.Models;

public sealed class AgentRuntimeState
{
    public bool IsRunning { get; private set; }
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastRunId { get; private set; }
    public string? LastTextReportPath { get; private set; }
    public string? LastJsonReportPath { get; private set; }
    public int CompletedRuns { get; private set; }

    public void MarkRunStarted()
    {
        IsRunning = true;
    }

    public void MarkRunCompleted(ExecutionReport report, RunArtifacts artifacts)
    {
        IsRunning = false;
        LastRunAt = report.GeneratedAt;
        LastRunId = report.RunId;
        LastTextReportPath = artifacts.TextReportPath;
        LastJsonReportPath = artifacts.JsonReportPath;
        CompletedRuns++;
    }

    public void MarkRunFailed()
    {
        IsRunning = false;
    }

    public void HydrateFromHistory(IReadOnlyList<RunHistoryEntry> history)
    {
        CompletedRuns = history.Count;

        if (history.Count == 0)
        {
            LastRunAt = null;
            LastRunId = null;
            LastTextReportPath = null;
            LastJsonReportPath = null;
            return;
        }

        RunHistoryEntry latestRun = history
            .OrderByDescending(entry => entry.GeneratedAt)
            .First();

        LastRunAt = latestRun.GeneratedAt;
        LastRunId = latestRun.RunId;
        LastTextReportPath = latestRun.TextReportPath;
        LastJsonReportPath = latestRun.JsonReportPath;
    }
}
