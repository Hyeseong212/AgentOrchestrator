using AgentOrchestrator.Agents;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class AgentManager
{
    private readonly TaskPlanner _planner;
    private readonly AgentScaler _scaler;
    private readonly SubAgentFactory _subAgentFactory;

    public AgentManager(TaskPlanner planner, AgentScaler scaler, SubAgentFactory subAgentFactory)
    {
        _planner = planner;
        _scaler = scaler;
        _subAgentFactory = subAgentFactory;
    }

    public async Task<ExecutionReport> ExecuteAsync(ProjectRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentTask> tasks = _planner.BuildPlan(request);
        int subAgentCount = _scaler.DetermineAgentCount(tasks.Count);
        IReadOnlyList<SubAgent> subAgents = _subAgentFactory.CreateAgents(subAgentCount);

        var executions = tasks
            .Select((task, index) => subAgents[index % subAgents.Count].ExecuteAsync(task, cancellationToken))
            .ToArray();

        TaskResult[] results = await Task.WhenAll(executions);

        return new ExecutionReport
        {
            ProjectName = request.Name,
            Goal = request.Goal,
            SubAgentCount = subAgentCount,
            PlannedTasks = tasks,
            Results = results,
            NextSteps =
            [
                "LLM API를 연결해 SubAgent가 실제 추론 결과를 생성하도록 확장한다.",
                "TaskQueue를 영속화해 실패 작업 재시도와 실행 이력을 남긴다.",
                "MainAgent에 우선순위 재조정과 의존성 기반 스케줄링을 추가한다."
            ],
            GeneratedAt = DateTimeOffset.Now
        };
    }
}
