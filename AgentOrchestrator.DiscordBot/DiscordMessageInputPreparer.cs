using System.Net.Http;
using System.Text;
using AgentOrchestrator.Models;
using AgentOrchestrator.Services;
using Discord;
using Discord.WebSocket;

namespace AgentOrchestrator.DiscordBot;

public sealed class DiscordMessageInputPreparer
{
    private static readonly HttpClient HttpClient = new();
    private readonly LocalArtifactInsightBuilder _artifactInsightBuilder;
    private readonly string _storageRoot;

    public DiscordMessageInputPreparer(string storageRoot, LocalArtifactInsightBuilder artifactInsightBuilder)
    {
        _storageRoot = storageRoot;
        _artifactInsightBuilder = artifactInsightBuilder;
    }

    public async Task<PreparedMessageInput> PrepareAsync(
        SocketUserMessage message,
        string input,
        CancellationToken cancellationToken = default)
    {
        string trimmedInput = input.Trim();
        bool hasOriginalText = !string.IsNullOrWhiteSpace(trimmedInput);
        bool hasAttachments = message.Attachments.Count > 0;
        string baseInput = hasOriginalText
            ? trimmedInput
            : hasAttachments
                ? "첨부파일을 확인해줘"
                : string.Empty;

        if (!hasAttachments)
        {
            return new PreparedMessageInput(baseInput, HostContext: null, HasAttachments: false, hasOriginalText);
        }

        string targetDirectory = Path.Combine(
            _storageRoot,
            ".codex-discord",
            "attachments",
            message.Channel.Id.ToString(),
            message.Id.ToString());
        Directory.CreateDirectory(targetDirectory);

        var downloadedFiles = new List<(Attachment Attachment, string LocalPath)>();
        var notes = new List<string>();

        foreach (Attachment attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string targetPath = BuildUniquePath(targetDirectory, attachment.Filename);

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(
                    attachment.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                await using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream targetStream = File.Create(targetPath);
                await sourceStream.CopyToAsync(targetStream, cancellationToken);
                downloadedFiles.Add((attachment, targetPath));
            }
            catch (Exception exception)
            {
                notes.Add($"- {attachment.Filename}: 다운로드 실패 ({exception.Message})");
            }
        }

        IReadOnlyList<LocalArtifactInsight> insights = await _artifactInsightBuilder.BuildAsync(
            downloadedFiles.Select(item => item.LocalPath),
            cancellationToken);
        string hostContext = BuildHostContext(downloadedFiles, insights, notes);

        return new PreparedMessageInput(
            baseInput,
            string.IsNullOrWhiteSpace(hostContext) ? null : hostContext,
            HasAttachments: hasAttachments,
            hasOriginalText);
    }

    private static string BuildHostContext(
        IReadOnlyList<(Attachment Attachment, string LocalPath)> downloadedFiles,
        IReadOnlyList<LocalArtifactInsight> insights,
        IReadOnlyList<string> notes)
    {
        if (downloadedFiles.Count == 0 && notes.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Discord attachment context:");

        foreach ((Attachment attachment, string localPath) in downloadedFiles)
        {
            LocalArtifactInsight? insight = insights.FirstOrDefault(item =>
                string.Equals(item.FullPath, localPath, StringComparison.OrdinalIgnoreCase));

            builder.AppendLine($"- 원본 파일명: {attachment.Filename}");
            builder.AppendLine($"  저장 경로: {localPath}");
            builder.AppendLine($"  종류: {insight?.Kind ?? attachment.ContentType ?? "Attachment"}");
            builder.AppendLine($"  요약: {insight?.Summary ?? "파일을 저장했지만 추출 요약은 만들지 못했습니다."}");
        }

        foreach (string note in notes)
        {
            builder.AppendLine(note);
        }

        string context = builder.ToString().Trim();
        return context.Length <= 8000
            ? context
            : $"{context[..8000].TrimEnd()} ...(생략)";
    }

    private static string BuildUniquePath(string directoryPath, string originalFileName)
    {
        string sanitizedFileName = SanitizeFileName(originalFileName);
        string baseName = Path.GetFileNameWithoutExtension(sanitizedFileName);
        string extension = Path.GetExtension(sanitizedFileName);
        string candidatePath = Path.Combine(directoryPath, sanitizedFileName);
        int suffix = 1;

        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(directoryPath, $"{baseName}-{suffix}{extension}");
            suffix++;
        }

        return candidatePath;
    }

    private static string SanitizeFileName(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);

        foreach (char character in fileName)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment.bin" : sanitized;
    }
}
