using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>
/// Applies an already-approved route proposal to one persisted session. The
/// optimizer never calls this service. Application is a separate, explicit
/// action which rechecks the setup fingerprint and current model immediately
/// before the concurrency-checked snapshot write.
/// </summary>
public sealed class ArenaRouteApplicationService
{
    private readonly SessionStore _sessionStore;
    private readonly TimeProvider _timeProvider;

    public ArenaRouteApplicationService(SessionStore sessionStore, TimeProvider? timeProvider = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ArenaRouteApplicationReceiptContract> ApplyApprovedAsync(
        string sessionId,
        ArenaRouteProposalContract proposal,
        string approvedBy,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        RequireUtc(approvedAtUtc, nameof(approvedAtUtc));

        var proposalValidation = ArenaContractCodec.Validate(proposal);
        if (!proposalValidation.IsValid || proposal.Status != ArenaRouteProposalStatus.Proposed)
        {
            throw new InvalidOperationException("Only a valid proposed route can cross the explicit application boundary.");
        }

        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected persisted session is unavailable.");
        var actualSetupFingerprint = SessionStore.SetupFingerprint(snapshot);
        if (!actualSetupFingerprint.Equals(proposal.SetupFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArenaRouteApplicationConflictException("The persisted setup changed after the proposal was created.");
        }

        var duplicateAgent = proposal.Changes
            .GroupBy(item => item.AgentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAgent is not null)
        {
            throw new ArenaRouteApplicationConflictException("A route proposal cannot target the same agent more than once.");
        }

        var allowedAgentIds = snapshot.Engine.Agents
            .Select(item => item.Id)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        allowedAgentIds.Add("narrator");
        if (proposal.Changes.Any(change => !allowedAgentIds.Contains(change.AgentId)))
        {
            throw new ArenaRouteApplicationConflictException("A proposed route target is not present in the persisted arena roster.");
        }

        // Validate every target before mutating the in-memory snapshot. This
        // keeps a later conflict from partially shaping an otherwise rejected
        // application plan.
        var pending = ImmutableArray.CreateBuilder<(ArenaModelRouteChange Change, ModelProviderConfig Current)>(proposal.Changes.Length);
        foreach (var change in proposal.Changes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ModelProviderRouting.Resolve(snapshot, change.AgentId, out _)
                ?? throw new ArenaRouteApplicationConflictException("A proposed route no longer resolves to a configured provider.");
            if (!string.Equals(current.Model, change.CurrentModelId, StringComparison.Ordinal))
            {
                throw new ArenaRouteApplicationConflictException("A proposed current model no longer matches the persisted route.");
            }
            pending.Add((change, current));
        }

        var applied = ImmutableArray.CreateBuilder<ArenaAppliedRouteChange>(pending.Count);
        foreach (var (change, current) in pending)
        {
            snapshot.Configs[change.AgentId] = WithModel(current, change.ProposedModelId);
            applied.Add(new(change.Id, change.AgentId, current.Model, change.ProposedModelId));
        }

        var appliedAtUtc = _timeProvider.GetUtcNow();
        RequireUtc(appliedAtUtc, nameof(_timeProvider));
        if (appliedAtUtc < approvedAtUtc)
        {
            throw new InvalidOperationException("Approval time cannot follow application time.");
        }

        var receipt = BuildReceipt(proposal, approvedBy, approvedAtUtc, appliedAtUtc, applied.ToImmutable());
        var receiptValidation = ArenaContractCodec.Validate(receipt);
        if (!receiptValidation.IsValid)
        {
            throw new InvalidDataException("The route application receipt did not satisfy its frozen contract.");
        }

        // SessionStore rejects stale PersistenceRevision values. A concurrent
        // edit therefore cannot be silently overwritten between the recheck
        // above and this write.
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private static ArenaRouteApplicationReceiptContract BuildReceipt(
        ArenaRouteProposalContract proposal,
        string approvedBy,
        DateTimeOffset approvedAtUtc,
        DateTimeOffset appliedAtUtc,
        ImmutableArray<ArenaAppliedRouteChange> changes)
    {
        var suffix = StableSuffix(
            proposal.Id,
            approvedBy,
            approvedAtUtc.ToString("O"),
            string.Join("\n", changes.Select(item => $"{item.ProposalChangeId}|{item.AgentId}|{item.PreviousModelId}|{item.AppliedModelId}")));
        return new(
            ArenaContractSchemas.RouteApplicationReceipt,
            $"route-receipt:{suffix}",
            appliedAtUtc,
            proposal.Id,
            proposal.ExperimentId,
            proposal.SetupFingerprint,
            approvedBy,
            approvedAtUtc,
            appliedAtUtc,
            changes,
            new(
                $"route-approval:{suffix}",
                ArenaEvidenceState.Observed,
                "A local operator explicitly approved this route application.",
                ReferenceId: proposal.Id),
            [new(
                $"route-application:{suffix}",
                ArenaEvidenceState.Observed,
                "The approved route changes were written through concurrency-checked session persistence.",
                ReferenceId: proposal.Id)]);
    }

    private static ModelProviderConfig WithModel(ModelProviderConfig source, string model) => new()
    {
        BaseUrl = source.BaseUrl,
        ApiMode = source.ApiMode,
        ApiToken = source.ApiToken,
        Model = model,
        ExplicitModelAssignment = true,
        Timeout = source.Timeout,
        Temperature = source.Temperature,
        MaxOutputTokens = source.MaxOutputTokens,
        ContextLength = source.ContextLength,
        ConfiguredContextWindow = source.ConfiguredContextWindow,
        HistoryPolicy = source.HistoryPolicy,
        ResponseTone = source.ResponseTone,
        CustomTone = source.CustomTone,
        Reasoning = source.Reasoning,
        NativeStatefulChat = source.NativeStatefulChat,
        NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
        PreviousResponseId = "",
        LastError = "",
        LastLatencyMs = 0,
        LastTestOk = false,
        Extra = source.Extra
    };

    private static string StableSuffix(params string[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))[..8]);

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}

public sealed class ArenaRouteApplicationConflictException : InvalidOperationException
{
    public ArenaRouteApplicationConflictException(string message) : base(message)
    {
    }
}
