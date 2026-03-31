namespace AgentOrchestrator.Models;

public sealed record AgentTask(
    int Id,
    string Title,
    string Description,
    int Complexity,
    int EstimatedMinutes,
    string ExecutionLane,
    int Phase)
{
    public string Priority => Complexity >= 4 ? "High" : Complexity == 3 ? "Medium" : "Low";
    public string ExecutionLaneLabel => string.Equals(ExecutionLane, "main", StringComparison.OrdinalIgnoreCase)
        ? "Main Codex"
        : string.Equals(ExecutionLane, "sub", StringComparison.OrdinalIgnoreCase)
            ? "Sub Codex"
            : ExecutionLane;
}
