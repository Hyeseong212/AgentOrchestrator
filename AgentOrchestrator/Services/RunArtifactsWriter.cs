using System.Text.Json;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class RunArtifactsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<RunArtifacts> WriteAsync(
        string projectRoot,
        ExecutionReport report,
        CancellationToken cancellationToken = default)
    {
        string runDirectory = Path.Combine(
            projectRoot,
            "runs",
            $"{report.GeneratedAt:yyyyMMdd-HHmmss}-{SanitizeSegment(report.ProjectName)}");

        Directory.CreateDirectory(runDirectory);

        string textReportPath = Path.Combine(runDirectory, "report.txt");
        string jsonReportPath = Path.Combine(runDirectory, "report.json");

        await File.WriteAllTextAsync(textReportPath, report.ToConsoleText(), cancellationToken);

        await using FileStream jsonStream = File.Create(jsonReportPath);
        await JsonSerializer.SerializeAsync(jsonStream, report, JsonOptions, cancellationToken);

        return new RunArtifacts(runDirectory, textReportPath, jsonReportPath);
    }

    private static string SanitizeSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();

        return new string(value
            .Trim()
            .Select(character => invalidChars.Contains(character) ? '-' : character)
            .ToArray())
            .Replace(' ', '-');
    }
}
