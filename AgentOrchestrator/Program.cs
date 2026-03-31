using AgentOrchestrator.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var runtime = new AgentOrchestratorRuntime();
var requestLoader = new ProjectRequestLoader();
var historyLoader = new RunHistoryLoader();
var host = new InteractiveAgentHost(runtime, requestLoader, historyLoader, runtime.RequestPath, runtime.ProjectRoot);

if (args.Any(argument => string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase)))
{
    await host.RunOnceAsync();
    return;
}

await host.RunInteractiveAsync();
