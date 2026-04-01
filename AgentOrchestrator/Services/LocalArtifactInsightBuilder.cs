using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using AgentOrchestrator.Models;
using UglyToad.PdfPig;

namespace AgentOrchestrator.Services;

public sealed class LocalArtifactInsightBuilder
{
    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".jsonl", ".yaml", ".yml", ".xml", ".csv", ".log",
        ".cs", ".csproj", ".sln", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".css", ".scss", ".sql", ".bat", ".cmd", ".ps1", ".sh", ".ini", ".toml"
    ];

    private static readonly HashSet<string> ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    ];

    public async Task<IReadOnlyList<LocalArtifactInsight>> BuildAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var insights = new List<LocalArtifactInsight>();

        foreach (string filePath in filePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                continue;
            }

            insights.Add(await BuildSingleAsync(filePath, cancellationToken));
        }

        return insights;
    }

    private async Task<LocalArtifactInsight> BuildSingleAsync(string filePath, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(filePath);
        string normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.ToLowerInvariant();
        string fileName = Path.GetFileName(filePath);

        try
        {
            if (ImageExtensions.Contains(normalizedExtension))
            {
                return BuildImageInsight(filePath, fileName);
            }

            if (string.Equals(normalizedExtension, ".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return new LocalArtifactInsight(
                    filePath,
                    fileName,
                    "PowerPoint",
                    Limit(ExtractPowerPointSummary(filePath)));
            }

            if (string.Equals(normalizedExtension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedExtension, ".xlsm", StringComparison.OrdinalIgnoreCase))
            {
                return new LocalArtifactInsight(
                    filePath,
                    fileName,
                    "Excel",
                    Limit(ExtractSpreadsheetSummary(filePath)));
            }

            if (string.Equals(normalizedExtension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return new LocalArtifactInsight(
                    filePath,
                    fileName,
                    "PDF",
                    Limit(ExtractPdfSummary(filePath)));
            }

            if (TextExtensions.Contains(normalizedExtension))
            {
                return new LocalArtifactInsight(
                    filePath,
                    fileName,
                    "Text",
                    Limit(await ExtractTextSummaryAsync(filePath, cancellationToken)));
            }

            return new LocalArtifactInsight(
                filePath,
                fileName,
                "Binary",
                BuildGenericBinarySummary(filePath));
        }
        catch (Exception exception)
        {
            return new LocalArtifactInsight(
                filePath,
                fileName,
                "Unknown",
                $"추출 중 오류가 발생했습니다: {exception.Message}");
        }
    }

    private static LocalArtifactInsight BuildImageInsight(string filePath, string fileName)
    {
        string sizeText = FormatFileSize(new FileInfo(filePath).Length);

        return new LocalArtifactInsight(
            filePath,
            fileName,
            "Image",
            $"이미지 파일입니다. 파일 크기: {sizeText}. 로컬 경로의 이미지를 직접 열어서 시각 정보를 반영하세요.",
            RequiresDirectInspection: true);
    }

    private static string ExtractPowerPointSummary(string filePath)
    {
        using PresentationDocument document = PresentationDocument.Open(filePath, false);
        PresentationPart? presentationPart = document.PresentationPart;
        SlideIdList? slideIdList = presentationPart?.Presentation?.SlideIdList;

        if (presentationPart is null || slideIdList is null)
        {
            return "슬라이드 정보를 찾지 못했습니다.";
        }

        var builder = new StringBuilder();
        int slideCount = slideIdList.Elements<SlideId>().Count();
        builder.AppendLine($"슬라이드 수: {slideCount}");

        int slideNumber = 0;
        foreach (SlideId slideId in slideIdList.Elements<SlideId>().Take(6))
        {
            slideNumber++;
            string? relationshipId = slideId.RelationshipId?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId))
            {
                continue;
            }

            if (presentationPart.GetPartById(relationshipId) is not SlidePart slidePart ||
                slidePart.Slide is null)
            {
                continue;
            }

            string slideText = NormalizeWhitespace(
                string.Join(" ", slidePart.Slide.Descendants<A.Text>().Select(text => text.Text)));

            if (string.IsNullOrWhiteSpace(slideText))
            {
                continue;
            }

            builder.AppendLine($"슬라이드 {slideNumber}: {slideText}");
        }

        return builder.ToString().Trim();
    }

    private static string ExtractSpreadsheetSummary(string filePath)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(filePath, false);
        WorkbookPart? workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
        {
            return "워크북 시트 정보를 찾지 못했습니다.";
        }

        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var builder = new StringBuilder();
        IEnumerable<Sheet> sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().Take(4);

        foreach (Sheet sheet in sheets)
        {
            if (sheet.Id?.Value is null)
            {
                continue;
            }

            if (workbookPart.GetPartById(sheet.Id.Value) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            builder.AppendLine($"시트: {sheet.Name?.Value ?? "Unnamed"}");
            SheetData? sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();
            IEnumerable<Row> rows = sheetData?.Elements<Row>().Take(8)
                ?? Enumerable.Empty<Row>();

            foreach (Row row in rows)
            {
                string[] values = row.Elements<Cell>()
                    .Take(8)
                    .Select(cell => $"{cell.CellReference?.Value ?? "Cell"}={NormalizeWhitespace(ReadCellValue(cell, sharedStrings))}")
                    .Where(value => !value.EndsWith("=", StringComparison.Ordinal))
                    .ToArray();

                if (values.Length > 0)
                {
                    builder.AppendLine(string.Join(" | ", values));
                }
            }
        }

        string summary = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(summary) ? "셀 내용을 읽지 못했습니다." : summary;
    }

    private static string ReadCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.InnerText ?? cell.InnerText, out int sharedIndex) &&
            sharedStrings is not null)
        {
            SharedStringItem? item = sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(sharedIndex);
            return item?.InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return (cell.CellValue?.InnerText ?? string.Empty) == "1" ? "TRUE" : "FALSE";
        }

        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static string ExtractPdfSummary(string filePath)
    {
        using PdfDocument document = PdfDocument.Open(filePath);
        var builder = new StringBuilder();
        builder.AppendLine($"페이지 수: {document.NumberOfPages}");

        for (int pageNumber = 1; pageNumber <= Math.Min(document.NumberOfPages, 4); pageNumber++)
        {
            string pageText = NormalizeWhitespace(document.GetPage(pageNumber).Text);
            if (string.IsNullOrWhiteSpace(pageText))
            {
                continue;
            }

            builder.AppendLine($"페이지 {pageNumber}: {pageText}");
        }

        return builder.ToString().Trim();
    }

    private static async Task<string> ExtractTextSummaryAsync(string filePath, CancellationToken cancellationToken)
    {
        string contents = await File.ReadAllTextAsync(filePath, cancellationToken);
        return NormalizeWhitespace(contents);
    }

    private static string BuildGenericBinarySummary(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return $"이진 파일입니다. 확장자: {fileInfo.Extension}, 파일 크기: {FormatFileSize(fileInfo.Length)}.";
    }

    private static string NormalizeWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            input.ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Limit(string text, int maxLength = 2400)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "추출된 텍스트가 비어 있습니다.";
        }

        string trimmed = text.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : $"{trimmed[..maxLength].TrimEnd()} ...(생략)";
    }

    private static string FormatFileSize(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }
}
