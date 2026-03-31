using System.Collections.Concurrent;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class ExecutionHistory
{
    private readonly ConcurrentBag<TaskExecutionEvent> _events = new();

    public TaskExecutionEvent Record(int taskId, string agentName, string state, int attempt, string message)
    {
        var executionEvent = new TaskExecutionEvent(
            Timestamp: DateTimeOffset.Now,
            TaskId: taskId,
            AgentName: agentName,
            State: state,
            Attempt: attempt,
            Message: message);

        _events.Add(executionEvent);
        return executionEvent;
    }

    public IReadOnlyList<TaskExecutionEvent> Snapshot()
    {
        return _events
            .OrderBy(item => item.Timestamp)
            .ToArray();
    }
}
