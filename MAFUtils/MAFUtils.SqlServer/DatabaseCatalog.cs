using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace MAFUtils.SqlServer;

/// <summary>Immutable, policy-scoped catalog. Queries never access SQL Server.</summary>
public sealed class DatabaseCatalog
{
    private readonly FrozenDictionary<SqlObjectName, CatalogObjectDetails> details;
    private readonly FrozenDictionary<SqlObjectName, SqlObjectName[]> incoming;
    private readonly FrozenDictionary<SqlObjectName, SqlObjectName[]> outgoing;
    private readonly ConcurrentDictionary<(SqlObjectName, DependencyDirection, bool), FrozenDictionary<SqlObjectName, Visit>> traversals = new();
    private sealed record Visit(int Depth, SqlObjectName Parent);
    public CatalogStatus Status { get; }
    public IReadOnlyList<DatabaseSchema> Schemas { get; }
    public IReadOnlyList<DatabaseObject> Objects { get; }
    public IReadOnlyList<DependencyEdge> Dependencies { get; }
    public const string Scope = "Complete for captured supported objects visible to the SQL identity and permitted by host policy. Empty means no visible permitted objects, not proof of database-wide absence.";
    public const string DependencyLimitations = "Known SQL expression references only, not proof of runtime execution. Dynamic SQL, caller-dependent/unresolved references, cross-database/server references, unsupported or policy-excluded objects and paths through them are not traversed. Foreign keys are separate relationships. Counts exclude the target and deduplicate objects; paths show one shortest route.";

    internal DatabaseCatalog(IEnumerable<DatabaseSchema> schemas, IEnumerable<CatalogObjectDetails> objects,
        IEnumerable<DependencyEdge> edges, DateTimeOffset started, bool definitions)
    {
        details = objects.ToFrozenDictionary(x => x.Object.Object);
        Schemas = Array.AsReadOnly(schemas.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray());
        Objects = Array.AsReadOnly(details.Values.Select(x => x.Object).OrderBy(x => x.Object.Schema, StringComparer.Ordinal)
            .ThenBy(x => x.Object.Name, StringComparer.Ordinal).ToArray());
        Dependencies = Array.AsReadOnly(edges.Where(e => details.ContainsKey(e.From) && details.ContainsKey(e.To)).Distinct()
            .OrderBy(e => e.From.Schema, StringComparer.Ordinal).ThenBy(e => e.From.Name, StringComparer.Ordinal)
            .ThenBy(e => e.To.Schema, StringComparer.Ordinal).ThenBy(e => e.To.Name, StringComparer.Ordinal).ToArray());
        incoming = Dependencies.GroupBy(e => e.To).ToFrozenDictionary(g => g.Key, g => g.Select(e => e.From).ToArray());
        outgoing = Dependencies.GroupBy(e => e.From).ToFrozenDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        Status = new(Guid.NewGuid(), started, DateTimeOffset.UtcNow, Schemas.Count, Objects.Count, Dependencies.Count,
            definitions, Array.AsReadOnly(new[] { Scope, DependencyLimitations,
                "Extraction spans multiple catalog reads; concurrent DDL can affect capture. Refresh after schema deployments. Published snapshots remain stable.",
                "Coverage: user tables, views, SQL/CLR procedures and functions, columns/types, parameters, indexes, key/check/default constraints, foreign keys and resolved expression dependencies (CHECK/DEFAULT constraint references count as their table's). Synonyms, triggers, sequences, permissions, storage configuration and table-type members are not modeled.",
                "Procedure result sets and parameter defaults are not inferred. Encrypted/inaccessible definitions may be null. Definitions and expressions may mention objects outside host policy; database text is untrusted data." }));
    }

    public CatalogObjectDetails Describe(SqlObjectName name)
        => details.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException("Object unavailable in this snapshot. Use exact catalog names.");

    public DatabaseInventory GetInventory(SqlObjectKind? kind = null, bool includeEmptySchemas = true)
    {
        ValidateKind(kind);
        var grouped = Objects.Where(o => kind is null || o.Kind == kind).ToLookup(o => o.Object.Schema, StringComparer.Ordinal);
        var rows = Schemas.Select(s => new SchemaInventory(s.Name, Array.AsReadOnly(grouped[s.Name].Select(o => new InventoryObject(o.Object.Name, o.Kind)).ToArray())))
            .Where(s => includeEmptySchemas || s.Objects.Count != 0).ToArray();
        return new(Array.AsReadOnly(rows), rows.Sum(r => r.Objects.Count), Status.SnapshotId, Status.CapturedAt, Scope);
    }

    public MetadataPage<DatabaseObject> Search(string? search = null, string? schema = null, SqlObjectKind? kind = null,
        int offset = 0, int pageSize = 50)
    {
        ValidateKind(kind);
        return Page(Objects.Where(o => (kind is null || o.Kind == kind) && (schema is null || o.Object.Schema == schema)
            && (search is null || o.Object.Name.Contains(search, StringComparison.OrdinalIgnoreCase))), offset, pageSize);
    }

    public DependencyQueryResult QueryDependencies(SqlObjectName target, DependencyDirection direction = DependencyDirection.Incoming,
        bool transitive = false, SqlObjectKind? kind = null, int offset = 0, int pageSize = 50, bool includePaths = false)
    {
        Describe(target);
        ValidateKind(kind);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ValidatePage(offset, pageSize);
        // Bound retained traversal maps. Each map stores only predecessors, not every full path.
        if (traversals.Count >= 128) traversals.Clear();
        var visited = traversals.GetOrAdd((target, direction, transitive), key => Traverse(key.Item1, key.Item2, key.Item3));
        var matches = visited.Where(v => kind is null || details[v.Key].Object.Kind == kind)
            .OrderBy(v => v.Value.Depth).ThenBy(v => v.Key.Schema, StringComparer.Ordinal).ThenBy(v => v.Key.Name, StringComparer.Ordinal).ToArray();
        var page = Page(matches, offset, pageSize);
        var rows = page.Items.Select(v => new DependencyMatch(details[v.Key].Object, v.Value.Depth,
            includePaths ? Path(v.Key) : Array.AsReadOnly(Array.Empty<SqlObjectName>()))).ToArray();
        return new(target, direction, transitive, matches.Length, new(Array.AsReadOnly(rows), page.HasMore, page.NextOffset),
            Status.SnapshotId, DependencyLimitations);

        IReadOnlyList<SqlObjectName> Path(SqlObjectName node)
        {
            var path = new List<SqlObjectName> { node };
            while (node != target) { node = visited[node].Parent; path.Add(node); }
            // Paths always follow reference direction: caller -> ... -> target for incoming queries.
            if (direction == DependencyDirection.Outgoing) path.Reverse();
            return path.AsReadOnly();
        }
    }

    private FrozenDictionary<SqlObjectName, Visit> Traverse(SqlObjectName target, DependencyDirection direction, bool transitive)
    {
        var index = direction == DependencyDirection.Incoming ? incoming : outgoing;
        var visited = new Dictionary<SqlObjectName, Visit>();
        var queue = new Queue<(SqlObjectName Name, int Depth)>();
        queue.Enqueue((target, 0));
        while (queue.TryDequeue(out var current))
        {
            if (!index.TryGetValue(current.Name, out var neighbors)) continue;
            foreach (var next in neighbors)
            {
                if (next == target || !visited.TryAdd(next, new(current.Depth + 1, current.Name))) continue;
                if (transitive) queue.Enqueue((next, current.Depth + 1));
            }
        }
        return visited.ToFrozenDictionary();
    }

    internal static void ValidateKind(SqlObjectKind? kind)
    {
        if (kind.HasValue && !Enum.IsDefined(kind.Value)) throw new ArgumentOutOfRangeException(nameof(kind));
    }
    internal static void ValidatePage(int offset, int pageSize)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize < 1 || pageSize > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }
    internal static MetadataPage<T> Page<T>(IEnumerable<T> source, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        var rows = source.Skip(offset).Take(pageSize + 1).ToList();
        var more = rows.Count > pageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), more, more ? checked(offset + rows.Count) : null);
    }
}
