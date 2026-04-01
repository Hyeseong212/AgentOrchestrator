namespace AgentOrchestrator.DiscordBot;

public sealed record PreparedMessageInput(
    string BaseInput,
    string? HostContext,
    bool HasAttachments,
    bool HasOriginalText);
