using System.Text;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;
using Discord;
using Discord.WebSocket;

namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordProjectWorkspaceObserver : IExecutionObserver
{
    private readonly Dictionary<string, ITextChannel> _agentChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SocketGuild _guild;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private ITextChannel? _mainChannel;
    private string? _workspaceName;

    public DiscordProjectWorkspaceObserver(SocketGuild guild)
    {
        _guild = guild;
    }

    public async Task OnRunStartedAsync(
        ProjectRequest request,
        IReadOnlyList<AgentTask> plannedTasks,
        int subAgentCount,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);

        try
        {
            string categoryName = BuildUniqueCategoryName(request.Name);
            _workspaceName = categoryName;

            ICategoryChannel category = await _guild.CreateCategoryChannelAsync(categoryName);
            _mainChannel = await _guild.CreateTextChannelAsync(
                "main-codex",
                properties => properties.CategoryId = category.Id);

            for (int index = 1; index <= subAgentCount; index++)
            {
                ITextChannel subChannel = await _guild.CreateTextChannelAsync(
                    $"sub-codex-{index}",
                    properties => properties.CategoryId = category.Id);
                _agentChannels[$"sub-agent-{index}"] = subChannel;

                await subChannel.SendMessageAsync(
                    $"`sub-codex-{index}` 준비 완료\n연결된 에이전트 ID: `sub-agent-{index}`");
            }

            if (_mainChannel is not null)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"Main Codex가 프로젝트를 시작했습니다: `{request.Name}`");
                builder.AppendLine($"목표: {request.Goal}");
                builder.AppendLine($"Sub Codex 수: `{subAgentCount}`");
                builder.AppendLine("계획된 작업:");

                foreach (AgentTask task in plannedTasks)
                {
                    builder.AppendLine($"- #{task.Id} {task.Title} [{task.Priority}]");
                }

                await _mainChannel.SendMessageAsync(builder.ToString());
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task OnExecutionEventAsync(
        TaskExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);

        try
        {
            ITextChannel? targetChannel = ResolveTargetChannel(executionEvent.AgentName);
            string eventText =
                $"[{executionEvent.Timestamp:HH:mm:ss}] `{TranslateState(executionEvent.State)}` | " +
                $"작업 #{executionEvent.TaskId} | 시도 {executionEvent.Attempt}\n" +
                executionEvent.Message;

            if (targetChannel is not null)
            {
                await targetChannel.SendMessageAsync(eventText);
            }

            if (_mainChannel is not null)
            {
                await _mainChannel.SendMessageAsync(
                    $"`{executionEvent.AgentName}` | `{TranslateState(executionEvent.State)}` | " +
                    $"작업 #{executionEvent.TaskId} | 시도 {executionEvent.Attempt}");
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task OnRunCompletedAsync(
        OrchestratorRunResult runResult,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);

        try
        {
            if (_mainChannel is null)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine($"`{runResult.Report.ProjectName}` 실행이 완료되었습니다.");
            builder.AppendLine($"실행 ID: `{runResult.Report.RunId}`");
            builder.AppendLine($"워크스페이스: `{_workspaceName ?? runResult.Report.ProjectName}`");
            builder.AppendLine("완료된 작업 요약:");

            foreach (TaskResult result in runResult.Report.Results.OrderBy(item => item.TaskId))
            {
                builder.AppendLine(
                    $"- #{result.TaskId} {result.TaskTitle} | {result.AssignedAgent} | {TranslateState(result.Status)}");
            }

            await using var reportStream = File.OpenRead(runResult.Artifacts.TextReportPath);
            await _mainChannel.SendFileAsync(
                reportStream,
                Path.GetFileName(runResult.Artifacts.TextReportPath),
                builder.ToString());
        }
        finally
        {
            _sync.Release();
        }
    }

    private ITextChannel? ResolveTargetChannel(string agentName)
    {
        if (_agentChannels.TryGetValue(agentName, out ITextChannel? agentChannel))
        {
            return agentChannel;
        }

        return _mainChannel;
    }

    private string BuildUniqueCategoryName(string projectName)
    {
        string baseName = projectName.Trim();

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "project";
        }

        baseName = baseName.Length > 90 ? baseName[..90].TrimEnd() : baseName;
        string candidate = baseName;
        int suffix = 2;

        while (_guild.CategoryChannels.Any(channel =>
                   string.Equals(channel.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string TranslateState(string state)
    {
        return state switch
        {
            "Started" => "시작",
            "RetryScheduled" => "재시도 예약",
            "Completed" => "완료",
            _ => state
        };
    }
}
