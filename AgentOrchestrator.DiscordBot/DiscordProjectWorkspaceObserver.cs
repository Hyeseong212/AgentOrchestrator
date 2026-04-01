using System.Text;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;
using Discord;
using Discord.WebSocket;

namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordProjectWorkspaceObserver : IExecutionObserver
{
    public sealed record WorkspaceRouteInfo(ulong CategoryId, ulong MainChannelId, string WorkspaceName);

    private const string RouteTopicMarker = "codex-route";
    private readonly Dictionary<string, ITextChannel> _agentChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SocketGuild _guild;
    private readonly ulong? _requesterUserId;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly TaskCompletionSource<WorkspaceRouteInfo> _workspaceReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _workspaceRoot;
    private ICategoryChannel? _category;
    private ITextChannel? _mainChannel;
    private string? _workspaceName;

    public DiscordProjectWorkspaceObserver(SocketGuild guild, string workspaceRoot, ulong? requesterUserId = null)
    {
        _guild = guild;
        _workspaceRoot = workspaceRoot;
        _requesterUserId = requesterUserId;
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

            _category = await _guild.CreateCategoryChannelAsync(categoryName);
            _mainChannel = await _guild.CreateTextChannelAsync(
                "main-codex",
                properties =>
                {
                    properties.CategoryId = _category.Id;
                    properties.Topic = BuildChannelTopic("main-codex", _workspaceRoot);
                });

            await SyncSubAgentChannelsAsync(subAgentCount);
            _workspaceReady.TrySetResult(new WorkspaceRouteInfo(_category.Id, _mainChannel.Id, categoryName));

            if (_mainChannel is not null)
            {
                await _mainChannel.SendMessageAsync(BuildPlanAnnouncement(
                    request,
                    plannedTasks,
                    subAgentCount,
                    heading: $"Main Codex가 프로젝트를 시작했습니다: `{request.Name}`"));
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task OnPlanAdjustedAsync(
        ProjectRequest request,
        IReadOnlyList<AgentTask> plannedTasks,
        int subAgentCount,
        string reason,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);

        try
        {
            if (_mainChannel is null)
            {
                return;
            }

            await SyncSubAgentChannelsAsync(subAgentCount);
            await _mainChannel.SendMessageAsync(
                BuildPlanAnnouncement(
                    request,
                    plannedTasks,
                    subAgentCount,
                    heading: "Main Codex가 계획을 다시 조정했습니다.",
                    reason: reason));
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
                executionEvent.TaskId > 0
                    ? $"[{executionEvent.Timestamp:HH:mm:ss}] `{TranslateState(executionEvent.State)}` | " +
                      $"작업 #{executionEvent.TaskId} | 시도 {executionEvent.Attempt} | {executionEvent.Message}"
                    : $"[{executionEvent.Timestamp:HH:mm:ss}] `{TranslateState(executionEvent.State)}` | {executionEvent.Message}";

            if (targetChannel is not null)
            {
                await targetChannel.SendMessageAsync(eventText);
            }

            if (_mainChannel is not null)
            {
                string summaryLine = executionEvent.TaskId > 0
                    ? $"`{executionEvent.AgentName}` | `{TranslateState(executionEvent.State)}` | 작업 #{executionEvent.TaskId} | 시도 {executionEvent.Attempt}"
                    : $"`{executionEvent.AgentName}` | `{TranslateState(executionEvent.State)}`";
                await _mainChannel.SendMessageAsync(summaryLine);
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
            if (_requesterUserId is ulong requesterUserId)
            {
                builder.AppendLine($"<@{requesterUserId}> 요청하신 task가 완료되었습니다.");
            }
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

    public Task<WorkspaceRouteInfo> WaitForWorkspaceReadyAsync(CancellationToken cancellationToken = default)
    {
        return _workspaceReady.Task.WaitAsync(cancellationToken);
    }

    private async Task SyncSubAgentChannelsAsync(int desiredCount)
    {
        if (_category is null)
        {
            return;
        }

        for (int index = 1; index <= desiredCount; index++)
        {
            string agentName = $"sub-agent-{index}";
            if (_agentChannels.ContainsKey(agentName))
            {
                continue;
            }

            ITextChannel subChannel = await _guild.CreateTextChannelAsync(
                $"sub-codex-{index}",
                properties =>
                {
                    properties.CategoryId = _category.Id;
                    properties.Topic = BuildChannelTopic(agentName, _workspaceRoot);
                });
            _agentChannels[agentName] = subChannel;

            await subChannel.SendMessageAsync(
                $"`sub-codex-{index}` 준비 완료\n연결된 에이전트 ID: `{agentName}`\n이 채널에 바로 말하면 자동으로 이 서브 에이전트에게 라우팅됩니다.");
        }

        string[] channelsToRemove = _agentChannels.Keys
            .Select(name => new
            {
                AgentName = name,
                Index = ParseSubAgentIndex(name)
            })
            .Where(item => item.Index > desiredCount)
            .OrderByDescending(item => item.Index)
            .Select(item => item.AgentName)
            .ToArray();

        foreach (string agentName in channelsToRemove)
        {
            if (!_agentChannels.TryGetValue(agentName, out ITextChannel? channel))
            {
                continue;
            }

            await channel.DeleteAsync();
            _agentChannels.Remove(agentName);
        }
    }

    private string BuildPlanAnnouncement(
        ProjectRequest request,
        IReadOnlyList<AgentTask> plannedTasks,
        int subAgentCount,
        string heading,
        string? reason = null)
    {
        var builder = new StringBuilder();
        int estimatedMinutes = plannedTasks.Sum(task => task.EstimatedMinutes);
        int maxComplexity = plannedTasks.Count == 0 ? 1 : plannedTasks.Max(task => task.Complexity);
        builder.AppendLine(heading);
        builder.AppendLine($"목표: {request.Goal}");
        if (!string.IsNullOrWhiteSpace(reason))
        {
            builder.AppendLine($"재계획 이유: {reason}");
        }
        builder.AppendLine($"예상 난이도: `{TranslatePriority(maxComplexity)}`");
        builder.AppendLine($"예상 소요: `약 {estimatedMinutes}분`");
        if (subAgentCount == 0)
        {
            builder.AppendLine("이번 작업은 Main Codex가 단독으로 처리합니다.");
        }
        else
        {
            builder.AppendLine($"Sub Codex 수: `{subAgentCount}`");
            builder.AppendLine("Main Codex가 분석/통합 단계를 맡고, Sub Codex가 병렬 가능한 구현 단계를 맡습니다.");
        }
        builder.AppendLine("이 채널에 바로 말하면 Main Codex에게 자동으로 라우팅됩니다.");
        builder.AppendLine("계획된 작업:");

        foreach (AgentTask task in plannedTasks)
        {
            builder.AppendLine(
                $"- #{task.Id} {task.Title} [{task.Priority}] | " +
                $"{task.ExecutionLaneLabel} | phase {task.Phase} | 약 {task.EstimatedMinutes}분");
        }

        return builder.ToString();
    }

    private static int ParseSubAgentIndex(string agentName)
    {
        string[] parts = agentName.Split('-');
        return parts.Length > 0 && int.TryParse(parts[^1], out int index) ? index : 0;
    }

    public static string BuildChannelTopic(string agentName, string workspaceRoot)
    {
        return
            $"{RouteTopicMarker}\n" +
            $"agent={agentName}\n" +
            $"workspace={workspaceRoot}";
    }

    public static bool TryParseChannelTopic(string? topic, out string agentName, out string workspaceRoot)
    {
        agentName = string.Empty;
        workspaceRoot = string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
        {
            return false;
        }

        string[] lines = topic
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !string.Equals(lines[0], RouteTopicMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string line in lines.Skip(1))
        {
            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();

            if (string.Equals(key, "agent", StringComparison.OrdinalIgnoreCase))
            {
                agentName = value;
            }
            else if (string.Equals(key, "workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspaceRoot = value;
            }
        }

        return !string.IsNullOrWhiteSpace(agentName) && !string.IsNullOrWhiteSpace(workspaceRoot);
    }

    private static string TranslateState(string state)
    {
        return state switch
        {
            "Started" => "시작",
            "RetryScheduled" => "재시도 예약",
            "Completed" => "완료",
            "Replanned" => "재계획",
            _ => state
        };
    }

    private static string TranslatePriority(int complexity)
    {
        return complexity >= 4 ? "높음" : complexity == 3 ? "중간" : "낮음";
    }
}
