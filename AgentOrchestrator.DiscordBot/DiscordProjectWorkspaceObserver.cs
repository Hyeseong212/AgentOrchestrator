using System.Text;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;
using Discord;
using Discord.WebSocket;

namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordProjectWorkspaceObserver : IExecutionObserver
{
    private const string RouteTopicMarker = "codex-route";
    private readonly Dictionary<string, ITextChannel> _agentChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SocketGuild _guild;
    private readonly ulong? _requesterUserId;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly string _workspaceRoot;
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

            ICategoryChannel category = await _guild.CreateCategoryChannelAsync(categoryName);
            _mainChannel = await _guild.CreateTextChannelAsync(
                "main-codex",
                properties =>
                {
                    properties.CategoryId = category.Id;
                    properties.Topic = BuildChannelTopic("main-codex", _workspaceRoot);
                });

            for (int index = 1; index <= subAgentCount; index++)
            {
                string agentName = $"sub-agent-{index}";
                ITextChannel subChannel = await _guild.CreateTextChannelAsync(
                    $"sub-codex-{index}",
                    properties =>
                    {
                        properties.CategoryId = category.Id;
                        properties.Topic = BuildChannelTopic(agentName, _workspaceRoot);
                    });
                _agentChannels[agentName] = subChannel;

                await subChannel.SendMessageAsync(
                    $"`sub-codex-{index}` 준비 완료\n연결된 에이전트 ID: `{agentName}`\n이 채널에 바로 말하면 자동으로 이 서브 에이전트에게 라우팅됩니다.");
            }

            if (_mainChannel is not null)
            {
                var builder = new StringBuilder();
                int estimatedMinutes = plannedTasks.Sum(task => task.EstimatedMinutes);
                int maxComplexity = plannedTasks.Count == 0 ? 1 : plannedTasks.Max(task => task.Complexity);
                builder.AppendLine($"Main Codex가 프로젝트를 시작했습니다: `{request.Name}`");
                builder.AppendLine($"목표: {request.Goal}");
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
                $"작업 #{executionEvent.TaskId} | 시도 {executionEvent.Attempt} | {executionEvent.Message}";

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
            _ => state
        };
    }

    private static string TranslatePriority(int complexity)
    {
        return complexity >= 4 ? "높음" : complexity == 3 ? "중간" : "낮음";
    }
}
