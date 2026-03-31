using AgentOrchestrator.Models;
using AgentOrchestrator.Services;

namespace AgentOrchestrator.Agents;

public sealed class MainAgent
{
    private readonly AgentManager _manager;

    public MainAgent(AgentManager manager)
    {
        _manager = manager;
    }

    public Task<ExecutionReport> RunProjectAsync(
        ProjectRequest request,
        IExecutionObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        return _manager.ExecuteAsync(request, observer, cancellationToken);
    }
}
