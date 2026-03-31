using AgentOrchestrator.Agents;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class InteractiveAgentHost
{
    private readonly RunHistoryLoader _historyLoader;
    private readonly string _projectRoot;
    private readonly string _requestPath;
    private readonly ProjectRequestLoader _requestLoader;
    private readonly AgentOrchestratorRuntime _runtime;
    private readonly AgentRuntimeState _state = new();
    private readonly List<RunHistoryEntry> _history = [];

    public InteractiveAgentHost(
        AgentOrchestratorRuntime runtime,
        ProjectRequestLoader requestLoader,
        RunHistoryLoader historyLoader,
        string requestPath,
        string projectRoot)
    {
        _runtime = runtime;
        _requestLoader = requestLoader;
        _historyLoader = historyLoader;
        _requestPath = requestPath;
        _projectRoot = projectRoot;

        _history.AddRange(_historyLoader.Load(projectRoot));
        _state.HydrateFromHistory(_history);
    }

    public async Task RunInteractiveAsync(CancellationToken cancellationToken = default)
    {
        PrintBanner();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("agent> ");
            string? input = Console.ReadLine();

            if (input is null)
            {
                break;
            }

            string trimmedInput = input.Trim();
            string[] commandParts = trimmedInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string command = commandParts.Length == 0 ? string.Empty : commandParts[0].ToLowerInvariant();
            string argument = commandParts.Length > 1 ? commandParts[1].Trim() : string.Empty;

            try
            {
                switch (command)
                {
                    case "":
                        break;
                    case "help":
                        PrintHelp();
                        break;
                    case "run":
                        if (string.IsNullOrWhiteSpace(argument))
                        {
                            await RunOnceAsync(cancellationToken);
                        }
                        else
                        {
                            await RunAdHocAsync(argument, cancellationToken);
                        }
                        break;
                    case "task":
                        if (string.IsNullOrWhiteSpace(argument))
                        {
                            Console.WriteLine("Usage: task <goal text>");
                            Console.WriteLine();
                        }
                        else
                        {
                            await RunAdHocAsync(argument, cancellationToken);
                        }
                        break;
                    case "status":
                        PrintStatus();
                        break;
                    case "request":
                        await PrintRequestAsync(cancellationToken);
                        break;
                    case "history":
                        PrintHistory();
                        break;
                    case "clear":
                        Console.Clear();
                        PrintBanner();
                        break;
                    case "exit":
                    case "quit":
                        Console.WriteLine("Main agent stopped.");
                        return;
                    default:
                        Console.WriteLine("Unknown command. Type 'help' to see the available commands.");
                        break;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Command failed: {exception.Message}");
                Console.WriteLine();
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ProjectRequestLoadResult loadResult = await _requestLoader.LoadAsync(_requestPath, cancellationToken);
        await ExecuteRequestAsync(
            loadResult.Request,
            loadResult.RequestPath,
            loadResult.TemplateCreated
                ? "A starter project-request.json file was created for you."
                : "Loaded project request from the existing JSON file.",
            cancellationToken);
    }

    private async Task RunAdHocAsync(string goal, CancellationToken cancellationToken = default)
    {
        ProjectRequest request = _requestLoader.CreateAdHocRequest(goal);
        await ExecuteRequestAsync(
            request,
            "Interactive CLI input",
            "Created an ad-hoc project request from the interactive command.",
            cancellationToken);
    }

    private async Task ExecuteRequestAsync(
        ProjectRequest request,
        string requestSource,
        string requestSourceMessage,
        CancellationToken cancellationToken)
    {
        _state.MarkRunStarted();

        try
        {
            OrchestratorRunResult runResult = requestSource == _requestPath
                ? await _runtime.RunFromFileAsync(cancellationToken)
                : await _runtime.RunAdHocAsync(request.Goal, cancellationToken);

            ExecutionReport report = runResult.Report;
            RunArtifacts artifacts = runResult.Artifacts;

            _state.MarkRunCompleted(report, artifacts);
            _history.Add(new RunHistoryEntry(
                report.RunId,
                report.ProjectName,
                report.GeneratedAt,
                artifacts.TextReportPath,
                artifacts.JsonReportPath));
            _state.HydrateFromHistory(_history);

            Console.WriteLine();
            Console.WriteLine(report.ToConsoleText());
            Console.WriteLine($"Request Source: {runResult.RequestSource}");
            Console.WriteLine(runResult.RequestSourceMessage);
            Console.WriteLine($"Saved Text Report: {artifacts.TextReportPath}");
            Console.WriteLine($"Saved Json Report: {artifacts.JsonReportPath}");
            Console.WriteLine();
        }
        catch (Exception exception)
        {
            _state.MarkRunFailed();
            Console.WriteLine($"Run failed: {exception.Message}");
            Console.WriteLine();
        }
    }

    private async Task PrintRequestAsync(CancellationToken cancellationToken)
    {
        ProjectRequestLoadResult loadResult = await _requestLoader.LoadAsync(_requestPath, cancellationToken);
        ProjectRequest request = loadResult.Request;

        Console.WriteLine($"Request File: {loadResult.RequestPath}");
        Console.WriteLine($"Project: {request.Name}");
        Console.WriteLine($"Goal: {request.Goal}");
        Console.WriteLine("Deliverables:");

        foreach (string deliverable in request.Deliverables)
        {
            Console.WriteLine($"- {deliverable}");
        }

        Console.WriteLine();
    }

    private void PrintStatus()
    {
        Console.WriteLine($"Started At: {_state.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Running Now: {_state.IsRunning}");
        Console.WriteLine($"Completed Runs: {_state.CompletedRuns}");
        Console.WriteLine($"Last Run Id: {_state.LastRunId ?? "none"}");
        Console.WriteLine($"Last Run At: {_state.LastRunAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "none"}");
        Console.WriteLine($"Last Text Report: {_state.LastTextReportPath ?? "none"}");
        Console.WriteLine($"Last Json Report: {_state.LastJsonReportPath ?? "none"}");
        Console.WriteLine();
    }

    private void PrintHistory()
    {
        if (_history.Count == 0)
        {
            Console.WriteLine("No runs have been completed yet.");
            Console.WriteLine();
            return;
        }

        foreach (RunHistoryEntry entry in _history.OrderByDescending(item => item.GeneratedAt))
        {
            Console.WriteLine($"{entry.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz} | {entry.RunId} | {entry.ProjectName}");
            Console.WriteLine($"  Text: {entry.TextReportPath}");
            Console.WriteLine($"  Json: {entry.JsonReportPath}");
        }

        Console.WriteLine();
    }

    private static void PrintBanner()
    {
        Console.WriteLine("AgentOrchestrator interactive host");
        Console.WriteLine("Type 'help' to see the available commands.");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("- run               : execute the current project request file");
        Console.WriteLine("- run <goal text>   : execute a one-off request from CLI input");
        Console.WriteLine("- task <goal text>  : alias for one-off CLI execution");
        Console.WriteLine("- status            : show runtime status and last run output");
        Console.WriteLine("- request           : print the current project request");
        Console.WriteLine("- history           : show completed run history");
        Console.WriteLine("- clear             : clear the console and reprint the banner");
        Console.WriteLine("- exit              : stop the main agent host");
        Console.WriteLine();
    }
}
