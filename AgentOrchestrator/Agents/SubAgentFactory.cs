namespace AgentOrchestrator.Agents;

public sealed class SubAgentFactory
{
    public IReadOnlyList<SubAgent> CreateAgents(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new SubAgent($"sub-agent-{index}"))
            .ToArray();
    }
}
