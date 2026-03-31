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
        Task
    }

    private sealed record PendingApprovalRequest(ApprovalActionKind ActionKind, string Input);

    private readonly DiscordSocketClient _client;
    private bool _slashCommandsRegistered;
    private readonly string _commandPrefix;
    private readonly AgentOrchestratorRuntime _runtime;
    private readonly HashSet<string> _approvedAccessRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _approvalSync = new();
    private readonly Dictionary<string, PendingApprovalRequest> _pendingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public DiscordAgentBot(AgentOrchestratorRuntime runtime, DiscordBotSettings settings)
    {
        _runtime = runtime;
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
        _client.SlashCommandExecuted += OnSlashCommandExecutedAsync;

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
        if (!_slashCommandsRegistered)
        {
            await RegisterSlashCommandsAsync();
            _slashCommandsRegistered = true;
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

    private async Task OnMessageReceivedAsync(SocketMessage socketMessage)
    {
        if (socketMessage.Author.IsBot || socketMessage is not SocketUserMessage message)
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
                if (!string.IsNullOrWhiteSpace(input))
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
        bool isTaskRequest = LooksLikeTaskRequest(input);
        bool? allowFullAccess = await ResolveAccessApprovalAsync(
            message,
            isTaskRequest ? ApprovalActionKind.Task : ApprovalActionKind.Ask,
            input);

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

            await HandleTaskCoreAsync(message, input, allowFullAccess.Value);

            return;
        }

        await HandleAskCoreAsync(message, input, allowFullAccess.Value);
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
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

    private async Task HandleTaskAsync(SocketUserMessage message, string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            await message.Channel.SendMessageAsync("사용법: `!task <목표>`");
            return;
        }

        if (LooksLikeQuestion(goal))
        {
            await message.Channel.SendMessageAsync(
                "이 입력은 질문처럼 보여서 답변 모드로 처리할게요. 작업 실행은 `!task <목표>`로 쓰고, 질문은 `!ask <질문>`으로 물어보면 됩니다.");
            bool? questionFullAccess = await ResolveAccessApprovalAsync(message, ApprovalActionKind.Ask, goal);
            if (questionFullAccess is null)
            {
                return;
            }

            await HandleAskCoreAsync(message, goal, questionFullAccess.Value);
            return;
        }

        bool? allowFullAccess = await ResolveAccessApprovalAsync(message, ApprovalActionKind.Task, goal);
        if (allowFullAccess is null)
        {
            return;
        }

        await HandleTaskCoreAsync(message, goal, allowFullAccess.Value);
    }

    private async Task HandleTaskCoreAsync(SocketUserMessage message, string goal, bool allowFullAccess)
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

            await message.Channel.SendMessageAsync(
                $"작업 실행을 시작합니다: `{goal}`\n" +
                "프로젝트 카테고리와 `main-codex`, `sub-codex-*` 채널을 만들고 진행 기록을 남길게요." +
                (allowFullAccess ? "\n이번 실행은 승인된 full access 모드로 진행합니다." : string.Empty));

            OrchestratorRunResult runResult = await RunTaskInGuildAsync(guildChannel.Guild, goal, allowFullAccess);
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
                "프로젝트 카테고리와 `main-codex`, `sub-codex-*` 채널을 만들고 진행 기록을 남길게요.");

            OrchestratorRunResult runResult = await RunTaskInGuildAsync(guildChannel.Guild, goal, allowFullAccess: false);
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
        if (string.IsNullOrWhiteSpace(question))
        {
            await message.Channel.SendMessageAsync("사용법: `!ask <질문>`");
            return;
        }

        bool? allowFullAccess = await ResolveAccessApprovalAsync(message, ApprovalActionKind.Ask, question);
        if (allowFullAccess is null)
        {
            return;
        }

        await HandleAskCoreAsync(message, question, allowFullAccess.Value);
    }

    private async Task HandleAskCoreAsync(SocketUserMessage message, string question, bool allowFullAccess)
    {
        using IDisposable typing = message.Channel.EnterTypingState();
        CodexTaskResponse answer = await _runtime.AskAsync(question, allowFullAccess);
        await message.Channel.SendMessageAsync(answer.Message);
    }

    private async Task HandleAskSlashAsync(SocketSlashCommand command, string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            await command.FollowupAsync("Usage: `/ask question:<질문>`");
            return;
        }

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
            "- 슬래시 커맨드 `/`를 입력하면 Discord 입력창에서 명령 목록이 바로 보여요.\n" +
            "- `일반/general`, `main-codex`, DM, 또는 봇을 멘션한 메시지에서는 그냥 대화해도 됩니다. 질문이면 답하고, 작업 요청처럼 보이면 자동으로 프로젝트 워크스페이스를 만들고 진행해요.\n" +
            $"- `{_commandPrefix}ask <question>`\n" +
            $"- `{_commandPrefix}capabilities`\n" +
            $"- `{_commandPrefix}task <goal>` 프로젝트 카테고리와 main/sub codex 채널 생성\n" +
            $"- `{_commandPrefix}status`\n" +
            $"- `{_commandPrefix}history`\n" +
            $"- `{_commandPrefix}report <runId>`\n" +
            $"- `{_commandPrefix}request`\n" +
            $"- `{_commandPrefix}help`\n" +
            "또는 `/ask`, `/task`, `/status`, `/history`, `/report`, `/request`, `/help`를 사용할 수 있어요.";
    }

    private string BuildCapabilitiesText()
    {
        return
            "지금 이 봇은 두 가지를 할 수 있어요.\n" +
            $"- `{_commandPrefix}task <목표>` 또는 `/task`: 프로젝트 카테고리와 `main-codex`/`sub-codex-*` 채널을 만들고, 목표를 작업으로 분해해서 진행 기록과 report를 남김\n" +
            $"- `{_commandPrefix}ask <질문>` 또는 `/ask`: 바로 질문에 짧게 답변\n" +
            "- 일반 채널에서 그냥 대화해도 돼요. 질문이면 답하고, 작업 요청이면 스스로 작업으로 전환해요.\n" +
            $"- `{_commandPrefix}status`, `{_commandPrefix}history`, `{_commandPrefix}report <runId>`, `{_commandPrefix}request`도 사용할 수 있어요.\n" +
            "- 입력창에서 명령 목록 자동완성을 보려면 `!`가 아니라 `/`를 사용해야 해요.";
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

    private async Task<bool?> ResolveAccessApprovalAsync(
        SocketUserMessage message,
        ApprovalActionKind actionKind,
        string input)
    {
        if (!RequiresSensitiveLocalAccess(input))
        {
            return false;
        }

        string approvalKey = BuildApprovalCacheKey(message.Author.Id, actionKind, input);

        lock (_approvalSync)
        {
            if (_approvedAccessRequests.Contains(approvalKey))
            {
                return true;
            }

            _pendingApprovals[BuildPendingApprovalKey(message)] = new PendingApprovalRequest(actionKind, input);
        }

        await message.Channel.SendMessageAsync(
            "이 요청은 현재 작업 폴더 밖의 파일이나 다른 프로젝트를 찾고 읽어야 할 수 있어요.\n" +
            "full access로 진행할까요? 같은 요청은 한 번 승인하면 다시 안 물을게요.\n" +
            "`승인` 또는 `취소`라고 답해주세요.");

        return null;
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
                _approvedAccessRequests.Add(
                    BuildApprovalCacheKey(message.Author.Id, pendingRequest.ActionKind, pendingRequest.Input));
            }

            await message.Channel.SendMessageAsync(
                "승인 확인했어요. 같은 요청은 이번 봇 세션에서 다시 묻지 않고 바로 진행할게요.");
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

    private async Task ExecuteApprovedRequestAsync(SocketUserMessage message, PendingApprovalRequest pendingRequest)
    {
        switch (pendingRequest.ActionKind)
        {
            case ApprovalActionKind.Ask:
                await HandleAskCoreAsync(message, pendingRequest.Input, allowFullAccess: true);
                break;
            case ApprovalActionKind.Task:
                await HandleTaskCoreAsync(message, pendingRequest.Input, allowFullAccess: true);
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
            "workspace 밖",
            "outside workspace",
            "full access",
            "absolute path",
            "folder",
            "directory",
            "path",
            "locate",
            "find project",
            "search project"
        ];

        return keywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
               normalized.Contains(":\\", StringComparison.Ordinal) ||
               normalized.Contains(":/", StringComparison.Ordinal) ||
               normalized.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static string BuildPendingApprovalKey(SocketUserMessage message)
    {
        return $"{message.Author.Id}:{message.Channel.Id}";
    }

    private static string BuildApprovalCacheKey(ulong userId, ApprovalActionKind actionKind, string input)
    {
        return $"{userId}:{actionKind}:{NormalizeApprovalDecision(input)}";
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

    private Task<OrchestratorRunResult> RunTaskInGuildAsync(SocketGuild guild, string goal, bool allowFullAccess)
    {
        var observer = new DiscordProjectWorkspaceObserver(guild);
        return _runtime.RunAdHocAsync(goal, observer, allowFullAccess);
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
