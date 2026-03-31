namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordBotSettings
{
    public string BotToken { get; init; } = string.Empty;
    public string CommandPrefix { get; init; } = "!";
}
