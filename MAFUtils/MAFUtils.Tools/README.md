# MAFUtils.Tools

Tool contract inspection for **Microsoft Agent Framework** applications.
Use `ToolContractInspector` in .NET 10 applications to check exposed tool names, descriptions and input schemas
before passing a tool collection to an agent. It works with standard `Microsoft.Extensions.AI` tool abstractions.

Inspection reads metadata and returns an immutable report with stable diagnostic codes and per-function metrics.
It runs offline, never invokes functions, and does not modify the tools or their schemas. No model provider,
Agent Framework host, credentials or database connection is required.

Use it to catch duplicate tool names, missing descriptions and selected schema problems before an agent receives
your tools. This is especially useful when an application assembles functions from several modules or changes
their contracts over time. The package's current functionality is `ToolContractInspector`: it produces findings
for your application to review, rather than providing a function for the agent to call.

## What is a tool contract?

A tool contract is the metadata an AI service receives about a tool: its exposed name, description and the JSON
schema describing its input arguments. The input schema describes argument names, types and requirements; it is
not the argument values from an individual tool call. For example, a `FindCustomer` contract might describe a
required string argument named `customerId`.

The inspector checks this metadata for authoring problems. It does not inspect the function body or decide whether
an actual customer identifier is valid. A function declaration supplies metadata without an executable implementation;
an executable function supplies both. Either can be inspected.

## Install

From your .NET 10 application directory:

```powershell
dotnet add package MAFUtils.Tools
```

The library references `Microsoft.Extensions.AI.Abstractions` 10.10.0, which NuGet installs as a dependency.

## Quick start: inspect function tools

Create your functions as usual, then inspect the same collection you intend to give your agent:

```csharp
using System.ComponentModel;
using MAFUtils.Tools;
using Microsoft.Extensions.AI;

AITool[] tools = [AIFunctionFactory.Create(FindCustomer)];
ToolInspectionReport report = ToolContractInspector.Inspect(tools);
Console.WriteLine(report.ToText());

if (report.HasErrors)
    throw new InvalidOperationException(report.ToText());

[Description("Find a customer by identifier.")]
static string FindCustomer([Description("Customer identifier.")] string customerId)
    => $"Customer {customerId}";
```

The example inspects the factory-generated contract; it does not call `FindCustomer`.
The method's `Description` attribute describes the tool, and the parameter's attribute describes its input.
These are copied into the contract by `AIFunctionFactory`. The inspector reads the resulting contract rather than
looking for attributes itself. For manually authored declarations, supply the tool description and the parameter
schemas' `description` values directly.

Declaration-only `AIFunctionDeclaration` instances are also supported. Other `AITool` subclasses receive name
and description checks, with schema inspection applied only to function declarations.

Run inspection after assembling your complete tool collection, typically in an automated test or during application
startup before registering tools with your agent. Inspecting the complete collection lets it detect name collisions
between tools assembled from different parts of your application. Re-run it when contracts or collection membership
change; the report is a snapshot, not a monitor. No agent registration or middleware setup is required for inspection.

## Use before Microsoft Agent Framework registration

Inspection belongs in your host application's setup or validation code. Inspect the assembled collection first,
apply your acceptance policy, then pass that same collection to the agent. Register your application functions
with the agent; do not register `ToolContractInspector.Inspect` as an agent tool.

For the agent host, add Microsoft Agent Framework if your application does not already reference it:

```powershell
dotnet add package Microsoft.Agents.AI
```

The following continues with `tools` from the quick start and assumes your host has created `chatClient`, a configured
model provider's `IChatClient` with function-calling support:

```csharp
using MAFUtils.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var inspection = ToolContractInspector.Inspect(tools);
if (inspection.HasErrors)
    throw new InvalidOperationException(inspection.ToText());

AIAgent agent = chatClient.AsAIAgent(
    name: "CustomerAssistant",
    instructions: "Use the available tools to answer customer questions.",
    tools: [.. tools]);
```

This example blocks registration on error findings. Warnings and partial analysis need an explicit application
policy, as described below. Once you run the agent normally, it can invoke the registered functions; the earlier
inspection never invokes them. You do not need to repeat inspection for each chat message unless the contracts
or tool collection change. Inspect the final contracts after any application code that wraps or alters their metadata.

The same inspector can run in a console utility or an automated test without constructing an agent or configuring
a model provider. Installing `Microsoft.Agents.AI` is only necessary for the agent-host example above.

## Inspect a declaration without an implementation

If you have a JSON input schema, create a declaration and inspect it directly:

```csharp
using System.Text.Json;
using MAFUtils.Tools;
using Microsoft.Extensions.AI;

var declaration = AIFunctionFactory.CreateDeclaration(
    "FindCustomer", "Find a customer by identifier.",
    JsonElement.Parse("""
        {"type":"object","properties":{"customerId":{"type":"string","description":"Customer identifier."}},
         "required":["customerId"]}
        """));
var report = ToolContractInspector.Inspect([declaration]);
Console.WriteLine(report.ToText());
```

`HasErrors` is true when at least one error finding exists. `IsSuccessful` means no errors were found;
warnings, informational findings and partial schema analysis do not fail that status. Check `IsPartial`
separately if your application requires every schema to fit the supported inspection scope.

## Deciding whether to accept a report

The inspector reports findings; your application chooses which findings should block startup or fail a test.

| Result | Meaning | Typical next step |
| --- | --- | --- |
| `HasErrors` is true | A definite contract problem or metadata-access failure was found. | Read each finding's `Remedy` and correct the affected tool. |
| `WarningCount` is greater than zero | An authoring concern was found, such as a missing description. | Review it; warnings do not automatically fail inspection. |
| `IsPartial` is true | Some schema analysis was skipped, even if no errors were found. | Read `MFT031` findings or schema-access failures to understand what was skipped. |
| `IsSuccessful` is true and `IsPartial` is false | No errors were found and no partial-analysis conditions were recorded. | Review any warnings; this still does not certify full JSON Schema or provider compliance. |

For example, an application that requires both no errors and no skipped analysis can use:

```csharp
var report = ToolContractInspector.Inspect(tools);
if (report.HasErrors || report.IsPartial)
    throw new InvalidOperationException(report.ToText());
```

This is stricter than the default status: valid schemas using unsupported constructs can fail this application
policy. To make warnings fail your own check too, also test `report.WarningCount > 0`. There is no automatic
throw-on-findings mode. An empty collection produces a successful empty report; if your application requires
at least one tool, check `ToolCount` separately.

### Check contracts in your application's tests

If your application already uses xUnit, a contract test can inspect its functions without executing them:

```csharp
using System.ComponentModel;
using MAFUtils.Tools;
using Microsoft.Extensions.AI;
using Xunit;

public sealed class CustomerToolContractTests
{
    [Fact]
    public void CustomerToolsHaveNoContractErrors()
    {
        AITool[] tools = [AIFunctionFactory.Create(FindCustomer)];
        var report = ToolContractInspector.Inspect(tools);
        Assert.False(report.HasErrors, report.ToText());
    }

    [Description("Find a customer by identifier.")]
    private static string FindCustomer([Description("Customer identifier.")] string customerId)
        => throw new InvalidOperationException("Contract inspection must not execute this function.");
}
```

In your application, assemble the same tool collection used by the host rather than maintaining separate test-only
contracts. The throwing body above demonstrates that inspection reads metadata only. This package does not depend
on xUnit; use your existing test framework's equivalent assertion. Add an `IsPartial` or warning-count assertion
if that is part of your application's acceptance policy.

## Inspection rules

| Code | Severity | Purpose |
| --- | --- | --- |
| `MFT001` | Error | Tool name is null, empty or whitespace. |
| `MFT002` | Error | An exposed name exactly duplicates an earlier name using ordinal comparison. |
| `MFT003` | Warning | A new spelling differs from an earlier name only by ordinal case. Optional. |
| `MFT004` | Error | The input collection contains a null tool entry. |
| `MFT005` | Error | Tool metadata could not be read. Exception details are omitted. |
| `MFT010` | Warning | Tool description is null, empty or whitespace. |
| `MFT011` | Info | Nonblank tool description is shorter than the configured minimum. Disabled by default. |
| `MFT020` | Warning | A directly declared input parameter has no meaningful description. |
| `MFT030` | Info | Input schema is undefined, `true` or an empty object. |
| `MFT031` | Info | Unsupported schema constructs or analysis budgets prevented complete inspection. |
| `MFT032` | Error | A supported `properties` keyword contains something other than an object. |
| `MFT033` | Error | A supported `required` keyword is not an array of distinct strings. |
| `MFT040` | Warning | Input schema exceeds the advisory UTF-8 byte threshold. |
| `MFT041` | Warning | Analysed JSON depth exceeds the advisory threshold. |
| `MFT042` | Warning | Analysed declared-property count exceeds the advisory threshold. |

Each repeated exact name produces one collision finding referring to its first occurrence, so three identical
names produce two `MFT002` findings. Case-only warnings are emitted once per new spelling within a case-insensitive
name group. Blank names are reported separately rather than entered into collision groups.
Ordinal comparison is culture-independent: `Lookup` and `lookup` are different exact names, but form a case-only
collision. These comparisons do not assume that a particular AI provider uses the same naming rules.

Description and complexity warnings are authoring advice. They are not provider limits or predictions of how
well a model will select or invoke a tool.

## Reports and diagnostics

`ToolInspectionReport` includes `ToolCount`, `FunctionCount`, `ErrorCount`, `WarningCount`, `InfoCount`,
`Diagnostics`, `SchemaMetrics`, `HasErrors`, `IsSuccessful` and `IsPartial`. Counts include null input entries
in `ToolCount` and recognised function declarations with inaccessible metadata in `FunctionCount`.

Each diagnostic carries its `Code`, `Severity`, `ToolIndex`, `ToolName`, `Location`, `Summary`, `Remedy` and an
optional `RelatedToolIndex` for collisions. Indices are zero-based. Schema locations use JSON Pointer paths such
as `#/properties/customerId`; names containing `~` or `/` are escaped. Other locations identify metadata members.
Findings are ordered by input index, then ordinal code and ordinal location.

`ToolIndex` identifies the position in the collection supplied to this inspection call, so it distinguishes
duplicate or unnamed tools. It is not a persistent identifier across reordered collections. A schema location of
`#` means the schema root; `#/properties/customerId` means the schema for the `customerId` parameter. In pointer
segments, `~0` represents `~` and `~1` represents `/`.

For example, inspecting two declaration-only tools named `Lookup`, both with `{"type":"object"}` input schemas,
where the first has no description and the second has a nonblank description, produces:

```text
MAFUtils.Tools — tool contract inspection
Tools: 2 | Functions: 2 | Errors: 1 | Warnings: 1 | Info: 0
WARNING MFT010 tool #0 "Lookup" "Description" — Tool description is missing.
ERROR MFT002 tool #1 "Lookup" (related tool #0) "Name" — Duplicate tool name.
Result: FAIL (error-level findings present)
Schema analysis: complete for supported shapes
```

Use diagnostic codes, severities and locations for automated assertions rather than matching human-readable
sentences. `ToText()` uses deterministic LF line endings, escapes names and locations, and truncates each of those
fields after 160 characters. It does not print tool descriptions, schemas or exception messages. Structured
diagnostics retain full names and locations. The inspector does not log or write to the console itself.

The structured report also provides remedies that are not included in the compact text output:

```csharp
foreach (var finding in report.Diagnostics)
{
    Console.WriteLine($"{finding.Code} at tool #{finding.ToolIndex}, {finding.Location}: {finding.Summary}");
    Console.WriteLine(finding.Remedy);
}
```

## Configuration

Pass options for each inspection call. Options use init-only properties: set them when constructing the object,
then reuse it for subsequent calls if desired. Omitted properties keep their defaults.

All seven numeric settings are optional (`int?`) and default to `null`. For a warning threshold, null disables
that check; for an analysis budget, null means unrestricted by that budget. Calling `Inspect(tools)` therefore
applies no description-length, schema-size, depth or property-count thresholds and no schema traversal budgets.
Structural checks, name checks, missing-description checks and unsupported-schema findings still run, and metrics
are still collected. You can enable any threshold or budget independently.

For example, enable selected advisory checks without restricting traversal:

```csharp
var report = ToolContractInspector.Inspect(tools, new ToolInspectionOptions
{
    WarnOnCaseOnlyNames = true,
    MinimumDescriptionLength = 20,
    SchemaByteWarningThreshold = 8_192,
    PropertyCountWarningThreshold = 32,
    IncludeInfo = false
});
```

| Option | Default | Behaviour |
| --- | --- | --- |
| `WarnOnCaseOnlyNames` | `true` | Enables `MFT003`. |
| `IncludeInfo` | `true` | Includes informational findings; disabling them does not clear partial-analysis flags. |
| `InspectAllToolDescriptions` | `true` | Checks non-function tool descriptions too; false restricts description checks to functions. |
| `MinimumDescriptionLength` | `null` | Optional minimum trimmed description length in UTF-16 characters; null or zero disables `MFT011`. |
| `SchemaByteWarningThreshold` | `null` | Optional advisory raw UTF-8 schema size; null disables `MFT040`. |
| `DepthWarningThreshold` | `null` | Optional advisory JSON value depth, with root at zero; null disables `MFT041`. |
| `PropertyCountWarningThreshold` | `null` | Optional advisory count of declared properties at supported schema positions; null disables `MFT042`. |
| `MaxSchemaBytes` | `null` | Optional maximum raw UTF-8 size before traversal is skipped; null imposes no byte budget. |
| `MaxSchemaNodes` | `null` | Optional maximum JSON values visited per schema; null imposes no node budget. |
| `MaxSchemaDepth` | `null` | Optional maximum JSON value depth visited; null imposes no depth budget. |

Advisory warnings use strict greater-than comparisons: a value equal to the threshold does not trigger a warning.
When supplied, description and advisory thresholds must be nonnegative; byte, node and depth budgets must be positive.
Depth budgets have no additional fixed ceiling; any positive `int` is accepted. Invalid options
throw `ArgumentOutOfRangeException` before the collection is enumerated. There are no global mutable defaults.

### Budgets versus warning thresholds

A **budget** limits how much schema inspection work the inspector will perform. It is not a financial, model-token
or tool-execution allowance. Budgets help keep unusually large or deeply nested metadata from consuming excessive
inspection time and memory when you opt into them. Each function's input schema gets its own configured byte, node
and depth budgets; they are not shared across the whole tool collection. With null budgets, large schemas can take
more time and memory to inspect. Traversal does not use recursion, so deeply nested schemas do not consume the call
stack, but unrestricted inspection still depends on available memory. Set budgets if you need predictable work limits,
particularly when inspecting metadata outside your application's control.

**Deep or recursive schemas deserve particular care.** A schema can refer back to itself through `$ref`. A walker
that repeatedly follows such references without detecting cycles can recurse forever; even a finite but very deep
schema can overflow the call stack of a recursive implementation. This inspector avoids those specific problems:
it uses iterative traversal and does not follow `$ref` references. It marks unresolved references as partial analysis
instead. A finite JSON input therefore cannot cause infinite recursion in this inspector.

**Setting budgets is a reasonable safeguard against excessive inspection work**, especially for third-party or
generated metadata. With the unrestricted defaults, a very large or deeply nested schema can still consume substantial
time and memory. `MaxSchemaDepth` bounds how deeply the inspector explores, `MaxSchemaNodes` bounds how many values
it visits, and `MaxSchemaBytes` skips traversal of oversized schemas. Use them together when you need both deep and
wide schemas to stay within application-chosen limits. These budgets control schema inspection; they cannot stop an
infinite loop inside a custom metadata getter, collection enumerator or tool implementation.

A **warning threshold** expresses an authoring preference. Crossing it emits a warning but does not stop inspection.
The corresponding budget controls whether inspection can continue. For example, explicitly opt into a 16 KiB
size warning and a 1 MiB byte budget, with optional node and depth budgets:

```csharp
var options = new ToolInspectionOptions
{
    SchemaByteWarningThreshold = 16_384,
    MaxSchemaBytes = 1_048_576,
    MaxSchemaNodes = 10_000,
    MaxSchemaDepth = 64
};
var report = ToolContractInspector.Inspect(tools, options);
```

With those settings, the size-related outcomes are:

| Raw schema size | Size-related outcome |
| --- | --- |
| 16 KiB (16,384 bytes) | No size warning; the size threshold is not exceeded. |
| 20 KiB (20,480 bytes) | `MFT040` warns about size; traversal proceeds subject to node and depth budgets. |
| 2 MiB (2,097,152 bytes) | `MFT040` warns about size and `MFT031` reports that traversal was skipped because the byte budget was exceeded. |

`MaxSchemaBytes` skips traversal only when size is greater than the budget. `MaxSchemaNodes` stops visiting more
values after the permitted count. `MaxSchemaDepth` skips children beyond the permitted depth; other shallower
branches can still be inspected while the node budget allows. Exactly reaching a node or depth limit is not partial
analysis unless there are additional values or children to skip. Tool name and description checks still run, and
inspection continues with the remaining tools.

When a budget prevents further inspection, findings already collected are retained, the affected metrics and report
have `IsPartial = true`, and an `MFT031` finding explains the limit. Budget exhaustion does not itself produce an
error: `IsSuccessful` can still be true. Counts based on traversal describe only the visited part of the schema.

If a legitimate schema exceeds a configured budget, review its size and structure, then either simplify
it, increase the relevant `MaxSchema...` option, or set that option to null to remove the budget. Raising an advisory threshold only changes
warnings; it does not give the inspector more budget. Raising a budget permits more work; it does not resolve unsupported
schema constructs such as `$ref` or `allOf`.

To remove all budgets from the options above while retaining the size warning:

```csharp
var unrestricted = options with
{
    MaxSchemaBytes = null,
    MaxSchemaNodes = null,
    MaxSchemaDepth = null
};
var report = ToolContractInspector.Inspect(tools, unrestricted);
```

Set `SchemaByteWarningThreshold = null` too if you also want to remove that warning. The other advisory checks
are already disabled in this example. `new ToolInspectionOptions()` restores all numeric settings to null.

`IncludeInfo = false` hides informational findings and makes `InfoCount` zero; it does not disable their underlying
analysis or clear `IsPartial`. Leave informational findings enabled when investigating incomplete inspection.
Budgets do not cap the number of tools in the input collection or impose a wall-clock timeout on custom metadata getters.

### What counts as a node or a level of depth?

A node is one JSON value: an object, array, string, number, boolean or null. Containers count too. Object member
names are not separate nodes. Depth measures JSON nesting, with the root value at zero, rather than the number of
levels in your C# class hierarchy. Descriptions and examples contribute to these metrics even though they are data.

For example:

```json
{"properties":{"customerId":{"type":"string"}}}
```

This has four nodes: the root object at depth 0, the `properties` object at depth 1, the `customerId` schema object
at depth 2, and the string value `"string"` at depth 3. It declares one property. Adding a `description` string inside
the parameter schema adds one node at depth 3, without increasing the maximum depth or declared-property count.
Byte size is the raw UTF-8 JSON length, including internal whitespace; it is not a character count or a model-token estimate.

## Schema coverage and metrics

Structural checks cover `properties` and `required` at the root and in supported nested schema positions:
property schemas, non-tuple `items`, `additionalProperties`, `contains` and `propertyNames`. Only root `properties`
are checked for parameter descriptions. A property using unresolved references or composition does not receive
a guessed missing-description finding.

Boolean schemas are accepted. Undefined input, `true` and `{}` receive unconstrained-schema information;
`false` is not treated as a malformed schema. Nullable type unions are accepted, and a required name does not
have to appear in `properties`. Return schemas are not read, so an absent return schema is not an error.

An **unconstrained schema** does not restrict which input values are accepted. In JSON Schema, `true` and `{}`
accept any value; `false` accepts no values. An unconstrained input schema is not the same as declaring a function
with no parameters. `MFT030` asks you to confirm the intended contract rather than classifying it as malformed.

References, definitions and unsupported applicators produce partial-analysis findings. These include `$ref`,
`$defs`, `allOf`, `anyOf`, `oneOf`, conditional schemas, `not`, `patternProperties`, `dependentSchemas`,
`prefixItems` and tuple-style `items`. Their contents may contribute to JSON depth and node metrics, but their
parameter contracts are not resolved or checked. Annotation data such as `default`, `examples` and `enum`
is never interpreted as a schema. Other schema keywords are not validated against a meta-schema.

For example, `$ref` points to a schema defined elsewhere, and `allOf` combines schema requirements. Resolving those
constructs is outside this inspector's supported scope. A small, valid schema using them can therefore be partial
even when it is well within all budgets. Partial analysis describes the inspector's coverage, not proof of a defect
in the schema. "Complete for supported shapes" in the text report means the supported checks finished, not that
every JSON Schema keyword was validated.

Each function has a `ToolSchemaMetrics` entry:

- `SchemaBytes`: exact raw UTF-8 size, including internal whitespace; zero for undefined input and null when inaccessible.
- `DeclaredPropertyCount`: entries in visited `properties` maps at supported schema positions, including nested properties.
- `MaximumDepth`: deepest visited JSON value, with root at zero. Container and annotation values contribute to depth.
- `AnalyzedNodeCount`: visited JSON values, including containers and annotations.
- `IsPartial`: unsupported constructs, inaccessible metadata or budgets limited inspection.

Use each metrics entry's `ToolIndex` to associate it with the original input collection. There is one entry per
function declaration, including executable functions; non-function tools have no schema metrics entry.

```csharp
foreach (var metrics in report.SchemaMetrics)
{
    var byteCount = metrics.SchemaBytes?.ToString() ?? "unavailable";
    Console.WriteLine($"Tool #{metrics.ToolIndex}: {byteCount} bytes, " +
        $"{metrics.DeclaredPropertyCount} properties, depth {metrics.MaximumDepth}, " +
        $"{metrics.AnalyzedNodeCount} visited values, partial={metrics.IsPartial}");
}
```

When tuning thresholds, inspect these measurements together with the findings. A zero property count can mean
there were no directly inspectable properties, or that traversal was skipped; check `IsPartial` and `MFT031` before
interpreting it. A non-function tool's absence from `SchemaMetrics` is expected and does not itself make the report partial.

Traversal counts can be lower bounds when analysis is partial. Oversized schemas retain their measured byte size
but have zero visited nodes. Byte measurement uses the underlying UTF-8 representation without copying the schema.
When configured, depth and node budgets bound traversal; reaching a limit with remaining values produces `MFT031`.

Inspection is not full JSON Schema validation, runtime argument validation or provider compatibility certification.
It does not establish application authorisation or guarantee that a model will use a tool correctly.

## Input and failure handling

The input collection is materialised once. Null collections throw `ArgumentNullException`; null entries become
`MFT004` findings. Collection-enumeration exceptions propagate. Recoverable metadata getter failures become
`MFT005` findings, allowing unrelated members and tools to be inspected. Inaccessible input schemas also mark
the report as partial. Process-fatal exceptions are not converted into findings.

Reports retain diagnostic values and metrics rather than references to tool implementations. Inspection calls
have independent state. Custom metadata getters and enumerable implementations remain caller-controlled code;
the schema budgets do not impose execution timeouts on them.

## Compatibility

The package targets .NET 10 and references `Microsoft.Extensions.AI.Abstractions` 10.10.0. That combination has
been exercised by offline tests covering factory-generated functions, declaration-only functions and custom tool metadata.
Other version combinations are not claimed as tested.

## Troubleshooting findings

| Situation | What to check |
| --- | --- |
| A description warning remains after changing an attribute. | Recreate the function through `AIFunctionFactory` and inspect its exposed metadata. The inspector checks the contract supplied to it. |
| A report passes but some schema analysis was skipped. | Check `IsPartial` separately from `IsSuccessful`. Keep `IncludeInfo` enabled to see the reasons. |
| Increasing budgets does not remove `MFT031`. | References and composition are unsupported regardless of budget. Review the finding's location and summary. |
| `MFT005` reports inaccessible metadata. | Check your custom metadata getter. For `JsonElement` values backed by a `JsonDocument`, keep the document alive through inspection or use a cloned element. Exception messages are deliberately omitted from the report. |
| You need a stricter acceptance policy. | Evaluate `HasErrors`, `WarningCount`, `IsPartial` or selected diagnostic codes in your application. The package does not expose custom-rule registration or per-rule severity overrides. |

The inspector offers diagnostics and metrics only. It does not rename tools, repair schemas, generate descriptions,
compare stored contract snapshots or validate argument values during invocation.

## License

MAFUtils.Tools is distributed under the [MIT license](https://github.com/deanchalk/MAFUtils/blob/main/LICENSE).
