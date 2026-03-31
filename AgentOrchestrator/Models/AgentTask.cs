namespace AgentOrchestrator.Models;

public sealed record AgentTask(
    int Id,
    string Title,
    string Description,
    int Complexity)
{
    public string Priority => Complexity >= 4 ? "High" : Complexity == 3 ? "Medium" : "Low";
}
