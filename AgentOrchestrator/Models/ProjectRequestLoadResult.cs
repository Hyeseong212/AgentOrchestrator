namespace AgentOrchestrator.Models;

public sealed record ProjectRequestLoadResult(
    ProjectRequest Request,
    string RequestPath,
    bool TemplateCreated);
