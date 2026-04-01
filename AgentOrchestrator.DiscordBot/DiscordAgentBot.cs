using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordAgentBot
{
    private const string MissingPermissionsMessage =
        "권한이 부족해서 작업을 진행하지 못했어요.\n" +
        "봇에 아래 권한을 추가해 주세요.\n" +
        "- Manage Channels\n" +
        "- View Channels\n" +
        "- Send Messages\n" +
        "- Attach Files\n" +
        "- Read Message History";

    private const string MissingWorkspacePermissionsMessage =
        "이 서버에서 프로젝트 채널을 만들 권한이 부족해요.\n" +
        "봇 역할에 아래 권한을 추가해 주세요.\n" +
        "- Manage Channels\n" +
        "- View Channels\n" +
        "- Send Messages\n" +
        "- Attach Files\n" +
        "- Read Message History";

    private const string NoCompletedRunsMessage = "완료된 실행 기록이 아직 없어요.";

    private enum ApprovalActionKind
    {
        Ask,
        Task,
        Adjust
    }

    private sealed record AgentChannelRoute(
        string AgentName,
        string WorkspaceRoot,
        string? HostContext,
        bool RequiresFullAccess);
    private sealed record PendingApprovalRequest(
        ApprovalActionKind ActionKind,
        string Input,
        AgentChannelRoute? Route = null,
        string? HostContext = null);
    private sealed record PendingWorkspaceDeleteRequest(
        ulong CategoryId,
        string CategoryName);
    private sealed record WorkspaceResolution(
        string? WorkspaceRoot,
        string? HostContext,
        bool UseFullAccess,
        bool StopProcessing);

    private sealed class ActiveWorkspaceRun : IExecutionAdjustmentSource
    {
        private readonly List<string> _pendingAdjustments = [];
        private readonly Lock _sync = new();

        public ActiveWorkspaceRun(
            DiscordProjectWorkspaceObserver observer,
            string workspaceRoot,
            bool allowFullAccess,
            string? hostContext)
        {
            Observer = observer;
            WorkspaceRoot = workspaceRoot;
            AllowFullAccess = allowFullAccess;
            HostContext = hostContext;
        }

        public DiscordProjectWorkspaceObserver Observer { get; }
        public string WorkspaceRoot { get; }
        public bool AllowFullAccess { get; }
        public string? HostContext { get; }

        public void QueueAdjustment(string input)
        {
            lock (_sync)
            {
                _pendingAdjustments.Add(input.Trim());
            }
        }

        public IReadOnlyList<string> DrainPendingAdjustments()
        {
            lock (_sync)
            {
                if (_pendingAdjustments.Count == 0)
                {
                    return [];
                }

                string[] items = _pendingAdjustments.ToArray();
                _pendingAdjustments.Clear();
                return items;
            }
        }
    }

    private readonly DiscordSocketClient _client;
    private bool _legacySlashCommandsCleared;
    private readonly string _commandPrefix;
    private readonly AgentOrchestratorRuntime _runtime;
    private readonly LocalArtifactInsightBuilder _artifactInsightBuilder;
    private readonly DiscordMessageInputPreparer _messageInputPreparer;
    private readonly Dictionary<string, string> _activeWorkspaceRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> _approvedFullAccessUsers = [];
    private readonly Lock _approvalSync = new();
    private readonly Lock _activeRunSync = new();
    private readonly Dictionary<string, PendingApprovalRequest> _pendingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingWorkspaceDeleteRequest> _pendingWorkspaceDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ActiveWorkspaceRun> _activeWorkspaceRuns = [];
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public DiscordAgentBot(AgentOrchestratorRuntime runtime, DiscordBotSettings settings)
    {
        _runtime = runtime;
        _artifactInsightBuilder = new LocalArtifactInsightBuilder();
        _messageInputPreparer = new DiscordMessageInputPreparer(_runtime.WorkspaceRoot, _artifactInsightBuilder);
        _commandPrefix = string.IsNullOrWhiteSpace(settings.CommandPrefix) ? "!" : settings.CommandPrefix;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.DirectMessages |
                             GatewayIntents.MessageContent
        });

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageReceivedAsync;

        BotToken = settings.BotToken;
    }

    public string BotToken { get; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _client.LoginAsync(TokenType.Bot, BotToken);
        await _client.StartAsync();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            await _client.StopAsync();
        }
    }

    private async Task OnReadyAsync()
    {
        if (!_legacySlashCommandsCleared)
        {
            await ClearSlashCommandsAsync();
            _legacySlashCommandsCleared = true;
        }

        Console.WriteLine($"Discord bot ready as {_client.CurrentUser.Username}.");
    }

    private Task OnLogAsync(LogMessage message)
    {
        Console.WriteLine($"[{message.Severity}] {message.Source}: {message.Message}");
        if (message.Exception is not null)
        {
            Console.WriteLine(message.Exception);
        }

        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage socketMessage)
    {
        _ = RunGatewayHandlerAsync(() => ProcessMessageReceivedAsync(socketMessage), "MessageReceived");
        return Task.CompletedTask;
    }

    private async Task ProcessMessageReceivedAsync(SocketMessage socketMessage)
    {
        if (socketMessage.Author.IsBot || socketMessage is not SocketUserMessage message)
        {
            return;
        }

        if (await TryHandleWorkspaceDeleteResponseAsync(message))
        {
            return;
        }

        if (await TryHandleApprovalResponseAsync(message))
        {
            return;
        }

        if (!message.Content.StartsWith(_commandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldHandleConversationalMessage(message))
            {
                string input = NormalizeConversationalInput(message);
                if (!string.IsNullOrWhiteSpace(input) || message.Attachments.Count > 0)
                {
                    await HandleConversationalMessageAsync(message, input);
                }
                else if (IsMentioningCurrentBot(message))
                {
                    await message.Channel.SendMessageAsync(
                        "불렀어요. 그냥 질문하면 답하고, 작업 요청처럼 말하면 프로젝트를 만들어서 진행할게요.\n" +
                        BuildHelpText());
                }
            }
            return;
        }

        string commandLine = message.Content[_commandPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            await message.Channel.SendMessageAsync(BuildHelpText());
            return;
        }

        string[] commandParts = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string command = commandParts[0].ToLowerInvariant();
        string argument = commandParts.Length > 1 ? commandParts[1].Trim() : string.Empty;

        try
        {
            switch (command)
            {
                case "help":
                    await message.Channel.SendMessageAsync(BuildHelpText());
                    break;
                case "ask":
                    await HandleAskAsync(message, argument);
                    break;
                case "capabilities":
                    await HandleCapabilitiesAsync(message);
                    break;
                case "task":
                    await HandleTaskAsync(message, argument);
                    break;
                case "status":
                    await HandleStatusAsync(message);
                    break;
                case "history":
                    await HandleHistoryAsync(message);
                    break;
                case "report":
                    await HandleReportAsync(message, argument);
                    break;
                case "request":
                    await HandleRequestAsync(message);
                    break;
                default:
                    await message.Channel.SendMessageAsync(
                        $"알 수 없는 명령어예요: `{command}`\n{BuildHelpText()}");
                    break;
            }
        }
        catch (HttpException httpException) when (httpException.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            await message.Channel.SendMessageAsync(MissingPermissionsMessage);
        }
        catch (Exception exception)
        {
            await message.Channel.SendMessageAsync($"Command failed: {exception.Message}");
        }
    }

    private async Task HandleConversationalMessageAsync(SocketUserMessage message, string input)
    {
        if (LooksLikeWorkspaceDeletionRequest(input))
        {
            await HandleWorkspaceDeletionRequestAsync(message);
            return;
        }

        PreparedMessageInput prepared = await _messageInputPreparer.PrepareAsync(message, input);
        bool attachmentOnlyInput = prepared.HasAttachments && !prepared.HasOriginalText;

        if (TryResolveAgentChannelRoute(message, out AgentChannelRoute? route))
        {
            if (TryGetActiveWorkspaceRun(message, out ActiveWorkspaceRun? activeRun) &&
                string.Equals(route.AgentName, "main-codex", StringComparison.OrdinalIgnoreCase) &&
                (LooksLikeTaskRequest(prepared.BaseInput) || attachmentOnlyInput))
            {
                string adjustmentInput = MergeInputWithHostContext(prepared.BaseInput, prepared.HostContext);
                bool? adjustmentFullAccess = await ResolveAccessApprovalAsync(
                    message,
                    ApprovalActionKind.Adjust,
                    prepared.BaseInput,
                    adjustmentInput,
                    route: route);
                if (adjustmentFullAccess is null)
                {
                    return;
                }

                await HandleActiveRunAdjustmentAsync(message, adjustmentInput, activeRun);
                return;
            }

            if (TryGetActiveWorkspaceRun(message, out _) &&
                !string.Equals(route.AgentName, "main-codex", StringComparison.OrdinalIgnoreCase) &&
                LooksLikeTaskRequest(prepared.BaseInput))
            {
                await message.Channel.SendMessageAsync(
                    "진행 중 task에 추가 요청을 붙이려면 `main-codex` 채널에 말해 주세요. 그러면 난도를 다시 계산해서 sub 코덱스를 증감합니다.");
                return;
            }

            bool? routedFullAccess = await ResolveAccessApprovalAsync(
                message,
                ApprovalActionKind.Ask,
                prepared.BaseInput,
                prepared.BaseInput,
                prepared.HostContext,
                route);
            if (routedFullAccess is null)
            {
                return;
            }

            await HandleAskCoreAsync(message, prepared.BaseInput, routedFullAccess.Value, route, prepared.HostContext);
            return;
        }

        bool isTaskRequest = !attachmentOnlyInput && LooksLikeTaskRequest(prepared.BaseInput);
        bool? allowFullAccess = await ResolveAccessApprovalAsync(
            message,
            isTaskRequest ? ApprovalActionKind.Task : ApprovalActionKind.Ask,
            prepared.BaseInput,
            prepared.BaseInput,
            prepared.HostContext);

        if (allowFullAccess is null)
        {
            return;
        }

        if (isTaskRequest)
        {
            if (message.Channel is not SocketGuildChannel guildChannel)
            {
                await message.Channel.SendMessageAsync(
                    "이 요청은 작업으로 처리하는 게 좋아 보여요. 서버 채널에서 말해주면 프로젝트 워크스페이스를 만들어서 진행할게요.");
                return;
            }
            await message.Channel.SendMessageAsync(
                "이건 작업으로 처리하는 게 맞아 보여요. 프로젝트 워크스페이스를 만들고 바로 진행할게요.");

            await HandleTaskCoreAsync(message, prepared.BaseInput, allowFullAccess.Value, prepared.HostContext);

            return;
        }

        await HandleAskCoreAsync(message, prepared.BaseInput, allowFullAccess.Value, messageHostContext: prepared.HostContext);
    }

    private Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        _ = RunGatewayHandlerAsync(() => ProcessSlashCommandExecutedAsync(command), "SlashCommandExecuted");
        return Task.CompletedTask;
    }

    private async Task ProcessSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        try
        {
            switch (command.Data.Name)
            {
                case "help":
                    await command.RespondAsync(BuildHelpText());
                    break;
                case "ask":
                    await command.DeferAsync();
                    await HandleAskSlashAsync(command, GetRequiredStringOption(command, "question"));
                    break;
                case "capabilities":
                    await command.RespondAsync(BuildCapabilitiesText());
                    break;
                case "task":
                    await command.DeferAsync();
                    await HandleTaskSlashAsync(command, GetRequiredStringOption(command, "goal"));
                    break;
                case "status":
                    await HandleStatusSlashAsync(command);
                    break;
                case "history":
                    await HandleHistorySlashAsync(command);
                    break;
                case "report":
                    await command.DeferAsync();
                    await HandleReportSlashAsync(command, GetRequiredStringOption(command, "run_id"));
                    break;
                case "request":
                    await HandleRequestSlashAsync(command);
                    break;
                default:
                    await command.RespondAsync($"알 수 없는 명령어입니다: `{command.Data.Name}`");
                    break;
            }
        }
        catch (HttpException httpException) when (httpException.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            if (command.HasResponded)
            {
                await command.FollowupAsync(MissingPermissionsMessage);
            }
            else
            {
                await command.RespondAsync(MissingPermissionsMessage);
            }
        }
        catch (Exception exception)
        {
            if (command.HasResponded)
            {
                await command.FollowupAsync($"명령 처리 실패: {exception.Message}");
            }
            else
            {
                await command.RespondAsync($"명령 처리 실패: {exception.Message}");
            }
        }
    }

    private async Task RunGatewayHandlerAsync(Func<Task> handler, string handlerName)
    {
        try
        {
            await handler();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Error] {handlerName} handler failed: {exception}");
        }
    }

    private async Task HandleTaskAsync(SocketUserMessage message, string goal)
    {
        PreparedMessageInput prepared = await _messageInputPreparer.PrepareAsync(message, goal);

        if (string.IsNullOrWhiteSpace(prepared.BaseInput))
        {
            await message.Channel.SendMessageAsync("사용법: `!task <목표>`");
            return;
        }

        if (LooksLikeQuestion(prepared.BaseInput))
        {
            await message.Channel.SendMessageAsync(
                "이 입력은 질문처럼 보여서 답변 모드로 처리할게요. 작업 실행은 `!task <목표>`로 쓰고, 질문은 `!ask <질문>`으로 물어보면 됩니다.");
            bool? questionFullAccess = await ResolveAccessApprovalAsync(
                message,
                ApprovalActionKind.Ask,
                prepared.BaseInput,
                prepared.BaseInput,
                prepared.HostContext);
            if (questionFullAccess is null)
            {
                return;
            }

            await HandleAskCoreAsync(message, prepared.BaseInput, questionFullAccess.Value, messageHostContext: prepared.HostContext);
            return;
        }

        bool? allowFullAccess = await ResolveAccessApprovalAsync(
            message,
            ApprovalActionKind.Task,
            prepared.BaseInput,
            prepared.BaseInput,
            prepared.HostContext);
        if (allowFullAccess is null)
        {
            return;
        }

        await HandleTaskCoreAsync(message, prepared.BaseInput, allowFullAccess.Value, prepared.HostContext);
    }

    private async Task HandleTaskCoreAsync(
        SocketUserMessage message,
        string goal,
        bool allowFullAccess,
        string? messageHostContext = null)
    {
        if (!await _runLock.WaitAsync(TimeSpan.Zero))
        {
            await message.Channel.SendMessageAsync("다른 실행이 진행 중이라 끝난 뒤 다시 시도해 주세요.");
            return;
        }

        try
        {
            using IDisposable typing = message.Channel.EnterTypingState();

            if (message.Channel is not SocketGuildChannel guildChannel)
            {
                await message.Channel.SendMessageAsync(
                    "`!task`는 서버 채널에서만 사용할 수 있어요. 프로젝트 카테고리와 main/sub codex 채널을 만들어야 하기 때문입니다.");
                return;
            }

            if (!HasWorkspacePermissions(guildChannel))
            {
                await message.Channel.SendMessageAsync(MissingWorkspacePermissionsMessage);
                return;
            }

            WorkspaceResolution workspace = await ResolveWorkspaceAsync(
                message,
                goal,
                allowFullAccess,
                defaultHostContext: messageHostContext);
            if (workspace.StopProcessing)
            {
                return;
            }

            await message.Channel.SendMessageAsync(
                $"작업 실행을 시작합니다: `{goal}`\n" +
                "프로젝트 카테고리와 `main-codex` 채널을 만들고, 일이 커지면 `sub-codex-*` 채널도 추가해서 진행 기록을 남길게요." +
                (string.IsNullOrWhiteSpace(workspace.WorkspaceRoot)
                    ? string.Empty
                    : $"\n작업 기준 폴더: `{workspace.WorkspaceRoot}`") +
                (workspace.UseFullAccess ? "\n이번 실행은 승인된 full access 모드로 진행합니다." : string.Empty));

            string activeWorkspaceRoot = workspace.WorkspaceRoot ?? _runtime.WorkspaceRoot;
            var observer = new DiscordProjectWorkspaceObserver(guildChannel.Guild, activeWorkspaceRoot, message.Author.Id);
            var activeRun = new ActiveWorkspaceRun(observer, activeWorkspaceRoot, workspace.UseFullAccess, workspace.HostContext);
            Task<OrchestratorRunResult> runTask = RunTaskInGuildAsync(
                guildChannel.Guild,
                goal,
                workspace.UseFullAccess,
                observer,
                workspace.WorkspaceRoot,
                workspace.HostContext,
                activeRun,
                message.Author.Id);
            DiscordProjectWorkspaceObserver.WorkspaceRouteInfo? routeInfo = await WaitForWorkspaceReadyAsync(observer, runTask);

            if (routeInfo is not null)
            {
                RegisterActiveWorkspaceRun(routeInfo.CategoryId, activeRun);
            }

            OrchestratorRunResult runResult;
            try
            {
                runResult = await runTask;
            }
            finally
            {
                if (routeInfo is not null)
                {
                    UnregisterActiveWorkspaceRun(routeInfo.CategoryId);
                }
            }
            string summary = BuildRunCompletionSummary(runResult);

            await using var reportStream = File.OpenRead(runResult.Artifacts.TextReportPath);
            await message.Channel.SendFileAsync(
                reportStream,
                Path.GetFileName(runResult.Artifacts.TextReportPath),
                summary);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task HandleTaskSlashAsync(SocketSlashCommand command, string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            await command.FollowupAsync("Usage: `/task goal:<목표>`");
            return;
        }

        if (LooksLikeQuestion(goal))
        {
            await command.FollowupAsync(
                "이 입력은 질문처럼 보여서 답변 모드로 처리할게요. 작업 실행은 `/task goal:<목표>`로 쓰고, 질문은 `/ask question:<질문>`으로 물어보면 됩니다.");
            await HandleAskSlashAsync(command, goal);
            return;
        }

        if (!await _runLock.WaitAsync(TimeSpan.Zero))
        {
            await command.FollowupAsync("다른 실행이 진행 중이라 잠시 후 다시 시도해 주세요.");
            return;
        }

        try
        {
            if (command.Channel is not SocketGuildChannel guildChannel)
            {
                await command.FollowupAsync(
                    "`/task`는 서버 채널에서만 사용할 수 있어요. 프로젝트 카테고리와 main/sub codex 채널을 만들어야 하기 때문입니다.");
                return;
            }

            if (!HasWorkspacePermissions(guildChannel))
            {
                await command.FollowupAsync(MissingWorkspacePermissionsMessage);
                return;
            }

            await command.FollowupAsync(
                $"작업 실행을 시작합니다: `{goal}`\n" +
                "프로젝트 카테고리와 `main-codex` 채널을 만들고, 일이 커지면 `sub-codex-*` 채널도 추가해서 진행 기록을 남길게요.");
            var observer = new DiscordProjectWorkspaceObserver(guildChannel.Guild, _runtime.WorkspaceRoot, command.User.Id);
            var activeRun = new ActiveWorkspaceRun(observer, _runtime.WorkspaceRoot, allowFullAccess: false, hostContext: null);
            Task<OrchestratorRunResult> runTask = RunTaskInGuildAsync(
                guildChannel.Guild,
                goal,
                allowFullAccess: false,
                observer,
                requesterUserId: command.User.Id,
                adjustmentSource: activeRun);
            DiscordProjectWorkspaceObserver.WorkspaceRouteInfo? routeInfo = await WaitForWorkspaceReadyAsync(observer, runTask);

            if (routeInfo is not null)
            {
                RegisterActiveWorkspaceRun(routeInfo.CategoryId, activeRun);
            }

            OrchestratorRunResult runResult;
            try
            {
                runResult = await runTask;
            }
            finally
            {
                if (routeInfo is not null)
                {
                    UnregisterActiveWorkspaceRun(routeInfo.CategoryId);
                }
            }
            string summary = BuildRunCompletionSummary(runResult);

            await using var reportStream = File.OpenRead(runResult.Artifacts.TextReportPath);
            await command.FollowupWithFileAsync(
                reportStream,
                Path.GetFileName(runResult.Artifacts.TextReportPath),
                summary);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task HandleAskAsync(SocketUserMessage message, string question)
    {
        PreparedMessageInput prepared = await _messageInputPreparer.PrepareAsync(message, question);

        if (string.IsNullOrWhiteSpace(prepared.BaseInput))
        {
            await message.Channel.SendMessageAsync("사용법: `!ask <질문>`");
            return;
        }

        AgentChannelRoute? route = TryResolveAgentChannelRoute(message, out AgentChannelRoute? resolvedRoute)
            ? resolvedRoute
            : null;

        bool? allowFullAccess = await ResolveAccessApprovalAsync(
            message,
            ApprovalActionKind.Ask,
            prepared.BaseInput,
            prepared.BaseInput,
            prepared.HostContext,
            route);
        if (allowFullAccess is null)
        {
            return;
        }

        await HandleAskCoreAsync(message, prepared.BaseInput, allowFullAccess.Value, route, prepared.HostContext);
    }

    private async Task HandleAskCoreAsync(
        SocketUserMessage message,
        string question,
        bool allowFullAccess,
        AgentChannelRoute? route = null,
        string? messageHostContext = null)
    {
        WorkspaceResolution workspace = await ResolveWorkspaceAsync(
            message,
            question,
            allowFullAccess,
            route?.WorkspaceRoot,
            CombineHostContexts(route?.HostContext, messageHostContext));
        if (workspace.StopProcessing)
        {
            return;
        }

        await message.Channel.SendMessageAsync(
            BuildAskStartedMessage(workspace.UseFullAccess, workspace.WorkspaceRoot, route?.AgentName));
        using IDisposable typing = message.Channel.EnterTypingState();
        CodexTaskResponse answer = await _runtime.AskAsync(
            question,
            workspace.UseFullAccess,
            workspace.WorkspaceRoot,
            workspace.HostContext,
            route?.AgentName);
        await message.Channel.SendMessageAsync(answer.Message);
    }

    private async Task HandleAskSlashAsync(SocketSlashCommand command, string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            await command.FollowupAsync("Usage: `/ask question:<질문>`");
            return;
        }

        await command.FollowupAsync(BuildAskStartedMessage(allowFullAccess: false, workspaceRoot: null));
        CodexTaskResponse answer = await _runtime.AskAsync(question);
        await command.FollowupAsync(answer.Message);
    }

    private async Task HandleCapabilitiesAsync(SocketUserMessage message)
    {
        await message.Channel.SendMessageAsync(BuildCapabilitiesText());
    }

    private async Task HandleStatusAsync(SocketUserMessage message)
    {
        RunHistoryEntry? latest = _runtime.LoadHistory().FirstOrDefault();

        if (latest is null)
        {
            await message.Channel.SendMessageAsync(NoCompletedRunsMessage);
            return;
        }

        await message.Channel.SendMessageAsync(BuildLatestRunMessage(latest));
    }

    private async Task HandleStatusSlashAsync(SocketSlashCommand command)
    {
        RunHistoryEntry? latest = _runtime.LoadHistory().FirstOrDefault();

        if (latest is null)
        {
            await command.RespondAsync(NoCompletedRunsMessage);
            return;
        }

        await command.RespondAsync(BuildLatestRunMessage(latest));
    }

    private async Task HandleHistoryAsync(SocketUserMessage message)
    {
        IReadOnlyList<RunHistoryEntry> history = _runtime.LoadHistory();

        if (history.Count == 0)
        {
            await message.Channel.SendMessageAsync(NoCompletedRunsMessage);
            return;
        }

        await message.Channel.SendMessageAsync(BuildHistoryMessage(history));
    }

    private async Task HandleHistorySlashAsync(SocketSlashCommand command)
    {
        IReadOnlyList<RunHistoryEntry> history = _runtime.LoadHistory();

        if (history.Count == 0)
        {
            await command.RespondAsync(NoCompletedRunsMessage);
            return;
        }

        await command.RespondAsync(BuildHistoryMessage(history));
    }

    private async Task HandleReportAsync(SocketUserMessage message, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            await message.Channel.SendMessageAsync("사용법: `!report <runId>`");
            return;
        }

        RunHistoryEntry? entry = _runtime.LoadHistory()
            .FirstOrDefault(item => item.RunId.StartsWith(runId, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            await message.Channel.SendMessageAsync($"`{runId}`에 해당하는 실행 기록을 찾지 못했어요.");
            return;
        }

        await using var reportStream = File.OpenRead(entry.TextReportPath);
        await message.Channel.SendFileAsync(
            reportStream,
            Path.GetFileName(entry.TextReportPath),
            $"`{entry.RunId}` 실행 리포트입니다.");
    }

    private async Task HandleReportSlashAsync(SocketSlashCommand command, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            await command.FollowupAsync("Usage: `/report run_id:<runId>`");
            return;
        }

        RunHistoryEntry? entry = _runtime.LoadHistory()
            .FirstOrDefault(item => item.RunId.StartsWith(runId, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            await command.FollowupAsync($"`{runId}`에 해당하는 실행 기록을 찾지 못했어요.");
            return;
        }

        await using var reportStream = File.OpenRead(entry.TextReportPath);
        await command.FollowupWithFileAsync(
            reportStream,
            Path.GetFileName(entry.TextReportPath),
            $"`{entry.RunId}` 실행 리포트입니다.");
    }

    private async Task HandleRequestAsync(SocketUserMessage message)
    {
        ProjectRequestLoadResult request = await _runtime.LoadRequestAsync();
        await message.Channel.SendMessageAsync(BuildRequestMessage(request));
    }

    private async Task HandleRequestSlashAsync(SocketSlashCommand command)
    {
        ProjectRequestLoadResult request = await _runtime.LoadRequestAsync();
        await command.RespondAsync(BuildRequestMessage(request));
    }

    private string BuildHelpText()
    {
        return
            $"Commands:\n" +
            "- `일반/general`, DM, 또는 봇을 멘션한 메시지에서는 그냥 대화해도 됩니다. 질문이면 답하고, 작업 요청처럼 보이면 자동으로 프로젝트 워크스페이스를 만들고 진행해요.\n" +
            "- 이미지, PPTX, XLSX, PDF 같은 Discord 첨부파일도 같이 보내면 로컬에 저장하고 내용을 추출해서 함께 반영해요.\n" +
            "- 프로젝트 워크스페이스 안의 `main-codex`, `sub-codex-*` 채널에 바로 말하면 그 채널에 연결된 agent로 자동 라우팅됩니다.\n" +
            "- 워크스페이스 채널 안에서 카테고리/채널 삭제를 요청하면 현재 프로젝트만 지우도록 확인 절차를 거쳐 처리할 수 있어요.\n" +
            $"- `{_commandPrefix}ask <question>`\n" +
            $"- `{_commandPrefix}capabilities`\n" +
            $"- `{_commandPrefix}task <goal>` 프로젝트 카테고리와 main/sub codex 채널 생성\n" +
            $"- `{_commandPrefix}status`\n" +
            $"- `{_commandPrefix}history`\n" +
            $"- `{_commandPrefix}report <runId>`\n" +
            $"- `{_commandPrefix}request`\n" +
            $"- `{_commandPrefix}help`";
    }

    private string BuildCapabilitiesText()
    {
        return
            "지금 이 봇은 두 가지를 할 수 있어요.\n" +
            $"- `{_commandPrefix}task <목표>`: 프로젝트 카테고리와 `main-codex` 채널을 만들고, 일이 커지면 `sub-codex-*`도 추가로 만들어서 진행 기록과 report를 남김\n" +
            $"- `{_commandPrefix}ask <질문>`: 바로 질문에 짧게 답변\n" +
            "- 만들어진 `main-codex`와 `sub-codex-*` 채널에 바로 말하면 해당 Main/Sub Codex로 자동 라우팅\n" +
            "- Discord 첨부파일을 자동 저장하고, 이미지/PPTX/XLSX/PDF는 내용을 읽거나 직접 볼 수 있게 문맥으로 붙여줌\n" +
            "- 워크스페이스 삭제 요청을 받으면 현재 카테고리만 확인 후 정리 가능\n" +
            "- 일반 채널에서 그냥 대화해도 돼요. 질문이면 답하고, 작업 요청이면 스스로 작업으로 전환해요.\n" +
            $"- `{_commandPrefix}status`, `{_commandPrefix}history`, `{_commandPrefix}report <runId>`, `{_commandPrefix}request`도 사용할 수 있어요.";
    }

    private static bool LooksLikeQuestion(string input)
    {
        string trimmed = input.Trim();

        return trimmed.Contains('?') ||
               trimmed.Contains('？') ||
               trimmed.EndsWith("까", StringComparison.Ordinal) ||
               trimmed.EndsWith("요", StringComparison.Ordinal) ||
               trimmed.Contains("뭐", StringComparison.Ordinal) ||
               trimmed.Contains("무엇", StringComparison.Ordinal) ||
               trimmed.Contains("어떻게", StringComparison.Ordinal) ||
               trimmed.Contains("왜", StringComparison.Ordinal) ||
               trimmed.Contains("가능", StringComparison.Ordinal) ||
               trimmed.Contains("할수있", StringComparison.Ordinal) ||
               trimmed.Contains("할 수 있", StringComparison.Ordinal);
    }

    private static bool LooksLikeTaskRequest(string input)
    {
        string trimmed = input.Trim();

        if (LooksLikeQuestion(trimmed))
        {
            return trimmed.Contains("만들어줘", StringComparison.Ordinal) ||
                   trimmed.Contains("해줘", StringComparison.Ordinal) ||
                   trimmed.Contains("구현해줘", StringComparison.Ordinal) ||
                   trimmed.Contains("고쳐줘", StringComparison.Ordinal) ||
                   trimmed.Contains("추가해줘", StringComparison.Ordinal);
        }

        string[] taskKeywords =
        [
            "만들어",
            "만들기",
            "구현",
            "작성",
            "추가",
            "수정",
            "고쳐",
            "리팩터",
            "설정",
            "세팅",
            "연결",
            "연동",
            "구축",
            "개발",
            "프로젝트",
            "작업",
            "build",
            "create",
            "implement",
            "write",
            "add",
            "fix",
            "refactor",
            "setup",
            "connect"
        ];

        return taskKeywords.Any(keyword => trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeWorkspaceDeletionRequest(string input)
    {
        string trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        bool hasDeleteVerb =
            trimmed.Contains("삭제", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("지워", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("정리", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("닫아", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("cleanup", StringComparison.OrdinalIgnoreCase);
        bool hasWorkspaceTarget =
            trimmed.Contains("카테고리", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("채널", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("워크스페이스", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("프로젝트", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("project", StringComparison.OrdinalIgnoreCase);

        return hasDeleteVerb && hasWorkspaceTarget;
    }

    private static bool HasWorkspacePermissions(SocketGuildChannel guildChannel)
    {
        ChannelPermissions permissions = guildChannel.Guild.CurrentUser.GetPermissions(guildChannel);

        return permissions.ManageChannel &&
               permissions.ViewChannel &&
               permissions.SendMessages &&
               permissions.AttachFiles &&
               permissions.ReadMessageHistory;
    }

    private bool ShouldHandleConversationalMessage(SocketUserMessage message)
    {
        if (message.Channel is IDMChannel)
        {
            return true;
        }

        if (IsMentioningCurrentBot(message))
        {
            return true;
        }

        if (TryResolveAgentChannelRoute(message, out _))
        {
            return true;
        }

        if (message.Channel is SocketGuildChannel guildChannel)
        {
            if (guildChannel.Name.StartsWith("sub-codex-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (guildChannel.Name.StartsWith("main-codex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(guildChannel.Name, "일반", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(guildChannel.Name, "general", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool IsMentioningCurrentBot(SocketUserMessage message)
    {
        return _client.CurrentUser is not null &&
               message.MentionedUsers.Any(user => user.Id == _client.CurrentUser.Id);
    }

    private string NormalizeConversationalInput(SocketUserMessage message)
    {
        string content = message.Content.Trim();
        string mentionA = $"<@{_client.CurrentUser.Id}>";
        string mentionB = $"<@!{_client.CurrentUser.Id}>";

        return content
            .Replace(mentionA, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(mentionB, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private bool TryResolveAgentChannelRoute(
        SocketUserMessage message,
        [NotNullWhen(true)] out AgentChannelRoute? route)
    {
        route = null;

        if (message.Channel is not SocketTextChannel textChannel)
        {
            return false;
        }

        if (!DiscordProjectWorkspaceObserver.TryParseChannelTopic(
                textChannel.Topic,
                out string agentName,
                out string workspaceRoot))
        {
            return false;
        }

        route = new AgentChannelRoute(
            agentName,
            workspaceRoot,
            BuildAgentChannelHostContext(textChannel, agentName, workspaceRoot),
            RequiresFullAccessForWorkspace(workspaceRoot));
        return true;
    }

    private bool TryResolveManagedWorkspaceCategory(
        SocketUserMessage message,
        [NotNullWhen(true)] out SocketCategoryChannel? category)
    {
        category = null;

        if (message.Channel is not SocketTextChannel textChannel ||
            textChannel.CategoryId is not ulong categoryId)
        {
            return false;
        }

        SocketCategoryChannel? candidate = textChannel.Guild.CategoryChannels
            .FirstOrDefault(item => item.Id == categoryId);
        if (candidate is null)
        {
            return false;
        }

        bool hasManagedMainChannel = textChannel.Guild.TextChannels.Any(channel =>
            channel.CategoryId == categoryId &&
            string.Equals(channel.Name, "main-codex", StringComparison.OrdinalIgnoreCase) &&
            DiscordProjectWorkspaceObserver.TryParseChannelTopic(channel.Topic, out string agentName, out _ ) &&
            string.Equals(agentName, "main-codex", StringComparison.OrdinalIgnoreCase));

        if (!hasManagedMainChannel)
        {
            return false;
        }

        category = candidate;
        return true;
    }

    private async Task UpdateAgentChannelWorkspaceTopicsAsync(
        SocketUserMessage message,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (message.Channel is not SocketTextChannel currentChannel)
        {
            return;
        }

        IEnumerable<SocketTextChannel> routeChannels = currentChannel.Guild.TextChannels
            .Where(channel => channel.CategoryId == currentChannel.CategoryId);

        foreach (SocketTextChannel channel in routeChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!DiscordProjectWorkspaceObserver.TryParseChannelTopic(
                    channel.Topic,
                    out string agentName,
                    out string existingWorkspaceRoot))
            {
                continue;
            }

            if (string.Equals(existingWorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string updatedTopic = DiscordProjectWorkspaceObserver.BuildChannelTopic(agentName, workspaceRoot);
            await channel.ModifyAsync(properties => properties.Topic = updatedTopic);
        }
    }

    private static string BuildAgentChannelHostContext(
        SocketTextChannel guildChannel,
        string agentName,
        string workspaceRoot)
    {
        string categoryName = guildChannel.Category?.Name ?? "uncategorized";
        return
            $"This Discord channel is `{guildChannel.Name}` under category `{categoryName}` and is directly routed to `{agentName}`. " +
            $"Continue using `{workspaceRoot}` as the project working directory unless the user explicitly switches to a different path.";
    }

    private bool RequiresFullAccessForWorkspace(string workspaceRoot)
    {
        return !IsPathWithinRoot(workspaceRoot, _runtime.WorkspaceRoot);
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        string normalizedCandidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));
        string normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private async Task<bool?> ResolveAccessApprovalAsync(
        SocketUserMessage message,
        ApprovalActionKind actionKind,
        string approvalInput,
        string pendingInput,
        string? hostContext = null,
        AgentChannelRoute? route = null)
    {
        string scopedApprovalInput = route is { RequiresFullAccess: true }
            ? $"{route.WorkspaceRoot}\n{approvalInput}"
            : approvalInput;

        if (!RequiresSensitiveLocalAccess(scopedApprovalInput))
        {
            return false;
        }

        lock (_approvalSync)
        {
            if (_approvedFullAccessUsers.Contains(message.Author.Id))
            {
                return true;
            }

            _pendingApprovals[BuildPendingApprovalKey(message)] =
                new PendingApprovalRequest(actionKind, pendingInput, route, hostContext);
        }

        await message.Channel.SendMessageAsync(
            "이 요청은 현재 작업 폴더 밖의 파일이나 다른 프로젝트를 찾고 읽어야 할 수 있어요.\n" +
            "full access로 진행할까요? 같은 요청은 한 번 승인하면 다시 안 물을게요.\n" +
            "`승인` 또는 `취소`라고 답해주세요.");

        return null;
    }

    private async Task HandleWorkspaceDeletionRequestAsync(SocketUserMessage message)
    {
        if (!TryResolveManagedWorkspaceCategory(message, out SocketCategoryChannel? category))
        {
            await message.Channel.SendMessageAsync(
                "워크스페이스 삭제는 `main-codex`나 `sub-codex-*` 같은 현재 프로젝트 채널 안에서만 할 수 있어요.");
            return;
        }

        if (IsWorkspaceRunActive(category.Id))
        {
            await message.Channel.SendMessageAsync(
                "지금은 이 워크스페이스에서 task가 실행 중이라 바로 삭제할 수 없어요. 실행이 끝난 뒤 다시 요청해 주세요.");
            return;
        }

        lock (_approvalSync)
        {
            _pendingWorkspaceDeletes[BuildPendingApprovalKey(message)] =
                new PendingWorkspaceDeleteRequest(category.Id, category.Name);
        }

        await message.Channel.SendMessageAsync(
            $"현재 워크스페이스 `{category.Name}` 전체를 삭제할까요?\n" +
            "카테고리 아래의 `main-codex`, `sub-codex-*` 채널도 함께 삭제됩니다.\n" +
            "`삭제확인` 또는 `취소`라고 답해주세요.");
    }

    private async Task<bool> TryHandleWorkspaceDeleteResponseAsync(SocketUserMessage message)
    {
        PendingWorkspaceDeleteRequest? pendingDelete;

        lock (_approvalSync)
        {
            _pendingWorkspaceDeletes.TryGetValue(BuildPendingApprovalKey(message), out pendingDelete);
        }

        if (pendingDelete is null)
        {
            return false;
        }

        string normalized = NormalizeApprovalDecision(message.Content);
        if (IsWorkspaceDeleteConfirmed(normalized))
        {
            lock (_approvalSync)
            {
                _pendingWorkspaceDeletes.Remove(BuildPendingApprovalKey(message));
            }

            SocketCategoryChannel? category = (message.Channel as SocketGuildChannel)?.Guild.CategoryChannels
                .FirstOrDefault(item => item.Id == pendingDelete.CategoryId);
            if (category is null)
            {
                await message.Channel.SendMessageAsync("삭제하려던 워크스페이스를 찾지 못했어요. 이미 지워졌을 수 있어요.");
                return true;
            }

            if (IsWorkspaceRunActive(category.Id))
            {
                await message.Channel.SendMessageAsync(
                    "확인하는 사이에 새 task가 시작돼서 지금은 삭제할 수 없어요. 실행이 끝난 뒤 다시 요청해 주세요.");
                return true;
            }

            await message.Channel.SendMessageAsync(
                $"`{category.Name}` 워크스페이스를 삭제합니다. 이 채널도 곧 함께 사라져요.");
            await DeleteWorkspaceCategoryAsync(category);
            return true;
        }

        if (IsApprovalRejected(normalized))
        {
            lock (_approvalSync)
            {
                _pendingWorkspaceDeletes.Remove(BuildPendingApprovalKey(message));
            }

            await message.Channel.SendMessageAsync("워크스페이스 삭제를 취소했어요.");
            return true;
        }

        await message.Channel.SendMessageAsync("삭제 대기 중인 요청이 있어요. `삭제확인` 또는 `취소`라고 답해주세요.");
        return true;
    }

    private async Task<bool> TryHandleApprovalResponseAsync(SocketUserMessage message)
    {
        PendingApprovalRequest? pendingRequest;

        lock (_approvalSync)
        {
            _pendingApprovals.TryGetValue(BuildPendingApprovalKey(message), out pendingRequest);
        }

        if (pendingRequest is null)
        {
            return false;
        }

        string normalized = NormalizeApprovalDecision(message.Content);

        if (IsApprovalAccepted(normalized))
        {
            lock (_approvalSync)
            {
                _pendingApprovals.Remove(BuildPendingApprovalKey(message));
                _approvedFullAccessUsers.Add(message.Author.Id);
            }

            await message.Channel.SendMessageAsync(
                "승인 확인했어요. 이번 봇 세션에서는 같은 사용자 요청에 다시 묻지 않고 바로 진행할게요.");
            await ExecuteApprovedRequestAsync(message, pendingRequest);
            return true;
        }

        if (IsApprovalRejected(normalized))
        {
            lock (_approvalSync)
            {
                _pendingApprovals.Remove(BuildPendingApprovalKey(message));
            }

            await message.Channel.SendMessageAsync("취소했어요. 이 요청은 현재 작업 폴더 범위 안에서만 다루거나, 다시 승인해주면 full access로 진행할게요.");
            return true;
        }

        await message.Channel.SendMessageAsync("승인 대기 중인 요청이 있어요. `승인` 또는 `취소`라고 답해주세요.");
        return true;
    }

    private async Task DeleteWorkspaceCategoryAsync(SocketCategoryChannel category)
    {
        lock (_activeRunSync)
        {
            _activeWorkspaceRuns.Remove(category.Id);
        }

        SocketTextChannel[] channels = category.Guild.TextChannels
            .Where(channel => channel.CategoryId == category.Id)
            .OrderByDescending(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (SocketTextChannel channel in channels)
        {
            await channel.DeleteAsync();
        }

        await category.DeleteAsync();
    }

    private async Task ExecuteApprovedRequestAsync(SocketUserMessage message, PendingApprovalRequest pendingRequest)
    {
        AgentChannelRoute? route = pendingRequest.Route;

        if (route is null && TryResolveAgentChannelRoute(message, out AgentChannelRoute? resolvedRoute))
        {
            route = resolvedRoute;
        }

        switch (pendingRequest.ActionKind)
        {
            case ApprovalActionKind.Ask:
                await HandleAskCoreAsync(message, pendingRequest.Input, allowFullAccess: true, route, pendingRequest.HostContext);
                break;
            case ApprovalActionKind.Task:
                await HandleTaskCoreAsync(message, pendingRequest.Input, allowFullAccess: true, pendingRequest.HostContext);
                break;
            case ApprovalActionKind.Adjust:
                if (TryGetActiveWorkspaceRun(message, out ActiveWorkspaceRun? activeRun))
                {
                    await HandleActiveRunAdjustmentAsync(message, pendingRequest.Input, activeRun);
                }
                break;
        }
    }

    private static bool RequiresSensitiveLocalAccess(string input)
    {
        string normalized = NormalizeApprovalDecision(input);

        string[] keywords =
        [
            "경로",
            "위치",
            "폴더",
            "디렉터리",
            "프로젝트 위치",
            "파일 찾아",
            "찾아줘",
            "찾아 봐",
            "검색",
            "로컬",
            "내 컴퓨터",
            "다른 프로젝트",
            "바깥 폴더",
            "작업 루트",
            "워크스페이스",
            "워크스페이스 변경",
            "작업 폴더",
            "외부 폴더",
            "여기로 이동",
            "이동",
            "전환",
            "바꿔",
            "열어",
            "workspace 밖",
            "outside workspace",
            "full access",
            "absolute path",
            "folder",
            "directory",
            "path",
            "workdir",
            "workspace root",
            "locate",
            "find project",
            "search project"
        ];

        return keywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
               LooksLikeLocalPathReference(input);
    }

    private static string BuildPendingApprovalKey(SocketUserMessage message)
    {
        return $"{message.Author.Id}:{message.Channel.Id}";
    }

    private async Task<WorkspaceResolution> ResolveWorkspaceAsync(
        SocketUserMessage message,
        string input,
        bool allowFullAccess,
        string? defaultWorkspaceRoot = null,
        string? defaultHostContext = null)
    {
        string conversationKey = BuildPendingApprovalKey(message);
        string? explicitPath = ExtractLocalPath(input);
        bool hasApprovedFullAccess;
        string? activeWorkspaceRoot;

        lock (_approvalSync)
        {
            hasApprovedFullAccess = _approvedFullAccessUsers.Contains(message.Author.Id);
            _activeWorkspaceRoots.TryGetValue(conversationKey, out activeWorkspaceRoot);
        }

        string? preferredWorkspaceRoot = activeWorkspaceRoot ?? defaultWorkspaceRoot;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string normalizedPath = explicitPath;

            if (Directory.Exists(normalizedPath))
            {
                bool changedWorkspace = !string.Equals(preferredWorkspaceRoot, normalizedPath, StringComparison.OrdinalIgnoreCase);

                lock (_approvalSync)
                {
                    _activeWorkspaceRoots[conversationKey] = normalizedPath;
                }

                if (changedWorkspace)
                {
                    await UpdateAgentChannelWorkspaceTopicsAsync(message, normalizedPath);
                    await message.Channel.SendMessageAsync(
                        $"외부 작업 경로를 확인했어요. 이 채널의 작업 루트를 `{normalizedPath}` 로 전환합니다.");
                }

                return new WorkspaceResolution(
                    normalizedPath,
                    CombineHostContexts(
                        defaultHostContext,
                        $"Host verified that the requested local directory exists and set it as the active working directory: {normalizedPath}."),
                    allowFullAccess || hasApprovedFullAccess,
                    StopProcessing: false);
            }

            if (File.Exists(normalizedPath))
            {
                string parentDirectory = Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
                bool changedWorkspace = !string.Equals(preferredWorkspaceRoot, parentDirectory, StringComparison.OrdinalIgnoreCase);

                lock (_approvalSync)
                {
                    _activeWorkspaceRoots[conversationKey] = parentDirectory;
                }

                if (changedWorkspace)
                {
                    await UpdateAgentChannelWorkspaceTopicsAsync(message, parentDirectory);
                    await message.Channel.SendMessageAsync(
                        $"외부 파일을 확인했어요. 이 채널의 작업 루트를 `{parentDirectory}` 로 전환합니다.");
                }

                return new WorkspaceResolution(
                    parentDirectory,
                    CombineHostContexts(
                        defaultHostContext,
                        $"Host verified that the requested local file exists: {normalizedPath}. Use its parent directory as the working directory: {parentDirectory}."),
                    allowFullAccess || hasApprovedFullAccess,
                    StopProcessing: false);
            }

            await message.Channel.SendMessageAsync($"경로를 직접 확인했는데 찾지 못했어요: `{normalizedPath}`");
            return new WorkspaceResolution(
                null,
                CombineHostContexts(
                    defaultHostContext,
                    $"Host attempted to resolve the requested local path, but it was not found: {normalizedPath}."),
                allowFullAccess || hasApprovedFullAccess,
                StopProcessing: true);
        }

        if (!string.IsNullOrWhiteSpace(preferredWorkspaceRoot))
        {
            return new WorkspaceResolution(
                preferredWorkspaceRoot,
                CombineHostContexts(
                    defaultHostContext,
                    $"Host is continuing in the active local working directory already selected for this channel: {preferredWorkspaceRoot}."),
                UseFullAccess: allowFullAccess || hasApprovedFullAccess,
                StopProcessing: false);
        }

        return new WorkspaceResolution(
            null,
            HostContext: defaultHostContext,
            UseFullAccess: allowFullAccess,
            StopProcessing: false);
    }

    private static string? CombineHostContexts(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first}\n{second}";
    }

    private static string MergeInputWithHostContext(string input, string? hostContext)
    {
        return string.IsNullOrWhiteSpace(hostContext)
            ? input
            : $"{input}\n\n[Discord attachment context]\n{hostContext}";
    }

    private static string? ExtractLocalPath(string input)
    {
        foreach (string line in input.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = line.Trim().Trim('"', '\'');

            if (TryNormalizeLocalPathCandidate(trimmed, out string normalizedPath) &&
                !ContainsLikelySentenceSuffix(trimmed))
            {
                return normalizedPath;
            }

            Match lineTokenMatch = Regex.Match(
                trimmed,
                @"^(?<path>(?:[A-Za-z]:\\|\\\\|%[A-Za-z0-9_]+%\\|Desktop\\|desktop\\|바탕화면\\|~\\|~/)\S+)",
                RegexOptions.IgnoreCase);
            if (lineTokenMatch.Success &&
                TryNormalizeLocalPathCandidate(lineTokenMatch.Groups["path"].Value, out string tokenNormalizedPath))
            {
                return tokenNormalizedPath;
            }
        }

        Match quotedMatch = Regex.Match(input, "\"([^\\r\\n\"]+)\"");
        if (quotedMatch.Success)
        {
            if (TryNormalizeLocalPathCandidate(quotedMatch.Groups[1].Value, out string normalizedPath))
            {
                return normalizedPath;
            }
        }

        Match bareMatch = Regex.Match(input, @"((?:[A-Za-z]:\\|\\\\|%[A-Za-z0-9_]+%\\|Desktop\\|desktop\\|바탕화면\\|~\\|~/)[^\s""']+)");
        if (bareMatch.Success &&
            TryNormalizeLocalPathCandidate(bareMatch.Groups[1].Value, out string bareNormalizedPath))
        {
            return bareNormalizedPath;
        }

        return null;
    }

    private static bool ContainsLikelySentenceSuffix(string value)
    {
        return value.Contains(' ') || value.Contains('\t');
    }

    private static bool LooksLikeLocalPathReference(string input)
    {
        return TryNormalizeLocalPathCandidate(input, out _) ||
               input.ReplaceLineEndings(" ").Contains("\\", StringComparison.Ordinal) &&
               (
                   input.Contains("Desktop\\", StringComparison.OrdinalIgnoreCase) ||
                   input.Contains("바탕화면\\", StringComparison.OrdinalIgnoreCase) ||
                   input.Contains("%USERPROFILE%\\", StringComparison.OrdinalIgnoreCase) ||
                   input.Contains("OneDrive\\Desktop\\", StringComparison.OrdinalIgnoreCase)
               );
    }

    private static bool TryNormalizeLocalPathCandidate(string candidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string trimmed = candidate.Trim().Trim('"', '\'');
        string expanded = Environment.ExpandEnvironmentVariables(trimmed).Replace('/', '\\');

        if (Regex.IsMatch(expanded, @"^[A-Za-z]:\\") || expanded.StartsWith("\\\\", StringComparison.Ordinal))
        {
            normalizedPath = Path.GetFullPath(expanded);
            return true;
        }

        if (expanded.StartsWith("~\\", StringComparison.Ordinal) || expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            normalizedPath = Path.GetFullPath(Path.Combine(userProfile, expanded[2..]));
            return true;
        }

        if (TryResolveDesktopRelativePath(expanded, out normalizedPath))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveDesktopRelativePath(string candidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        string normalizedCandidate = candidate.Replace('/', '\\').TrimStart('\\');
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        string[] prefixes =
        [
            "desktop\\",
            "Desktop\\",
            "바탕화면\\"
        ];

        foreach (string prefix in prefixes)
        {
            if (normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = normalizedCandidate[prefix.Length..];
                normalizedPath = Path.GetFullPath(Path.Combine(desktopPath, relative));
                return true;
            }
        }

        return false;
    }

    private static string NormalizeApprovalDecision(string input)
    {
        return string.Join(
            ' ',
            input
                .ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsApprovalAccepted(string normalized)
    {
        return normalized is "승인" or "허용" or "yes" or "y" or "ok" or "ㅇㅇ";
    }

    private static bool IsApprovalRejected(string normalized)
    {
        return normalized is "취소" or "거절" or "no" or "n" or "ㄴㄴ";
    }

    private static bool IsWorkspaceDeleteConfirmed(string normalized)
    {
        return normalized is "삭제확인" or "삭제 확인" or "confirm delete" or "delete confirm";
    }

    private static string BuildAskStartedMessage(bool allowFullAccess, string? workspaceRoot, string? agentName = null)
    {
        string routeText = string.IsNullOrWhiteSpace(agentName)
            ? "질문 확인했어요."
            : $"`{agentName}`로 라우팅했어요.";
        string workspaceText = string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : $" `{workspaceRoot}` 기준으로";

        return allowFullAccess
            ? $"{routeText}{workspaceText} 승인된 full access로 바로 확인 중입니다."
            : $"{routeText}{workspaceText} 바로 확인 중입니다.";
    }

    private static string BuildRunCompletionSummary(OrchestratorRunResult runResult)
    {
        ExecutionReport report = runResult.Report;

        return
            $"실행 완료\n" +
            $"- Run Id: `{report.RunId}`\n" +
            $"- Project: `{report.ProjectName}`\n" +
            $"- Sub Agents: `{report.SubAgentCount}`\n" +
            $"- Report: `{Path.GetFileName(runResult.Artifacts.TextReportPath)}`\n" +
            "- 서버에 프로젝트 워크스페이스 채널을 생성했습니다.";
    }

    private static string BuildLatestRunMessage(RunHistoryEntry latest)
    {
        return
            $"최근 실행:\n" +
            $"- Run Id: `{latest.RunId}`\n" +
            $"- Project: `{latest.ProjectName}`\n" +
            $"- Generated At: `{latest.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}`";
    }

    private static string BuildHistoryMessage(IReadOnlyList<RunHistoryEntry> history)
    {
        string body = string.Join(
            "\n",
            history.Take(5).Select(entry =>
                $"- `{entry.RunId}` | {entry.ProjectName} | {entry.GeneratedAt:MM-dd HH:mm}"));

        return $"최근 실행 기록:\n{body}";
    }

    private static string BuildRequestMessage(ProjectRequestLoadResult request)
    {
        string deliverables = string.Join("\n", request.Request.Deliverables.Select(item => $"- {item}"));

        return
            $"현재 요청 파일:\n" +
            $"- Project: `{request.Request.Name}`\n" +
            $"- Goal: {request.Request.Goal}\n" +
            $"Deliverables:\n{deliverables}";
    }

    private Task<OrchestratorRunResult> RunTaskInGuildAsync(
        SocketGuild guild,
        string goal,
        bool allowFullAccess,
        DiscordProjectWorkspaceObserver observer,
        string? workspaceRootOverride = null,
        string? hostContext = null,
        IExecutionAdjustmentSource? adjustmentSource = null,
        ulong? requesterUserId = null)
    {
        return _runtime.RunAdHocAsync(goal, observer, allowFullAccess, workspaceRootOverride, hostContext, adjustmentSource);
    }

    private async Task HandleActiveRunAdjustmentAsync(
        SocketUserMessage message,
        string input,
        ActiveWorkspaceRun activeRun)
    {
        activeRun.QueueAdjustment(input);
        await message.Channel.SendMessageAsync(
            "추가 요청 받았어요. 현재 단계가 끝나는 즉시 난도와 예상 소요를 다시 계산해서 계획을 재배치하고, 필요하면 `sub-codex-*` 채널을 동적으로 생성/정리할게요.");
    }

    private bool TryGetActiveWorkspaceRun(
        SocketUserMessage message,
        [NotNullWhen(true)] out ActiveWorkspaceRun? activeRun)
    {
        activeRun = null;

        if (message.Channel is not SocketTextChannel textChannel || textChannel.CategoryId is not ulong categoryId)
        {
            return false;
        }

        lock (_activeRunSync)
        {
            return _activeWorkspaceRuns.TryGetValue(categoryId, out activeRun);
        }
    }

    private bool IsWorkspaceRunActive(ulong categoryId)
    {
        lock (_activeRunSync)
        {
            return _activeWorkspaceRuns.ContainsKey(categoryId);
        }
    }

    private void RegisterActiveWorkspaceRun(ulong categoryId, ActiveWorkspaceRun activeRun)
    {
        lock (_activeRunSync)
        {
            _activeWorkspaceRuns[categoryId] = activeRun;
        }
    }

    private void UnregisterActiveWorkspaceRun(ulong categoryId)
    {
        lock (_activeRunSync)
        {
            _activeWorkspaceRuns.Remove(categoryId);
        }
    }

    private static async Task<DiscordProjectWorkspaceObserver.WorkspaceRouteInfo?> WaitForWorkspaceReadyAsync(
        DiscordProjectWorkspaceObserver observer,
        Task<OrchestratorRunResult> runTask)
    {
        Task<DiscordProjectWorkspaceObserver.WorkspaceRouteInfo> readyTask = observer.WaitForWorkspaceReadyAsync();
        Task completedTask = await Task.WhenAny(runTask, readyTask);
        if (completedTask == readyTask)
        {
            return await readyTask;
        }

        return null;
    }

    private async Task ClearSlashCommandsAsync()
    {
        foreach (SocketGuild guild in _client.Guilds)
        {
            await guild.BulkOverwriteApplicationCommandAsync(Array.Empty<ApplicationCommandProperties>());
        }
    }

    private async Task RegisterSlashCommandsAsync()
    {
        ApplicationCommandProperties[] commands =
        [
            new SlashCommandBuilder()
                .WithName("help")
                .WithDescription("사용 가능한 명령어 목록을 보여줍니다.")
                .Build(),
            new SlashCommandBuilder()
                .WithName("capabilities")
                .WithDescription("이 봇이 할 수 있는 일을 보여줍니다.")
                .Build(),
            new SlashCommandBuilder()
                .WithName("ask")
                .WithDescription("Codex에게 질문합니다.")
                .AddOption("question", ApplicationCommandOptionType.String, "질문 내용", isRequired: true)
                .Build(),
            new SlashCommandBuilder()
                .WithName("task")
                .WithDescription("프로젝트 작업을 시작하고 main/sub codex 채널을 생성합니다.")
                .AddOption("goal", ApplicationCommandOptionType.String, "실행할 목표", isRequired: true)
                .Build(),
            new SlashCommandBuilder()
                .WithName("status")
                .WithDescription("최근 실행 상태를 보여줍니다.")
                .Build(),
            new SlashCommandBuilder()
                .WithName("history")
                .WithDescription("최근 실행 이력을 보여줍니다.")
                .Build(),
            new SlashCommandBuilder()
                .WithName("report")
                .WithDescription("특정 실행 리포트를 가져옵니다.")
                .AddOption("run_id", ApplicationCommandOptionType.String, "실행 ID", isRequired: true)
                .Build(),
            new SlashCommandBuilder()
                .WithName("request")
                .WithDescription("현재 요청 파일 내용을 보여줍니다.")
                .Build()
        ];

        foreach (SocketGuild guild in _client.Guilds)
        {
            await guild.BulkOverwriteApplicationCommandAsync(commands);
        }
    }

    private static string GetRequiredStringOption(SocketSlashCommand command, string optionName)
    {
        return command.Data.Options
            .FirstOrDefault(option => string.Equals(option.Name, optionName, StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString() ?? string.Empty;
    }
}
