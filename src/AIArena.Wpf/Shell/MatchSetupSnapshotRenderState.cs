using System.Text;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

[Flags]
internal enum MatchSetupProjectionArea
{
    None = 0,
    SeedInspector = 1 << 0,
    GenerationHistory = 1 << 1,
    SetupSummary = 1 << 2,
    SetupFeedback = 1 << 3,
    RivalryMatrix = 1 << 4,
    ModelBehavior = 1 << 5,
    All = SeedInspector | GenerationHistory | SetupSummary | SetupFeedback | RivalryMatrix | ModelBehavior
}

internal sealed record MatchSetupProjectionPlan(
    ArenaViewSnapshot Snapshot,
    MatchSetupProjectionIdentity Identity,
    MatchSetupProjectionArea Areas,
    long Generation);

/// <summary>
/// Keeps Match Setup's latest authoritative snapshot separate from the last
/// snapshot projected into its hidden visual tree. Observing a snapshot never
/// claims that it was rendered; callers commit only after every requested
/// projection succeeds, so a failed render remains retryable on the next open.
/// </summary>
internal sealed class MatchSetupSnapshotRenderState
{
    private ArenaViewSnapshot? pendingSnapshot;
    private MatchSetupProjectionIdentity projectedIdentity;
    private bool hasProjectedIdentity;
    private long pendingGeneration;

    public ArenaViewSnapshot? LatestSnapshot => pendingSnapshot;

    public void Observe(ArenaViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        pendingSnapshot = snapshot;
        pendingGeneration++;
    }

    public MatchSetupProjectionPlan? Plan(bool visible)
    {
        if (!visible || pendingSnapshot is null)
        {
            return null;
        }

        // Identity construction walks setup-only collections. Keep even that
        // bounded work off the normal hidden snapshot path.
        var pendingIdentity = MatchSetupProjectionIdentity.Create(pendingSnapshot);
        var areas = hasProjectedIdentity
            ? pendingIdentity.ChangesSince(projectedIdentity)
            : MatchSetupProjectionArea.All;
        return areas == MatchSetupProjectionArea.None
            ? null
            : new MatchSetupProjectionPlan(pendingSnapshot, pendingIdentity, areas, pendingGeneration);
    }

    public void Commit(MatchSetupProjectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // A newer snapshot can be observed by nested dispatcher work while a
        // projection is running. Do not mark that newer state as rendered.
        if (pendingGeneration != plan.Generation || !ReferenceEquals(pendingSnapshot, plan.Snapshot))
        {
            return;
        }

        projectedIdentity = plan.Identity;
        hasProjectedIdentity = true;
    }

    public void Reset()
    {
        pendingSnapshot = null;
        projectedIdentity = default;
        hasProjectedIdentity = false;
        pendingGeneration = 0;
    }
}

internal readonly record struct MatchSetupProjectionIdentity(
    string Seed,
    string GenerationHistory,
    string Setup,
    string Rivalry,
    bool FactoryMode)
{
    public static MatchSetupProjectionIdentity Create(ArenaViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var seed = new ProjectionIdentityBuilder()
            .Add(snapshot.ScenarioGeneratorSeed)
            .Add(snapshot.ScenarioGeneratorStyle)
            .Add(snapshot.ScenarioGeneratorIntensity)
            .Add(snapshot.ScenarioGeneratorRolePack)
            .Add(snapshot.ScenarioGeneratorAbsurdity)
            .Add(snapshot.PersonaGeneratorSeed)
            .Add(snapshot.PersonaGeneratorStyle)
            .Build();

        var historyBuilder = new ProjectionIdentityBuilder();
        foreach (var item in snapshot.GenerationHistory)
        {
            historyBuilder
                .Add(item.Id)
                .Add(item.Kind)
                .Add(item.Label)
                .Add(item.Style)
                .Add(item.Intensity)
                .Add(item.RolePack)
                .Add(item.Absurdity)
                .Add(item.ScenarioSeed)
                .Add(item.PersonaSeed)
                .Add(item.CreatedAt)
                .Add(item.Topic)
                .Add(item.Global)
                .Add(item.NarratorBrief)
                .Add(item.PersonaCount)
                .Add(item.PersonaPreview);
        }

        var history = historyBuilder.Build();
        var rivalryBuilder = new ProjectionIdentityBuilder()
            // A session switch is an authoritative boundary even when two
            // sessions happen to contain identical relationship rules. Local
            // drafts must never leak from one session into another.
            .Add(snapshot.SessionId)
            .Add(snapshot.RivalryMatrixEnabled);
        foreach (var agent in snapshot.Agents.Where(agent => agent.Active))
        {
            rivalryBuilder.Add(agent.Id);
        }

        foreach (var link in snapshot.RivalryMatrix)
        {
            rivalryBuilder.Add(link.Source).Add(link.Target).Add(link.Stance);
        }

        var rivalry = rivalryBuilder.Build();
        var setupBuilder = new ProjectionIdentityBuilder()
            .Add(snapshot.SessionId)
            .Add(snapshot.ScenarioTopic)
            .Add(snapshot.ScenarioGlobal)
            .Add(snapshot.TopicLocked)
            .Add(snapshot.GlobalLocked)
            .Add(seed)
            .Add(history)
            .Add(snapshot.RivalryMatrixEnabled)
            .Add(rivalry)
            .Add(snapshot.FactoryMode)
            .Add(snapshot.HasFactoryConversationRoot)
            .Add(snapshot.FactoryConversationRootAssigned)
            .Add(snapshot.FactoryConversationEntryCount)
            .Add(snapshot.FactoryConversationOmittedCount)
            .Add(snapshot.TurnCount)
            .Add(snapshot.NarratorPersona)
            .Add(snapshot.NarratorVoiceStyle)
            .Add(snapshot.NarratorAccentColor)
            .Add(snapshot.NarratorLocked)
            .Add(snapshot.ProviderModel)
            .Add(snapshot.ProviderApiMode)
            .Add(snapshot.ProviderBaseUrl)
            .Add(snapshot.ProviderOnline)
            .Add(snapshot.ProviderLastError)
            .Add(snapshot.ProviderConfiguredContextWindow)
            .Add(snapshot.ProviderHistoryPolicy)
            .Add(snapshot.ProviderResponseTone)
            .Add(snapshot.ProviderCustomTone)
            .Add(snapshot.DefaultForUnassignedAgentsEnabled);

        foreach (var route in snapshot.ExplicitRoleModels.OrderBy(route => route.Key, StringComparer.OrdinalIgnoreCase))
        {
            setupBuilder.Add(route.Key).Add(route.Value);
        }

        foreach (var agent in snapshot.Agents)
        {
            setupBuilder
                .Add(agent.Id)
                .Add(agent.Name)
                .Add(agent.Persona)
                .Add(agent.VoiceStyle)
                .Add(agent.PressureProfile)
                .Add(agent.AccentColor)
                .Add(agent.Model)
                .Add(agent.Active)
                .Add(agent.Locked);
        }

        return new MatchSetupProjectionIdentity(seed, history, setupBuilder.Build(), rivalry, snapshot.FactoryMode);
    }

    public MatchSetupProjectionArea ChangesSince(MatchSetupProjectionIdentity previous)
    {
        var changes = MatchSetupProjectionArea.None;
        if (!Seed.Equals(previous.Seed, StringComparison.Ordinal))
        {
            changes |= MatchSetupProjectionArea.SeedInspector;
        }

        if (!GenerationHistory.Equals(previous.GenerationHistory, StringComparison.Ordinal))
        {
            changes |= MatchSetupProjectionArea.GenerationHistory;
        }

        if (!Setup.Equals(previous.Setup, StringComparison.Ordinal))
        {
            changes |= MatchSetupProjectionArea.SetupSummary | MatchSetupProjectionArea.SetupFeedback;
        }

        if (!Rivalry.Equals(previous.Rivalry, StringComparison.Ordinal))
        {
            changes |= MatchSetupProjectionArea.RivalryMatrix;
        }

        if (FactoryMode != previous.FactoryMode)
        {
            changes |= MatchSetupProjectionArea.ModelBehavior;
        }

        return changes;
    }

    private sealed class ProjectionIdentityBuilder
    {
        private readonly StringBuilder value = new();

        public ProjectionIdentityBuilder Add(string? item)
        {
            item ??= string.Empty;
            value.Append(item.Length).Append(':').Append(item).Append(';');
            return this;
        }

        public ProjectionIdentityBuilder Add(bool item) => Add(item ? "1" : "0");

        public ProjectionIdentityBuilder Add(int item) =>
            Add(item.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public ProjectionIdentityBuilder Add(double item) =>
            Add(item.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        public string Build() => value.ToString();
    }
}
