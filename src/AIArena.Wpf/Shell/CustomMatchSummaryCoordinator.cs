using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class CustomMatchSummaryCoordinator
{
    private readonly Panel scenarioPreviewItems;
    private readonly Panel castPreviewItems;
    private readonly ShellCardFactory shellCards;
    private readonly MatchLockCoordinator matchLock;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;

    public CustomMatchSummaryCoordinator(
        Panel scenarioPreviewItems,
        Panel castPreviewItems,
        ShellCardFactory shellCards,
        MatchLockCoordinator matchLock,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<Brush, Brush, double, Brush> blendBrush)
    {
        this.scenarioPreviewItems = scenarioPreviewItems;
        this.castPreviewItems = castPreviewItems;
        this.shellCards = shellCards;
        this.matchLock = matchLock;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.blendBrush = blendBrush;
    }

    public void Populate(ArenaViewSnapshot snapshot)
    {
        scenarioPreviewItems.Children.Clear();
        castPreviewItems.Children.Clear();
        matchLock.ClearControls();

        PopulateScenario(snapshot);
        PopulateCast(snapshot);
    }

    internal static string ScenarioTopicText(string topic)
    {
        return string.IsNullOrWhiteSpace(topic) ? "No topic is set for this match yet." : topic;
    }

    internal static string ScenarioGlobalText(string global)
    {
        return string.IsNullOrWhiteSpace(global) ? "No global instruction is set for this match yet." : global;
    }

    internal static string AgentPersonaText(string persona)
    {
        return string.IsNullOrWhiteSpace(persona) ? "(no persona)" : persona;
    }

    internal static string NarratorPersonaText(string persona)
    {
        return string.IsNullOrWhiteSpace(persona) ? "(no narrator persona)" : persona;
    }

    internal static string SetupProfileText(ArenaViewSnapshot snapshot)
    {
        return string.Join(
            " / ",
            DisplayLabel(snapshot.ScenarioGeneratorRolePack, "auto pack"),
            DisplayLabel(snapshot.ScenarioGeneratorStyle, "auto style"),
            DisplayLabel(snapshot.ScenarioGeneratorIntensity, "normal pressure"),
            DisplayLabel(snapshot.ScenarioGeneratorAbsurdity, "grounded personas"),
            DisplayLabel(snapshot.ScenarioGeneratorSeed, "no seed"));
    }

    internal static string RunShapeText(ArenaViewSnapshot snapshot)
    {
        var active = ActiveAgents(snapshot).ToArray();
        if (active.Length == 0)
        {
            return "No active agents. Activate at least two participants to shape an arena run.";
        }

        var cast = string.Join(" -> ", active.Select(agent => DisplayName(agent)));
        var narrator = string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "narrator unbriefed" : "narrator briefed";
        var turnBudget = snapshot.TurnCount <= 0
            ? "manual turn budget"
            : $"{snapshot.TurnCount} turn budget";
        return $"{active.Length} active: {cast} -> Narrator ({narrator}); {turnBudget}.";
    }

    internal static string RelationshipMapText(ArenaViewSnapshot snapshot)
    {
        var activeIds = ActiveAgents(snapshot)
            .Select(agent => agent.Id)
            .ToArray();
        if (!snapshot.RivalryMatrixEnabled)
        {
            var dormant = MatchSetupCoordinator.BuildRivalryMatrixPlan(snapshot.RivalryMatrix, activeIds);
            return dormant.Links.Count == 0
                ? "Relationship matrix disabled; agents use neutral debate pressure."
                : $"Relationship matrix disabled; {dormant.Links.Count} saved rule(s) are dormant.";
        }

        var plan = MatchSetupCoordinator.BuildRivalryMatrixPlan(snapshot.RivalryMatrix, activeIds);
        if (plan.Links.Count == 0)
        {
            return "Relationship matrix enabled, but no active participant rules are set.";
        }

        var routes = MatchSetupCoordinator.RelationshipPreviewLines(true, plan.Links, activeIds);
        var insight = MatchSetupCoordinator.RelationshipInsight(true, plan.Links, activeIds, plan.SkippedInvalidRules);
        return $"{string.Join("; ", routes)}. {insight}";
    }

    internal static string LockPlanText(ArenaViewSnapshot snapshot)
    {
        var locks = LockLabels(snapshot).ToArray();
        return locks.Length == 0
            ? "No locked setup fields; generated setups can replace topic, rules, cast, and narrator."
            : $"Locked: {string.Join(", ", locks)}.";
    }

    internal static string SetupSourceText(ArenaViewSnapshot snapshot)
    {
        var source = DisplayLabel(snapshot.ScenarioGeneratorSeed, "manual setup");
        var persona = DisplayLabel(snapshot.PersonaGeneratorSeed, "manual personas");
        var history = snapshot.GenerationHistory.Count == 0
            ? "no recent generated setups"
            : $"{snapshot.GenerationHistory.Count} recent generated setup(s)";
        return $"{source}; {persona}; {history}.";
    }

    internal static string RunConstraintText(ArenaViewSnapshot snapshot)
    {
        var activeAgents = snapshot.Agents.Count(agent => agent.Active);
        var locks = LockLabels(snapshot).ToArray();
        var lockText = locks.Length == 0 ? "no locks" : $"{locks.Length} lock(s)";
        var activeIds = ActiveAgents(snapshot)
            .Select(agent => agent.Id)
            .ToArray();
        var topology = MatchSetupCoordinator.Topology(snapshot.RivalryMatrixEnabled, snapshot.RivalryMatrix, activeIds);
        var relationshipText = snapshot.RivalryMatrixEnabled && topology.ActiveRules > 0
            ? $"{topology.ActiveRules} relationship rule(s), coverage {topology.ActiveSources}/{topology.TotalSources}"
            : "neutral relationships";
        var historyText = snapshot.GenerationHistory.Count == 0
            ? "no replay history"
            : $"{snapshot.GenerationHistory.Count} replayable setup(s)";
        return $"{activeAgents} active agent(s), {lockText}, {relationshipText}, {historyText}.";
    }

    internal static string CurrentSetupBrief(ArenaViewSnapshot snapshot)
    {
        return string.Join(Environment.NewLine,
            "AI Arena current setup",
            $"Session: {DisplayLabel(snapshot.SessionId, "unknown session")}",
            $"Readiness: {ScenarioWorkflowCoordinator.SetupReadinessStatus(snapshot)}",
            "",
            "Scenario",
            $"- Topic: {ScenarioTopicText(snapshot.ScenarioTopic)}",
            $"- Global: {ScenarioGlobalText(snapshot.ScenarioGlobal)}",
            $"- Recipe: {SetupProfileText(snapshot)}",
            $"- {ScenarioWorkflowCoordinator.GenerationPresetMatchSummary(snapshot.ScenarioGeneratorRolePack, snapshot.ScenarioGeneratorStyle, snapshot.ScenarioGeneratorIntensity, snapshot.ScenarioGeneratorAbsurdity)}",
            "",
            "Run",
            $"- Shape: {RunShapeText(snapshot)}",
            $"- Constraints: {RunConstraintText(snapshot)}",
            $"- Relationship map: {RelationshipMapText(snapshot)}",
            $"- Locks: {LockPlanText(snapshot)}",
            "",
            "Cast",
            ActiveCastBrief(snapshot),
            "",
            $"Narrator: {NarratorPersonaText(snapshot.NarratorPersona)}",
            $"Provider: {DisplayLabel(snapshot.ProviderModel, "no provider model")} / {DisplayLabel(snapshot.ProviderApiMode, "compatible mode")}");
    }

    internal static string CurrentSetupSpec(ArenaViewSnapshot snapshot)
    {
        var activeIds = ActiveAgents(snapshot).Select(agent => agent.Id).ToArray();
        var relationshipPlan = MatchSetupCoordinator.BuildRivalryMatrixPlan(snapshot.RivalryMatrix, activeIds);
        var spec = new
        {
            schema = "ai_arena.current_setup.v1",
            session = new
            {
                id = DisplayLabel(snapshot.SessionId, "unknown session"),
                snapshotPath = DisplayLabel(snapshot.SnapshotPath, "unknown snapshot"),
                lastWriteTimeUtc = snapshot.LastWriteTimeUtc
            },
            readiness = new
            {
                status = ScenarioWorkflowCoordinator.SetupReadinessStatus(snapshot),
                constraints = RunConstraintText(snapshot)
            },
            scenario = new
            {
                topic = ScenarioTopicText(snapshot.ScenarioTopic),
                global = ScenarioGlobalText(snapshot.ScenarioGlobal),
                narrator = NarratorPersonaText(snapshot.NarratorPersona)
            },
            tuning = new
            {
                rolePack = DisplayLabel(snapshot.ScenarioGeneratorRolePack, "auto pack"),
                style = DisplayLabel(snapshot.ScenarioGeneratorStyle, "auto style"),
                intensity = DisplayLabel(snapshot.ScenarioGeneratorIntensity, "normal pressure"),
                absurdity = DisplayLabel(snapshot.ScenarioGeneratorAbsurdity, "grounded personas"),
                scenarioSeed = DisplayLabel(snapshot.ScenarioGeneratorSeed, "no seed"),
                personaSeed = DisplayLabel(snapshot.PersonaGeneratorSeed, "no persona seed"),
                presetMatches = ScenarioWorkflowCoordinator.GenerationPresetMatchLabels(
                    snapshot.ScenarioGeneratorRolePack,
                    snapshot.ScenarioGeneratorStyle,
                    snapshot.ScenarioGeneratorIntensity,
                    snapshot.ScenarioGeneratorAbsurdity)
            },
            run = new
            {
                shape = RunShapeText(snapshot),
                turnBudget = snapshot.TurnCount,
                locks = LockLabels(snapshot).ToArray(),
                replayableSetups = snapshot.GenerationHistory.Count
            },
            relationship = new
            {
                enabled = snapshot.RivalryMatrixEnabled,
                summary = MatchSetupCoordinator.Summary(snapshot.RivalryMatrixEnabled, snapshot.RivalryMatrix, activeIds),
                insight = MatchSetupCoordinator.RelationshipInsight(snapshot.RivalryMatrixEnabled, relationshipPlan.Links, activeIds, relationshipPlan.SkippedInvalidRules),
                links = relationshipPlan.Links.Select(link => new
                {
                    link.Source,
                    link.Target,
                    stance = DisplayLabel(link.Stance, "neutral")
                }).ToArray()
            },
            cast = ActiveAgents(snapshot).Select(agent => new
            {
                id = agent.Id,
                name = DisplayName(agent),
                agent.Model,
                locked = agent.Locked,
                persona = AgentPersonaText(agent.Persona),
                voiceStyle = DisplayLabel(agent.VoiceStyle, "default voice"),
                pressureProfile = DisplayLabel(agent.PressureProfile, "default pressure")
            }).ToArray(),
            provider = new
            {
                model = DisplayLabel(snapshot.ProviderModel, "no provider model"),
                apiMode = DisplayLabel(snapshot.ProviderApiMode, "compatible mode"),
                baseUrl = DisplayLabel(snapshot.ProviderBaseUrl, "no provider URL"),
                online = snapshot.ProviderOnline
            }
        };

        return JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
    }

    private void PopulateScenario(ArenaViewSnapshot snapshot)
    {
        scenarioPreviewItems.Children.Add(matchLock.CreateLockCard(
            "topic",
            "Topic",
            ScenarioTopicText(snapshot.ScenarioTopic),
            resourceBrush("CardBrush"),
            snapshot.TopicLocked ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush"),
            snapshot.TopicLocked));
        scenarioPreviewItems.Children.Add(matchLock.CreateLockCard(
            "global",
            "Global",
            ScenarioGlobalText(snapshot.ScenarioGlobal),
            resourceBrush("CardBrush"),
            snapshot.GlobalLocked ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush"),
            snapshot.GlobalLocked));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Setup Profile",
            SetupProfileText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("PrimaryBorderBrush")));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Run Shape",
            RunShapeText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("GammaAccentBrush")));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Relationship Map",
            RelationshipMapText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("BetaAccentBrush")));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Lock Plan",
            LockPlanText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("AssistBorderBrush")));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Setup Source",
            SetupSourceText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("PrimaryBorderBrush")));
        scenarioPreviewItems.Children.Add(shellCards.CreateCard(
            "Run Constraints",
            RunConstraintText(snapshot),
            resourceBrush("CardBrush"),
            resourceBrush("AssistBorderBrush")));
    }

    private void PopulateCast(ArenaViewSnapshot snapshot)
    {
        var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
        if (activeAgents.Length == 0)
        {
            castPreviewItems.Children.Add(shellCards.CreateEmptyStateCard(
                "Cast",
                "No active cast is available for this session yet.",
                resourceBrush("MutedTextBrush"),
                "Awaiting cast"));
        }
        else
        {
            foreach (var agent in activeAgents)
            {
                var accent = accentForSpeaker(agent.Id);
                castPreviewItems.Children.Add(matchLock.CreateLockCard(
                    agent.Id,
                    MatchLockCoordinator.FormatCastPreviewTitle(agent.Id, agent.Name),
                    AgentPersonaText(agent.Persona),
                    blendBrush(resourceBrush("CardBrush"), accent, 0.16),
                    accent,
                    agent.Locked,
                    agent.VoiceStyle,
                    agent.PressureProfile,
                    agent.AccentColor));
            }
        }

        var narratorAccent = accentForSpeaker("narrator");
        castPreviewItems.Children.Add(matchLock.CreateLockCard(
            "narrator",
            "Narrator",
            NarratorPersonaText(snapshot.NarratorPersona),
            blendBrush(resourceBrush("CardBrush"), narratorAccent, 0.16),
            narratorAccent,
            snapshot.NarratorLocked,
            snapshot.NarratorVoiceStyle,
            accentColor: snapshot.NarratorAccentColor));
    }

    private static string DisplayLabel(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-"
            ? fallback
            : value.Trim().Replace('_', ' ').Replace('-', ' ');
    }

    private static IEnumerable<AgentState> ActiveAgents(ArenaViewSnapshot snapshot)
    {
        return snapshot.Agents
            .Where(agent => agent.Active)
            .Where(agent => AgentRosterService.IsParticipantId(agent.Id));
    }

    private static string DisplayName(AgentState agent)
    {
        return string.IsNullOrWhiteSpace(agent.Name) ? DisplayLabel(agent.Id, agent.Id) : agent.Name.Trim();
    }

    private static IEnumerable<string> LockLabels(ArenaViewSnapshot snapshot)
    {
        if (snapshot.TopicLocked)
        {
            yield return "topic";
        }

        if (snapshot.GlobalLocked)
        {
            yield return "global";
        }

        if (snapshot.NarratorLocked)
        {
            yield return "narrator";
        }

        foreach (var agent in ActiveAgents(snapshot).Where(agent => agent.Locked))
        {
            yield return DisplayName(agent);
        }
    }

    private static IEnumerable<RivalryMatrixItem> ActiveRelationshipRules(ArenaViewSnapshot snapshot)
    {
        if (!snapshot.RivalryMatrixEnabled)
        {
            return [];
        }

        var activeIds = ActiveAgents(snapshot)
            .Select(agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.RivalryMatrix
            .Where(link => activeIds.Contains(link.Source) &&
                activeIds.Contains(link.Target) &&
                !link.Source.Equals(link.Target, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(link.Stance) &&
                !link.Stance.Equals("neutral", StringComparison.OrdinalIgnoreCase));
    }

    private static string ActiveCastBrief(ArenaViewSnapshot snapshot)
    {
        var active = ActiveAgents(snapshot).ToArray();
        if (active.Length == 0)
        {
            return "- No active agents.";
        }

        return string.Join(Environment.NewLine, active.Select(agent =>
            $"- {DisplayName(agent)}: {AgentPersonaText(agent.Persona)}"));
    }
}
