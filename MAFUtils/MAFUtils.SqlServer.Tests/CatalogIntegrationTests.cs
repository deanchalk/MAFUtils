using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;

namespace MAFUtils.SqlServer.Tests;

public sealed class CatalogIntegrationTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private async Task Execute(string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [SqlServerFact]
    public async Task InventoryIsCompleteBeyondPageLimitsAndDetailsAreNotTruncatedInMemory()
    {
        await Execute("CREATE SCHEMA inventory;");
        await Execute(string.Join("\n", Enumerable.Range(0, 65).Select(i => $"CREATE TABLE inventory.T{i:D2}(Id int PRIMARY KEY, Amount int DEFAULT 3 CHECK(Amount>0), Doubled AS Amount*2);")));
        var service = new SqlServerMetadataService(database.ConnectionString, new()
        {
            AllowedSchemas = ["inventory", "Empty%_"], MaxPageSize = 1, MaxDetailRows = 1, MaxTextLength = 1
        });
        var snapshot = await service.GetCatalogAsync();
        var inventory = await service.GetDatabaseInventoryAsync(SqlObjectKind.Table);
        Assert.True(inventory.IsComplete);
        Assert.Equal(65, inventory.TotalObjects);
        Assert.Equal(65, inventory.Schemas.Single(s => s.Schema == "inventory").Objects.Count);
        Assert.Empty(inventory.Schemas.Single(s => s.Schema == "Empty%_").Objects);
        Assert.Single((await service.ListDatabaseObjectsAsync()).Items);
        var details = snapshot.Describe(new("inventory", "T00"));
        Assert.Equal(3, details.Columns.Count);
        Assert.Contains(details.Constraints, c => c.Kind == "CHECK_CONSTRAINT" && c.Definition!.Contains("Amount"));
        Assert.Contains(details.Constraints, c => c.Kind == "DEFAULT_CONSTRAINT");
        Assert.Contains(details.Constraints, c => c.Kind == "PRIMARY_KEY_CONSTRAINT");
        Assert.Contains(details.ColumnExpressions, c => c.Column == "Doubled" && c.ComputedDefinition != null);
        Assert.Contains(details.ColumnExpressions, c => c.Column == "Amount" && c.DefaultDefinition != null);
        Assert.True(Assert.Single(details.Indexes).IsPrimaryKey);
        var tool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "GetDatabaseInventory");
        var json = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments { ["kind"] = "Table" }));
        Assert.Contains("T64", json);
        Assert.Contains("\"totalObjects\":65", json);
        Assert.Contains("\"isComplete\":true", json);
    }

    [SqlServerFact]
    public async Task ConcurrentInitializationLoadsOnceAndAllSubsequentQueriesStayInMemory()
    {
        var calls = 0;
        var service = new SqlServerMetadataService(() => { Interlocked.Increment(ref calls); return new(database.ConnectionString); });
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetCatalogAsync()));
        Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
        Assert.Equal(1, calls);
        await service.ListSchemasAsync();
        await service.ListDatabaseObjectsAsync();
        await service.DescribeTableAsync("sales", "Customers");
        await service.GetDatabaseInventoryAsync();
        await service.QueryDependenciesAsync("sales", "Customers", transitive: true);
        foreach (var tool in SqlServerDiscoveryTools.Create(service).Where(t => t.Name is "GetCatalogStatus" or "GetDatabaseInventory"))
            await tool.InvokeAsync(new AIFunctionArguments());
        Assert.Equal(1, calls);
        Assert.Throws<NotSupportedException>(() => ((IList<ColumnMetadata>)snapshots[0].Describe(new("sales", "Customers")).Columns).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DatabaseObject>)snapshots[0].Objects).Clear());
    }

    [SqlServerFact]
    public async Task RefreshIsExplicitAndFailedOrCanceledRefreshPreservesOldSnapshot()
    {
        var fail = false;
        var service = new SqlServerMetadataService(() => fail ? throw new InvalidOperationException("Simulated factory failure") : new(database.ConnectionString));
        var before = await service.GetCatalogAsync();
        await Execute("CREATE TABLE dbo.AfterCapture(Id int);");
        Assert.DoesNotContain((await service.GetDatabaseInventoryAsync()).Schemas.SelectMany(s => s.Objects), o => o.Name == "AfterCapture");
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshCatalogAsync());
        Assert.Same(before, await service.GetCatalogAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshCatalogAsync(canceled.Token));
        Assert.Same(before, await service.GetCatalogAsync());
        fail = false;
        var after = await service.RefreshCatalogAsync();
        Assert.NotEqual(before.Status.SnapshotId, after.Status.SnapshotId);
        Assert.Contains(after.Objects, o => o.Object.Name == "AfterCapture");
        Assert.DoesNotContain(before.Objects, o => o.Object.Name == "AfterCapture");
    }

    [SqlServerFact]
    public async Task FailedFirstLoadCanBeRetried()
    {
        var fail = true;
        var service = new SqlServerMetadataService(() => fail ? throw new InvalidOperationException("Simulated failure") : new(database.ConnectionString));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogAsync());
        fail = false;
        Assert.NotEmpty((await service.GetCatalogAsync()).Objects);
    }

    [SqlServerFact]
    public async Task DependencyGraphHandlesCyclesDiamondsIntermediateKindsAndBothDirections()
    {
        await Execute("CREATE SCHEMA graph;");
        await Execute("CREATE TABLE graph.Target(Id int);");
        await Execute("CREATE VIEW graph.MiddleView AS SELECT Id FROM graph.Target;");
        await Execute("CREATE FUNCTION graph.MiddleFunction() RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM graph.MiddleView); END;");
        await Execute("CREATE PROCEDURE graph.Leaf AS SELECT graph.MiddleFunction();");
        await Execute("CREATE PROCEDURE graph.LeftBranch AS EXEC graph.Leaf;");
        await Execute("CREATE PROCEDURE graph.RightBranch AS EXEC graph.Leaf;");
        await Execute("CREATE PROCEDURE graph.Root AS EXEC graph.LeftBranch; EXEC graph.RightBranch;");
        await Execute("CREATE PROCEDURE graph.CycleA AS SELECT 1;");
        await Execute("CREATE PROCEDURE graph.CycleB AS EXEC graph.CycleA; EXEC graph.Leaf;");
        await Execute("ALTER PROCEDURE graph.CycleA AS EXEC graph.CycleB; EXEC graph.CycleA;");
        await Execute("CREATE PROCEDURE graph.DynamicOnly AS EXEC(N'SELECT Id FROM graph.Target');");
        var service = new SqlServerMetadataService(database.ConnectionString, new() { AllowedSchemas = ["graph"] });
        var direct = await service.QueryDependenciesAsync("graph", "Leaf", kind: SqlObjectKind.Procedure);
        Assert.Equal(3, direct.TotalCount);
        var all = await service.QueryDependenciesAsync("graph", "Leaf", transitive: true, kind: SqlObjectKind.Procedure, pageSize: 1, includePaths: true);
        Assert.Equal(5, all.TotalCount);
        Assert.Single(all.Matches.Items);
        Assert.True(all.Matches.HasMore);
        var pages = new List<DependencyMatch>();
        int? offset = 0;
        do
        {
            var result = await service.QueryDependenciesAsync("graph", "Leaf", transitive: true, kind: SqlObjectKind.Procedure, offset: offset.Value, pageSize: 1, includePaths: true);
            Assert.Equal(5, result.TotalCount);
            pages.AddRange(result.Matches.Items);
            offset = result.Matches.NextOffset;
        } while (offset != null);
        Assert.Equal(5, pages.Select(p => p.Object.Object).Distinct().Count());
        var root = pages.Single(p => p.Object.Object.Name == "Root");
        Assert.Equal(2, root.Depth);
        Assert.Equal("Root", root.Path[0].Name);
        Assert.Equal("Leaf", root.Path[^1].Name);
        var tableProcedures = await service.QueryDependenciesAsync("graph", "Target", transitive: true, kind: SqlObjectKind.Procedure, includePaths: true);
        Assert.Equal(6, tableProcedures.TotalCount);
        Assert.DoesNotContain(tableProcedures.Matches.Items, p => p.Object.Object.Name == "DynamicOnly");
        Assert.Equal(0, (await service.QueryDependenciesAsync("graph", "Target", kind: SqlObjectKind.Procedure)).TotalCount);
        var outgoing = await service.QueryDependenciesAsync("graph", "Root", DependencyDirection.Outgoing, true, SqlObjectKind.Table, includePaths: true);
        Assert.Equal(1, outgoing.TotalCount);
        Assert.Equal(new[] { "Root", "LeftBranch", "Leaf", "MiddleFunction", "MiddleView", "Target" }, Assert.Single(outgoing.Matches.Items).Path.Select(p => p.Name));
        var cycle = await service.QueryDependenciesAsync("graph", "CycleA", transitive: true);
        Assert.Equal("CycleB", Assert.Single(cycle.Matches.Items).Object.Object.Name);
        Assert.Contains("Dynamic SQL", cycle.Limitations);
        var graphTool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "QueryDependencies");
        var serialized = JsonSerializer.Serialize(await graphTool.InvokeAsync(new AIFunctionArguments
        { ["schema"] = "graph", ["name"] = "Target", ["transitive"] = true, ["kind"] = "Procedure", ["pageSize"] = 1 }));
        Assert.Contains("\"totalCount\":6", serialized);
        // Filtering output must never allow traversal through policy-excluded nodes.
        var restricted = new SqlServerMetadataService(database.ConnectionString, new() { AllowedObjects = [new("graph", "Root"), new("graph", "Target")] });
        Assert.Equal(0, (await restricted.QueryDependenciesAsync("graph", "Target", transitive: true)).TotalCount);
    }

    [SqlServerFact]
    public async Task DependenciesIncludeCurrentDatabaseQualifiedNamesAndConstraintFunctions()
    {
        var databaseName = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;
        await Execute("CREATE SCHEMA qualified;");
        await Execute("CREATE TABLE qualified.Target(Id int);");
        await Execute($"CREATE VIEW qualified.ThreePart AS SELECT Id FROM [{databaseName}].qualified.Target;");
        await Execute("CREATE FUNCTION qualified.Positive() RETURNS int AS BEGIN RETURN 1; END;");
        await Execute("CREATE TABLE qualified.Checked(Id int CHECK (qualified.Positive() > 0 AND Id > 0) DEFAULT qualified.Positive());");
        var service = new SqlServerMetadataService(database.ConnectionString, new() { AllowedSchemas = ["qualified"] });
        Assert.Equal("ThreePart", Assert.Single((await service.QueryDependenciesAsync("qualified", "Target")).Matches.Items).Object.Object.Name);
        var constraintUsers = await service.QueryDependenciesAsync("qualified", "Positive");
        Assert.Equal("Checked", Assert.Single(constraintUsers.Matches.Items).Object.Object.Name);
        // A constraint referencing its own table's columns must not become a table self-reference.
        Assert.DoesNotContain((await service.GetCatalogAsync()).Dependencies, e => e.From == e.To);
    }

    [SqlServerFact]
    public async Task BuiltInSchemasAreListedOnlyWhenRequestedOrPopulated()
    {
        await Execute("CREATE TABLE guest.Visitor(Id int);");
        var names = (await new SqlServerMetadataService(database.ConnectionString).GetCatalogAsync()).Schemas.Select(s => s.Name).ToArray();
        Assert.Contains("dbo", names);
        Assert.Contains("guest", names);
        Assert.DoesNotContain("sys", names);
        Assert.DoesNotContain("INFORMATION_SCHEMA", names);
        Assert.DoesNotContain("db_owner", names);
        var all = await new SqlServerMetadataService(database.ConnectionString, new() { IncludeBuiltInSchemas = true }).GetCatalogAsync();
        Assert.Contains(all.Schemas, s => s.Name == "sys");
        Assert.Contains(all.Schemas, s => s.Name == "db_owner");
    }

    [SqlServerFact]
    public async Task DefinitionSlicesAndTruncationNeverSplitSurrogatePairs()
    {
        await Execute("CREATE SCHEMA emoji;");
        await Execute("CREATE PROCEDURE emoji.Faces AS SELECT N'\U0001F600\U0001F601\U0001F602' AS Faces;");
        var service = new SqlServerMetadataService(database.ConnectionString, new() { AllowedSchemas = ["emoji"], IncludeDefinitions = true });
        var full = (await service.DescribeObjectAsync("emoji", "Faces")).Definition.Definition!.Value;
        var pair = full.IndexOf('\uD83D');
        var truncating = new SqlServerMetadataService(database.ConnectionString,
            new() { AllowedSchemas = ["emoji"], IncludeDefinitions = true, MaxDefinitionLength = pair + 1 });
        var truncated = (await truncating.GetObjectDefinitionAsync("emoji", "Faces")).Definition!;
        Assert.True(truncated.IsTruncated);
        Assert.Equal(full[..pair], truncated.Value);
        var tool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "GetObjectDefinition");
        foreach (var count in new[] { 1, 2, pair + 1 })
        {
            var pieces = new List<string>();
            int? offset = 0;
            do
            {
                var response = JsonSerializer.SerializeToElement(await tool.InvokeAsync(new AIFunctionArguments
                { ["schema"] = "emoji", ["name"] = "Faces", ["offset"] = offset.Value, ["characterCount"] = count }));
                var value = response.GetProperty("data").GetProperty("value");
                pieces.Add(value.GetProperty("text").GetString()!);
                offset = value.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
            } while (offset != null);
            Assert.Equal(full, string.Concat(pieces));
            Assert.DoesNotContain(pieces, p => p.Contains('�'));
        }
    }

    [SqlServerFact]
    public async Task FullDefinitionsAreCachedAndCanBeRetrievedInSlices()
    {
        var service = new SqlServerMetadataService(database.ConnectionString, new() { IncludeDefinitions = true, MaxDefinitionLength = 10 });
        var snapshot = await service.GetCatalogAsync();
        var full = snapshot.Describe(new("sales", "FindCustomer")).Definition.Definition!;
        Assert.False(full.IsTruncated);
        Assert.True(full.Value.Length > 10);
        Assert.True((await service.GetObjectDefinitionAsync("sales", "FindCustomer")).Definition!.IsTruncated);
        var tool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "GetObjectDefinition");
        var pieces = new List<string>();
        int? offset = 0;
        do
        {
            var response = JsonSerializer.SerializeToElement(await tool.InvokeAsync(new AIFunctionArguments
            { ["schema"] = "sales", ["name"] = "FindCustomer", ["offset"] = offset.Value, ["characterCount"] = 17 }));
            var value = response.GetProperty("data").GetProperty("value");
            pieces.Add(value.GetProperty("text").GetString()!);
            offset = value.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
        } while (offset != null);
        Assert.Equal(full.Value, string.Concat(pieces));
        var disabled = await new SqlServerMetadataService(database.ConnectionString).GetCatalogAsync();
        Assert.Null(disabled.Describe(new("sales", "FindCustomer")).Definition.Definition);
    }
}
