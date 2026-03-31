namespace AgentOrchestrator.Services;

public sealed class CodexCliLocator
{
    public string Locate()
    {
        string? environmentPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        string extensionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode",
            "extensions");

        if (Directory.Exists(extensionsRoot))
        {
            string? discoveredPath = Directory
                .EnumerateDirectories(extensionsRoot, "openai.chatgpt-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "bin", "windows-x86_64", "codex.exe"))
                .FirstOrDefault(File.Exists);

            if (discoveredPath is not null)
            {
                return discoveredPath;
            }
        }

        string expectedPath = Path.Combine(
            extensionsRoot,
            "openai.chatgpt-*",
            "bin",
            "windows-x86_64",
            "codex.exe");

        throw new FileNotFoundException(
            "Unable to locate codex.exe from the installed VS Code extension. Set CODEX_CLI_PATH to override discovery.",
            expectedPath);
    }
}
