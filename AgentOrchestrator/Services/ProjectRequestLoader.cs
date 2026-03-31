using System.Text.Json;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class ProjectRequestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<ProjectRequestLoadResult> LoadAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(requestPath))
        {
            ProjectRequest template = CreateTemplate();
            Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);

            await using FileStream createStream = File.Create(requestPath);
            await JsonSerializer.SerializeAsync(createStream, template, JsonOptions, cancellationToken);

            return new ProjectRequestLoadResult(template, requestPath, TemplateCreated: true);
        }

        await using FileStream readStream = File.OpenRead(requestPath);
        ProjectRequest? request = await JsonSerializer.DeserializeAsync<ProjectRequest>(readStream, JsonOptions, cancellationToken);

        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new InvalidOperationException("project-request.json is missing required project fields.");
        }

        if (request.Deliverables.Count == 0)
        {
            throw new InvalidOperationException("project-request.json must contain at least one deliverable.");
        }

        return new ProjectRequestLoadResult(request, requestPath, TemplateCreated: false);
    }

    public ProjectRequest CreateAdHocRequest(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new InvalidOperationException("A goal is required to create an ad-hoc request.");
        }

        string trimmedGoal = goal.Trim();
        string projectName = BuildProjectName(trimmedGoal);
        IReadOnlyList<string> deliverables = BuildDeliverables(trimmedGoal);

        return new ProjectRequest(projectName, trimmedGoal, deliverables);
    }

    private static ProjectRequest CreateTemplate()
    {
        return new ProjectRequest(
            Name: "Agentic Delivery Platform",
            Goal: "메인 에이전트가 작업을 분해하고 서브 에이전트가 동적으로 늘어나며 결과를 집계하는 MVP를 만든다.",
            Deliverables:
            [
                "코어 오케스트레이션 루프",
                "동적 서브 에이전트 스케일링",
                "작업 결과 집계와 최종 보고",
                "다음 확장 포인트 정의"
            ]);
    }

    private static string BuildProjectName(string goal)
    {
        const int maxLength = 36;
        string normalized = goal.ReplaceLineEndings(" ").Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength].TrimEnd()}...";
    }

    private static IReadOnlyList<string> BuildDeliverables(string goal)
    {
        string normalized = goal.ReplaceLineEndings(" ").Trim();
        string[] separators = [",", ";", " 그리고 ", " and "];

        foreach (string separator in separators)
        {
            string[] parts = normalized
                .Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return parts
                    .Take(4)
                    .Select(part => part.EndsWith('.') ? part[..^1] : part)
                    .ToArray();
            }
        }

        return
        [
            normalized,
            "실행 전략 정리",
            "결과 검증",
            "후속 리스크 점검"
        ];
    }
}
