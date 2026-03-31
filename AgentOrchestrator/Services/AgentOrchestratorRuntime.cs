using AgentOrchestrator.Agents;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class AgentOrchestratorRuntime
{
    private readonly RunArtifactsWriter _artifactsWriter;
    private readonly string _codexExecutablePath;
    private readonly string _projectRoot;
    private readonly ProjectRequestLoader _requestLoader;
    private readonly string _requestPath;
    private readonly RunHistoryLoader _runHistoryLoader;
    private readonly string _workspaceRoot;

    public AgentOrchestratorRuntime()
    {
        var workspaceLocator = new WorkspaceLocator();
        _projectRoot = workspaceLocator.FindProjectRoot();
        _workspaceRoot = workspaceLocator.FindWorkspaceRoot();
        _requestPath = Path.Combine(_projectRoot, "project-request.json");

        _requestLoader = new ProjectRequestLoader();
        var codexCliLocator = new CodexCliLocator();
        _codexExecutablePath = codexCliLocator.Locate();
        _artifactsWriter = new RunArtifactsWriter();
        _runHistoryLoader = new RunHistoryLoader();
    }

    public string ProjectRoot => _projectRoot;
    public string RequestPath => _requestPath;

    public async Task<OrchestratorRunResult> RunFromFileAsync(CancellationToken cancellationToken = default)
    {
        ProjectRequestLoadResult loadResult = await _requestLoader.LoadAsync(_requestPath, cancellationToken);

        return await ExecuteRequestAsync(
            loadResult.Request,
            loadResult.RequestPath,
            loadResult.TemplateCreated
                ? "A starter project-request.json file was created for you."
                : "Loaded project request from the existing JSON file.",
            allowFullAccess: false,
            observer: null,
            cancellationToken);
    }

    public Task<OrchestratorRunResult> RunAdHocAsync(string goal, CancellationToken cancellationToken = default)
    {
        ProjectRequest request = _requestLoader.CreateAdHocRequest(goal);

        return ExecuteRequestAsync(
            request,
            "Interactive CLI input",
            "Created an ad-hoc project request from interactive input.",
            allowFullAccess: false,
            observer: null,
            cancellationToken);
    }

    public Task<OrchestratorRunResult> RunAdHocAsync(
        string goal,
        IExecutionObserver observer,
        bool allowFullAccess = false,
        CancellationToken cancellationToken = default)
    {
        ProjectRequest request = _requestLoader.CreateAdHocRequest(goal);

        return ExecuteRequestAsync(
            request,
            "Interactive CLI input",
            "Created an ad-hoc project request from interactive input.",
            allowFullAccess,
            observer,
            cancellationToken);
    }

    public Task<ProjectRequestLoadResult> LoadRequestAsync(CancellationToken cancellationToken = default)
    {
        return _requestLoader.LoadAsync(_requestPath, cancellationToken);
    }

    public IReadOnlyList<RunHistoryEntry> LoadHistory()
    {
        return _runHistoryLoader.Load(_projectRoot);
    }

    public Task<CodexTaskResponse> AskAsync(
        string question,
        bool allowFullAccess = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidOperationException("A question is required.");
        }

        string prompt = BuildQuestionPrompt(question, allowFullAccess);
        return CreateCodexCliRunner(allowFullAccess).ExecutePromptAsync(prompt, cancellationToken);
    }

    private async Task<OrchestratorRunResult> ExecuteRequestAsync(
        ProjectRequest request,
        string requestSource,
        string requestSourceMessage,
        bool allowFullAccess,
        IExecutionObserver? observer,
        CancellationToken cancellationToken)
    {
        MainAgent mainAgent = CreateMainAgent(allowFullAccess);
        ExecutionReport report = await mainAgent.RunProjectAsync(request, observer, cancellationToken);
        RunArtifacts artifacts = await _artifactsWriter.WriteAsync(_projectRoot, report, cancellationToken);

        var runResult = new OrchestratorRunResult(report, artifacts, requestSource, requestSourceMessage);

        if (observer is not null)
        {
            await observer.OnRunCompletedAsync(runResult, cancellationToken);
        }

        return runResult;
    }

    private MainAgent CreateMainAgent(bool allowFullAccess)
    {
        var planner = new TaskPlanner();
        var scaler = new AgentScaler(minAgents: 1, maxAgents: 6, tasksPerAgent: 2);
        var runner = CreateCodexCliRunner(allowFullAccess);
        var subAgentFactory = new SubAgentFactory(maxRetries: 2, codexCliRunner: runner);
        var manager = new AgentManager(planner, scaler, subAgentFactory);

        return new MainAgent(manager);
    }

    private CodexCliRunner CreateCodexCliRunner(bool allowFullAccess)
    {
        return new CodexCliRunner(_codexExecutablePath, _workspaceRoot, allowFullAccess);
    }

    private static string BuildQuestionPrompt(string question, bool allowFullAccess)
    {
        string accessGuidance = allowFullAccess
            ? "You may inspect local files and folders on this machine, including paths outside the current workspace, when that helps answer the question. Do not claim local access is blocked unless a command actually fails."
            : "You are currently operating within the active workspace. If broader local file or folder access would help, explain that user approval is needed before searching outside the workspace.";

        return
            "You are the Codex Agent Orchestration Discord bot. " +
            "Answer the user's question concisely in Korean. " +
            "If they ask what you can do, explain the available bot commands and the difference between task execution and direct Q&A. " +
            "Keep the answer practical and short. " +
            accessGuidance +
            "\n\n" +
            $"User question: {question.Trim()}";
    }
}
