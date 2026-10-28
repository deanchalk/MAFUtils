using System.Buffers;
using System.Globalization;
using System.Text;

namespace MAFUtils.Tools;

/// <summary>The severity of a contract finding; only errors fail inspection.</summary>
public enum ToolDiagnosticSeverity
{
    /// <summary>A definite defect or inaccessible metadata.</summary>
    Error,
    /// <summary>An advisory authoring concern.</summary>
    Warning,
    /// <summary>Optional advice or an analysis limitation.</summary>
    Info
}

/// <summary>A finding detached from the original tool instance.</summary>
/// <param name="Code">Stable MFT rule identifier.</param>
/// <param name="Severity">Finding severity.</param>
/// <param name="ToolIndex">Zero-based input position.</param>
/// <param name="ToolName">Exposed name, or null if unavailable.</param>
/// <param name="Location">JSON Pointer fragment for schema findings; metadata member name otherwise.</param>
/// <param name="Summary">A short description without raw descriptions, schemas or exception messages.</param>
/// <param name="Remedy">Suggested investigation or correction.</param>
/// <param name="RelatedToolIndex">First matching input position for a name collision, if any.</param>
public sealed record ToolDiagnostic(string Code, ToolDiagnosticSeverity Severity, int ToolIndex,
    string? ToolName, string Location, string Summary, string Remedy, int? RelatedToolIndex = null);

/// <summary>Metrics for a function input schema. Counts are lower bounds when analysis is partial.</summary>
/// <param name="ToolIndex">Zero-based input position.</param>
/// <param name="SchemaBytes">Exact raw UTF-8 size, including internal whitespace; zero for undefined, null if inaccessible.</param>
/// <param name="DeclaredPropertyCount">Entries in properties maps at visited supported schema positions, including nested properties.</param>
/// <param name="MaximumDepth">Deepest visited JSON value; root is zero, including annotation values.</param>
/// <param name="AnalyzedNodeCount">Number of visited JSON values, including containers and annotation values.</param>
/// <param name="IsPartial">Unsupported constructs, inaccessible metadata or budgets limited analysis.</param>
public sealed record ToolSchemaMetrics(int ToolIndex, int? SchemaBytes, int DeclaredPropertyCount,
    int MaximumDepth, int AnalyzedNodeCount, bool IsPartial);

/// <summary>Immutable inspection results. Findings are ordered by input index, ordinal code, then ordinal location.</summary>
public sealed class ToolInspectionReport
{
    internal ToolInspectionReport(int toolCount, int functionCount, IEnumerable<ToolDiagnostic> diagnostics,
        IEnumerable<ToolSchemaMetrics> metrics)
    {
        ToolCount = toolCount;
        FunctionCount = functionCount;
        Diagnostics = Array.AsReadOnly(diagnostics.OrderBy(d => d.ToolIndex)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Location, StringComparer.Ordinal).ToArray());
        SchemaMetrics = Array.AsReadOnly(metrics.ToArray());
        ErrorCount = Diagnostics.Count(d => d.Severity == ToolDiagnosticSeverity.Error);
        WarningCount = Diagnostics.Count(d => d.Severity == ToolDiagnosticSeverity.Warning);
        InfoCount = Diagnostics.Count(d => d.Severity == ToolDiagnosticSeverity.Info);
    }

    /// <summary>Total input entries, including null entries reported as MFT004.</summary>
    public int ToolCount { get; }
    /// <summary>Number of function declarations, including executable functions.</summary>
    public int FunctionCount { get; }
    /// <summary>Ordered, read-only diagnostic snapshot.</summary>
    public IReadOnlyList<ToolDiagnostic> Diagnostics { get; }
    /// <summary>Read-only metrics, ordered by input position.</summary>
    public IReadOnlyList<ToolSchemaMetrics> SchemaMetrics { get; }
    /// <summary>Number of error findings.</summary>
    public int ErrorCount { get; }
    /// <summary>Number of warning findings.</summary>
    public int WarningCount { get; }
    /// <summary>Number of included informational findings.</summary>
    public int InfoCount { get; }
    /// <summary>Whether any error finding exists.</summary>
    public bool HasErrors => ErrorCount != 0;
    /// <summary>True when no errors exist. Warnings and partial analysis do not fail this status.</summary>
    public bool IsSuccessful => !HasErrors;
    /// <summary>True even if informational diagnostics are disabled.</summary>
    public bool IsPartial => SchemaMetrics.Any(m => m.IsPartial);

    /// <summary>Format a deterministic LF-separated report. Names/locations are escaped and truncated;
    /// descriptions, schemas and exception details are never printed. Tool numbers are zero-based.</summary>
    public string ToText()
    {
        var text = new StringBuilder("MAFUtils.Tools — tool contract inspection\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Tools: {ToolCount} | Functions: {FunctionCount} | Errors: {ErrorCount} | Warnings: {WarningCount} | Info: {InfoCount}\n");
        foreach (var d in Diagnostics)
        {
            text.Append(CultureInfo.InvariantCulture, $"{d.Severity.ToString().ToUpperInvariant()} {d.Code} tool #{d.ToolIndex}");
            if (d.ToolName is not null) text.Append(' ').Append(Safe(d.ToolName));
            if (d.RelatedToolIndex is int related) text.Append(CultureInfo.InvariantCulture, $" (related tool #{related})");
            text.Append(' ').Append(Safe(d.Location)).Append(" — ").Append(d.Summary).Append('\n');
        }
        text.Append(IsSuccessful ? "Result: PASS (no error-level findings)\n" : "Result: FAIL (error-level findings present)\n");
        text.Append(IsPartial ? "Schema analysis: partial\n" : "Schema analysis: complete for supported shapes\n");
        return text.ToString();
    }

    // Hand-written quoting needs no reflection-based JSON and keeps non-ASCII names readable. Control and invisible
    // formatting characters (including bidirectional overrides and tag characters) and unpaired surrogates are escaped.
    private static string Safe(string value)
    {
        const int maximum = 160;
        int length = value.Length <= maximum ? value.Length
            : char.IsHighSurrogate(value[maximum - 1]) && char.IsLowSurrogate(value[maximum]) ? maximum - 1 : maximum;
        var text = new StringBuilder(length + 3).Append('"');
        for (var rest = value.AsSpan(0, length); !rest.IsEmpty;)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
            {
                text.Append(CultureInfo.InvariantCulture, $"\\u{(int)rest[0]:X4}");
                consumed = 1;
            }
            else if (rune.Value is '"' or '\\')
                text.Append('\\').Append((char)rune.Value);
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                foreach (char unit in rest[..consumed]) text.Append(CultureInfo.InvariantCulture, $"\\u{(int)unit:X4}");
            else
                text.Append(rest[..consumed]);
            rest = rest[consumed..];
        }
        if (length < value.Length) text.Append('…');
        return text.Append('"').ToString();
    }
}
