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

    public async Task<ExecutionReport> ExecuteAsync(
        ProjectRequest request,
        IExecutionObserver? observer,
        CancellationToken cancellationToken)
    {
        DateTimeOffset generatedAt = DateTimeOffset.Now;
        var history = new ExecutionHistory();
        IReadOnlyList<AgentTask> tasks = _planner.BuildPlan(request);
        int subAgentCount = _scaler.DetermineAgentCount(tasks.Count);
        IReadOnlyList<SubAgent> subAgents = _subAgentFactory.CreateAgents(subAgentCount);
        var queue = new TaskQueue(tasks);
        var results = new List<TaskResult>();
        var sync = new Lock();

        if (observer is not null)
        {
            await observer.OnRunStartedAsync(request, tasks, subAgentCount, cancellationToken);
        }

        Task[] workers = subAgents
            .Select(agent => RunWorkerAsync(agent, queue, results, history, sync, observer, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers);

        return new ExecutionReport
        {
            RunId = $"{generatedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24],
            ProjectName = request.Name,
            Goal = request.Goal,
            SubAgentCount = subAgentCount,
            PlannedTasks = tasks,
            Results = results.OrderBy(item => item.TaskId).ToArray(),
            ExecutionTimeline = history.Snapshot(),
            NextSteps =
            [
                "LLM API를 연결해 SubAgent가 실제 추론 결과와 근거를 생성하도록 확장한다.",
                "TaskQueue를 파일 또는 DB로 영속화해 중단 후에도 복구 가능하게 만든다.",
                "MainAgent에 작업 의존성 그래프와 우선순위 재계산 로직을 추가한다."
            ],
            GeneratedAt = generatedAt
        };
    }

    private async Task RunWorkerAsync(
        SubAgent agent,
        TaskQueue queue,
        List<TaskResult> results,
        ExecutionHistory history,
        Lock sync,
        IExecutionObserver? observer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out TaskQueueItem? queueItem) || queueItem is null)
            {
                break;
            }

            queueItem.IncrementAttempt();
            int attempt = queueItem.AttemptCount;
            AgentTask task = queueItem.Task;

            TaskExecutionEvent startedEvent = history.Record(
                task.Id,
                agent.Name,
                "Started",
                attempt,
                $"{task.Title} execution started.");
            if (observer is not null)
            {
                await observer.OnExecutionEventAsync(startedEvent, cancellationToken);
            }

            if (agent.ShouldRetry(task, attempt))
            {
                TaskExecutionEvent retryEvent = history.Record(
                    task.Id,
                    agent.Name,
                    "RetryScheduled",
                    attempt,
                    "A transient issue was detected. The task was placed back into the queue.");
                if (observer is not null)
                {
                    await observer.OnExecutionEventAsync(retryEvent, cancellationToken);
                }

                queue.Requeue(queueItem);
                continue;
            }

            TaskResult result = await agent.ExecuteAsync(task, attempt, cancellationToken);

            TaskExecutionEvent completedEvent = history.Record(
                task.Id,
                agent.Name,
                result.Status,
                attempt,
                result.Summary);
            if (observer is not null)
            {
                await observer.OnExecutionEventAsync(completedEvent, cancellationToken);
            }

            lock (sync)
            {
                results.Add(result);
            }
        }
    }
}
