using System.Text;
using static MAFUtils.Tools.Tests.TestTools;

namespace MAFUtils.Tools.Tests;

public sealed class BudgetTests
{
    [Fact]
    public void AllNumericOptionsDefaultToNull()
    {
        var options = new ToolInspectionOptions();
        Assert.Null(options.MinimumDescriptionLength);
        Assert.Null(options.SchemaByteWarningThreshold);
        Assert.Null(options.DepthWarningThreshold);
        Assert.Null(options.PropertyCountWarningThreshold);
        Assert.Null(options.MaxSchemaBytes);
        Assert.Null(options.MaxSchemaNodes);
        Assert.Null(options.MaxSchemaDepth);
    }

    [Fact]
    public void NullThresholdsDisableOnlyTheirOwnWarnings()
    {
        var tools = new[] { Function("""{"properties":{"id":{"description":"ID"}}}""", description: "Find") };
        var enabled = new ToolInspectionOptions
        {
            MinimumDescriptionLength = 20, SchemaByteWarningThreshold = 0,
            DepthWarningThreshold = 0, PropertyCountWarningThreshold = 0
        };
        var baseline = ToolContractInspector.Inspect(tools, enabled);
        (ToolInspectionOptions Options, string Code)[] cases =
        [
            (enabled with { MinimumDescriptionLength = null }, "MFT011"),
            (enabled with { SchemaByteWarningThreshold = null }, "MFT040"),
            (enabled with { DepthWarningThreshold = null }, "MFT041"),
            (enabled with { PropertyCountWarningThreshold = null }, "MFT042")
        ];
        foreach (var (options, code) in cases)
        {
            Assert.Contains(baseline.Diagnostics, d => d.Code == code);
            var report = ToolContractInspector.Inspect(tools, options);
            Assert.Equal(baseline.Diagnostics.Where(d => d.Code != code), report.Diagnostics);
            Assert.Equal(baseline.SchemaMetrics, report.SchemaMetrics);
        }
        Assert.Empty(ToolContractInspector.Inspect(tools).Diagnostics);
    }

    [Fact]
    public void EachBudgetCanBeRemovedWithNull()
    {
        var tools = new[] { Function("""{"properties":{"id":{"description":"ID"}}}""") };
        (ToolInspectionOptions Limited, ToolInspectionOptions Unlimited)[] cases =
        [
            (new() { MaxSchemaBytes = 1 }, new() { MaxSchemaBytes = null }),
            (new() { MaxSchemaNodes = 1 }, new() { MaxSchemaNodes = null }),
            (new() { MaxSchemaDepth = 1 }, new() { MaxSchemaDepth = null })
        ];
        foreach (var (limited, unlimited) in cases)
        {
            Assert.True(ToolContractInspector.Inspect(tools, limited).IsPartial);
            var report = ToolContractInspector.Inspect(tools, unlimited);
            Assert.False(report.IsPartial);
            Assert.Empty(report.Diagnostics);
            Assert.Equal(4, Assert.Single(report.SchemaMetrics).AnalyzedNodeCount);
        }
    }

    [Fact]
    public void UnrestrictedDefaultsInspectBeyondFormerByteNodeAndPropertyLimits()
    {
        string description = new('d', 100);
        string schema = "{\"properties\":{" + string.Join(',', Enumerable.Range(0, 12_000)
            .Select(i => $"\"p{i}\":{{\"description\":\"{description}\"}}")) + "}}";
        var report = ToolContractInspector.Inspect([Function(schema)]);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.IsPartial);
        var metrics = Assert.Single(report.SchemaMetrics);
        Assert.True(metrics.SchemaBytes > 1_048_576);
        Assert.Equal(12_000, metrics.DeclaredPropertyCount);
        Assert.Equal(24_002, metrics.AnalyzedNodeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(25_000)]
    public void DeepSchemasAndDiagnosticPathsDoNotUseTheCallStack(int? maxDepth)
    {
        const int nesting = 20_000;
        string json = string.Concat(Enumerable.Repeat("{\"items\":", nesting)) + "{\"required\":42}" + new string('}', nesting);
        using var document = System.Text.Json.JsonDocument.Parse(json, new() { MaxDepth = nesting + 10 });
        var report = ToolContractInspector.Inspect([new Declaration("Deep", "Deep schema", document.RootElement)],
            new() { MaxSchemaDepth = maxDepth });
        Assert.False(report.IsPartial);
        Assert.Equal(nesting + 1, Assert.Single(report.SchemaMetrics).MaximumDepth);
        Assert.Equal(nesting + 2, report.SchemaMetrics[0].AnalyzedNodeCount);
        var finding = Assert.Single(report.Diagnostics);
        Assert.Equal("MFT033", finding.Code);
        Assert.Equal("#" + string.Concat(Enumerable.Repeat("/items", nesting)) + "/required", finding.Location);
    }

    [Fact]
    public void UnrestrictedInspectionStillReportsStructuralAndUnsupportedSchemaFindings()
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":[],"required":[1],"$ref":"#/$defs/X"}""", description: "")]);
        Assert.True(report.HasErrors);
        Assert.True(report.IsPartial);
        Assert.Equal(["MFT010", "MFT031", "MFT032", "MFT033"], report.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void DepthLimitSkipsDeepChildrenButInspectsLaterShallowSiblings()
    {
        var report = ToolContractInspector.Inspect([Function("""{"items":{"items":{"type":"string"}},"required":42}""")],
            new() { MaxSchemaDepth = 2 });
        Assert.True(report.IsPartial);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT031" && d.Location == "#/items/items");
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT033" && d.Location == "#/required");
    }

    [Fact]
    public void NodeLimitPreservesRequiredErrorsAlreadyProven()
    {
        var report = ToolContractInspector.Inspect([Function("""{"required":[1,"id","id"]}""")], new() { MaxSchemaNodes = 3 });
        Assert.True(report.IsPartial);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT033");
        Assert.Single(report.Diagnostics, d => d.Code == "MFT031");
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ByteWarningUsesStrictThresholdAndRawUtf8IncludingWhitespace(int offset, bool warning)
    {
        const string json = "{ \"description\":\"工具🔎\" }";
        int bytes = Encoding.UTF8.GetByteCount(json);
        var report = ToolContractInspector.Inspect([Function(json)], new() { SchemaByteWarningThreshold = bytes + offset });
        Assert.Equal(bytes, Assert.Single(report.SchemaMetrics).SchemaBytes);
        Assert.Equal(warning, report.Diagnostics.Any(d => d.Code == "MFT040"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void PropertyWarningUsesStrictThreshold(int threshold, bool warning)
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":{"a":{}}}""")],
            new() { PropertyCountWarningThreshold = threshold });
        Assert.Equal(warning, report.Diagnostics.Any(d => d.Code == "MFT042"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void DepthWarningUsesStrictThreshold(int threshold, bool warning)
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":{"a":{}}}""")],
            new() { DepthWarningThreshold = threshold });
        Assert.Equal(warning, report.Diagnostics.Any(d => d.Code == "MFT041"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void ByteBudgetSkipsOversizedSchemasAndRetainsExactSize(int budget, bool partial)
    {
        var report = ToolContractInspector.Inspect([Function("{}")], new() { MaxSchemaBytes = budget });
        Assert.Equal(partial, report.IsPartial);
        var metrics = Assert.Single(report.SchemaMetrics);
        Assert.Equal(2, metrics.SchemaBytes);
        Assert.Equal(partial ? 0 : 1, metrics.AnalyzedNodeCount);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void NodeBudgetStopsAtTheLimit(int budget, bool partial)
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":{"a":{}}}""")], new() { MaxSchemaNodes = budget });
        Assert.Equal(partial, report.IsPartial);
        Assert.InRange(Assert.Single(report.SchemaMetrics).AnalyzedNodeCount, 1, budget);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void DepthBudgetStopsAtTheLimit(int budget, bool partial)
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":{"a":{}}}""")], new() { MaxSchemaDepth = budget });
        Assert.Equal(partial, report.IsPartial);
        Assert.InRange(Assert.Single(report.SchemaMetrics).MaximumDepth, 0, budget);
    }

    [Fact]
    public void DeepHostileSchemaIsBoundedWithoutRecursingThroughTheWholeDocument()
    {
        string json = string.Concat(Enumerable.Repeat("{\"items\":", 1_000)) + "{}" + new string('}', 1_000);
        using var document = System.Text.Json.JsonDocument.Parse(json, new() { MaxDepth = 1_010 });
        var report = ToolContractInspector.Inspect([new Declaration("Deep", "Deep schema", document.RootElement)],
            new() { MaxSchemaDepth = 8, DepthWarningThreshold = 4 });
        Assert.True(report.IsPartial);
        Assert.Equal(8, Assert.Single(report.SchemaMetrics).MaximumDepth);
        Assert.Equal(9, report.SchemaMetrics[0].AnalyzedNodeCount);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT041");
    }

    [Fact]
    public void WideSchemaStopsCountingAndDoesNotFloodDiagnosticsBeyondNodeBudget()
    {
        string schema = "{\"properties\":{" + string.Join(',', Enumerable.Range(0, 20_000).Select(i => $"\"p{i}\":{{}}")) + "}}";
        var report = ToolContractInspector.Inspect([Function(schema)], new() { MaxSchemaNodes = 10 });
        Assert.True(report.IsPartial);
        var metrics = Assert.Single(report.SchemaMetrics);
        Assert.Equal(10, metrics.AnalyzedNodeCount);
        Assert.Equal(8, metrics.DeclaredPropertyCount);
        Assert.Single(report.Diagnostics, d => d.Code == "MFT031");
        Assert.Equal(8, report.Diagnostics.Count(d => d.Code == "MFT020"));
    }

    [Fact]
    public void DisablingInfoPreservesWarningsErrorsAndPartialStatus()
    {
        var tools = new[] { Function("""{"$ref":"#/$defs/X","properties":[],"required":42}""", description: "") };
        var complete = ToolContractInspector.Inspect(tools);
        var filtered = ToolContractInspector.Inspect(tools, new() { IncludeInfo = false });
        Assert.Equal(complete.Diagnostics.Where(d => d.Severity != ToolDiagnosticSeverity.Info), filtered.Diagnostics);
        Assert.True(filtered.IsPartial);
        Assert.True(filtered.HasErrors);
        Assert.Equal(0, filtered.InfoCount);
        Assert.Contains("Schema analysis: partial", filtered.ToText());
    }

    [Fact]
    public void DescriptionLookupsDoNotScanPastNodeBudget()
    {
        var report = ToolContractInspector.Inspect([Function("""{"properties":{"a":{"type":"string","description":"Description"}}}""")],
            new() { MaxSchemaNodes = 3 });
        Assert.True(report.IsPartial);
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "MFT020");
    }

    [Fact]
    public void InvalidOptionsAreRejectedBeforeEnumeratingTools()
    {
        ToolInspectionOptions[] invalid =
        [
            new() { MinimumDescriptionLength = -1 }, new() { SchemaByteWarningThreshold = -1 },
            new() { DepthWarningThreshold = -1 }, new() { PropertyCountWarningThreshold = -1 },
            new() { MaxSchemaBytes = 0 }, new() { MaxSchemaNodes = 0 },
            new() { MaxSchemaDepth = 0 }, new() { MaxSchemaDepth = -1 },
            new() { MaxSchemaBytes = -1 }, new() { MaxSchemaNodes = -1 }
        ];
        foreach (var options in invalid)
            Assert.Throws<ArgumentOutOfRangeException>(() => ToolContractInspector.Inspect([], options));
    }
}
