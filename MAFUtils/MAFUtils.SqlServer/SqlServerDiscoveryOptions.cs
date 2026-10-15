namespace MAFUtils.SqlServer;

/// <summary>Host-controlled limits and visibility policy. Copied when the service is constructed.</summary>
public sealed class SqlServerDiscoveryOptions
{
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int MaxPageSize { get; init; } = 100;
    public int MaxDetailRows { get; init; } = 200;
    public int MaxTextLength { get; init; } = 2_000;
    public int MaxDefinitionLength { get; init; } = 16_000;
    public bool IncludeDefinitions { get; init; }

    /// <summary>Filters the entire catalog, including schema lists. Null or an empty collection
    /// means no schema filter. Non-empty names use ordinal, case-sensitive comparison.</summary>
    public IReadOnlyCollection<string>? AllowedSchemas { get; init; }

    /// <summary>Null permits all visible objects; an empty collection permits none. Combined with AllowedSchemas.</summary>
    public IReadOnlyCollection<SqlObjectName>? AllowedObjects { get; init; }
}
