namespace AIArena.CodeIntelligence;

public sealed class ImpactQueryService
{
    public IReadOnlyList<ImpactNode> SearchSymbols(
        ImpactSnapshot snapshot,
        string query,
        int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }
        var boundedLimit = Math.Min(limit, 1_000);
        var value = query.Trim();
        return snapshot.Nodes
            .Where(IsSymbol)
            .Where(node =>
                node.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                || node.QualifiedName.Contains(value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Name.Equals(value, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(node => node.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Take(boundedLimit)
            .ToArray();
    }

    public ImpactNodeDetails? GetDetails(ImpactSnapshot snapshot, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }
        var node = snapshot.Nodes.FirstOrDefault(candidate =>
            candidate.Id.Equals(nodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return null;
        }

        var incoming = snapshot.Relationships
            .Where(edge => edge.TargetId == nodeId)
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        var outgoing = snapshot.Relationships
            .Where(edge => edge.SourceId == nodeId)
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        return new ImpactNodeDetails(
            node,
            incoming.Where(edge => edge.Kind == ImpactRelationshipKind.References).ToArray(),
            outgoing.Where(edge => edge.Kind == ImpactRelationshipKind.References).ToArray(),
            incoming.Where(edge => edge.Kind == ImpactRelationshipKind.Calls).ToArray(),
            outgoing.Where(edge => edge.Kind == ImpactRelationshipKind.Calls).ToArray(),
            outgoing.Where(edge =>
                edge.Kind is ImpactRelationshipKind.Inherits or ImpactRelationshipKind.Implements).ToArray(),
            incoming.Where(edge =>
                edge.Kind is ImpactRelationshipKind.Inherits or ImpactRelationshipKind.Implements).ToArray());
    }

    private static bool IsSymbol(ImpactNode node) =>
        node.Kind is ImpactNodeKind.Type
            or ImpactNodeKind.Method
            or ImpactNodeKind.Constructor
            or ImpactNodeKind.Property
            or ImpactNodeKind.Field
            or ImpactNodeKind.Event
            or ImpactNodeKind.Test;
}
