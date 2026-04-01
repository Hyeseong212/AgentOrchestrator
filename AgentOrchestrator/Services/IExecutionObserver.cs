using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public interface IExecutionObserver
{
    Task OnRunStartedAsync(
        ProjectRequest request,
        IReadOnlyList<AgentTask> plannedTasks,
        int subAgentCount,
        CancellationToken cancellationToken);

    Task OnExecutionEventAsync(
        TaskExecutionEvent executionEvent,
        CancellationToken cancellationToken);

    Task OnPlanAdjustedAsync(
        ProjectRequest request,
        IReadOnlyList<AgentTask> plannedTasks,
        int subAgentCount,
        string reason,
        CancellationToken cancellationToken);

    Task OnRunCompletedAsync(
        OrchestratorRunResult runResult,
        CancellationToken cancellationToken);
}
