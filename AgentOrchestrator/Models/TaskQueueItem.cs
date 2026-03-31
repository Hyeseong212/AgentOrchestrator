namespace AgentOrchestrator.Models;

public sealed class TaskQueueItem
{
    public TaskQueueItem(AgentTask task)
    {
        Task = task;
    }

    public AgentTask Task { get; }
    public int AttemptCount { get; private set; }

    public void IncrementAttempt()
    {
        AttemptCount++;
    }
}
