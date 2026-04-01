namespace AgentOrchestrator.Services;

public interface IExecutionAdjustmentSource
{
    IReadOnlyList<string> DrainPendingAdjustments();
}
