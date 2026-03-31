namespace AgentOrchestrator.Services;

public sealed class AgentScaler
{
    private readonly int _maxAgents;
    private readonly int _minAgents;
    private readonly int _tasksPerAgent;

    public AgentScaler(int minAgents, int maxAgents, int tasksPerAgent)
    {
        _minAgents = minAgents;
        _maxAgents = maxAgents;
        _tasksPerAgent = Math.Max(1, tasksPerAgent);
    }

    public int DetermineAgentCount(int taskCount)
    {
        if (taskCount <= 0)
        {
            return _minAgents;
        }

        var scaled = (int)Math.Ceiling(taskCount / (double)_tasksPerAgent);
        return Math.Clamp(scaled, _minAgents, _maxAgents);
    }
}
