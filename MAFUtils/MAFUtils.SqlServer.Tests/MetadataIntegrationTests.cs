using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace MAFUtils.SqlServer.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAFUTILS_TEST_SQLSERVER")))
            Skip = "Set MAFUTILS_TEST_SQLSERVER to a test server connection with CREATE DATABASE permission.";
    }
}

public sealed class TestDatabase : IAsyncLifetime
{
    private readonly string name = "MAFUtilsTests_" + Guid.NewGuid().ToString("N");
    private string? adminConnection;
    private bool created;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("MAFUTILS_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        adminConnection = builder.ConnectionString;
        await Execute(adminConnection, $"CREATE DATABASE [{name}];");
        created = true;
        builder.InitialCatalog = name;
        ConnectionString = builder.ConnectionString;
        try
        {
            foreach (var batch in Schema.ReplaceLineEndings("\n").Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries))
                await Execute(ConnectionString, batch);
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task DisposeAsync()
    {
        if (!created) return;
        // Only the GUID-named database created by this fixture is ever removed.
        await Execute(adminConnection!, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        created = false;
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private const string Schema = """
        CREATE SCHEMA sales;
        GO
        CREATE SCHEMA [Empty%_];
        GO
        CREATE TABLE sales.Customers (
            TenantId int NOT NULL,
            CustomerId int NOT NULL,
            Name nvarchar(80) NOT NULL,
            CONSTRAINT PK_Customers PRIMARY KEY (TenantId,CustomerId));
        CREATE TABLE sales.Orders (
            OrderId int IDENTITY PRIMARY KEY,
            TenantId int NOT NULL,
            CustomerId int NOT NULL,
            Amount decimal(12,2) NULL,
            Doubled AS Amount*2,
            CONSTRAINT FK_Orders_Customers FOREIGN KEY (TenantId,CustomerId)
                REFERENCES sales.Customers(TenantId,CustomerId));
        CREATE INDEX IX_Orders_Customer ON sales.Orders(TenantId,CustomerId DESC) INCLUDE(Amount) WHERE Amount>0;
        CREATE TABLE dbo.Hidden (Id int PRIMARY KEY, TenantId int, CustomerId int,
            FOREIGN KEY(TenantId,CustomerId) REFERENCES sales.Customers(TenantId,CustomerId));
        CREATE TABLE sales.[Odd']];--%_] (Id int);
        EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=N'Customer business description',
            @level0type=N'SCHEMA',@level0name=N'sales',@level1type=N'TABLE',@level1name=N'Customers';
        EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=N'Customer display name',
            @level0type=N'SCHEMA',@level0name=N'sales',@level1type=N'TABLE',@level1name=N'Customers',
            @level2type=N'COLUMN',@level2name=N'Name';
        GO
        CREATE VIEW sales.CustomerNames AS SELECT TenantId,CustomerId,Name FROM sales.Customers;
        GO
        CREATE PROCEDURE sales.FindCustomer @CustomerId int, @Label nvarchar(80)=NULL OUTPUT AS
        BEGIN SELECT @Label=Name FROM sales.Customers WHERE CustomerId=@CustomerId; END;
        GO
        CREATE FUNCTION sales.CustomerCount(@TenantId int) RETURNS int AS
        BEGIN RETURN (SELECT COUNT(*) FROM sales.Customers WHERE TenantId=@TenantId); END;
        GO
        CREATE FUNCTION sales.CustomersForTenant(@TenantId int) RETURNS TABLE AS
        RETURN (SELECT CustomerId,Name FROM sales.Customers WHERE TenantId=@TenantId);
        GO
        CREATE VIEW sales.BoundCustomers WITH SCHEMABINDING AS
        SELECT TenantId,CustomerId,Name FROM sales.Customers;
        GO
        CREATE PROCEDURE dbo.HiddenReader AS SELECT Name FROM sales.Customers;
        GO
        CREATE PROCEDURE sales.DynamicReader AS EXEC(N'SELECT Name FROM sales.Customers');
        GO
        CREATE PROCEDURE sales.IndirectReader AS SELECT Name FROM sales.CustomerNames;
        """;
}

public sealed class MetadataIntegrationTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private SqlServerMetadataService Service(SqlServerDiscoveryOptions? options = null) => new(database.ConnectionString, options);

    [SqlServerFact]
    public async Task SchemasIncludeEmptyUserSchemasWithLiteralSearchAndPaging()
    {
        var service = Service();
        var all = await service.ListSchemasAsync();
        Assert.Contains(all.Items, s => s.Name == "Empty%_");
        Assert.Contains(all.Items, s => s.Name == "sales");
        Assert.Contains(all.Items, s => s.Name == "dbo");
        Assert.DoesNotContain(all.Items, s => s.Name == "sys");
        Assert.Contains((await Service(new() { IncludeBuiltInSchemas = true }).ListSchemasAsync(pageSize: 100)).Items, s => s.Name == "sys");
        Assert.Equal("Empty%_", Assert.Single((await service.ListSchemasAsync(search: "%_")).Items).Name);
        Assert.Empty((await service.ListSchemasAsync(search: "' OR 1=1 --")).Items);
        var items = new List<DatabaseSchema>();
        int? offset = 0;
        do
        {
            var page = await service.ListSchemasAsync(offset: offset.Value, pageSize: 2);
            items.AddRange(page.Items);
            Assert.Equal(page.HasMore, page.NextOffset.HasValue);
            offset = page.NextOffset;
        } while (offset.HasValue);
        Assert.Equal(all.Items, items);
        Assert.Single((await Service(new() { MaxPageSize = 1 }).ListSchemasAsync()).Items);
    }

    [SqlServerFact]
    public async Task SchemasAndObjectsUseTheSamePolicy()
    {
        var all = (await Service().ListSchemasAsync()).Items;
        Assert.Equal(all, (await Service(new() { AllowedSchemas = [] }).ListSchemasAsync()).Items);
        Assert.Equal(new[] { "Empty%_", "sales" }, (await Service(new() { AllowedSchemas = ["sales", "Empty%_"] }).ListSchemasAsync()).Items.Select(s => s.Name));
        Assert.Empty((await Service(new() { AllowedSchemas = ["SALES", "sales "] }).ListSchemasAsync()).Items);
        var objects = Service(new() { AllowedObjects = [new("sales", "Customers"), new("Empty%_", "Missing")] });
        Assert.Equal("sales", Assert.Single((await objects.ListSchemasAsync()).Items).Name);
        var intersected = Service(new() { AllowedSchemas = ["dbo"], AllowedObjects = [new("sales", "Customers")] });
        Assert.Empty((await intersected.ListSchemasAsync()).Items);
        Assert.Empty((await Service(new() { AllowedObjects = [] }).ListSchemasAsync()).Items);
    }
    [SqlServerFact]
    public async Task SchemaToolCanBeInvokedAndCancellationPropagates()
    {
        var service = Service(new() { AllowedSchemas = [] });
        var tool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "GetDatabaseInventory");
        var result = await tool.InvokeAsync(new AIFunctionArguments());
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Empty%_", json);
        Assert.Contains("sales", json);
        Assert.DoesNotContain("database_error", json);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListSchemasAsync(cancellationToken: cancellation.Token));
    }

    [SqlServerFact]
    public async Task EmptySchemaFilterMatchesNullAcrossDiscoveryOperations()
    {
        var unrestricted = Service(new() { IncludeDefinitions = true });
        var empty = Service(new() { AllowedSchemas = [], IncludeDefinitions = true });
        Assert.Equal((await unrestricted.ListDatabaseObjectsAsync(schema: "sales", kind: SqlObjectKind.Table)).Items,
            (await empty.ListDatabaseObjectsAsync(schema: "sales", kind: SqlObjectKind.Table)).Items);
        Assert.Equal((await unrestricted.DescribeTableAsync("sales", "Customers")).Relationships.Items,
            (await empty.DescribeTableAsync("sales", "Customers")).Relationships.Items);
        Assert.Equal((await unrestricted.ListReferencingObjectsAsync("sales", "Customers")).References.Items,
            (await empty.ListReferencingObjectsAsync("sales", "Customers")).References.Items);
        Assert.Equal(await unrestricted.GetObjectDefinitionAsync("sales", "FindCustomer"),
            await empty.GetObjectDefinitionAsync("sales", "FindCustomer"));
        var tool = SqlServerDiscoveryTools.Create(empty).Single(t => t.Name == "ListDatabaseObjects");
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["schema"] = "sales", ["kind"] = SqlObjectKind.Table });
        Assert.Contains("Customers", JsonSerializer.Serialize(result));
        Assert.Empty((await Service(new() { AllowedSchemas = [], AllowedObjects = [] }).ListDatabaseObjectsAsync()).Items);
    }

    [SqlServerFact]
    public async Task ReferencingObjectsFindsDirectReferencesOncePerObjectAndPagesThem()
    {
        var service = Service();
        var result = await service.ListReferencingObjectsAsync("sales", "Customers");
        Assert.Equal(new SqlObjectName("sales", "Customers"), result.Target);
        Assert.Equal(new[] { "HiddenReader", "BoundCustomers", "CustomerCount", "CustomerNames", "CustomersForTenant", "FindCustomer" },
            result.References.Items.Select(o => o.Object.Name));
        Assert.Contains("Dynamic SQL", result.Limitations);
        var pages = new List<DatabaseObject>();
        int? offset = 0;
        do
        {
            var page = (await service.ListReferencingObjectsAsync("sales", "Customers", offset: offset.Value, pageSize: 1)).References;
            pages.AddRange(page.Items);
            Assert.Equal(page.HasMore, page.NextOffset.HasValue);
            offset = page.NextOffset;
        } while (offset.HasValue);
        Assert.Equal(result.References.Items, pages);
        var functions = await service.ListReferencingObjectsAsync("sales", "Customers", kind: SqlObjectKind.Function);
        Assert.Equal(2, functions.References.Items.Count);
        Assert.All(functions.References.Items, o => Assert.Equal(SqlObjectKind.Function, o.Kind));
        var procedures = await service.ListReferencingObjectsAsync("sales", "Customers", kind: SqlObjectKind.Procedure);
        Assert.Equal(2, procedures.References.Items.Count);
        Assert.DoesNotContain(procedures.References.Items, o => o.Object.Name is "DynamicReader" or "IndirectReader");
    }

    [SqlServerFact]
    public async Task ReferencePolicyAppliesToBothTargetAndReferencingObjects()
    {
        var schemas = Service(new() { AllowedSchemas = ["sales"] });
        Assert.DoesNotContain((await schemas.ListReferencingObjectsAsync("sales", "Customers")).References.Items,
            o => o.Object.Schema == "dbo");
        var objects = Service(new() { AllowedObjects = [new("sales", "Customers"), new("sales", "FindCustomer")] });
        Assert.Equal("FindCustomer", Assert.Single((await objects.ListReferencingObjectsAsync("sales", "Customers")).References.Items).Object.Name);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => objects.ListReferencingObjectsAsync("sales", "CustomerNames"));
        var onlyTarget = Service(new() { AllowedObjects = [new("sales", "Customers")] });
        Assert.Empty((await onlyTarget.ListReferencingObjectsAsync("sales", "Customers")).References.Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service().ListReferencingObjectsAsync("sales", "Missing"));
        Assert.Empty((await Service().ListReferencingObjectsAsync("sales", "Odd'];--%_")).References.Items);
    }

    [SqlServerFact]
    public async Task ReferenceToolWorksWithoutDefinitionAccessAndPropagatesCancellation()
    {
        var service = Service();
        Assert.False(service.DefinitionsEnabled);
        var tool = SqlServerDiscoveryTools.Create(service).Single(t => t.Name == "QueryDependencies");
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["schema"] = "sales", ["name"] = "Customers" });
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("FindCustomer", json);
        Assert.Contains("CustomerCount", json);
        Assert.Contains("limitations", json);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListReferencingObjectsAsync("sales", "Customers", cancellationToken: cancellation.Token));
    }

    [SqlServerFact]
    public async Task PagesAreStableAndSearchIsLiteral()
    {
        var service = Service();
        var all = await service.ListDatabaseObjectsAsync();
        var paged = new List<DatabaseObject>();
        int? offset = 0;
        do
        {
            var page = await service.ListDatabaseObjectsAsync(offset: offset.Value, pageSize: 2);
            paged.AddRange(page.Items);
            Assert.Equal(page.HasMore, page.NextOffset.HasValue);
            offset = page.NextOffset;
        } while (offset.HasValue);
        Assert.Equal(all.Items, paged);
        Assert.Single((await service.ListDatabaseObjectsAsync(search: "%_")).Items);
        Assert.Empty((await service.ListDatabaseObjectsAsync(search: "' OR 1=1 --")).Items);
        Assert.All((await service.ListDatabaseObjectsAsync(kind: SqlObjectKind.Function)).Items,
            o => Assert.Equal(SqlObjectKind.Function, o.Kind));
    }

    [SqlServerFact]
    public async Task TableMetadataIncludesTypesCompositeKeysIndexesAndBothRelationshipDirections()
    {
        var service = Service();
        var customers = await service.DescribeTableAsync("sales", "Customers");
        Assert.Equal("Customer business description", customers.Object.Description!.Value);
        var name = customers.Columns.Items.Single(c => c.Name == "Name");
        Assert.Equal(160, name.Type.MaxLengthBytes);
        Assert.Equal("Customer display name", name.Description!.Value);
        Assert.Equal(2, customers.IndexColumns.Items.Count(i => i.IsPrimaryKey));
        Assert.Contains(customers.Relationships.Items, r => r.FromObject.Name == "Orders" && r.Ordinal == 2);
        var orders = await service.DescribeTableAsync("sales", "Orders");
        Assert.True(orders.Columns.Items.Single(c => c.Name == "OrderId").IsIdentity);
        Assert.True(orders.Columns.Items.Single(c => c.Name == "Doubled").IsComputed);
        Assert.Equal((byte)2, orders.Columns.Items.Single(c => c.Name == "Amount").Type.Scale);
        Assert.Contains(orders.IndexColumns.Items, i => i.IsIncluded && i.Column == "Amount" && i.HasFilter);
        Assert.Contains(orders.IndexColumns.Items, i => i.Column == "CustomerId" && i.IsDescending);
        Assert.Equal(2, orders.Relationships.Items.Count);
    }

    [SqlServerFact]
    public async Task ViewsProceduresAndFunctionsAreDescribedWithoutExecution()
    {
        var service = Service();
        Assert.Equal(3, (await service.DescribeViewAsync("sales", "CustomerNames")).Columns.Items.Count);
        var procedure = await service.DescribeProcedureAsync("sales", "FindCustomer");
        Assert.Equal(2, procedure.Parameters.Items.Count);
        Assert.True(procedure.Parameters.Items.Single(p => p.Name == "@Label").IsOutput);
        Assert.Empty(procedure.ResultColumns.Items);
        var function = await service.DescribeFunctionAsync("sales", "CustomerCount");
        Assert.Contains(function.Parameters.Items, p => p.Ordinal == 0 && p.Type.Name == "int");
        Assert.Equal(2, (await service.DescribeFunctionAsync("sales", "CustomersForTenant")).ResultColumns.Items.Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DescribeTableAsync("sales", "CustomerNames"));
    }

    [SqlServerFact]
    public async Task PolicyIsAppliedToListsDirectLookupsDefinitionsAndRelationships()
    {
        var schemas = new List<string> { "sales" };
        var service = Service(new() { AllowedSchemas = schemas, IncludeDefinitions = true });
        schemas.Add("dbo"); // The service has already taken a snapshot.
        Assert.DoesNotContain((await service.ListDatabaseObjectsAsync()).Items, o => o.Object.Schema == "dbo");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DescribeTableAsync("dbo", "Hidden"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetObjectDefinitionAsync("dbo", "Hidden"));
        var customers = await service.DescribeTableAsync("sales", "Customers");
        Assert.DoesNotContain(customers.Relationships.Items, r => r.FromObject.Schema == "dbo");
        var single = Service(new() { AllowedObjects = [new("sales", "Customers")] });
        Assert.Single((await single.ListDatabaseObjectsAsync()).Items);
        Assert.Empty((await single.DescribeTableAsync("sales", "Customers")).Relationships.Items);
        Assert.Equal((await Service().ListDatabaseObjectsAsync()).Items,
            (await Service(new() { AllowedSchemas = [] }).ListDatabaseObjectsAsync()).Items);
        Assert.Empty((await Service(new() { AllowedObjects = [] }).ListDatabaseObjectsAsync()).Items);
        Assert.Empty((await Service(new() { AllowedSchemas = ["SALES"] }).ListDatabaseObjectsAsync()).Items);
        Assert.Empty((await Service(new() { AllowedSchemas = ["sales "] }).ListDatabaseObjectsAsync()).Items);
    }

    [SqlServerFact]
    public async Task ResultsAndDefinitionsReportTruncation()
    {
        var service = Service(new() { IncludeDefinitions = true, MaxDetailRows = 1, MaxTextLength = 8, MaxDefinitionLength = 30 });
        var customers = await service.DescribeTableAsync("sales", "Customers");
        Assert.Single(customers.Columns.Items);
        Assert.True(customers.Columns.IsTruncated);
        Assert.True(customers.IndexColumns.IsTruncated);
        Assert.True(customers.Relationships.IsTruncated);
        Assert.True(customers.Object.Description!.IsTruncated);
        Assert.Equal(8, customers.Object.Description.Value.Length);
        var definition = await service.GetObjectDefinitionAsync("sales", "FindCustomer");
        Assert.True(definition.Definition!.IsTruncated);
        Assert.Equal(30, definition.Definition.Value.Length);
        Assert.Null((await service.GetObjectDefinitionAsync("sales", "Customers")).Definition);
        var smallPage = await Service(new() { MaxPageSize = 1 }).ListDatabaseObjectsAsync();
        Assert.Single(smallPage.Items);
        Assert.True(smallPage.HasMore);
    }

    [SqlServerFact]
    public async Task UnusualIdentifiersAreValuesNotSqlAndUnknownObjectsAreUnavailable()
    {
        var service = Service();
        Assert.Single((await service.DescribeTableAsync("sales", "Odd'];--%_")).Columns.Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DescribeTableAsync("sales", "Customers'; DROP TABLE sales.Customers;--"));
        Assert.Equal(3, (await service.DescribeTableAsync("sales", "Customers")).Columns.Items.Count);
    }

    [SqlServerFact]
    public async Task ToolCanBeInvokedThroughMicrosoftExtensionsAI()
    {
        var tool = SqlServerDiscoveryTools.Create(Service()).Single(t => t.Name == "DescribeObject");
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["schema"] = "sales", ["name"] = "Customers" });
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Customer business description", json);
        Assert.DoesNotContain("database_error", json);
        var unavailable = await tool.InvokeAsync(new AIFunctionArguments { ["schema"] = "dbo", ["name"] = "Missing" });
        Assert.Contains("object_unavailable", JsonSerializer.Serialize(unavailable));
    }

    [SqlServerFact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ListDatabaseObjectsAsync(cancellationToken: cancellation.Token));
    }
}

