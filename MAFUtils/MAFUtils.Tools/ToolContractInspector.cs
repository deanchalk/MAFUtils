using Microsoft.Extensions.AI;

namespace MAFUtils.Tools;

/// <summary>Inspects model-facing contracts offline, without invoking functions or reading return schemas.</summary>
/// <remarks>
/// Structural checks cover properties and required at the root and supported nested schema positions
/// (properties, non-tuple items, additionalProperties, contains and propertyNames). Only root properties
/// are checked for parameter descriptions. References, definitions, composition and other unsupported
/// applicators produce MFT031; they are traversed as data for depth/node metrics, without interpreting
/// their parameter contracts. Other keywords and annotations are not validated against a meta-schema.
/// Exact name collisions produce one MFT002 per occurrence after the first. Case-only collisions produce
/// one MFT003 per new spelling after the first spelling in an ordinal case-insensitive group.
/// MFT001–003 concern names, MFT004 null entries, MFT005 inaccessible metadata, MFT010–011 tool descriptions,
/// MFT020 direct parameter descriptions, MFT030 unconstrained schemas, MFT031 partial analysis,
/// MFT032–033 properties/required shapes, and MFT040–042 advisory byte/depth/property thresholds.
/// Numeric thresholds and budgets default to null (disabled/unrestricted). Configured threshold warnings use
/// strict greater-than comparisons. No result certifies provider compatibility.
/// </remarks>
public static class ToolContractInspector
{
    /// <summary>Enumerate the collection once and inspect metadata. Null entries yield MFT004;
    /// nonfatal metadata getter failures yield MFT005 and do not prevent inspection of other members/tools.
    /// Collection enumeration exceptions propagate. Arbitrary custom getters remain caller-controlled code.</summary>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    public static ToolInspectionReport Inspect(IEnumerable<AITool> tools, ToolInspectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        options ??= new();
        options.Validate();
        var inputs = tools.ToArray();
        var findings = new List<ToolDiagnostic>();
        var metrics = new List<ToolSchemaMetrics>();
        var exactNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var foldedNames = new Dictionary<string, (string Name, int Index)>(StringComparer.OrdinalIgnoreCase);
        int functions = 0;

        for (int index = 0; index < inputs.Length; index++)
        {
            var tool = inputs[index];
            string? name = null;
            void Add(string code, ToolDiagnosticSeverity severity, string location, string summary, string remedy, int? related = null)
            {
                if (options.IncludeInfo || severity != ToolDiagnosticSeverity.Info)
                    findings.Add(new(code, severity, index, name, location, summary, remedy, related));
            }
            void Failure(string member) => Add("MFT005", ToolDiagnosticSeverity.Error, member,
                "Tool metadata could not be read.", "Ensure the metadata getter returns a usable value; exception details are deliberately omitted.");

            if (tool is null)
            {
                Add("MFT004", ToolDiagnosticSeverity.Error, "", "The tool entry is null.", "Remove or replace the null entry.");
                continue;
            }
            bool isFunction = tool is AIFunctionDeclaration;
            if (isFunction) functions++;
            try
            {
                name = tool.Name;
                if (string.IsNullOrWhiteSpace(name))
                    Add("MFT001", ToolDiagnosticSeverity.Error, "Name", "Tool name is blank.", "Supply a nonblank exposed tool name.");
                else
                {
                    // Each later occurrence refers to the first matching name: N duplicates produce N-1 findings.
                    if (!exactNames.TryAdd(name, index))
                        Add("MFT002", ToolDiagnosticSeverity.Error, "Name", "Duplicate tool name.", "Assign distinct names to the colliding tools.", exactNames[name]);
                    else if (options.WarnOnCaseOnlyNames && foldedNames.TryGetValue(name, out var first) && first.Name != name)
                        Add("MFT003", ToolDiagnosticSeverity.Warning, "Name", "Tool names differ only by case.", "Consider names that are unambiguous across consumers.", first.Index);
                    foldedNames.TryAdd(name, (name, index));
                }
            }
            catch (Exception e) when (IsRecoverable(e)) { Failure("Name"); }

            if (isFunction || options.InspectAllToolDescriptions)
            {
                try
                {
                    var description = tool.Description;
                    if (string.IsNullOrWhiteSpace(description))
                        Add("MFT010", ToolDiagnosticSeverity.Warning, "Description", "Tool description is missing.", "Describe when to use the tool and what it does.");
                    else if (description.Trim().Length < options.MinimumDescriptionLength)
                        Add("MFT011", ToolDiagnosticSeverity.Info, "Description", "Tool description is unusually short.", "Check that it explains the tool's purpose sufficiently.");
                }
                catch (Exception e) when (IsRecoverable(e)) { Failure("Description"); }
            }

            if (tool is AIFunctionDeclaration declaration)
            {
                try
                {
                    metrics.Add(SchemaInspector.Inspect(declaration.JsonSchema, index, options,
                        (code, severity, path, summary, remedy) => Add(code, severity, path, summary, remedy)));
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    Failure("JsonSchema");
                    metrics.Add(new(index, null, 0, 0, 0, true));
                }
            }
        }
        return new(inputs.Length, functions, findings, metrics);
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
