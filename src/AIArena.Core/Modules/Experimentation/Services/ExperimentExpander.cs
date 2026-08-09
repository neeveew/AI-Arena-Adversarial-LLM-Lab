using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaExperimentVariantValue(
    string DimensionId,
    string Parameter,
    string Value);

public sealed record ArenaExperimentVariant(
    string ProviderProfileId,
    ImmutableArray<ArenaExperimentVariantValue> Values,
    string VariantFingerprint);

public sealed record ArenaExperimentCellPlan(
    string ExperimentId,
    string ExperimentFingerprint,
    string ProviderProfileId,
    ImmutableArray<ArenaExperimentVariantValue> Values,
    string VariantFingerprint,
    int Repetition,
    string CellKey,
    string RunId);

public sealed record ArenaExperimentExpansion(
    string ExperimentId,
    string ExperimentFingerprint,
    ImmutableArray<ArenaExperimentVariant> Variants,
    ImmutableArray<ArenaExperimentCellPlan> Cells);

/// <summary>
/// Produces the provider/dimension Cartesian matrix in semantic ordinal order.
/// Fingerprints are content-derived and independent of process, machine, path,
/// culture, and enumeration order.
/// </summary>
public static class ExperimentExpander
{
    // A cell is not useful unless its one-file run record can be retained. Keep
    // the expansion ceiling compatible with the default durable store instead
    // of accepting matrices that the runner can never persist completely.
    public const int MaximumCells = ArenaExperimentRunStoreOptions.DefaultMaximumRuns;

    public static ArenaExperimentExpansion Expand(ArenaExperimentContract experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        var validation = ArenaContractCodec.Validate(experiment);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Experiment does not satisfy its frozen contract.");
        }

        var experimentFingerprint = ArenaExperimentFingerprints.Experiment(experiment);
        var dimensions = experiment.Dimensions
            .OrderBy(item => item.Parameter, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var providers = experiment.ProviderProfileIds
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToImmutableArray();

        long variantCount = providers.Length;
        foreach (var dimension in dimensions)
        {
            variantCount = checked(variantCount * dimension.Values.Length);
        }

        var cellCount = checked(variantCount * experiment.Repetitions);
        if (cellCount > MaximumCells)
        {
            throw new InvalidDataException($"Experiment expands beyond the {MaximumCells} cell safety limit.");
        }

        var variants = ImmutableArray.CreateBuilder<ArenaExperimentVariant>((int)variantCount);
        foreach (var provider in providers)
        {
            ExpandDimension(0, provider, [], dimensions, variants);
        }

        var orderedVariants = variants
            .OrderBy(VariantSortKey, StringComparer.Ordinal)
            .ToImmutableArray();
        var cells = ImmutableArray.CreateBuilder<ArenaExperimentCellPlan>((int)cellCount);
        foreach (var variant in orderedVariants)
        {
            for (var repetition = 0; repetition < experiment.Repetitions; repetition++)
            {
                var cellKey = ArenaExperimentRunPolicy.CreateCellKey(
                    experimentFingerprint,
                    variant.VariantFingerprint,
                    repetition);
                cells.Add(new(
                    experiment.Id,
                    experimentFingerprint,
                    variant.ProviderProfileId,
                    variant.Values,
                    variant.VariantFingerprint,
                    repetition,
                    cellKey,
                    $"run:{cellKey["cell:".Length..]}"));
            }
        }

        return new(experiment.Id, experimentFingerprint, orderedVariants, cells.MoveToImmutable());
    }

    private static void ExpandDimension(
        int index,
        string providerProfileId,
        ImmutableArray<ArenaExperimentVariantValue> values,
        ImmutableArray<ArenaExperimentDimension> dimensions,
        ImmutableArray<ArenaExperimentVariant>.Builder output)
    {
        if (index == dimensions.Length)
        {
            var descriptor = VariantDescriptor(providerProfileId, values);
            output.Add(new(providerProfileId, values, Hash(descriptor)));
            return;
        }

        var dimension = dimensions[index];
        foreach (var value in dimension.Values.OrderBy(item => item, StringComparer.Ordinal))
        {
            ExpandDimension(
                index + 1,
                providerProfileId,
                values.Add(new(dimension.Id, dimension.Parameter, value)),
                dimensions,
                output);
        }
    }

    private static string VariantSortKey(ArenaExperimentVariant variant) =>
        VariantDescriptor(variant.ProviderProfileId, variant.Values);

    private static string VariantDescriptor(
        string providerProfileId,
        ImmutableArray<ArenaExperimentVariantValue> values)
    {
        var builder = new StringBuilder();
        builder.Append("provider\0").Append(providerProfileId).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Parameter).Append('\0')
                .Append(value.DimensionId).Append('\0')
                .Append(value.Value).Append('\n');
        }

        return builder.ToString();
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
