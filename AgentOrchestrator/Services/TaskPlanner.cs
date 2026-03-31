using AgentOrchestrator.Models;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.Services;

public sealed class TaskPlanner
{
    private sealed record PlanningAnalysis(
        int OverallComplexity,
        int EstimatedMinutes,
        bool ShouldUseMainOnly,
        bool UseSubAgents,
        bool RequiresVerificationStage,
        IReadOnlyList<string> ImplementationSlices);

    public IReadOnlyList<AgentTask> BuildPlan(ProjectRequest request)
    {
        string goal = request.Goal.ReplaceLineEndings(" ").Trim();
        PlanningAnalysis analysis = AnalyzeRequest(request, goal);

        if (analysis.ShouldUseMainOnly)
        {
            return
            [
                new AgentTask(
                    Id: 1,
                    Title: "1단계. 요청 실행",
                    Description: goal,
                    Complexity: analysis.OverallComplexity,
                    EstimatedMinutes: analysis.EstimatedMinutes,
                    ExecutionLane: "main",
                    Phase: 1)
            ];
        }

        List<AgentTask> stagedTasks = BuildStagedPlan(request, goal, analysis);
        return stagedTasks;
    }

    private static List<AgentTask> BuildStagedPlan(
        ProjectRequest request,
        string goal,
        PlanningAnalysis analysis)
    {
        var tasks = new List<AgentTask>();
        int stageId = 1;
        int prepMinutes = Math.Clamp(Math.Max(2, analysis.EstimatedMinutes / 8), 2, 10);
        int closingMinutes = Math.Clamp(Math.Max(2, analysis.EstimatedMinutes / 10), 2, 10);

        tasks.Add(new AgentTask(
            Id: stageId,
            Title: $"{stageId}단계. 요구사항 분석 및 작업 범위 고정",
            Description: $"요청 목표를 분석하고 작업 루트, 산출물, 우선순위를 확정한다. 원문 요청: {goal}",
            Complexity: Math.Min(3, analysis.OverallComplexity),
            EstimatedMinutes: prepMinutes,
            ExecutionLane: "main",
            Phase: 1));

        stageId++;

        int remainingMinutes = Math.Max(analysis.EstimatedMinutes - prepMinutes - closingMinutes, analysis.ImplementationSlices.Count * 3);
        IReadOnlyList<string> implementationSlices = analysis.ImplementationSlices
            .Take(Math.Max(1, 10 - (analysis.RequiresVerificationStage ? 3 : 2)))
            .ToArray();
        int perSliceMinutes = Math.Max(3, remainingMinutes / Math.Max(1, implementationSlices.Count + (analysis.RequiresVerificationStage ? 1 : 0)));

        foreach (string slice in implementationSlices)
        {
            tasks.Add(new AgentTask(
                Id: stageId,
                Title: $"{stageId}단계. {BuildImplementationTitle(slice)}",
                Description: $"구현/수정 대상: {slice}",
                Complexity: Math.Clamp(analysis.OverallComplexity, 2, 4),
                EstimatedMinutes: perSliceMinutes,
                ExecutionLane: analysis.UseSubAgents ? "sub" : "main",
                Phase: analysis.UseSubAgents ? 2 : 1));
            stageId++;
        }

        if (analysis.RequiresVerificationStage && stageId <= 9)
        {
            tasks.Add(new AgentTask(
                Id: stageId,
                Title: $"{stageId}단계. 검증 및 리스크 확인",
                Description: "변경 결과를 확인하고 빌드/실행/파일 반영 여부와 남은 리스크를 점검한다.",
                Complexity: Math.Clamp(analysis.OverallComplexity, 2, 4),
                EstimatedMinutes: Math.Max(3, perSliceMinutes),
                ExecutionLane: analysis.UseSubAgents ? "sub" : "main",
                Phase: analysis.UseSubAgents ? 2 : 1));
            stageId++;
        }

        tasks.Add(new AgentTask(
            Id: stageId,
            Title: $"{stageId}단계. 결과 통합 및 보고",
            Description: $"완료 상태를 정리하고 '{request.Name}' 작업 결과를 최종 보고한다.",
            Complexity: Math.Min(3, analysis.OverallComplexity),
            EstimatedMinutes: closingMinutes,
            ExecutionLane: "main",
            Phase: analysis.UseSubAgents ? 3 : 1));

        return tasks;
    }

    private static PlanningAnalysis AnalyzeRequest(ProjectRequest request, string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return new PlanningAnalysis(
                OverallComplexity: 1,
                EstimatedMinutes: 3,
                ShouldUseMainOnly: true,
                UseSubAgents: false,
                RequiresVerificationStage: false,
                ImplementationSlices: [request.Name]);
        }

        IReadOnlyList<string> implementationSlices = BuildImplementationSlices(request, goal);

        string[] projectScaleKeywords =
        [
            "프로젝트",
            "시스템",
            "플랫폼",
            "아키텍처",
            "리팩터",
            "구축",
            "연동",
            "파이프라인",
            "workflow",
            "pipeline",
            "architecture",
            "system",
            "project",
            "refactor",
            "setup",
            "migration"
        ];

        string[] verificationKeywords =
        [
            "빌드",
            "실행",
            "테스트",
            "검증",
            "확인",
            "build",
            "run",
            "test",
            "verify"
        ];

        string[] directActionKeywords =
        [
            "작성",
            "작성해",
            "만들어",
            "생성",
            "추가",
            "수정",
            "고쳐",
            "바꿔",
            "rename",
            "write",
            "create",
            "edit",
            "update"
        ];

        bool looksProjectScale = projectScaleKeywords.Any(
            keyword => ContainsPlanningKeyword(goal, keyword));
        bool hasExplicitSplits =
            goal.Contains(',', StringComparison.Ordinal) ||
            goal.Contains(';', StringComparison.Ordinal) ||
            goal.Contains(" 그리고 ", StringComparison.Ordinal) ||
            goal.Contains(" 및 ", StringComparison.Ordinal) ||
            goal.Contains(" and ", StringComparison.OrdinalIgnoreCase);
        bool hasVerificationIntent = verificationKeywords.Any(
            keyword => goal.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        bool looksDirectAction = directActionKeywords.Any(
            keyword => goal.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        bool referencesConcretePathOrFile =
            goal.Contains(":\\", StringComparison.Ordinal) ||
            goal.Contains("\\", StringComparison.Ordinal) ||
            goal.Contains(".txt", StringComparison.OrdinalIgnoreCase) ||
            goal.Contains(".cs", StringComparison.OrdinalIgnoreCase) ||
            goal.Contains(".json", StringComparison.OrdinalIgnoreCase) ||
            goal.Contains(".cpp", StringComparison.OrdinalIgnoreCase) ||
            goal.Contains(".py", StringComparison.OrdinalIgnoreCase);
        bool referencesNamedFile = Regex.IsMatch(
            goal,
            @"\.[A-Za-z0-9]{1,8}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool explicitSingleFileTask = implementationSlices.Count == 1 &&
                                      referencesNamedFile &&
                                      (looksDirectAction || goal.Contains("내용", StringComparison.OrdinalIgnoreCase));

        bool simpleDirectTask = explicitSingleFileTask ||
                                (!looksProjectScale &&
                                 !hasExplicitSplits &&
                                 implementationSlices.Count == 1 &&
                                 (looksDirectAction || referencesConcretePathOrFile) &&
                                 !hasVerificationIntent &&
                                 (referencesConcretePathOrFile || goal.Length <= 140));

        int complexity = simpleDirectTask ? 2 : 1;
        if (!simpleDirectTask && goal.Length > 60)
        {
            complexity++;
        }
        if (implementationSlices.Count >= 2 || hasExplicitSplits)
        {
            complexity++;
        }
        if (hasVerificationIntent || (!simpleDirectTask && referencesConcretePathOrFile))
        {
            complexity++;
        }
        if (looksProjectScale)
        {
            complexity += 2;
        }

        complexity = Math.Clamp(complexity, 1, 5);

        int estimatedMinutes = simpleDirectTask
            ? 5
            : complexity switch
            {
                1 => 3,
                2 => 8,
                3 => 20,
                4 => 45,
                _ => 90
            };

        estimatedMinutes += simpleDirectTask ? 0 : Math.Max(0, implementationSlices.Count - 1) * 5;

        bool useSubAgents = !simpleDirectTask &&
                            (looksProjectScale || implementationSlices.Count >= 2 || complexity >= 4 || estimatedMinutes >= 30);

        bool requiresVerificationStage = !simpleDirectTask &&
                                         (hasVerificationIntent || complexity >= 3 || implementationSlices.Count >= 2);

        return new PlanningAnalysis(
            OverallComplexity: complexity,
            EstimatedMinutes: estimatedMinutes,
            ShouldUseMainOnly: simpleDirectTask,
            UseSubAgents: useSubAgents,
            RequiresVerificationStage: requiresVerificationStage,
            ImplementationSlices: implementationSlices);
    }

    private static IReadOnlyList<string> BuildImplementationSlices(ProjectRequest request, string goal)
    {
        IReadOnlyList<string> candidateSlices = request.Deliverables
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidateSlices.Count > 1)
        {
            return candidateSlices;
        }

        string normalized = goal.ReplaceLineEndings(" ").Trim();
        string[] separators = [",", ";", " 그리고 ", " 및 ", " and "];

        foreach (string separator in separators)
        {
            string[] parts = normalized
                .Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return parts.Take(6).ToArray();
            }
        }

        return [normalized];
    }

    private static string BuildImplementationTitle(string slice)
    {
        string trimmed = slice.Trim();
        if (trimmed.Length <= 28)
        {
            return trimmed;
        }

        return $"{trimmed[..28].TrimEnd()}...";
    }

    private static bool ContainsPlanningKeyword(string text, string keyword)
    {
        if (keyword.All(character => char.IsAsciiLetter(character)))
        {
            return Regex.IsMatch(
                text,
                $@"(^|[^A-Za-z]){Regex.Escape(keyword)}([^A-Za-z]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
