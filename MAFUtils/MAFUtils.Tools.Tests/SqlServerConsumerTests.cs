using MAFUtils.SqlServer;

namespace MAFUtils.Tools.Tests;

public sealed class SqlServerConsumerTests
{
    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 7)]
    public void ExistingSqlServerToolsCanBeInspectedWithoutOpeningAConnection(bool definitions, int count)
    {
        var service = new SqlServerMetadataService(() => throw new InvalidOperationException("Must not open a connection."),
            new() { IncludeDefinitions = definitions });
        var tools = SqlServerDiscoveryTools.Create(service);
        var report = ToolContractInspector.Inspect(tools);
        Assert.Equal(count, report.ToolCount);
        Assert.Equal(count, report.FunctionCount);
        Assert.False(report.HasErrors, report.ToText());
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "MFT010");
        // Existing SQL tools omit some parameter descriptions: check the findings against their real contracts.
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT020");
        foreach (var finding in report.Diagnostics.Where(d => d.Code == "MFT020"))
        {
            var parameterName = finding.Location["#/properties/".Length..].Replace("~1", "/").Replace("~0", "~");
            var parameter = tools[finding.ToolIndex].JsonSchema.GetProperty("properties").GetProperty(parameterName);
            Assert.False(parameter.TryGetProperty("description", out var description) &&
                description.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(description.GetString()));
        }
    }
}
