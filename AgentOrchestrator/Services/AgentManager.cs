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
        IExecutionAdjustmentSource? adjustmentSource,
        CancellationToken cancellationToken)
    {
        DateTimeOffset generatedAt = DateTimeOffset.Now;
        var history = new ExecutionHistory();
        var results = new List<TaskResult>();
        var sync = new Lock();
        var requestLoader = new ProjectRequestLoader();
        var allPlannedTasks = new List<AgentTask>();
        ProjectRequest currentRequest = request;
        int nextTaskId = 1;
        int maxSubAgentCount = 0;
        bool initialPlan = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<AgentTask> tasks = RenumberTasks(_planner.BuildPlan(currentRequest), nextTaskId);
            if (tasks.Count > 0)
            {
                nextTaskId = tasks.Max(task => task.Id) + 1;
                allPlannedTasks.AddRange(tasks);
            }

            SubAgent mainAgent = _subAgentFactory.CreateAgent("main-codex");
            int subTaskCount = tasks.Count(task => string.Equals(task.ExecutionLane, "sub", StringComparison.OrdinalIgnoreCase));
            int subAgentCount = subTaskCount == 0 ? 0 : _scaler.DetermineAgentCount(subTaskCount);
            IReadOnlyList<SubAgent> subAgents = subAgentCount == 0
                ? []
                : _subAgentFactory.CreateAgents(subAgentCount);
            maxSubAgentCount = Math.Max(maxSubAgentCount, subAgentCount);

            if (observer is not null)
            {
                if (initialPlan)
                {
                    await observer.OnRunStartedAsync(currentRequest, tasks, subAgentCount, cancellationToken);
                }
                else
                {
                    await observer.OnPlanAdjustedAsync(
                        currentRequest,
                        tasks,
                        subAgentCount,
                        "추가 요청을 반영해 난도와 소요 시간을 다시 계산하고 계획을 재구성했습니다.",
                        cancellationToken);
                }
            }

            bool replanned = false;

            foreach (IGrouping<int, AgentTask> phaseGroup in tasks
                         .OrderBy(task => task.Phase)
                         .GroupBy(task => task.Phase))
            {
                AgentTask[] mainPhaseTasks = phaseGroup
                    .Where(task => string.Equals(task.ExecutionLane, "main", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(task => task.Id)
                    .ToArray();
                AgentTask[] subPhaseTasks = phaseGroup
                    .Where(task => string.Equals(task.ExecutionLane, "sub", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(task => task.Id)
                    .ToArray();

                foreach (AgentTask task in mainPhaseTasks)
                {
                    TaskResult result = await ExecuteTaskWithRetryAsync(mainAgent, task, history, observer, cancellationToken);
                    lock (sync)
                    {
                        results.Add(result);
                    }
                }

                if (subPhaseTasks.Length > 0)
                {
                    var queue = new TaskQueue(subPhaseTasks);
                    Task[] workers = subAgents
                        .Select(agent => RunWorkerAsync(agent, queue, results, history, sync, observer, cancellationToken))
                        .ToArray();

                    await Task.WhenAll(workers);
                }

                IReadOnlyList<string> adjustments = adjustmentSource?.DrainPendingAdjustments() ?? [];
                if (adjustments.Count > 0)
                {
                    string mergedGoal = MergeGoals(currentRequest.Goal, adjustments);
                    currentRequest = requestLoader.CreateAdHocRequest(mergedGoal, currentRequest.Name);
                    replanned = true;

                    TaskExecutionEvent replanEvent = history.Record(
                        0,
                        "main-codex",
                        "Replanned",
                        1,
                        $"추가 요청 {adjustments.Count}건을 반영해 다음 phase부터 계획을 다시 계산합니다.");
                    if (observer is not null)
                    {
                        await observer.OnExecutionEventAsync(replanEvent, cancellationToken);
                    }

                    break;
                }
            }

            if (!replanned)
            {
                break;
            }

            initialPlan = false;
        }

        return new ExecutionReport
        {
            RunId = $"{generatedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24],
            ProjectName = request.Name,
            Goal = currentRequest.Goal,
            SubAgentCount = maxSubAgentCount,
            PlannedTasks = allPlannedTasks,
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

    private static IReadOnlyList<AgentTask> RenumberTasks(IReadOnlyList<AgentTask> tasks, int nextTaskId)
    {
        return tasks
            .Select((task, index) => task with { Id = nextTaskId + index })
            .ToArray();
    }

    private static string MergeGoals(string currentGoal, IReadOnlyList<string> adjustments)
    {
        IEnumerable<string> normalizedAdjustments = adjustments
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item));

        return string.Join(
            "\n",
            new[] { currentGoal.Trim() }
                .Concat(normalizedAdjustments.Select((item, index) => $"추가 요청 {index + 1}: {item}")));
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

            TaskResult result = await ExecuteTaskWithRetryAsync(agent, queueItem.Task, history, observer, cancellationToken);

            lock (sync)
            {
                results.Add(result);
            }
        }
    }

    private static async Task<TaskResult> ExecuteTaskWithRetryAsync(
        SubAgent agent,
        AgentTask task,
        ExecutionHistory history,
        IExecutionObserver? observer,
        CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (true)
        {
            attempt++;

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

            return result;
        }
    }
}
