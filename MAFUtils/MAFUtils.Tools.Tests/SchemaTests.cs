using System.Text;
using static MAFUtils.Tools.Tests.TestTools;

namespace MAFUtils.Tools.Tests;

public sealed class SchemaTests
{
    private static ToolInspectionReport Inspect(string? schema, ToolInspectionOptions? options = null)
        => ToolContractInspector.Inspect([Function(schema)], options);

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("{}")]
    public void UnspecifiedAndUnconstrainedSchemasAreInformational(string? schema)
    {
        var report = Inspect(schema);
        var finding = Assert.Single(report.Diagnostics);
        Assert.Equal("MFT030", finding.Code);
        Assert.Equal(ToolDiagnosticSeverity.Info, finding.Severity);
        Assert.False(report.IsPartial);
        Assert.True(report.IsSuccessful);
        Assert.Equal(schema is null ? 0 : Encoding.UTF8.GetByteCount(schema), Assert.Single(report.SchemaMetrics).SchemaBytes);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("""{"type":["string","null"]}""")]
    [InlineData("""{"required":["undeclared"]}""")]
    [InlineData("""{"properties":{"id":{"description":"Identifier","type":"string"}},"required":["id"]}""")]
    [InlineData("""{"type":"array","items":{"type":"string"}}""")]
    public void ValidSchemasHaveNoFalseFindings(string schema)
    {
        var report = Inspect(schema);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.IsPartial);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("""{"description":"  "}""")]
    [InlineData("""{"description":null}""")]
    [InlineData("""{"description":123}""")]
    public void MissingDirectParameterDescriptionsHaveEscapedPointer(string parameter)
    {
        var report = Inspect("{\"properties\":{\"a/~b\":" + parameter + "}}");
        var finding = Assert.Single(report.Diagnostics);
        Assert.Equal("MFT020", finding.Code);
        Assert.Equal("#/properties/a~1~0b", finding.Location);
        Assert.Equal(ToolDiagnosticSeverity.Warning, finding.Severity);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void InvalidPropertiesShapeIsAnError(string value)
    {
        var report = Inspect("{\"properties\":" + value + "}");
        var finding = Assert.Single(report.Diagnostics, d => d.Code == "MFT032");
        Assert.Equal("#/properties", finding.Location);
        Assert.Equal(ToolDiagnosticSeverity.Error, finding.Severity);
    }

    [Theory]
    [InlineData("\"id\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"id\",42]")]
    [InlineData("[\"id\",\"id\"]")]
    [InlineData("[{},false,\"id\",\"id\"]")]
    public void InvalidRequiredShapeHasOneErrorPerKeyword(string value)
    {
        var report = Inspect("{\"required\":" + value + "}");
        var finding = Assert.Single(report.Diagnostics, d => d.Code == "MFT033");
        Assert.Equal("#/required", finding.Location);
        Assert.Equal(ToolDiagnosticSeverity.Error, finding.Severity);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"id\",\"ID\",\"\"]")]
    public void RequiredUsesOrdinalDistinctStringsAndAllowsEmptyNames(string value)
        => Assert.Empty(Inspect("{\"required\":" + value + "}").Diagnostics);

    [Theory]
    [InlineData("$ref", "\"#/$defs/X\"")]
    [InlineData("$defs", "{\"X\":{\"properties\":[]}}")]
    [InlineData("allOf", "[{\"properties\":[]}]" )]
    [InlineData("anyOf", "[{\"properties\":[]}]" )]
    [InlineData("oneOf", "[{\"properties\":[]}]" )]
    [InlineData("if", "{\"properties\":[]}")]
    [InlineData("then", "{\"properties\":[]}")]
    [InlineData("else", "{\"properties\":[]}")]
    [InlineData("not", "{\"properties\":[]}")]
    [InlineData("patternProperties", "{\".*\":{\"properties\":[]}}")]
    [InlineData("items", "[{\"properties\":[]}]")]
    [InlineData("prefixItems", "[{\"properties\":[]}]")]
    [InlineData("dependentSchemas", "{\"X\":{\"properties\":[]}}")]
    public void UnsupportedKeywordsMarkPartialWithoutInventingParameters(string keyword, string value)
    {
        var report = Inspect("{\"" + keyword + "\":" + value + "}");
        Assert.True(report.IsPartial);
        Assert.False(report.HasErrors);
        var finding = Assert.Single(report.Diagnostics);
        Assert.Equal("MFT031", finding.Code);
        Assert.Equal("#/" + keyword, finding.Location);
        Assert.Equal(0, Assert.Single(report.SchemaMetrics).DeclaredPropertyCount);
    }

    [Fact]
    public void ReferencedParameterDescriptionIsNotGuessed()
    {
        var report = Inspect("""{"properties":{"id":{"$ref":"#/$defs/Id"},"plain":{}}}""");
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT031" && d.Location == "#/properties/id/$ref");
        Assert.Equal("#/properties/plain", Assert.Single(report.Diagnostics, d => d.Code == "MFT020").Location);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("\"schema\"")]
    public void UnsupportedRootShapesArePartialWithoutUnprovenStructuralErrors(string schema)
    {
        var report = Inspect(schema);
        Assert.True(report.IsPartial);
        Assert.False(report.HasErrors);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT031" && d.Location == "#");
    }

    [Fact]
    public void AnnotationDataIsNeverInterpretedAsSchema()
    {
        var report = Inspect("""
            {"default":{"properties":[],"required":[1],"$ref":"anything"},
             "examples":[{"properties":[],"required":null}],
             "enum":[{"allOf":[],"properties":false}]}
            """);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.IsPartial);
        Assert.Equal(0, Assert.Single(report.SchemaMetrics).DeclaredPropertyCount);
    }

    [Fact]
    public void NestedSchemasAreCheckedButOnlyDirectParametersNeedDescriptions()
    {
        var report = Inspect("""
            {"properties":{"customer":{"description":"Customer", "properties":{"name":{"type":"string"}}},
             "rows":{"description":"Rows", "items":{"properties":[],"required":[1]}}}}
            """);
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "MFT020");
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT032" && d.Location == "#/properties/rows/items/properties");
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT033" && d.Location == "#/properties/rows/items/required");
        Assert.Equal(3, Assert.Single(report.SchemaMetrics).DeclaredPropertyCount);
    }

    [Fact]
    public void MetricsHaveDocumentedJsonValueDepthAndNestedPropertyCounts()
    {
        const string schema = """{"properties":{"a":{"description":"A","properties":{"b":{}}}}}""";
        var metrics = Assert.Single(Inspect(schema).SchemaMetrics);
        Assert.Equal(Encoding.UTF8.GetByteCount(schema), metrics.SchemaBytes);
        Assert.Equal(2, metrics.DeclaredPropertyCount);
        Assert.Equal(4, metrics.MaximumDepth);
        Assert.Equal(6, metrics.AnalyzedNodeCount);
        Assert.False(metrics.IsPartial);
    }

    [Fact]
    public void DisposedSchemaProducesControlledMetadataFailure()
    {
        var document = System.Text.Json.JsonDocument.Parse("{}");
        var tool = new Declaration("Disposed", "Description", document.RootElement);
        document.Dispose();
        var report = ToolContractInspector.Inspect([tool, Function(name: "Other")]);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT005" && d.ToolIndex == 0);
        Assert.True(report.IsPartial);
        Assert.Equal(2, report.SchemaMetrics.Count);
        Assert.Null(report.SchemaMetrics[0].SchemaBytes);
    }
}
