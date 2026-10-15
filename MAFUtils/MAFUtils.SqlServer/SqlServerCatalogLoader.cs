using Microsoft.Data.SqlClient;

namespace MAFUtils.SqlServer;

public sealed partial class SqlServerMetadataService
{
    // Unbounded bulk extraction: response limits must never truncate the underlying snapshot.
    private async Task<DatabaseCatalog> LoadCatalogAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        await using var connection = connectionFactory();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        var policy = Policy(command, "s.name", "o.name");
        command.CommandText = $"""
            SET NOCOUNT ON;
            SELECT o.object_id, o.schema_id, s.name AS schema_name, o.name, o.type
            INTO #catalog_objects
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT') AND {policy};
            CREATE UNIQUE CLUSTERED INDEX IX_catalog_objects ON #catalog_objects(object_id);

            SELECT name FROM sys.schemas ORDER BY name;

            SELECT o.object_id,o.schema_name,o.name,o.type,CONVERT(nvarchar(max),ep.value)
            FROM #catalog_objects o LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=o.object_id
                AND ep.minor_id=0 AND ep.name=N'MS_Description' ORDER BY o.object_id;

            SELECT c.object_id,c.column_id,c.name,ts.name,t.name,c.max_length,c.precision,c.scale,
                c.is_nullable,c.is_identity,c.is_computed,CONVERT(nvarchar(max),ep.value),cc.definition,dc.definition
            FROM sys.columns c JOIN #catalog_objects o ON o.object_id=c.object_id
            JOIN sys.types t ON t.user_type_id=c.user_type_id JOIN sys.schemas ts ON ts.schema_id=t.schema_id
            LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=c.object_id AND ep.minor_id=c.column_id AND ep.name=N'MS_Description'
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
            ORDER BY c.object_id,c.column_id;

            SELECT p.object_id,p.parameter_id,p.name,ts.name,t.name,p.max_length,p.precision,p.scale,p.is_output,p.is_readonly
            FROM sys.parameters p JOIN #catalog_objects o ON o.object_id=p.object_id
            JOIN sys.types t ON t.user_type_id=p.user_type_id JOIN sys.schemas ts ON ts.schema_id=t.schema_id
            ORDER BY p.object_id,p.parameter_id;

            SELECT i.object_id,i.name,i.type_desc,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.filter_definition
            FROM sys.indexes i JOIN #catalog_objects o ON o.object_id=i.object_id
            WHERE i.index_id>0 AND i.is_hypothetical=0 ORDER BY i.object_id,i.index_id;

            SELECT i.object_id,i.name,i.type_desc,i.is_unique,i.is_primary_key,c.name,
                CONVERT(int,ic.key_ordinal),ic.is_included_column,ic.is_descending_key,i.is_disabled,i.has_filter
            FROM sys.indexes i JOIN #catalog_objects o ON o.object_id=i.object_id
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.index_id>0 AND i.is_hypothetical=0 ORDER BY i.object_id,i.index_id,ic.index_column_id;

            SELECT fk.parent_object_id,fk.referenced_object_id,fk.name,fkc.constraint_column_id,pc.name,rc.name,fk.is_disabled,fk.is_not_trusted
            FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
            JOIN #catalog_objects po ON po.object_id=fk.parent_object_id
            JOIN #catalog_objects ro ON ro.object_id=fk.referenced_object_id
            JOIN sys.columns pc ON pc.object_id=po.object_id AND pc.column_id=fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id=ro.object_id AND rc.column_id=fkc.referenced_column_id
            ORDER BY fk.object_id,fkc.constraint_column_id;

            SELECT k.parent_object_id,k.name,k.type_desc,c.name,k.definition,k.is_disabled,k.is_not_trusted
            FROM sys.check_constraints k JOIN #catalog_objects o ON o.object_id=k.parent_object_id
            LEFT JOIN sys.columns c ON c.object_id=k.parent_object_id AND c.column_id=k.parent_column_id
            UNION ALL
            SELECT k.parent_object_id,k.name,k.type_desc,c.name,k.definition,CONVERT(bit,0),CONVERT(bit,0)
            FROM sys.default_constraints k JOIN #catalog_objects o ON o.object_id=k.parent_object_id
            LEFT JOIN sys.columns c ON c.object_id=k.parent_object_id AND c.column_id=k.parent_column_id
            UNION ALL
            SELECT k.parent_object_id,k.name,k.type_desc,NULL,NULL,CONVERT(bit,0),CONVERT(bit,0)
            FROM sys.key_constraints k JOIN #catalog_objects o ON o.object_id=k.parent_object_id;

            SELECT DISTINCT d.referencing_id,d.referenced_id
            FROM sys.sql_expression_dependencies d
            JOIN #catalog_objects src ON src.object_id=d.referencing_id
            JOIN #catalog_objects dst ON dst.object_id=d.referenced_id
            WHERE d.referencing_class=1 AND d.referenced_class=1 AND d.is_caller_dependent=0
                AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL;

            {(DefinitionsEnabled ? "SELECT m.object_id,m.definition FROM sys.sql_modules m JOIN #catalog_objects o ON o.object_id=m.object_id;" : "SELECT CAST(NULL AS int),CAST(NULL AS nvarchar(max)) WHERE 1=0;")}
            DROP TABLE #catalog_objects;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var schemas = new List<DatabaseSchema>();
        var items = new Dictionary<int, DetailBuilder>();
        var edges = new List<DependencyEdge>();
        while (await reader.ReadAsync(ct)) schemas.Add(new(reader.GetString(0)));
        await Next();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(3).Trim() switch
            {
                "U" => SqlObjectKind.Table, "V" => SqlObjectKind.View, "P" or "PC" => SqlObjectKind.Procedure, _ => SqlObjectKind.Function
            };
            items.Add(reader.GetInt32(0), new(new(new(reader.GetString(1), reader.GetString(2)), kind, Text(reader, 4))));
        }
        await Next();
        while (await reader.ReadAsync(ct))
        {
            var item = items[reader.GetInt32(0)];
            item.Columns.Add(new(reader.GetInt32(1), reader.GetString(2), Type(reader, 3), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10), Text(reader, 11)));
            if (!reader.IsDBNull(12) || !reader.IsDBNull(13)) item.Expressions.Add(new(reader.GetString(2), String(reader, 12), String(reader, 13)));
        }
        await Next();
        while (await reader.ReadAsync(ct)) items[reader.GetInt32(0)].Parameters.Add(new(reader.GetInt32(1), reader.GetString(2), Type(reader, 3), reader.GetBoolean(8), reader.GetBoolean(9)));
        await Next();
        while (await reader.ReadAsync(ct)) items[reader.GetInt32(0)].Indexes.Add(new(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), String(reader, 7)));
        await Next();
        while (await reader.ReadAsync(ct)) items[reader.GetInt32(0)].IndexColumns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetString(5), reader.GetInt32(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10)));
        await Next();
        while (await reader.ReadAsync(ct))
        {
            var from = items[reader.GetInt32(0)];
            var to = items[reader.GetInt32(1)];
            var relationship = new RelationshipMetadata(reader.GetString(2), reader.GetInt32(3), from.Object.Object, reader.GetString(4), to.Object.Object, reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7));
            from.Relationships.Add(relationship);
            if (from != to) to.Relationships.Add(relationship);
        }
        await Next();
        while (await reader.ReadAsync(ct)) items[reader.GetInt32(0)].Constraints.Add(new(reader.GetString(1), reader.GetString(2), String(reader, 3), String(reader, 4), reader.GetBoolean(5), reader.GetBoolean(6)));
        await Next();
        while (await reader.ReadAsync(ct)) edges.Add(new(items[reader.GetInt32(0)].Object.Object, items[reader.GetInt32(1)].Object.Object));
        await Next();
        while (await reader.ReadAsync(ct)) items[reader.GetInt32(0)].Definition = Text(reader, 1);
        // Consume completion so late SQL errors cannot publish a seemingly successful snapshot.
        while (await reader.NextResultAsync(ct)) while (await reader.ReadAsync(ct)) { }
        var schemaSet = items.Values.Select(x => x.Object.Object.Schema).ToHashSet(StringComparer.Ordinal);
        var permittedSchemas = schemas.Where(s => (allowedSchemas is null || allowedSchemas.Length == 0 || allowedSchemas.Contains(s.Name, StringComparer.Ordinal))
            && (allowedObjects is null || schemaSet.Contains(s.Name)));
        return new(permittedSchemas, items.Values.Select(x => x.Build()), edges, started, DefinitionsEnabled);

        async Task Next()
        {
            if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Incomplete catalog extraction.");
        }
    }

    private static string? String(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static MetadataText? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : new(r.GetString(i), false);
    private static SqlTypeMetadata Type(SqlDataReader r, int i) => new(r.GetString(i), r.GetString(i + 1), r.GetInt16(i + 2), r.GetByte(i + 3), r.GetByte(i + 4));
    private sealed class DetailBuilder(DatabaseObject obj)
    {
        public DatabaseObject Object { get; } = obj;
        public List<ColumnMetadata> Columns { get; } = [];
        public List<ParameterMetadata> Parameters { get; } = [];
        public List<IndexMetadata> Indexes { get; } = [];
        public List<IndexColumnMetadata> IndexColumns { get; } = [];
        public List<RelationshipMetadata> Relationships { get; } = [];
        public List<ConstraintMetadata> Constraints { get; } = [];
        public List<ColumnExpression> Expressions { get; } = [];
        public MetadataText? Definition { get; set; }
        public CatalogObjectDetails Build() => new(Object, Columns.AsReadOnly(), Parameters.AsReadOnly(), Indexes.AsReadOnly(),
            IndexColumns.AsReadOnly(), Relationships.AsReadOnly(), Constraints.AsReadOnly(), Expressions.AsReadOnly(), new(Object.Object, Definition));
    }
}
