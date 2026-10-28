using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MAFUtils.SqlServer.Tests;

public sealed class ToolTests
{
    private static SqlServerMetadataService NoConnection(SqlServerDiscoveryOptions? options = null)
        => new(() => throw new Exception("Should not open a connection."), options);

    [Fact]
    public void DefinitionsAreOptInAndSecretsAreNotInToolSchemas()
    {
        var tools = SqlServerDiscoveryTools.Create(NoConnection());
        Assert.Equal(6, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "GetObjectDefinition");
        Assert.Equal(7, SqlServerDiscoveryTools.Create(NoConnection(new() { IncludeDefinitions = true })).Count);
        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema.ToString();
            Assert.DoesNotContain("connectionString", schema);
            Assert.DoesNotContain("cancellationToken", schema);
        }
    }

    [Fact]
    public async Task DisabledDefinitionsAndInvalidArgumentsFailBeforeConnecting()
    {
        var service = NoConnection();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListSchemasAsync(offset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListSchemasAsync(pageSize: 101));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListSchemasAsync(search: new string('x', 129)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetObjectDefinitionAsync("dbo", "Test"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DescribeTableAsync("", "Test"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListDatabaseObjectsAsync(offset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListDatabaseObjectsAsync(pageSize: 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListDatabaseObjectsAsync(kind: (SqlObjectKind)999));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListReferencingObjectsAsync("", "Test"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListReferencingObjectsAsync("dbo", "Test", offset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListReferencingObjectsAsync("dbo", "Test", pageSize: 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListReferencingObjectsAsync("dbo", "Test", kind: (SqlObjectKind)999));
        Assert.Equal("offset", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListSchemasAsync(offset: int.MaxValue))).ParamName);
        Assert.Equal("pageSize", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListSchemasAsync(pageSize: 101))).ParamName);
    }

    [Fact]
    public void AgentRefreshCanBeLeftToTheHost()
    {
        var tools = SqlServerDiscoveryTools.Create(NoConnection(new() { AllowAgentRefresh = false, IncludeDefinitions = true }));
        Assert.Equal(6, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "RefreshCatalog");
        Assert.Contains(tools, t => t.Name == "GetObjectDefinition");
    }

    [Fact]
    public async Task ToolInvocationReturnsStructuredValidationError()
    {
        var tool = SqlServerDiscoveryTools.Create(NoConnection()).Single(t => t.Name == "DescribeObject");
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["schema"] = "", ["name"] = "secret-name" });
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("invalid_argument", json);
        Assert.DoesNotContain("secret-name", json);
    }

    [Fact]
    public async Task CatalogArgumentsAreValidatedBeforeLazyLoading()
    {
        var service = NoConnection(new() { IncludeDefinitions = true });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetDatabaseInventoryAsync((SqlObjectKind)999));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.QueryDependenciesAsync("dbo", "X", direction: (DependencyDirection)999));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DescribeObjectAsync("", "X"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCatalogAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshCatalogAsync(canceled.Token));
        var tools = SqlServerDiscoveryTools.Create(service);
        foreach (var (name, args) in new[]
        {
            ("GetDatabaseInventory", new AIFunctionArguments { ["kind"] = (SqlObjectKind)999 }),
            ("ListDatabaseObjects", new AIFunctionArguments { ["schema"] = "" }),
            ("QueryDependencies", new AIFunctionArguments { ["schema"] = "dbo", ["name"] = "X", ["pageSize"] = 101 }),
            ("GetObjectDefinition", new AIFunctionArguments { ["schema"] = "dbo", ["name"] = "X", ["offset"] = -1 })
        })
        {
            var result = await tools.Single(t => t.Name == name).InvokeAsync(args);
            Assert.Contains("invalid_argument", JsonSerializer.Serialize(result));
        }
    }

    [Fact]
    public void InvalidHostLimitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoConnection(new() { CommandTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => NoConnection(new() { MaxDetailRows = int.MaxValue }));
        Assert.Throws<ArgumentException>(() => NoConnection(new() { AllowedSchemas = [""] }));
    }
}

