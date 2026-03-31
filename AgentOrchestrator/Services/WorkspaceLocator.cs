namespace AgentOrchestrator.Services;

public sealed class WorkspaceLocator
{
    public string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AgentOrchestrator.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(FindProjectRoot());

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.GetFiles(current.FullName, "*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return FindProjectRoot();
    }
}
