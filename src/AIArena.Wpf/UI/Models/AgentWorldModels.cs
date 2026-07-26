namespace AIArena.Wpf.Models;

public sealed record AgentWorldSnapshot(
    string SessionId,
    int TurnIndex,
    int MessageCount,
    AgentWorldPulse Pulse,
    IReadOnlyList<AgentWorldCue> Cues,
    IReadOnlyList<AgentWorldAvatar> Avatars);

public sealed record AgentWorldPulse(
    int ActiveCount,
    int ThinkingCount,
    int AlertCount,
    int ToolActivityCount,
    int InternetActivityCount,
    int LockedCount,
    int SpeakingCount,
    string SpeakerName,
    int SpeakerTurn,
    int LatestTurn,
    int LatestPromptTokens,
    int LatestCompletionTokens,
    int LatestTotalTokens);

public sealed record AgentWorldCue(
    string Kind,
    string Label,
    string Detail,
    string Severity,
    string AgentId);

public sealed record AgentWorldAvatar(
    string Id,
    string Name,
    string Status,
    string Persona,
    string AccentColor,
    string Model,
    string VoiceStyle,
    string PressureProfile,
    bool Locked,
    string PublicNotesSummary,
    string PrivateNotesSummary,
    int Slot,
    double X,
    double Z,
    double FacingRadians,
    double MotionPhase,
    bool Speaking,
    bool Thinking,
    bool HasError,
    bool HasToolActivity,
    bool HasInternetActivity,
    int BubbleTurn,
    string BubbleText,
    int LastMessageTurn,
    string LastMessageText,
    string LastMessageStatus,
    string LastMessageKind,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public static class AgentWorldLayout
{
    private const int BubbleCharacterLimit = 128;
    private const int ActivityRecentTurnWindow = 3;

    public static AgentWorldSnapshot Build(ArenaViewSnapshot snapshot, int maxBubbles = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var agents = ActiveWorldAgents(snapshot)
            .ToArray();
        var messages = AnalyzeMessages(snapshot, agents);
        var speakerId = Math.Max(0, maxBubbles) == 0 ? "" : SpeakerId(agents, messages);
        var count = agents.Length;
        var avatars = agents
            .Select((agent, index) => BuildAvatar(agent, index, count, speakerId, messages, snapshot.Summary))
            .ToArray();

        var pulse = BuildPulse(avatars);
        return new AgentWorldSnapshot(
            snapshot.SessionId,
            snapshot.TurnIndex,
            snapshot.Messages.Count,
            pulse,
            BuildCues(avatars, pulse),
            avatars);
    }

    public static IReadOnlyList<AgentWorldCue> BuildCues(IReadOnlyList<AgentWorldAvatar> avatars, AgentWorldPulse pulse)
    {
        ArgumentNullException.ThrowIfNull(avatars);
        ArgumentNullException.ThrowIfNull(pulse);
        var cues = new List<AgentWorldCue>();
        var speaker = avatars
            .Where(avatar => avatar.Speaking)
            .OrderByDescending(avatar => avatar.BubbleTurn)
            .ThenBy(avatar => avatar.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (speaker is not null)
        {
            cues.Add(new AgentWorldCue(
                "speaker",
                "Speaker",
                speaker.BubbleTurn > 0 ? $"{speaker.Name} turn {speaker.BubbleTurn}" : speaker.Name,
                "active",
                speaker.Id));
        }

        AddRosterCue(cues, avatars.Where(avatar => avatar.HasError), "alert", "Alert", "needs review", "alert");
        AddRosterCue(cues, avatars.Where(avatar => avatar.Thinking), "thinking", "Thinking", "working", "active");
        AddRosterCue(cues, avatars.Where(avatar => avatar.HasToolActivity), "tool", "Tool", "recent tool", "signal");
        AddRosterCue(cues, avatars.Where(avatar => avatar.HasInternetActivity), "web", "Sources", "source/web", "signal");
        AddRosterCue(cues, avatars.Where(avatar => avatar.Locked), "lock", "Locks", "locked", "info");
        AddRosterCue(cues, avatars.Where(HasPrivateNotes), "memory", "Memory", "private notes", "info");
        AddRosterCue(cues, avatars.Where(HasStyleCue), "style", "Style", "voice/pressure", "info");
        if (pulse.LatestTotalTokens > 0)
        {
            cues.Add(new AgentWorldCue(
                "tokens",
                "Tokens",
                $"~{CompactWorldCount(pulse.LatestTotalTokens)} latest load",
                "signal",
                ""));
        }

        if (cues.Count == 0)
        {
            cues.Add(new AgentWorldCue("stable", "Stable", "arena ready", "info", ""));
        }

        return cues.Take(9).ToArray();
    }

    private static void AddRosterCue(
        List<AgentWorldCue> cues,
        IEnumerable<AgentWorldAvatar> source,
        string kind,
        string label,
        string detailLabel,
        string severity)
    {
        var items = source
            .OrderBy(avatar => avatar.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var names = string.Join(", ", items.Take(2).Select(avatar => avatar.Name));
        if (items.Length > 2)
        {
            names += $" +{items.Length - 2}";
        }

        cues.Add(new AgentWorldCue(
            kind,
            label,
            $"{items.Length} {detailLabel}: {names}",
            severity,
            items[0].Id));
    }

    private static AgentWorldPulse BuildPulse(IReadOnlyList<AgentWorldAvatar> avatars)
    {
        var speaker = avatars
            .Where(avatar => avatar.Speaking)
            .OrderByDescending(avatar => avatar.BubbleTurn)
            .ThenBy(avatar => avatar.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return new AgentWorldPulse(
            avatars.Count,
            avatars.Count(avatar => avatar.Thinking),
            avatars.Count(avatar => avatar.HasError),
            avatars.Count(avatar => avatar.HasToolActivity),
            avatars.Count(avatar => avatar.HasInternetActivity),
            avatars.Count(avatar => avatar.Locked),
            avatars.Count(avatar => avatar.Speaking),
            speaker?.Name ?? "",
            speaker?.BubbleTurn ?? 0,
            avatars.Select(avatar => Math.Max(0, avatar.LastMessageTurn)).DefaultIfEmpty(0).Max(),
            avatars.Sum(avatar => Math.Max(0, avatar.PromptTokens)),
            avatars.Sum(avatar => Math.Max(0, avatar.CompletionTokens)),
            avatars.Sum(avatar => Math.Max(0, avatar.TotalTokens)));
    }

    private static IEnumerable<AgentState> ActiveWorldAgents(ArenaViewSnapshot snapshot)
    {
        foreach (var agent in snapshot.Agents.Where(agent => agent.Active))
        {
            yield return agent;
        }

        if (ShouldIncludeNarrator(snapshot))
        {
            yield return new AgentState(
                "narrator",
                "Narrator",
                string.IsNullOrWhiteSpace(snapshot.NarratorStatus) ? "idle" : snapshot.NarratorStatus,
                snapshot.NarratorPersona,
                snapshot.NarratorVoiceStyle,
                "default",
                snapshot.NarratorAccentColor,
                snapshot.NarratorModel,
                true,
                snapshot.NarratorLocked,
                []);
        }
    }

    private static bool ShouldIncludeNarrator(ArenaViewSnapshot snapshot)
    {
        return IsThinking(snapshot.NarratorStatus) ||
            HasError(snapshot.NarratorStatus, null) ||
            snapshot.Messages.Any(message => message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentWorldAvatar BuildAvatar(
        AgentState agent,
        int index,
        int count,
        string speakerId,
        AgentWorldMessageIndex messages,
        string publicSummary)
    {
        var angle = count <= 1
            ? -Math.PI / 2
            : (-Math.PI / 2) + ((Math.PI * 2 * index) / count);
        var radiusX = count <= 2 ? 4.7 : 5.8;
        var radiusZ = count <= 2 ? 2.7 : 3.95;
        var x = Math.Round(Math.Cos(angle) * radiusX, 3);
        var z = Math.Round(Math.Sin(angle) * radiusZ, 3);
        var facing = Math.Atan2(-x, -z);
        var speaking = !string.IsNullOrWhiteSpace(speakerId) &&
            agent.Id.Equals(speakerId, StringComparison.OrdinalIgnoreCase);
        var thinkingSpeaker = speaking && IsThinking(agent.Status);
        var message = speaking && !thinkingSpeaker && messages.LatestChatByAgent.TryGetValue(agent.Id, out var latest)
            ? latest
            : null;
        var lastMessage = messages.LatestByAgent.TryGetValue(agent.Id, out var last)
            ? last
            : null;
        var agentActivity = messages.Activity.TryGetValue(agent.Id, out var item)
            ? item
            : new AgentWorldActivity(false, false);
        var bubbleText = speaking
            ? thinkingSpeaker
                ? "Thinking..."
                : message is null
                ? IsThinking(agent.Status) ? "Thinking..." : ""
                : CompactBubbleText(message.Text)
            : "";
        var bubbleTurn = speaking
            ? message?.Turn ?? lastMessage?.Turn ?? 0
            : 0;

        return new AgentWorldAvatar(
            agent.Id,
            string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name,
            agent.Status,
            agent.Persona,
            agent.AccentColor,
            agent.Model,
            agent.VoiceStyle,
            agent.PressureProfile,
            agent.Locked,
            CompactSummary(publicSummary, "No public summary yet."),
            CompactSummary(string.Join("; ", agent.PrivateNotes.Take(2)), "No private notes."),
            index,
            x,
            z,
            Math.Round(facing, 3),
            Math.Round(StablePhase(agent.Id, agent.Name), 3),
            speaking,
            IsThinking(agent.Status),
            HasError(agent.Status, lastMessage),
            agentActivity.HasToolActivity,
            agentActivity.HasInternetActivity,
            bubbleTurn,
            bubbleText,
            lastMessage?.Turn ?? 0,
            lastMessage is null ? "" : CompactBubbleText(lastMessage.Text),
            lastMessage?.Status ?? "",
            lastMessage?.Kind ?? "",
            lastMessage?.PromptTokens ?? 0,
            lastMessage?.CompletionTokens ?? 0,
            lastMessage?.TotalTokens ?? 0);
    }

    private static AgentWorldMessageIndex AnalyzeMessages(
        ArenaViewSnapshot snapshot,
        IReadOnlyCollection<AgentState> agents)
    {
        var latestChatByAgent = new Dictionary<string, TranscriptMessage>(StringComparer.OrdinalIgnoreCase);
        var latestByAgent = new Dictionary<string, TranscriptMessage>(StringComparer.OrdinalIgnoreCase);
        var activity = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToDictionary(agent => agent.Id, _ => new AgentWorldActivity(false, false), StringComparer.OrdinalIgnoreCase);
        if (agents.Count == 0)
        {
            return new AgentWorldMessageIndex(latestChatByAgent, latestByAgent, activity, "");
        }

        var agentIds = agents
            .Select(agent => agent.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TranscriptMessage? latestChat = null;

        var latestTurn = snapshot.Messages
            .Select(message => message.Turn)
            .DefaultIfEmpty(snapshot.TurnIndex)
            .Max();

        foreach (var message in snapshot.Messages)
        {
            if (agentIds.Contains(message.SpeakerId))
            {
                SetLatest(latestByAgent, message);
                if (!string.IsNullOrWhiteSpace(message.Text) && IsChatLikeMessage(message.Kind))
                {
                    SetLatest(latestChatByAgent, message);
                    if (latestChat is null || IsNewer(message, latestChat))
                    {
                        latestChat = message;
                    }
                }
            }

            var recentActivityTurn = message.Turn >= Math.Max(0, latestTurn - ActivityRecentTurnWindow + 1);
            if (!recentActivityTurn)
            {
                continue;
            }

            var ids = new[] { message.SpeakerId, message.InternetRequester }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                if (!activity.TryGetValue(id, out var item))
                {
                    continue;
                }

                var hasTool = item.HasToolActivity ||
                    !string.IsNullOrWhiteSpace(message.InternetTool) ||
                    message.Kind.Contains("internet", StringComparison.OrdinalIgnoreCase);
                var hasInternet = item.HasInternetActivity || message.InternetSources.Count > 0;
                activity[id] = new AgentWorldActivity(hasTool, hasInternet);
            }
        }

        return new AgentWorldMessageIndex(
            latestChatByAgent,
            latestByAgent,
            activity,
            latestChat?.SpeakerId ?? "");
    }

    private static string SpeakerId(IReadOnlyList<AgentState> agents, AgentWorldMessageIndex messages)
    {
        var working = agents.FirstOrDefault(agent =>
            !string.IsNullOrWhiteSpace(agent.Id) &&
            IsThinking(agent.Status));
        return working?.Id ?? messages.LatestChatSpeakerId;
    }

    private static void SetLatest(Dictionary<string, TranscriptMessage> latestByAgent, TranscriptMessage message)
    {
        if (latestByAgent.TryGetValue(message.SpeakerId, out var existing) && !IsNewer(message, existing))
        {
            return;
        }

        latestByAgent[message.SpeakerId] = message;
    }

    private static bool IsNewer(TranscriptMessage candidate, TranscriptMessage existing)
    {
        return candidate.Turn > existing.Turn ||
            candidate.Turn == existing.Turn && candidate.CreatedAt > existing.CreatedAt;
    }

    private static bool IsChatLikeMessage(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
        }

        return kind.Equals("message", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("narration", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("operator", StringComparison.OrdinalIgnoreCase);
    }

    private static string CompactBubbleText(string text)
    {
        var compact = string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= BubbleCharacterLimit
            ? compact
            : compact[..(BubbleCharacterLimit - 3)] + "...";
    }

    private static string CompactSummary(string text, string fallback)
    {
        var compact = string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(compact))
        {
            return fallback;
        }

        return compact.Length <= 96
            ? compact
            : compact[..93] + "...";
    }

    private static bool HasPrivateNotes(AgentWorldAvatar avatar)
    {
        return !string.IsNullOrWhiteSpace(avatar.PrivateNotesSummary) &&
            !avatar.PrivateNotesSummary.Equals("No private notes.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStyleCue(AgentWorldAvatar avatar)
    {
        return !IsDefaultWorldValue(avatar.VoiceStyle) || !IsDefaultWorldValue(avatar.PressureProfile);
    }

    private static bool IsDefaultWorldValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("default", StringComparison.OrdinalIgnoreCase);
    }

    private static string CompactWorldCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{(value / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}m";
        }

        if (value >= 1_000)
        {
            return $"{(value / 1_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}k";
        }

        return Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsThinking(string status)
    {
        return status.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("generating", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasError(string status, TranscriptMessage? message)
    {
        return status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
            message?.Status.Contains("error", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static double StablePhase(string id, string name)
    {
        var value = string.IsNullOrWhiteSpace(id) ? name : id;
        var hash = 2166136261u;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return (hash % 6283) / 1000.0;
    }

    private sealed record AgentWorldActivity(bool HasToolActivity, bool HasInternetActivity);

    private sealed record AgentWorldMessageIndex(
        IReadOnlyDictionary<string, TranscriptMessage> LatestChatByAgent,
        IReadOnlyDictionary<string, TranscriptMessage> LatestByAgent,
        IReadOnlyDictionary<string, AgentWorldActivity> Activity,
        string LatestChatSpeakerId);
}
