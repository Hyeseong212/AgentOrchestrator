using System.Collections.Concurrent;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class TaskQueue
{
    private readonly ConcurrentQueue<TaskQueueItem> _queue = new();

    public TaskQueue(IEnumerable<AgentTask> tasks)
    {
        foreach (AgentTask task in tasks)
        {
            _queue.Enqueue(new TaskQueueItem(task));
        }
    }

    public bool TryDequeue(out TaskQueueItem? item)
    {
        return _queue.TryDequeue(out item);
    }

    public void Requeue(TaskQueueItem item)
    {
        _queue.Enqueue(item);
    }

    public bool HasPendingWork => !_queue.IsEmpty;
}
