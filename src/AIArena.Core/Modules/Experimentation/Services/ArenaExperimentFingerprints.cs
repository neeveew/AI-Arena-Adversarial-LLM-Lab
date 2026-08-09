using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Frozen semantic identities for experimentation artifacts. Presentation,
/// lifecycle state, capture timestamps, and evidence receipts deliberately do
/// not change execution/content identity.
/// </summary>
public static class ArenaExperimentFingerprints
{
    public static string Experiment(ArenaExperimentContract experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        var fingerprint = new FingerprintWriter();
        fingerprint.Add(ArenaContractSchemas.Experiment);
        fingerprint.Add(experiment.Id);
        fingerprint.Add(experiment.ScenarioPackId);
        fingerprint.Add(experiment.BenchmarkPackId);
        fingerprint.AddRange(experiment.ProviderProfileIds);
        fingerprint.AddRange(experiment.RubricIds);
        fingerprint.AddRange(experiment.FaultProfileIds);
        foreach (var dimension in experiment.Dimensions)
        {
            fingerprint.Add(dimension.Id);
            fingerprint.Add(dimension.Parameter);
            fingerprint.AddRange(dimension.Values);
        }

        fingerprint.Add(experiment.Repetitions);
        fingerprint.Add(experiment.TurnBudget);
        fingerprint.Add(experiment.MaxParallelism);
        fingerprint.AddRange(experiment.BranchIds);
        return fingerprint.Hash();
    }

    public static string ScenarioPackContent(
        ImmutableArray<ArenaScenarioInvariant> invariants,
        ImmutableArray<ArenaScenarioDefinition> scenarios)
    {
        var fingerprint = new FingerprintWriter();
        fingerprint.Add(ArenaContractSchemas.ScenarioPack);
        foreach (var invariant in invariants)
        {
            fingerprint.Add(invariant.Id);
            fingerprint.Add(invariant.RuleId);
            fingerprint.Add(invariant.ExpectedOutcome);
            fingerprint.Add(invariant.Required);
        }

        foreach (var scenario in scenarios)
        {
            fingerprint.Add(scenario.Id);
            fingerprint.Add(scenario.MatchSetupReference);
            fingerprint.Add(scenario.SetupFingerprint);
            fingerprint.Add(scenario.ScenarioSeed);
            fingerprint.Add(scenario.TurnBudget);
            fingerprint.AddRange(scenario.Tags);
            fingerprint.AddRange(scenario.InvariantIds);
            fingerprint.AddRange(scenario.RequiredEvidenceIds);
        }

        return fingerprint.Hash();
    }

    public static string BenchmarkPackContent(
        string scenarioPackId,
        ImmutableArray<ArenaBenchmarkCase> cases)
    {
        var fingerprint = new FingerprintWriter();
        fingerprint.Add(ArenaContractSchemas.BenchmarkPack);
        fingerprint.Add(scenarioPackId);
        foreach (var benchmark in cases)
        {
            fingerprint.Add(benchmark.Id);
            fingerprint.Add(benchmark.ScenarioId);
            fingerprint.AddRange(benchmark.RubricIds);
            fingerprint.Add(benchmark.Repetitions);
            fingerprint.AddRange(benchmark.RequiredProviderCapabilities);
            fingerprint.AddRange(benchmark.RequiredEvidenceIds);
        }

        return fingerprint.Hash();
    }

    private sealed class FingerprintWriter
    {
        private readonly StringBuilder _builder = new();

        public void Add(string? value)
        {
            var normalized = value ?? "";
            _builder.Append(normalized.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(normalized)
                .Append(';');
        }

        public void Add(int value) => Add(value.ToString(CultureInfo.InvariantCulture));

        public void Add(bool value) => Add(value ? "1" : "0");

        public void AddRange(ImmutableArray<string> values)
        {
            Add(values.IsDefault ? -1 : values.Length);
            if (values.IsDefault) return;
            foreach (var value in values) Add(value);
        }

        public string Hash() => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(_builder.ToString())));
    }
}
