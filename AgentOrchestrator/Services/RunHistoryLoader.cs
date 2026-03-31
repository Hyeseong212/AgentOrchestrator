using System.Text.Json;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class RunHistoryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<RunHistoryEntry> Load(string projectRoot)
    {
        string runsDirectory = Path.Combine(projectRoot, "runs");

        if (!Directory.Exists(runsDirectory))
        {
            return [];
        }

        var history = new List<RunHistoryEntry>();

        foreach (string reportPath in Directory.EnumerateFiles(runsDirectory, "report.json", SearchOption.AllDirectories))
        {
            RunHistoryEntry? entry = TryLoadEntry(reportPath);

            if (entry is not null)
            {
                history.Add(entry);
            }
        }

        return history
            .OrderByDescending(entry => entry.GeneratedAt)
            .ToArray();
    }

    private static RunHistoryEntry? TryLoadEntry(string reportPath)
    {
        try
        {
            string json = File.ReadAllText(reportPath);
            ExecutionReport? report = JsonSerializer.Deserialize<ExecutionReport>(json, JsonOptions);

            if (report is null)
            {
                return null;
            }

            string? runDirectory = Path.GetDirectoryName(reportPath);

            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                return null;
            }

            return new RunHistoryEntry(
                report.RunId,
                report.ProjectName,
                report.GeneratedAt,
                Path.Combine(runDirectory, "report.txt"),
                reportPath);
        }
        catch
        {
            return null;
        }
    }
}
