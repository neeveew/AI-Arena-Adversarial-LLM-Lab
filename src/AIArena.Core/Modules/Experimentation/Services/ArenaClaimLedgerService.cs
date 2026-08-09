using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaClaimLedgerServiceOptions(
    int MaximumClaims = 2_000,
    int MaximumEvidence = 4_000,
    int MaximumClaimSummaryCharacters = 1_024,
    int MaximumSourcesPerClaim = 64,
    int MaximumReviewersPerClaim = 64);

/// <summary>
/// Pure transformations for bounded claim ledgers. Source material is represented
/// by evidence IDs only; raw tool output, transcripts, paths, and credentials are
/// rejected by the common contract privacy boundary.
/// </summary>
public sealed class ArenaClaimLedgerService
{
    private readonly ArenaClaimLedgerServiceOptions _options;

    public ArenaClaimLedgerService(ArenaClaimLedgerServiceOptions? options = null)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumClaims, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumEvidence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumClaimSummaryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumSourcesPerClaim, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumReviewersPerClaim, 1);
    }

    public ArenaClaimLedgerContract Create(
        string id,
        string experimentId,
        string branchId,
        DateTimeOffset createdAtUtc) =>
        Validate(new(
            ArenaContractSchemas.ClaimLedger,
            id,
            createdAtUtc,
            experimentId,
            branchId,
            [],
            []));

    public static ArenaEvidenceAssertion ToolEvidence(
        string evidenceId,
        string sourceArtifactId,
        string boundedSummary) =>
        new(
            evidenceId,
            ArenaEvidenceState.Observed,
            boundedSummary,
            sourceArtifactId);

    public ArenaClaimLedgerContract AddClaim(
        ArenaClaimLedgerContract ledger,
        DialogueMessage message,
        string claimantId,
        string claimSummary,
        decimal assertedConfidence,
        ArenaEvidenceAssertion provenance,
        ImmutableArray<ArenaEvidenceAssertion> sourceEvidence,
        int legacyOccurrence = 0,
        string? claimId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(message);
        ledger = Validate(ledger);
        if (ledger.Claims.Length >= _options.MaximumClaims)
            throw new InvalidOperationException("Claim ledger has reached its bounded record limit.");
        if (string.IsNullOrWhiteSpace(claimSummary) || claimSummary.Length > _options.MaximumClaimSummaryCharacters)
            throw new ArgumentOutOfRangeException(nameof(claimSummary), $"Claim summary must contain 1-{_options.MaximumClaimSummaryCharacters} characters.");
        if (assertedConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(assertedConfidence));
        if (sourceEvidence.IsDefault || sourceEvidence.Length > _options.MaximumSourcesPerClaim)
            throw new ArgumentException("Source evidence must be initialized and bounded.", nameof(sourceEvidence));

        var messageId = ResolveMessageReference(message, legacyOccurrence);
        claimId ??= StableId("claim", $"{ledger.Id}\n{messageId}\n{claimantId}\n{claimSummary.Trim()}");
        var evidence = MergeEvidence(ledger.Evidence, sourceEvidence);
        var value = new ArenaClaim(
            claimId,
            messageId,
            claimantId,
            claimSummary.Trim(),
            ArenaClaimStatus.Asserted,
            assertedConfidence,
            [.. sourceEvidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)],
            [],
            [],
            provenance);
        var existing = ledger.Claims.FirstOrDefault(item => item.Id == value.Id);
        if (existing is not null)
        {
            if (existing == value) return ledger;
            throw new InvalidOperationException($"A different claim already uses ID '{value.Id}'.");
        }
        return Validate(ledger with
        {
            Claims = [.. ledger.Claims.Append(value).OrderBy(item => item.Id, StringComparer.Ordinal)],
            Evidence = evidence
        });
    }

    public ArenaClaimLedgerContract AddModelAssertion(
        ArenaClaimLedgerContract ledger,
        DialogueMessage message,
        string claimantId,
        string claimSummary,
        decimal assertedConfidence,
        string extractionProfileId,
        ImmutableArray<ArenaEvidenceAssertion> sourceEvidence,
        int legacyOccurrence = 0,
        string? claimId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(message);
        ledger = Validate(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimSummary);
        var resolvedClaimId = claimId ?? StableId(
            "claim",
            $"{ledger.Id}\n{ResolveMessageReference(message, legacyOccurrence)}\n{claimantId}\n{claimSummary.Trim()}");
        var provenance = new ArenaEvidenceAssertion(
            StableId("provenance", resolvedClaimId),
            ArenaEvidenceState.Inferred,
            "Model assertion extracted from the referenced dialogue message; it is not a verified fact.",
            Basis: $"model extraction profile {extractionProfileId}");
        return AddClaim(
            ledger,
            message,
            claimantId,
            claimSummary,
            assertedConfidence,
            provenance,
            sourceEvidence,
            legacyOccurrence,
            resolvedClaimId);
    }

    public ArenaClaimLedgerContract ReviewClaim(
        ArenaClaimLedgerContract ledger,
        string claimId,
        ArenaClaimStatus status,
        string reviewerId,
        ArenaEvidenceAssertion reviewProvenance)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger = Validate(ledger);
        RequireObservedReview(reviewProvenance);
        var claim = ledger.Claims.FirstOrDefault(item => item.Id == claimId)
            ?? throw new KeyNotFoundException($"Claim '{claimId}' was not found.");
        if (status == ArenaClaimStatus.Supported)
        {
            var observedEvidenceIds = ledger.Evidence
                .Where(item => item.State == ArenaEvidenceState.Observed)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (!claim.SourceEvidenceIds.Any(observedEvidenceIds.Contains))
                throw new InvalidOperationException("A supported claim requires at least one observed source-evidence reference.");
        }
        if (status == ArenaClaimStatus.Contradicted && claim.ContradictionClaimIds.IsEmpty)
            throw new InvalidOperationException("Use contradiction linking before marking a claim contradicted.");
        var reviewers = claim.ReviewerIds.Append(reviewerId).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray();
        if (reviewers.Length > _options.MaximumReviewersPerClaim)
            throw new InvalidOperationException("Claim has reached its bounded reviewer limit.");
        return Validate(ledger with
        {
            Claims = [.. ledger.Claims.Select(item => item.Id == claimId ? item with { Status = status, ReviewerIds = reviewers } : item)
                .OrderBy(item => item.Id, StringComparer.Ordinal)],
            Evidence = MergeEvidence(ledger.Evidence, [reviewProvenance])
        });
    }

    public ArenaClaimLedgerContract LinkContradiction(
        ArenaClaimLedgerContract ledger,
        string firstClaimId,
        string secondClaimId,
        string reviewerId,
        ArenaEvidenceAssertion reviewProvenance)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger = Validate(ledger);
        RequireObservedReview(reviewProvenance);
        if (firstClaimId == secondClaimId) throw new ArgumentException("A claim cannot contradict itself.");
        var ids = ledger.Claims.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(firstClaimId) || !ids.Contains(secondClaimId))
            throw new KeyNotFoundException("Both contradiction endpoints must exist in the ledger.");

        var claims = ledger.Claims.Select(item =>
        {
            if (item.Id != firstClaimId && item.Id != secondClaimId) return item;
            var opposite = item.Id == firstClaimId ? secondClaimId : firstClaimId;
            var contradictions = item.ContradictionClaimIds.Append(opposite).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
            var reviewers = item.ReviewerIds.Append(reviewerId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
            if (reviewers.Length > _options.MaximumReviewersPerClaim)
                throw new InvalidOperationException("Claim has reached its bounded reviewer limit.");
            return item with
            {
                Status = ArenaClaimStatus.Contradicted,
                ContradictionClaimIds = contradictions,
                ReviewerIds = reviewers
            };
        }).OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();
        return Validate(ledger with
        {
            Claims = claims,
            Evidence = MergeEvidence(ledger.Evidence, [reviewProvenance])
        });
    }

    public static string ResolveMessageReference(DialogueMessage message, int legacyOccurrence = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        var resolved = DialogueMessageIdentity.Resolve(message, legacyOccurrence);
        return Regex.IsMatch(resolved, "^[a-z0-9][a-z0-9._:-]{0,159}$", RegexOptions.CultureInvariant)
            ? resolved
            : StableId("message", resolved);
    }

    private ImmutableArray<ArenaEvidenceAssertion> MergeEvidence(
        ImmutableArray<ArenaEvidenceAssertion> existing,
        ImmutableArray<ArenaEvidenceAssertion> incoming)
    {
        if (existing.IsDefault || incoming.IsDefault) throw new InvalidDataException("Evidence collections must be initialized.");
        var result = existing.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in incoming)
        {
            if (item is null) throw new InvalidDataException("Evidence cannot be null.");
            if (result.TryGetValue(item.Id, out var prior) && prior != item)
                throw new InvalidOperationException($"A different evidence assertion already uses ID '{item.Id}'.");
            result[item.Id] = item;
        }
        if (result.Count > _options.MaximumEvidence)
            throw new InvalidOperationException("Claim ledger has reached its bounded evidence limit.");
        return [.. result.Values.OrderBy(item => item.Id, StringComparer.Ordinal)];
    }

    private ArenaClaimLedgerContract Validate(ArenaClaimLedgerContract ledger)
    {
        var validation = ArenaContractCodec.Validate(ledger);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
        }
        if (ledger.Claims.Length > _options.MaximumClaims)
            throw new InvalidDataException("Claim ledger exceeds its bounded record limit.");
        if (ledger.Evidence.Length > _options.MaximumEvidence)
            throw new InvalidDataException("Claim ledger exceeds its bounded evidence limit.");
        if (ledger.Claims.Any(item => item.ClaimSummary.Length > _options.MaximumClaimSummaryCharacters))
            throw new InvalidDataException("Claim ledger contains an overlong claim summary.");
        if (ledger.Claims.Any(item => item.SourceEvidenceIds.Length > _options.MaximumSourcesPerClaim
                                      || item.ReviewerIds.Length > _options.MaximumReviewersPerClaim))
            throw new InvalidDataException("Claim ledger contains an over-bounded reference collection.");
        return ledger;
    }

    private static void RequireObservedReview(ArenaEvidenceAssertion provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.State != ArenaEvidenceState.Observed || string.IsNullOrWhiteSpace(provenance.ReferenceId))
            throw new InvalidDataException("Reviewer actions require observed provenance tied to a bounded reference ID.");
    }

    private static string StableId(string prefix, string input) =>
        $"{prefix}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24]}";
}
