using AIArena.Core.Models;

namespace AIArena.Core.Services;

public static class AgentRosterService
{
    public const int MinParticipants = 1;
    public const int MaxParticipants = 8;

    public static readonly string[] ParticipantIds =
    [
        "alpha",
        "beta",
        "gamma",
        "delta",
        "epsilon",
        "zeta",
        "eta",
        "theta"
    ];

    public static bool IsParticipantId(string id)
    {
        return ParticipantIds.Contains(NormalizeId(id), StringComparer.OrdinalIgnoreCase);
    }

    public static int ParticipantOrder(string id)
    {
        var normalized = NormalizeId(id);
        for (var index = 0; index < ParticipantIds.Length; index++)
        {
            if (ParticipantIds[index].Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 100;
    }

    public static string DisplayName(string id)
    {
        return NormalizeId(id) switch
        {
            "alpha" => "Alpha",
            "beta" => "Beta",
            "gamma" => "Gamma",
            "delta" => "Delta",
            "epsilon" => "Epsilon",
            "zeta" => "Zeta",
            "eta" => "Eta",
            "theta" => "Theta",
            var cleaned when !string.IsNullOrWhiteSpace(cleaned) => char.ToUpperInvariant(cleaned[0]) + cleaned[1..],
            _ => "Agent"
        };
    }

    public static int ActiveParticipantCount(ArenaSnapshot snapshot)
    {
        return snapshot.Engine.Agents.Count(agent => agent.Active && IsParticipantId(agent.Id));
    }

    public static void EnsureParticipantCount(ArenaSnapshot snapshot, int requestedCount)
    {
        var count = Math.Clamp(requestedCount, MinParticipants, MaxParticipants);
        for (var index = 0; index < count; index++)
        {
            var id = ParticipantIds[index];
            if (snapshot.Engine.Agents.Any(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            InsertParticipant(snapshot.Engine.Agents, CreateDefaultAgent(id));
        }

        var allowed = ParticipantIds.Take(count).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = snapshot.Engine.Agents.Count - 1; index >= 0; index--)
        {
            var agent = snapshot.Engine.Agents[index];
            if (!IsParticipantId(agent.Id))
            {
                continue;
            }

            if (allowed.Contains(agent.Id))
            {
                agent.Active = true;
                if (string.IsNullOrWhiteSpace(agent.Status) || agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase))
                {
                    agent.Status = "waiting";
                }
            }
            else
            {
                snapshot.Engine.Agents.RemoveAt(index);
                snapshot.Configs.Remove(agent.Id);
                snapshot.MatchLocks.Remove(agent.Id);
                snapshot.Engine.RivalryMatrix.Links.RemoveAll(link =>
                    link.Source.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)
                    || link.Target.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        snapshot.Engine.Agents.Sort((left, right) => ParticipantOrder(left.Id).CompareTo(ParticipantOrder(right.Id)));
        var active = snapshot.Engine.Agents.Where(agent => agent.Active && IsParticipantId(agent.Id)).ToArray();
        if (active.Length > 0)
        {
            snapshot.Engine.TurnIndex %= active.Length;
        }
        else
        {
            snapshot.Engine.TurnIndex = 0;
        }
    }

    public static DialogueAgent CreateDefaultAgent(string id)
    {
        var normalized = NormalizeId(id);
        return new DialogueAgent
        {
            Id = normalized,
            Name = DisplayName(normalized),
            Persona = DefaultPersona(normalized),
            Active = true,
            Status = "waiting"
        };
    }

    private static void InsertParticipant(List<DialogueAgent> agents, DialogueAgent agent)
    {
        var insertAt = agents.FindIndex(existing => ParticipantOrder(existing.Id) > ParticipantOrder(agent.Id));
        if (insertAt >= 0)
        {
            agents.Insert(insertAt, agent);
            return;
        }

        agents.Add(agent);
    }

    private static string DefaultPersona(string id)
    {
        return NormalizeId(id) switch
        {
            "alpha" => "Practical strategist. Surfaces assumptions, proposes concrete options, and keeps the exchange moving.",
            "beta" => "Critical reviewer. Tests weak premises, edge cases, and hidden tradeoffs before accepting conclusions.",
            "gamma" => "Evidence mapper. Separates facts from guesses and asks what would change the current conclusion.",
            "delta" => "Boundary tester. Identifies limits, misuse cases, escalation paths, and operational failure boundaries.",
            "epsilon" => "Counterfactual scout. Looks for what would be true if the current framing is wrong.",
            "zeta" => "Systems stress tester. Tracks second-order effects, bottlenecks, and coordination failures.",
            "eta" => "Human impact witness. Keeps practical consequences, comprehension, and trust visible.",
            "theta" => "Synthesis challenger. Compresses the debate into a decision while preserving unresolved risks.",
            _ => "Participant. Pressure-tests the conversation from a distinct useful angle."
        };
    }

    private static string NormalizeId(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? "" : id.Trim().ToLowerInvariant();
    }
}
