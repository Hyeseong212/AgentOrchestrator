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
    public string WorkspaceRoot => _workspaceRoot;

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
            workspaceRootOverride: null,
            hostContext: null,
            adjustmentSource: null,
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
            workspaceRootOverride: null,
            hostContext: null,
            adjustmentSource: null,
            cancellationToken);
    }

    public Task<OrchestratorRunResult> RunAdHocAsync(
        string goal,
        IExecutionObserver observer,
        bool allowFullAccess = false,
        string? workspaceRootOverride = null,
        string? hostContext = null,
        IExecutionAdjustmentSource? adjustmentSource = null,
        CancellationToken cancellationToken = default)
    {
        ProjectRequest request = _requestLoader.CreateAdHocRequest(goal);

        return ExecuteRequestAsync(
            request,
            "Interactive CLI input",
            "Created an ad-hoc project request from interactive input.",
            allowFullAccess,
            observer,
            workspaceRootOverride,
            hostContext,
            adjustmentSource,
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
        string? workspaceRootOverride = null,
        string? hostContext = null,
        string? agentName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidOperationException("A question is required.");
        }

        string activeWorkspaceRoot = workspaceRootOverride ?? _workspaceRoot;
        string prompt = BuildQuestionPrompt(question, allowFullAccess, activeWorkspaceRoot, hostContext, agentName);
        return CreateCodexCliRunner(allowFullAccess, workspaceRootOverride, hostContext)
            .ExecutePromptAsync(prompt, cancellationToken);
    }

    private async Task<OrchestratorRunResult> ExecuteRequestAsync(
        ProjectRequest request,
        string requestSource,
        string requestSourceMessage,
        bool allowFullAccess,
        IExecutionObserver? observer,
        string? workspaceRootOverride,
        string? hostContext,
        IExecutionAdjustmentSource? adjustmentSource,
        CancellationToken cancellationToken)
    {
        MainAgent mainAgent = CreateMainAgent(allowFullAccess, workspaceRootOverride, hostContext);
        ExecutionReport report = await mainAgent.RunProjectAsync(request, observer, adjustmentSource, cancellationToken);
        RunArtifacts artifacts = await _artifactsWriter.WriteAsync(_projectRoot, report, cancellationToken);

        var runResult = new OrchestratorRunResult(report, artifacts, requestSource, requestSourceMessage);

        if (observer is not null)
        {
            await observer.OnRunCompletedAsync(runResult, cancellationToken);
        }

        return runResult;
    }

    private MainAgent CreateMainAgent(bool allowFullAccess, string? workspaceRootOverride = null, string? hostContext = null)
    {
        var planner = new TaskPlanner();
        var scaler = new AgentScaler(minAgents: 1, maxAgents: 6, tasksPerAgent: 2);
        var runner = CreateCodexCliRunner(allowFullAccess, workspaceRootOverride, hostContext);
        var subAgentFactory = new SubAgentFactory(maxRetries: 2, codexCliRunner: runner);
        var manager = new AgentManager(planner, scaler, subAgentFactory);

        return new MainAgent(manager);
    }

    private CodexCliRunner CreateCodexCliRunner(
        bool allowFullAccess,
        string? workspaceRootOverride = null,
        string? hostContext = null)
    {
        return new CodexCliRunner(
            _codexExecutablePath,
            workspaceRootOverride ?? _workspaceRoot,
            allowFullAccess,
            hostContext);
    }

    private static string BuildQuestionPrompt(
        string question,
        bool allowFullAccess,
        string activeWorkspaceRoot,
        string? hostContext,
        string? agentName)
    {
        string accessGuidance = allowFullAccess
            ? "The user has already approved full local access for this conversation. You may inspect local files and folders on this machine, including paths outside the current workspace, when that helps answer the question. Attempt the local inspection yourself before asking the user to paste directory listings. If a path cannot be accessed, explain the concrete reason such as path not found, command failure, or permission denied. Never claim environment policy blocked local access while full access is enabled."
            : "You are currently operating within the active workspace. If broader local file or folder access would help, explain that user approval is needed before searching outside the workspace.";

        string personaGuidance = string.IsNullOrWhiteSpace(agentName)
            ? "You are the Codex Agent Orchestration Discord bot. Answer the user's question concisely in Korean. If they ask what you can do, explain the available bot commands and the difference between task execution and direct Q&A. "
            : string.Equals(agentName, "main-codex", StringComparison.OrdinalIgnoreCase)
                ? "You are Main Codex, the lead coordinator for this Discord project workspace. Treat the user's latest message as something addressed directly to Main Codex and answer concisely in Korean. "
                : $"You are {agentName}, a focused sub-agent inside a larger orchestration system. Treat the user's latest message as something addressed directly to you in this Discord channel and answer concisely in Korean. ";

        return
            personaGuidance +
            "Keep the answer practical and short. " +
            "The machine is Windows-first. When referring to local inspection, prefer cmd-compatible commands such as dir, type, where, tree /f, and if exist, or explicitly use powershell -NoProfile -Command for PowerShell cmdlets. Never describe cmd.exe /c Get-ChildItem as a valid command because Get-ChildItem is not a cmd built-in. " +
            "If the host context includes downloaded Discord attachments, rely on the extracted PPTX/XLSX/PDF summaries and inspect local image paths directly for visual details when useful. " +
            $"The active working directory for this request is `{activeWorkspaceRoot}`. " +
            accessGuidance +
            (string.IsNullOrWhiteSpace(hostContext) ? string.Empty : $"\nHost context: {hostContext}") +
            "\n\n" +
            $"User message: {question.Trim()}";
    }
}
