using AgentOrchestrator.Services;

namespace AgentOrchestrator.Agents;

public sealed class SubAgentFactory
{
    private readonly CodexCliRunner? _codexCliRunner;
    private readonly int _maxRetries;

    public SubAgentFactory(int maxRetries = 2, CodexCliRunner? codexCliRunner = null)
    {
        _maxRetries = maxRetries;
        _codexCliRunner = codexCliRunner;
    }

    public IReadOnlyList<SubAgent> CreateAgents(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new SubAgent($"sub-agent-{index}", _maxRetries, _codexCliRunner))
            .ToArray();
    }

    public SubAgent CreateAgent(string name)
    {
        return new SubAgent(name, _maxRetries, _codexCliRunner);
    }
}
