using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.Models;

namespace AgentOrchestrator.Services;

public sealed class CodexCliRunner
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly bool _allowFullAccess;
    private readonly string _codexExecutablePath;
    private readonly string? _hostContext;
    private readonly string _workspaceRoot;

    public CodexCliRunner(
        string codexExecutablePath,
        string workspaceRoot,
        bool allowFullAccess = false,
        string? hostContext = null)
    {
        _codexExecutablePath = codexExecutablePath;
        _workspaceRoot = workspaceRoot;
        _allowFullAccess = allowFullAccess;
        _hostContext = hostContext;
    }

    public async Task<CodexTaskResponse> ExecuteTaskAsync(
        string agentName,
        AgentTask task,
        int attempt,
        CancellationToken cancellationToken)
    {
        string prompt = BuildTaskPrompt(agentName, task, attempt);
        return await ExecutePromptAsync(prompt, cancellationToken);
    }

    public async Task<CodexTaskResponse> ExecutePromptAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        string tempOutputPath = Path.Combine(Path.GetTempPath(), $"codex-agent-{Guid.NewGuid():N}.txt");

        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _codexExecutablePath,
                WorkingDirectory = _workspaceRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (_allowFullAccess)
            {
                // In non-interactive exec mode, -a never/-s danger-full-access can still leave
                // shell commands blocked by policy. The explicit bypass flag is what actually
                // enables remote local inspection after the Discord user has approved full access.
                processStartInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
            }

            processStartInfo.ArgumentList.Add("exec");
            processStartInfo.ArgumentList.Add("--skip-git-repo-check");
            processStartInfo.ArgumentList.Add("--json");
            processStartInfo.ArgumentList.Add("-C");
            processStartInfo.ArgumentList.Add(_workspaceRoot);
            processStartInfo.ArgumentList.Add("-o");
            processStartInfo.ArgumentList.Add(tempOutputPath);
            processStartInfo.ArgumentList.Add("-");

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            await process.StandardInput.WriteAsync(prompt);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Codex CLI exited with code {process.ExitCode}: {TrimDiagnostic(stderr)}");
            }

            if (!File.Exists(tempOutputPath))
            {
                throw new InvalidOperationException("Codex CLI did not produce an output message file.");
            }

            string message = await File.ReadAllTextAsync(tempOutputPath, Utf8WithoutBom, cancellationToken);
            (string? model, int? tokensUsed) = ParseJsonEvents(stdout);

            return new CodexTaskResponse(
                Message: string.IsNullOrWhiteSpace(message) ? "Codex returned an empty response." : message.Trim(),
                Model: model,
                TokensUsed: tokensUsed,
                RawDiagnostic: string.IsNullOrWhiteSpace(stderr) ? null : TrimDiagnostic(stderr));
        }
        finally
        {
            try
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
            }
            catch
            {
            }
        }
    }

    private static (string? Model, int? TokensUsed) ParseJsonEvents(string stdout)
    {
        string? model = null;
        int? tokensUsed = null;

        foreach (string line in EnumerateJsonLines(stdout))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                model ??= FindFirstString(root, "model", "model_slug", "model_name");

                if (TryFindUsage(root, out JsonElement usageElement))
                {
                    int inputTokens = ReadInt32(usageElement, "input_tokens");
                    int cachedInputTokens = ReadInt32(usageElement, "cached_input_tokens");
                    int outputTokens = ReadInt32(usageElement, "output_tokens");
                    int totalTokens = ReadInt32(usageElement, "total_tokens");

                    tokensUsed = totalTokens > 0
                        ? totalTokens
                        : inputTokens + cachedInputTokens + outputTokens;
                }
            }
            catch
            {
            }
        }

        return (model, tokensUsed);
    }

    private static IEnumerable<string> EnumerateJsonLines(string stdout)
    {
        using var reader = new StringReader(stdout);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static bool TryFindUsage(JsonElement element, out JsonElement usageElement)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("usage") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    usageElement = property.Value;
                    return true;
                }

                if (TryFindUsage(property.Value, out usageElement))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindUsage(item, out usageElement))
                {
                    return true;
                }
            }
        }

        usageElement = default;
        return false;
    }

    private static string? FindFirstString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (string propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out JsonElement propertyValue) &&
                    propertyValue.ValueKind == JsonValueKind.String)
                {
                    string? value = propertyValue.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? nestedValue = FindFirstString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nestedValue = FindFirstString(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement propertyValue))
        {
            return 0;
        }

        if (propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetInt32(out int numericValue))
        {
            return numericValue;
        }

        if (propertyValue.ValueKind == JsonValueKind.String &&
            int.TryParse(propertyValue.GetString(), out int stringValue))
        {
            return stringValue;
        }

        return 0;
    }

    private string BuildTaskPrompt(string agentName, AgentTask task, int attempt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You are {agentName}, a focused sub-agent inside a larger orchestration system.");
        builder.AppendLine("Reply in Korean only.");
        builder.AppendLine("Return a concise implementation summary for the assigned task.");
        builder.AppendLine("Do not mention that you are an AI model.");
        builder.AppendLine("Use plain text only, 4 sentences max.");
        builder.AppendLine("The machine is Windows-first. Prefer cmd-compatible commands such as dir, type, where, tree /f, and if exist, or explicitly use powershell -NoProfile -Command for PowerShell cmdlets. Never treat cmd.exe /c Get-ChildItem as a valid command.");
        builder.AppendLine("If host context includes downloaded Discord attachments, use the provided extracted summaries for PPTX/XLSX/PDF files and directly inspect local image paths for visual details when needed.");
        builder.AppendLine($"Working directory: {_workspaceRoot}");
        if (!string.IsNullOrWhiteSpace(_hostContext))
        {
            builder.AppendLine($"Host context: {_hostContext}");
        }
        if (_allowFullAccess)
        {
            builder.AppendLine("Full local access has already been approved for this run. You may inspect local files and folders outside the current workspace when needed. Attempt the inspection yourself before asking the user to paste listings, and do not claim policy blocked access unless a command actually fails.");
        }
        builder.AppendLine();
        builder.AppendLine($"Task Id: {task.Id}");
        builder.AppendLine($"Task Title: {task.Title}");
        builder.AppendLine($"Task Priority: {task.Priority}");
        builder.AppendLine($"Task Lane: {task.ExecutionLaneLabel}");
        builder.AppendLine($"Task Phase: {task.Phase}");
        builder.AppendLine($"Estimated Minutes: {task.EstimatedMinutes}");
        builder.AppendLine($"Attempt: {attempt}");
        builder.AppendLine($"Task Description: {task.Description}");
        builder.AppendLine();
        builder.AppendLine("Explain in Korean:");
        builder.AppendLine("1. What the task means in the current project.");
        builder.AppendLine("2. What should be done next.");
        builder.AppendLine("3. Any risk or dependency to watch.");
        return builder.ToString();
    }

    private static string TrimDiagnostic(string stderr)
    {
        string[] lines = stderr
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Contains("<html>", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();

        return lines.Length == 0 ? "No diagnostic output." : string.Join(" | ", lines);
    }
}
