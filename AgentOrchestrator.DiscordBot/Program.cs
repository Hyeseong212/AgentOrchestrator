using AgentOrchestrator.Services;
using Microsoft.Extensions.Configuration;

namespace AgentOrchestrator.DiscordBot;

internal static class Program
{
    private static async Task Main()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        DiscordBotSettings settings = configuration
            .GetSection("Discord")
            .Get<DiscordBotSettings>() ?? new DiscordBotSettings();

        if (string.IsNullOrWhiteSpace(settings.BotToken))
        {
            throw new InvalidOperationException(
                "Discord bot token is missing. Set Discord:BotToken in appsettings.Local.json or the DISCORD__BOTTOKEN environment variable.");
        }

        var runtime = new AgentOrchestratorRuntime();
        var bot = new DiscordAgentBot(runtime, settings);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await bot.RunAsync(shutdown.Token);
    }
}
