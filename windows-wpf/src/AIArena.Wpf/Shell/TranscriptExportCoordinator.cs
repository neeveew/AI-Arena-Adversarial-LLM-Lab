using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AIArena.Wpf.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class TranscriptExportCoordinator
{
    private readonly Window owner;
    private readonly TextBlock statusText;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<IReadOnlyList<TranscriptMessage>> renderedMessages;
    private readonly Func<IEnumerable<TranscriptMessage>, IEnumerable<TranscriptMessage>> filterMessages;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    public TranscriptExportCoordinator(
        Window owner,
        TextBlock statusText,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<IReadOnlyList<TranscriptMessage>> renderedMessages,
        Func<IEnumerable<TranscriptMessage>, IEnumerable<TranscriptMessage>> filterMessages,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.statusText = statusText;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.renderedMessages = renderedMessages;
        this.filterMessages = filterMessages;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public void CopyMessage(TranscriptMessage message)
    {
        if (isArenaBusy())
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.Text);
            SetRunStatus($"Copied turn {message.Turn}.");
        }
        catch (Exception ex)
        {
            SetRunStatus($"Copy failed: {ex.Message}");
        }
    }

    public void CopyInternetUrl(TranscriptMessage message)
    {
        if (isArenaBusy() || string.IsNullOrWhiteSpace(message.InternetUrl))
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.InternetUrl);
            SetRunStatus($"Copied URL from turn {message.Turn}.");
        }
        catch (Exception ex)
        {
            SetRunStatus($"Copy URL failed: {ex.Message}");
        }
    }

    public void ExportTranscript()
    {
        var session = activeSession();
        if (session is null)
        {
            SetRunStatus("No active session to export.");
            SetExportStatus("Export unavailable", "No active session to export.");
            return;
        }

        var allMessages = renderedMessages();
        var visibleMessages = filterMessages(allMessages)
            .OrderBy(message => message.Turn)
            .ToArray();
        var messages = visibleMessages.Length > 0
            ? visibleMessages
            : allMessages.OrderBy(message => message.Turn).ToArray();
        if (messages.Length == 0)
        {
            SetRunStatus("No transcript messages to export.");
            SetExportStatus("No transcript to export", "No transcript messages to export.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            Filter = "Markdown transcript (*.md)|*.md|Text transcript (*.txt)|*.txt",
            FileName = $"AI Arena - {SafeFilePart(session.Id)} - transcript.md",
            AddExtension = true,
            DefaultExt = ".md"
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            var markdown = BuildTranscriptExport(session.Id, messages);
            File.WriteAllText(dialog.FileName, markdown);
            var fileName = Path.GetFileName(dialog.FileName);
            var scope = visibleMessages.Length > 0 ? "visible" : "all";
            SetRunStatus($"Exported {messages.Length} {scope} transcript message(s) to {fileName}.");
            SetExportStatus($"Exported {messages.Length} message(s)", dialog.FileName);
        }
        catch (Exception ex)
        {
            SetRunStatus($"Export failed: {ex.Message}");
            SetExportStatus("Export failed", $"Export failed: {ex.Message}");
        }
    }

    private void SetRunStatus(string status)
    {
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    private void SetExportStatus(string text, string tooltip)
    {
        statusText.Text = text;
        statusText.ToolTip = tooltip;
    }

    private static string BuildTranscriptExport(string sessionId, IReadOnlyList<TranscriptMessage> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# AI Arena Transcript - {sessionId}");
        builder.AppendLine();
        builder.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Visible messages: {messages.Count}");
        builder.AppendLine();

        foreach (var message in messages)
        {
            builder.AppendLine($"## Turn {message.Turn} - {message.Speaker} - {message.Status}");
            if (!string.IsNullOrWhiteSpace(message.Model))
            {
                builder.AppendLine($"Model: `{message.Model}`");
            }

            var stats = string.Join(
                " | ",
                new[]
                {
                    message.LatencyMs > 0 ? $"Generated: {FormatDuration(message.LatencyMs)}" : "",
                    message.CompletionTokens > 0 ? $"Tokens: {FormatCompactNumber(message.CompletionTokens)}" : "",
                    message.PromptTokens > 0 ? $"Context: {FormatCompactNumber(message.PromptTokens)}" : ""
                }.Where(item => !string.IsNullOrWhiteSpace(item)));
            if (!string.IsNullOrWhiteSpace(stats))
            {
                builder.AppendLine(stats);
            }

            builder.AppendLine();
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text.Trim());
            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                builder.AppendLine();
                builder.AppendLine("<details><summary>Model reasoning</summary>");
                builder.AppendLine();
                builder.AppendLine(message.Reasoning.Trim());
                builder.AppendLine();
                builder.AppendLine("</details>");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }

    private static string FormatDuration(int latencyMs)
    {
        if (latencyMs <= 0)
        {
            return "time unknown";
        }

        return latencyMs < 1000
            ? $"{latencyMs} ms"
            : $"{latencyMs / 1000.0:0.0}s";
    }

    private static string FormatCompactNumber(int value)
    {
        return value >= 1000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
