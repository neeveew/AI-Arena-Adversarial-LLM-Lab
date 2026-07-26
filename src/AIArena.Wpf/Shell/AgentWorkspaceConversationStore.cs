using System.Globalization;
using AIArena.Wpf.Services;

using AgentWorkspaceMessage = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceMessage;

namespace AIArena.Wpf;

/// <summary>
/// Pure Agent workspace conversation persistence policy. The coordinator renders
/// WPF cards; this store owns message normalization, workspace matching, and caps.
/// </summary>
internal static class AgentWorkspaceConversationStore
{
    internal const int MaxPersistedMessages = 80;

    internal static bool WorkspaceMatches(string sessionWorkspacePath, string currentWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(sessionWorkspacePath) || string.IsNullOrWhiteSpace(currentWorkspacePath))
        {
            return false;
        }

        var normalized = AgentWorkspaceCommand.NormalizeWorkspacePath(sessionWorkspacePath, out var error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return normalized.Equals(currentWorkspacePath, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<AgentWorkspaceMessage> RestoreMessages(
        IReadOnlyList<WpfAgentWorkspaceMessage> savedMessages,
        string sessionWorkspacePath,
        string currentWorkspacePath,
        DateTimeOffset fallbackCreatedAt)
    {
        if (savedMessages.Count == 0 || !WorkspaceMatches(sessionWorkspacePath, currentWorkspacePath))
        {
            return [];
        }

        return savedMessages
            .TakeLast(MaxPersistedMessages)
            .Select(message => FromPersistedMessage(message, fallbackCreatedAt))
            .ToArray();
    }

    internal static List<WpfAgentWorkspaceMessage> PersistedMessages(IEnumerable<AgentWorkspaceMessage> messages)
    {
        return messages
            .TakeLast(MaxPersistedMessages)
            .Select(ToPersistedMessage)
            .ToList();
    }

    internal static string RestoreActivityDetail(int count)
    {
        return $"{count.ToString(CultureInfo.InvariantCulture)} Agent message{(count == 1 ? "" : "s")} restored.";
    }

    internal static WpfAgentWorkspaceMessage ToPersistedMessage(AgentWorkspaceMessage message)
    {
        return new WpfAgentWorkspaceMessage
        {
            RoleId = message.RoleId,
            Title = message.Title,
            Body = message.Body,
            Kind = message.Kind,
            Model = message.Model,
            CreatedAt = message.CreatedAt
        };
    }

    private static AgentWorkspaceMessage FromPersistedMessage(WpfAgentWorkspaceMessage saved, DateTimeOffset fallbackCreatedAt)
    {
        return new AgentWorkspaceMessage(
            string.IsNullOrWhiteSpace(saved.RoleId) ? "system" : saved.RoleId,
            string.IsNullOrWhiteSpace(saved.Title) ? "Agent" : saved.Title,
            saved.Body ?? "",
            string.IsNullOrWhiteSpace(saved.Kind) ? "Status" : saved.Kind,
            saved.Model ?? "",
            saved.CreatedAt == default ? fallbackCreatedAt : saved.CreatedAt);
    }
}
