using System.Runtime.InteropServices;
using System.Text.Json;

namespace MAFUtils.Tools;

// Walk JSON values for budgets/metrics, but apply schema rules only in known schema positions.
// In particular, examples/default/enum payloads are data, never schemas.
internal sealed class SchemaInspector
{
    private enum Position { Schema, Properties, Required, Data }

    private readonly ToolInspectionOptions options;
    private readonly Action<string, ToolDiagnosticSeverity, string, string, string> add;
    private int nodes;
    private int depth;
    private int properties;
    private bool partial;

    private SchemaInspector(ToolInspectionOptions options,
        Action<string, ToolDiagnosticSeverity, string, string, string> add)
    {
        this.options = options;
        this.add = add;
    }

    internal static ToolSchemaMetrics Inspect(JsonElement schema, int index, ToolInspectionOptions options,
        Action<string, ToolDiagnosticSeverity, string, string, string> add)
    {
        var walker = new SchemaInspector(options, add);
        // .NET 10 exposes the document's raw UTF-8 span: measure without copying or serializing hostile metadata.
        int bytes = schema.ValueKind == JsonValueKind.Undefined ? 0 : JsonMarshal.GetRawUtf8Value(schema).Length;
        if (bytes > options.SchemaByteWarningThreshold)
            add("MFT040", ToolDiagnosticSeverity.Warning, "#", "Input schema exceeds the advisory byte threshold.",
                "Consider reducing the schema size; this threshold is not a provider limit.");
        if (bytes > options.MaxSchemaBytes)
            walker.Partial("#", "Schema traversal was skipped because the byte budget was exceeded.");
        else if (schema.ValueKind == JsonValueKind.Undefined)
            walker.Unconstrained();
        else
            walker.Visit(schema);

        if (walker.depth > options.DepthWarningThreshold)
            add("MFT041", ToolDiagnosticSeverity.Warning, "#", "Input schema exceeds the advisory depth threshold.",
                "Consider simplifying the nested schema.");
        if (walker.properties > options.PropertyCountWarningThreshold)
            add("MFT042", ToolDiagnosticSeverity.Warning, "#", "Input schema exceeds the advisory property-count threshold.",
                "Consider reducing the number of declared properties.");
        return new(index, bytes, walker.properties, walker.depth, walker.nodes, walker.partial);
    }

    private void Unconstrained() => add("MFT030", ToolDiagnosticSeverity.Info, "#", "Input schema is unconstrained.",
        "Confirm that an unspecified or unconstrained input contract is intentional.");

    private void Partial(string path, string reason)
    {
        partial = true;
        add("MFT031", ToolDiagnosticSeverity.Info, path, reason,
            "Review the skipped construct separately or adjust the analysis budget; this is not full JSON Schema validation.");
    }

    // Retain only one frame per active ancestor, not every sibling. Paths are materialized only for findings,
    // avoiding recursive calls and quadratic retained path strings on very deep unrestricted schemas.
    private sealed record Pointer(Pointer? Parent, string Segment)
    {
        public override string ToString()
        {
            var segments = new Stack<string>();
            for (Pointer? current = this; current?.Parent is not null; current = current.Parent)
                segments.Push(current.Segment);
            var text = new System.Text.StringBuilder("#");
            foreach (var segment in segments) text.Append('/').Append(Escape(segment));
            return text.ToString();
        }
    }

    private sealed class Frame(JsonElement value, Pointer path, int depth, Position position, bool isParameter)
    {
        public JsonElement Value { get; } = value;
        public Pointer Path { get; } = path;
        public int Depth { get; } = depth;
        public Position Position { get; } = position;
        public bool IsParameter { get; } = isParameter;
        public bool Entered;
        public bool HasDescription;
        public bool HasUnsupportedKeyword;
        public bool InvalidRequired;
        public HashSet<string>? RequiredNames;
        public JsonElement.ObjectEnumerator Members;
        public JsonElement.ArrayEnumerator Items;
        public int ArrayIndex;
    }

    private void Visit(JsonElement schema)
    {
        var stack = new Stack<Frame>();
        stack.Push(new(schema, new(null, ""), 0, Position.Schema, false));
        while (stack.TryPeek(out var frame))
        {
            if (!frame.Entered)
            {
                frame.Entered = true;
                nodes++;
                depth = Math.Max(depth, frame.Depth);
                if (frame.Position == Position.Schema)
                {
                    if (frame.Depth == 0 && (frame.Value.ValueKind == JsonValueKind.True ||
                        (frame.Value.ValueKind == JsonValueKind.Object && frame.Value.GetPropertyCount() == 0))) Unconstrained();
                    if (frame.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                        Partial(frame.Path.ToString(), "This schema shape is not supported for parameter inspection.");
                }
                if (frame.Position == Position.Properties && frame.Value.ValueKind != JsonValueKind.Object)
                    add("MFT032", ToolDiagnosticSeverity.Error, frame.Path.ToString(), "The properties keyword must contain an object.",
                        "Use an object mapping property names to schemas.");
                if (frame.Position == Position.Required && frame.Value.ValueKind != JsonValueKind.Array)
                    InvalidRequired(frame.Path.ToString());

                bool hasChildren = frame.Value.ValueKind switch
                {
                    JsonValueKind.Object => frame.Value.GetPropertyCount() > 0,
                    JsonValueKind.Array => frame.Value.GetArrayLength() > 0,
                    _ => false
                };
                if (frame.Depth == options.MaxSchemaDepth && hasChildren)
                {
                    Partial(frame.Path.ToString(), "Child values were skipped because the depth budget was exhausted.");
                    stack.Pop();
                    continue;
                }
                if (frame.Value.ValueKind == JsonValueKind.Object) frame.Members = frame.Value.EnumerateObject();
                if (frame.Value.ValueKind == JsonValueKind.Array)
                {
                    frame.Items = frame.Value.EnumerateArray();
                    if (frame.Position == Position.Required) frame.RequiredNames = new(StringComparer.Ordinal);
                }
            }

            bool hasNext = frame.Value.ValueKind switch
            {
                JsonValueKind.Object => frame.Members.MoveNext(),
                JsonValueKind.Array => frame.Items.MoveNext(),
                _ => false
            };
            if (!hasNext)
            {
                Finish(frame, complete: true);
                stack.Pop();
                continue;
            }
            if (nodes == options.MaxSchemaNodes)
            {
                Partial(frame.Path.ToString(), "Remaining schema values were skipped because the node budget was exhausted.");
                // Preserve proven required-shape errors, but never infer missing descriptions in unfinished objects.
                foreach (var pending in stack) Finish(pending, complete: false);
                break;
            }

            if (frame.Value.ValueKind == JsonValueKind.Object)
            {
                var member = frame.Members.Current;
                string name = member.Name;
                var childPath = new Pointer(frame.Path, name);
                Position childPosition = Position.Data;
                if (frame.Position == Position.Properties)
                {
                    properties++;
                    childPosition = Position.Schema;
                }
                else if (frame.Position == Position.Schema)
                {
                    childPosition = name switch
                    {
                        "properties" => Position.Properties,
                        "required" => Position.Required,
                        "items" when member.Value.ValueKind != JsonValueKind.Array => Position.Schema,
                        "additionalProperties" or "contains" or "propertyNames" => Position.Schema,
                        _ => Position.Data
                    };
                    if (IsUnsupported(name) || (name == "items" && member.Value.ValueKind == JsonValueKind.Array))
                    {
                        frame.HasUnsupportedKeyword = true;
                        Partial(childPath.ToString(), "Reference, composition or other unsupported schema keyword was not resolved; its parameter rules were skipped.");
                    }
                    if (name == "description")
                        frame.HasDescription = member.Value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(member.Value.GetString());
                }
                stack.Push(new(member.Value, childPath, frame.Depth + 1, childPosition,
                    frame.Position == Position.Properties && frame.Depth == 1));
            }
            else
            {
                var item = frame.Items.Current;
                if (frame.RequiredNames is not null && (item.ValueKind != JsonValueKind.String || !frame.RequiredNames.Add(item.GetString()!)))
                    frame.InvalidRequired = true;
                var childPath = new Pointer(frame.Path, frame.ArrayIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                frame.ArrayIndex++;
                stack.Push(new(item, childPath, frame.Depth + 1, Position.Data, false));
            }
        }
    }

    private void Finish(Frame frame, bool complete)
    {
        if (frame.InvalidRequired) InvalidRequired(frame.Path.ToString());
        if (complete && frame.IsParameter && !frame.HasDescription && !frame.HasUnsupportedKeyword &&
            frame.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
            add("MFT020", ToolDiagnosticSeverity.Warning, frame.Path.ToString(), "Parameter description is missing.",
                "Add a description explaining the directly declared parameter.");
    }
    private void InvalidRequired(string path) => add("MFT033", ToolDiagnosticSeverity.Error, path,
        "The required keyword must be an array of distinct strings.", "Use each required property name once in a string array.");

    private static bool IsUnsupported(string keyword) => keyword is
        "$ref" or "$dynamicRef" or "$recursiveRef" or "$defs" or "definitions" or
        "allOf" or "anyOf" or "oneOf" or "not" or "if" or "then" or "else" or
        "dependentSchemas" or "dependencies" or "patternProperties" or "prefixItems" or
        "unevaluatedProperties" or "unevaluatedItems" or "additionalItems";

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}

