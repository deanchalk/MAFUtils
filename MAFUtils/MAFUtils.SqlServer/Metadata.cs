namespace MAFUtils.SqlServer;

public sealed record SqlObjectName(string Schema, string Name);
public sealed record DatabaseSchema(string Name);
public enum SqlObjectKind { Table, View, Procedure, Function }
public sealed record MetadataText(string Value, bool IsTruncated);
public sealed record MetadataPage<T>(IReadOnlyList<T> Items, bool HasMore, int? NextOffset);
public sealed record MetadataSection<T>(IReadOnlyList<T> Items, bool IsTruncated);
public sealed record DatabaseObject(SqlObjectName Object, SqlObjectKind Kind, MetadataText? Description);

/// <summary>Length is the SQL Server catalog length in bytes (-1 means MAX), not a character count.</summary>
public sealed record SqlTypeMetadata(string Schema, string Name, short MaxLengthBytes, byte Precision, byte Scale);
public sealed record ColumnMetadata(int Ordinal, string Name, SqlTypeMetadata Type, bool IsNullable,
    bool IsIdentity, bool IsComputed, MetadataText? Description);
/// <summary>One row per index column; key ordinal is zero for non-key columns.</summary>
public sealed record IndexColumnMetadata(string Index, string IndexType, bool IsUnique, bool IsPrimaryKey,
    string Column, int KeyOrdinal, bool IsIncluded, bool IsDescending, bool IsDisabled, bool HasFilter);
/// <summary>One row per column pair, for both incoming and outgoing foreign keys.</summary>
public sealed record RelationshipMetadata(string Constraint, int Ordinal, SqlObjectName FromObject,
    string FromColumn, SqlObjectName ToObject, string ToColumn, bool IsDisabled, bool IsNotTrusted);
public sealed record TableMetadata(DatabaseObject Object, MetadataSection<ColumnMetadata> Columns,
    MetadataSection<IndexColumnMetadata> IndexColumns, MetadataSection<RelationshipMetadata> Relationships);
public sealed record ParameterMetadata(int Ordinal, string Name, SqlTypeMetadata Type, bool IsOutput, bool IsReadOnly);
/// <summary>Ordinal zero is a function return value. T-SQL parameter defaults are not inferred from catalog flags.</summary>
public sealed record RoutineMetadata(DatabaseObject Object, MetadataSection<ParameterMetadata> Parameters,
    MetadataSection<ColumnMetadata> ResultColumns);
/// <summary>A null definition means it is unavailable (for example permissions, encryption or a non-SQL module).</summary>
public sealed record ObjectDefinition(SqlObjectName Object, MetadataText? Definition);

/// <summary>Direct, resolved references recorded in the current database's SQL expression dependency catalog.</summary>
public sealed record ReferencingObjectsMetadata(SqlObjectName Target, MetadataPage<DatabaseObject> References)
{
    public string Limitations => "Direct, resolved SQL expression dependencies in the current database only. " +
        "Results are restricted by permissions and host policy. Dynamic SQL, unresolved references, " +
        "cross-database references, indirect references through other objects, and foreign-key relationships " +
        "are not included. An empty result does not prove the object is unused.";
}

public enum DependencyDirection { Incoming, Outgoing }
public sealed record DependencyEdge(SqlObjectName From, SqlObjectName To);
public sealed record DependencyMatch(DatabaseObject Object, int Depth, IReadOnlyList<SqlObjectName> Path);
public sealed record DependencyQueryResult(SqlObjectName Target, DependencyDirection Direction, bool Transitive,
    int TotalCount, MetadataPage<DependencyMatch> Matches, Guid SnapshotId, string Limitations);
public sealed record InventoryObject(string Name, SqlObjectKind Kind);
public sealed record SchemaInventory(string Schema, IReadOnlyList<InventoryObject> Objects);
public sealed record DatabaseInventory(IReadOnlyList<SchemaInventory> Schemas, int TotalObjects,
    Guid SnapshotId, DateTimeOffset CapturedAt, string Scope)
{
    public bool IsComplete => true; // Complete within the captured, permitted catalog; not a claim of full SQL visibility.
}
public sealed record CatalogStatus(Guid SnapshotId, DateTimeOffset CaptureStartedAt, DateTimeOffset CapturedAt,
    int SchemaCount, int ObjectCount, int DependencyCount, bool DefinitionsIncluded,
    IReadOnlyList<string> Limitations);
public sealed record ConstraintMetadata(string Name, string Kind, string? Column, string? Definition,
    bool IsDisabled, bool IsNotTrusted);
public sealed record IndexMetadata(string Name, string Type, bool IsUnique, bool IsPrimaryKey,
    bool IsUniqueConstraint, bool IsDisabled, string? FilterDefinition);
public sealed record ColumnExpression(string Column, string? ComputedDefinition, string? DefaultDefinition);
/// <summary>Complete captured details, unaffected by legacy response truncation limits.</summary>
public sealed record CatalogObjectDetails(DatabaseObject Object, IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<ParameterMetadata> Parameters, IReadOnlyList<IndexMetadata> Indexes,
    IReadOnlyList<IndexColumnMetadata> IndexColumns, IReadOnlyList<RelationshipMetadata> Relationships,
    IReadOnlyList<ConstraintMetadata> Constraints, IReadOnlyList<ColumnExpression> ColumnExpressions,
    ObjectDefinition Definition);
public sealed record ObjectStructure(DatabaseObject Object, IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<ParameterMetadata> Parameters, IReadOnlyList<IndexMetadata> Indexes,
    IReadOnlyList<IndexColumnMetadata> IndexColumns, IReadOnlyList<RelationshipMetadata> Relationships,
    IReadOnlyList<ConstraintMetadata> Constraints, IReadOnlyList<ColumnExpression> ColumnExpressions);
