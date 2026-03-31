using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class TaskPlanner
{
    public IReadOnlyList<AgentTask> BuildPlan(ProjectRequest request)
    {
        var seededTasks = request.Deliverables
            .Select((deliverable, index) => new AgentTask(
                Id: index + 1,
                Title: deliverable,
                Description: $"'{request.Name}' 목표를 위해 {deliverable} 산출물을 준비한다.",
                Complexity: Math.Clamp((index % 4) + 2, 1, 5)))
            .ToList();

        seededTasks.Add(new AgentTask(
            Id: seededTasks.Count + 1,
            Title: "시스템 리스크 점검",
            Description: "작업 간 의존성과 병목 지점을 확인해 다음 반복을 안정화한다.",
            Complexity: 4));

        return seededTasks;
    }
}
