using System.Data;
using Microsoft.Data.SqlClient;

namespace MAFUtils.SqlServer;

/// <summary>Owns one policy-scoped session catalog. Reads SQL only on first use or explicit refresh.</summary>
public sealed partial class SqlServerMetadataService
{
    private readonly Func<SqlConnection> connectionFactory;
    private readonly SqlServerDiscoveryOptions options;
    private readonly SqlObjectName[]? allowedObjects;
    private readonly string[]? allowedSchemas;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private DatabaseCatalog? catalog;
    public bool DefinitionsEnabled => options.IncludeDefinitions;
    internal bool AgentRefreshEnabled => options.AllowAgentRefresh;

    public SqlServerMetadataService(string connectionString, SqlServerDiscoveryOptions? options = null)
        : this(CreateFactory(connectionString), options) { }

    /// <param name="connectionFactory">Returns a fresh unopened connection, owned by the loader. Keep its database and identity stable for this session.</param>
    /// <param name="options">Session visibility policy, definition access and response limits.</param>
    public SqlServerMetadataService(Func<SqlConnection> connectionFactory, SqlServerDiscoveryOptions? options = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.options = options ?? new();
        ValidateLimit(this.options.CommandTimeoutSeconds, 300, nameof(options.CommandTimeoutSeconds));
        ValidateLimit(this.options.MaxPageSize, 1000, nameof(options.MaxPageSize));
        ValidateLimit(this.options.MaxDetailRows, 2000, nameof(options.MaxDetailRows));
        ValidateLimit(this.options.MaxTextLength, 16000, nameof(options.MaxTextLength));
        ValidateLimit(this.options.MaxDefinitionLength, 100000, nameof(options.MaxDefinitionLength));
        allowedSchemas = this.options.AllowedSchemas?.ToArray();
        allowedObjects = this.options.AllowedObjects?.ToArray();
        if ((allowedSchemas?.Length ?? 0) + 2 * (allowedObjects?.Length ?? 0) > 500)
            throw new ArgumentException("Allow lists may contain at most 500 names in total (each object counts as two).", nameof(options));
        foreach (var schema in allowedSchemas ?? []) ValidateName(schema, nameof(options.AllowedSchemas));
        foreach (var obj in allowedObjects ?? [])
        {
            ArgumentNullException.ThrowIfNull(obj);
            ValidateNames(obj.Schema, obj.Name);
        }
    }

    /// <summary>Single-flight initialization. A failed/canceled load is not published and may be retried.</summary>
    public async Task<DatabaseCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Volatile.Read(ref catalog);
        if (current is not null) return current;
        await loadGate.WaitAsync(cancellationToken);
        try
        {
            current = catalog;
            if (current is not null) return current;
            var loaded = await LoadCatalogAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref catalog, loaded);
            return loaded;
        }
        finally { loadGate.Release(); }
    }

    /// <summary>Atomically replaces the snapshot. Readers can keep using the old snapshot during loading or after a failed refresh.</summary>
    public async Task<DatabaseCatalog> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        await loadGate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadCatalogAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref catalog, loaded);
            return loaded;
        }
        finally { loadGate.Release(); }
    }

    public async Task<DatabaseInventory> GetDatabaseInventoryAsync(SqlObjectKind? kind = null,
        bool includeEmptySchemas = true, CancellationToken cancellationToken = default)
    {
        DatabaseCatalog.ValidateKind(kind);
        return (await GetCatalogAsync(cancellationToken)).GetInventory(kind, includeEmptySchemas);
    }

    public async Task<DependencyQueryResult> QueryDependenciesAsync(string schema, string name,
        DependencyDirection direction = DependencyDirection.Incoming, bool transitive = false, SqlObjectKind? kind = null,
        int offset = 0, int? pageSize = null, bool includePaths = false, CancellationToken cancellationToken = default)
    {
        ValidateNames(schema, name);
        DatabaseCatalog.ValidateKind(kind);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        var size = PageSize(offset, pageSize);
        return (await GetCatalogAsync(cancellationToken)).QueryDependencies(new(schema, name), direction, transitive, kind, offset, size, includePaths);
    }

    public async Task<CatalogObjectDetails> DescribeObjectAsync(string schema, string name, CancellationToken cancellationToken = default)
    {
        ValidateNames(schema, name);
        return (await GetCatalogAsync(cancellationToken)).Describe(new(schema, name));
    }

    public async Task<MetadataPage<DatabaseSchema>> ListSchemasAsync(string? search = null,
        int offset = 0, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var size = PageSize(offset, pageSize);
        ValidateSearch(search);
        var snapshot = await GetCatalogAsync(cancellationToken);
        return DatabaseCatalog.Page(snapshot.Schemas.Where(s => search is null || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)), offset, size);
    }

    public async Task<MetadataPage<DatabaseObject>> ListDatabaseObjectsAsync(string? search = null,
        string? schema = null, SqlObjectKind? kind = null, int offset = 0, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var size = ValidateListQuery(search, schema, kind, offset, pageSize);
        return (await GetCatalogAsync(cancellationToken)).Search(search, schema, kind, offset, size);
    }

    internal int ValidateListQuery(string? search, string? schema, SqlObjectKind? kind, int offset, int? pageSize)
    {
        var size = PageSize(offset, pageSize);
        ValidateSearch(search);
        if (schema is not null) ValidateName(schema, nameof(schema));
        DatabaseCatalog.ValidateKind(kind);
        return size;
    }

    public async Task<ReferencingObjectsMetadata> ListReferencingObjectsAsync(string schema, string name,
        SqlObjectKind? kind = null, int offset = 0, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var result = await QueryDependenciesAsync(schema, name, kind: kind, offset: offset, pageSize: pageSize, cancellationToken: cancellationToken);
        return new(result.Target, new(Array.AsReadOnly(result.Matches.Items.Select(x => x.Object).ToArray()), result.Matches.HasMore, result.Matches.NextOffset));
    }

    public Task<TableMetadata> DescribeTableAsync(string schema, string name, CancellationToken cancellationToken = default)
        => DescribeTabularAsync(schema, name, SqlObjectKind.Table, cancellationToken);
    public Task<TableMetadata> DescribeViewAsync(string schema, string name, CancellationToken cancellationToken = default)
        => DescribeTabularAsync(schema, name, SqlObjectKind.View, cancellationToken);
    public Task<RoutineMetadata> DescribeProcedureAsync(string schema, string name, CancellationToken cancellationToken = default)
        => DescribeRoutineAsync(schema, name, SqlObjectKind.Procedure, cancellationToken);
    public Task<RoutineMetadata> DescribeFunctionAsync(string schema, string name, CancellationToken cancellationToken = default)
        => DescribeRoutineAsync(schema, name, SqlObjectKind.Function, cancellationToken);

    private async Task<TableMetadata> DescribeTabularAsync(string schema, string name, SqlObjectKind kind, CancellationToken ct)
    {
        var item = await DescribeObjectAsync(schema, name, ct);
        if (item.Object.Kind != kind) throw KindMismatch(kind);
        return new(Bounded(item.Object), Section(item.Columns.Select(c => c with { Description = Bounded(c.Description) }).ToArray()),
            Section(item.IndexColumns), Section(item.Relationships));
    }
    private async Task<RoutineMetadata> DescribeRoutineAsync(string schema, string name, SqlObjectKind kind, CancellationToken ct)
    {
        var item = await DescribeObjectAsync(schema, name, ct);
        if (item.Object.Kind != kind) throw KindMismatch(kind);
        return new(Bounded(item.Object), Section(item.Parameters), Section(item.Columns.Select(c => c with { Description = Bounded(c.Description) }).ToArray()));
    }
    public async Task<ObjectDefinition> GetObjectDefinitionAsync(string schema, string name, CancellationToken cancellationToken = default)
    {
        if (!DefinitionsEnabled) throw new InvalidOperationException("SQL definition access is disabled by the host.");
        var item = await DescribeObjectAsync(schema, name, cancellationToken);
        return item.Definition with { Definition = Truncate(item.Definition.Definition, options.MaxDefinitionLength) };
    }

    private static KeyNotFoundException KindMismatch(SqlObjectKind kind)
        => new($"Object unavailable as a {kind} in this snapshot. Use DescribeObjectAsync for any object kind.");
    private DatabaseObject Bounded(DatabaseObject value) => value with { Description = Bounded(value.Description) };
    private MetadataText? Bounded(MetadataText? value) => Truncate(value, options.MaxTextLength);
    private static MetadataText? Truncate(MetadataText? value, int maximum)
        => value is null ? null : new(value.Value[..TextBoundary(value.Value, Math.Min(value.Value.Length, maximum))], value.Value.Length > maximum);
    /// <summary>Moves a cut point back one character rather than split a UTF-16 surrogate pair, which JSON
    /// serialization would otherwise turn into U+FFFD replacement characters.</summary>
    internal static int TextBoundary(string text, int end)
        => end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end - 1 : end;
    private MetadataSection<T> Section<T>(IReadOnlyList<T> rows) => new(Array.AsReadOnly(rows.Take(options.MaxDetailRows).ToArray()), rows.Count > options.MaxDetailRows);
    private int PageSize(int offset, int? pageSize)
    {
        var size = pageSize ?? Math.Min(50, options.MaxPageSize);
        DatabaseCatalog.ValidatePage(offset, size);
        if (size > options.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (offset > int.MaxValue - options.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(offset));
        return size;
    }
    private static void ValidateSearch(string? search)
    {
        if (search?.Length > 128) throw new ArgumentException("Search is limited to 128 characters.", nameof(search));
    }
    private static void ValidateLimit(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
    internal static void ValidateNames(string schema, string name) { ValidateName(schema, nameof(schema)); ValidateName(name, nameof(name)); }
    private static void ValidateName(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Provide an unquoted identifier of 1 to 128 characters.", parameter);
    }
    private static Func<SqlConnection> CreateFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _ = new SqlConnectionStringBuilder(connectionString);
        return () => new SqlConnection(connectionString);
    }
    private string Policy(SqlCommand command, string schemaColumn, string nameColumn)
    {
        string Equal(string column, string value)
        {
            var parameter = "@policy" + command.Parameters.Count;
            command.Parameters.Add(new SqlParameter(parameter, SqlDbType.NVarChar, 128) { Value = value });
            return $"({column} COLLATE Latin1_General_100_BIN2={parameter} COLLATE Latin1_General_100_BIN2 AND DATALENGTH({column})=DATALENGTH({parameter}))";
        }
        var schemas = allowedSchemas is null || allowedSchemas.Length == 0 ? "1=1" : string.Join(" OR ", allowedSchemas.Select(s => Equal(schemaColumn, s)));
        var objects = allowedObjects is null ? "1=1" : allowedObjects.Length == 0 ? "1=0" : string.Join(" OR ", allowedObjects.Select(o => $"({Equal(schemaColumn, o.Schema)} AND {Equal(nameColumn, o.Name)})"));
        return $"(({schemas}) AND ({objects}))";
    }
}
