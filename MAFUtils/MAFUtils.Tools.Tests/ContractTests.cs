using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using static MAFUtils.Tools.Tests.TestTools;

namespace MAFUtils.Tools.Tests;

public sealed class ContractTests
{
    [Fact]
    public void EmptyCollectionHasSuccessfulEmptyReport()
    {
        var report = ToolContractInspector.Inspect([]);
        Assert.Equal(0, report.ToolCount);
        Assert.Equal(0, report.FunctionCount);
        Assert.Equal(0, report.ErrorCount + report.WarningCount + report.InfoCount);
        Assert.Empty(report.Diagnostics);
        Assert.Empty(report.SchemaMetrics);
        Assert.True(report.IsSuccessful);
        Assert.False(report.IsPartial);
    }

    [Fact]
    public void NullCollectionThrowsAndNullEntryIsAFinding()
    {
        Assert.Throws<ArgumentNullException>(() => ToolContractInspector.Inspect(null!));
        var report = ToolContractInspector.Inspect([null!, Function()]);
        var finding = Assert.Single(report.Diagnostics, d => d.Code == "MFT004");
        Assert.Equal(0, finding.ToolIndex);
        Assert.Null(finding.ToolName);
        Assert.Equal(ToolDiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(2, report.ToolCount);
        Assert.Equal(1, report.FunctionCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void BlankNamesHaveAnError(string? name)
    {
        var report = ToolContractInspector.Inspect([Function(name: name!)]);
        Assert.Equal(ToolDiagnosticSeverity.Error, Assert.Single(report.Diagnostics, d => d.Code == "MFT001").Severity);
    }

    [Fact]
    public void DuplicateNamesReferenceFirstOccurrenceWithoutQuadraticPairs()
    {
        var report = ToolContractInspector.Inspect([Function(), Function(), Function()]);
        var duplicates = report.Diagnostics.Where(d => d.Code == "MFT002").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.Equal([1, 2], duplicates.Select(d => d.ToolIndex));
        Assert.All(duplicates, d => Assert.Equal(0, d.RelatedToolIndex));
        Assert.All(duplicates, d => Assert.Equal(ToolDiagnosticSeverity.Error, d.Severity));
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "MFT003");
    }

    [Fact]
    public void CaseCollisionIsAnOptionalOrdinalWarning()
    {
        AITool[] tools = [Function(name: "lookup"), Function(name: "LOOKUP")];
        var report = ToolContractInspector.Inspect(tools);
        var collision = Assert.Single(report.Diagnostics, d => d.Code == "MFT003");
        Assert.Equal(0, collision.RelatedToolIndex);
        Assert.Equal(ToolDiagnosticSeverity.Warning, collision.Severity);
        Assert.False(report.HasErrors);
        var disabled = ToolContractInspector.Inspect(tools, new() { WarnOnCaseOnlyNames = false });
        Assert.Equal(report.Diagnostics.Where(d => d.Code != "MFT003"), disabled.Diagnostics);
    }

    [Fact]
    public void DescriptionsAreCheckedOnHostedToolsWithoutSchemaAccess()
    {
        AITool[] tools = [new Hosted("Hosted", " "), Function(description: "")];
        var report = ToolContractInspector.Inspect(tools);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(1, report.FunctionCount);
        Assert.Single(report.SchemaMetrics);
        Assert.True(report.IsSuccessful);
        var functionsOnly = ToolContractInspector.Inspect(tools, new() { InspectAllToolDescriptions = false });
        Assert.Equal(1, Assert.Single(functionsOnly.Diagnostics, d => d.Code == "MFT010").ToolIndex);
    }

    [Fact]
    public void ShortDescriptionsAreOptInAndMeasuredAfterTrimming()
    {
        AITool[] tools = [Function(description: "  abc  ")];
        Assert.DoesNotContain(ToolContractInspector.Inspect(tools).Diagnostics, d => d.Code == "MFT011");
        Assert.DoesNotContain(ToolContractInspector.Inspect(tools, new() { MinimumDescriptionLength = 3 }).Diagnostics, d => d.Code == "MFT011");
        Assert.Equal(ToolDiagnosticSeverity.Info, Assert.Single(ToolContractInspector.Inspect(tools,
            new() { MinimumDescriptionLength = 4 }).Diagnostics, d => d.Code == "MFT011").Severity);
    }

    [Fact]
    public void FactoryGeneratedContractsUseActualAttributesAndNeverInvokeTheFunction()
    {
        var function = AIFunctionFactory.Create(FindCustomer);
        var report = ToolContractInspector.Inspect([function]);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(1, report.FunctionCount);
        Assert.Equal(1, Assert.Single(report.SchemaMetrics).DeclaredPropertyCount);
        Assert.Contains("Customer identifier", function.JsonSchema.ToString());
    }

    [Description("Find a customer by identifier.")]
    private static string FindCustomer([Description("Customer identifier")] string customerId)
        => throw new InvalidOperationException("Function must never be invoked.");

    [Fact]
    public void FactoryDeclarationAndFactoryWithoutAttributesAreInspected()
    {
        var declaration = AIFunctionFactory.CreateDeclaration("Lookup", "Find a customer.",
            Json("""{"type":"object","properties":{"id":{"type":"string","description":"Identifier"}}}"""));
        Assert.False(declaration is AIFunction);
        Assert.Empty(ToolContractInspector.Inspect([declaration]).Diagnostics);
        var undescribed = AIFunctionFactory.Create((Func<string, string>)(id => throw new InvalidOperationException("never called")), "Undescribed");
        var report = ToolContractInspector.Inspect([undescribed]);
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT010");
        Assert.Contains(report.Diagnostics, d => d.Code == "MFT020" && d.Location == "#/properties/id");
    }

    [Fact]
    public void InputIsEnumeratedOnce()
    {
        var source = new SingleUse([Function(), Function(name: "Other")]);
        Assert.Equal(2, ToolContractInspector.Inspect(source).ToolCount);
        Assert.Equal(1, source.Enumerations);
    }

    private sealed class SingleUse(AITool[] tools) : IEnumerable<AITool>
    {
        public int Enumerations { get; private set; }
        public IEnumerator<AITool> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("Second enumeration");
            return ((IEnumerable<AITool>)tools).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Description")]
    [InlineData("JsonSchema")]
    public void ThrowingMetadataIsIsolatedAndExceptionDetailsArePrivate(string member)
    {
        var report = ToolContractInspector.Inspect([new Throwing(member), Function(name: "Other", description: "")]);
        var finding = Assert.Single(report.Diagnostics, d => d.Code == "MFT005");
        Assert.Equal(member, finding.Location);
        Assert.Equal(ToolDiagnosticSeverity.Error, finding.Severity);
        Assert.Contains(report.Diagnostics, d => d.ToolIndex == 1 && d.Code == "MFT010");
        Assert.DoesNotContain("secret", report.ToText());
        Assert.Equal(member == "JsonSchema", report.IsPartial);
    }

    [Fact]
    public void FatalExceptionsAreNotTurnedIntoSuccessfulReports()
        => Assert.Throws<OutOfMemoryException>(() => ToolContractInspector.Inspect([new Throwing("Name", new OutOfMemoryException())]));

    [Fact]
    public async Task ReportsAreDeterministicAcrossConcurrentCallsAndCultures()
    {
        AITool[] tools = [Function("""{"properties":{"z":{},"a":{}}}""", name: "İ工具"), Function(name: "İ工具")];
        var expected = ToolContractInspector.Inspect(tools);
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            return ToolContractInspector.Inspect(tools);
        }));
        foreach (var report in await Task.WhenAll(tasks))
        {
            Assert.Equal(expected.Diagnostics, report.Diagnostics);
            Assert.Equal(expected.SchemaMetrics, report.SchemaMetrics);
            Assert.Equal(expected.ToText(), report.ToText());
        }
        Assert.Equal(["#/properties/a", "#/properties/z"], expected.Diagnostics.Where(d => d.Code == "MFT020").Select(d => d.Location));
    }

    [Fact]
    public void ReportSnapshotsCannotBeMutatedAndTextOmitsSensitiveMetadata()
    {
        AITool[] input = [Function("""{"default":"secret-schema"}""", name: "bad\nname\u001b[31m", description: "secret-description")];
        var report = ToolContractInspector.Inspect(input, new() { MinimumDescriptionLength = 100 });
        input[0] = Function(name: "Changed");
        Assert.Throws<NotSupportedException>(() => ((IList<ToolDiagnostic>)report.Diagnostics).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ToolSchemaMetrics>)report.SchemaMetrics).Clear());
        Assert.DoesNotContain("secret", report.ToText());
        Assert.DoesNotContain("Changed", report.ToText());
        Assert.DoesNotContain("bad\nname", report.ToText());
        Assert.DoesNotContain("\u001b", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TextReportHasStableCountsAndStatus()
    {
        var report = ToolContractInspector.Inspect([new Hosted("Lookup", ""), new Hosted("Lookup", "Good description")]);
        Assert.Equal("""
            MAFUtils.Tools — tool contract inspection
            Tools: 2 | Functions: 0 | Errors: 1 | Warnings: 1 | Info: 0
            WARNING MFT010 tool #0 "Lookup" "Description" — Tool description is missing.
            ERROR MFT002 tool #1 "Lookup" (related tool #0) "Name" — Duplicate tool name.
            Result: FAIL (error-level findings present)
            Schema analysis: complete for supported shapes
            """.Replace("\r\n", "\n") + "\n", report.ToText());
    }
}
