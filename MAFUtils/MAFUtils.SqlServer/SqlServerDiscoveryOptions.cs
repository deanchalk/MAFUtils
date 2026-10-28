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

    /// <summary>Also list built-in schemas (guest, INFORMATION_SCHEMA, sys and fixed database-role schemas)
    /// that contain no captured objects. Built-in schemas containing captured objects are always listed.</summary>
    public bool IncludeBuiltInSchemas { get; init; }

    /// <summary>Register the RefreshCatalog agent tool. Defaults to true; set false to keep reloads host-controlled.</summary>
    public bool AllowAgentRefresh { get; init; } = true;

    /// <summary>Filters the entire catalog, including schema lists. Null or an empty collection
    /// means no schema filter. Non-empty names use ordinal, case-sensitive comparison.</summary>
    public IReadOnlyCollection<string>? AllowedSchemas { get; init; }

    /// <summary>Null permits all visible objects; an empty collection permits none. Combined with AllowedSchemas.</summary>
    public IReadOnlyCollection<SqlObjectName>? AllowedObjects { get; init; }
}
