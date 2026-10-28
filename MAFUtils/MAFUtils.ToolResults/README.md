# MAFUtils.ToolResults

Keeps oversized tool results out of the model context in **Microsoft Agent Framework** applications.

Wrap your existing tools with `WithResultLimits`. A result at or under the inline limit is returned unchanged. A larger
result is stored in a `ToolResultStore` you own, and the model receives a bounded preview with a handle in its place.
Two companion tools, `SearchStoredResult` and `ReadStoredResult`, let the model find and read the parts it needs.

This follows the same pattern as Pydantic AI Harness's `ToolOutputLimits` and LangChain Deep Agents' tool-result
offloading. Agent Framework's compaction strategies shrink results after they are already in the conversation; this
package bounds a result before the model first reads it, and keeps the full data retrievable.

It works with standard `Microsoft.Extensions.AI` tool abstractions. No model provider, credentials or Agent Framework
host is required by the package itself.

## Install

From your .NET 10 application directory:

```powershell
dotnet add package MAFUtils.ToolResults
```

The library references `Microsoft.Extensions.AI.Abstractions` 10.11.0, which NuGet installs as a dependency.

## Quick start

```csharp
using MAFUtils.ToolResults;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var results = new ToolResultStore();               // one store per conversation or agent session

AITool[] myTools = [AIFunctionFactory.Create(ListInvoices)];
AIAgent agent = chatClient.AsAIAgent(
    instructions: "You answer billing questions.\n" + StoredResultTools.RecommendedInstructions,
    tools: [.. myTools.WithResultLimits(results), .. StoredResultTools.Create(results)]);
```

`WithResultLimits` wraps every `AIFunction` in the collection and returns other tools, such as hosted tools,
unchanged. Wrapped tools keep their names, descriptions and input schemas, so the contract the model sees does not
change. Apply different limits to individual functions with the single-function overload:

```csharp
AIFunction search = AIFunctionFactory.Create(SearchLogs)
    .WithResultLimits(results, new ToolResultLimitOptions { MaxInlineCharacters = 4_000 });
```

## What the model receives

When a result exceeds `MaxInlineCharacters`, the tool returns a `StoredResultReference` instead:

```json
{
  "resultHandle": "res_3f9c0a1e8b7d4c2a91e0f5b6",
  "stored": true,
  "toolName": "ListInvoices",
  "format": "json",
  "totalCharacters": 78321,
  "totalLines": 5002,
  "shape": "array[1000] of object{id, total, status}",
  "preview": "[\n  {\n    \"id\": \"item-00000\",\n    ...",
  "note": "The result was too large to return in full and has been stored. ..."
}
```

- **Shape** is a one-line sketch of the top-level structure: array length and item properties, or object members
  and their kinds. At most 20 members are listed, followed by a count of the rest.
- **Preview** is the first whole lines of the stored text that fit `PreviewCharacters`. It is always incomplete.
- **Stored is false** when the result exceeds `MaxResultCharacters` or the store's total capacity. The handle is then
  null, only the preview exists, and the note tells the model to call the tool again with narrower arguments.

## Companion tools

| Tool | Parameters | Returns |
| --- | --- | --- |
| `SearchStoredResult` | `resultHandle`, `text`, `offset`, `limit` | Lines containing literal text (case-insensitive, not a regex), with the exact `TotalMatches`, a snippet and a zero-based `Line` per match. |
| `ReadStoredResult` | `resultHandle`, `offset`, `limit` | Consecutive lines. A negative `offset` counts from the end, so `-50` reads the last 50 lines. |

Both tools follow `NextOffset` for continuation and stop early when a response would exceed
`ToolResultStoreOptions.MaxReadCharacters`. A match's `Line` can be passed straight to `ReadStoredResult` as `offset`.
Errors are structured, and never presented as empty results:

| Error code | Meaning |
| --- | --- |
| `result_unavailable` | The handle expired, was evicted, was removed, or belongs to another store. |
| `invalid_argument` | Missing handle, search text empty or over 200 characters, or offset or limit out of range. |

`StoredResultTools.RecommendedInstructions` tells the model to search before reading, never to treat a preview as the
whole result, and to treat stored content as untrusted data.

## How results are classified

| Result | Treatment |
| --- | --- |
| `JsonElement` object or array, or a serializable object | JSON. Measured compactly serialized; stored indented, one value per line. |
| `string` or `TextContent` that parses as a JSON object or array | JSON, as above. |
| Any other `string` or `TextContent` | Text. Measured by length; stored line by line. |
| `null`, images, `DataContent`, other `AIContent`, JSON scalars | Returned unchanged. |
| Objects the function's `JsonSerializerOptions` cannot serialize | Returned unchanged. |

Objects are serialized with the wrapped function's own `JsonSerializerOptions`. JSON is written with relaxed escaping
so non-ASCII text stays readable and searchable. Lines over 1,000 characters are wrapped into several stored lines;
a search term that crosses a wrap point is not found. Line breaks never split a surrogate pair.

Exceptions and cancellation from the wrapped tool propagate unchanged; nothing is stored for them.

## Configuration

`ToolResultLimitOptions`, per wrapped tool:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxInlineCharacters` | 10,000 | Results at or under this length are returned unchanged. |
| `PreviewCharacters` | 1,500 | Maximum preview length. Must not exceed `MaxInlineCharacters`. |

`ToolResultStoreOptions`, per store. The store copies them at construction:

| Option | Default | Meaning |
| --- | --- | --- |
| `Expiry` | 30 minutes | Sliding: each read extends a result's lifetime. |
| `MaxEntries` | 32 | Least recently used results are evicted beyond this (at most 1,000). |
| `MaxTotalCharacters` | 4,000,000 | Least recently used results are evicted to stay within this. |
| `MaxResultCharacters` | 1,000,000 | Larger results are not stored; only a preview is returned. |
| `MaxReadCharacters` | 8,000 | Maximum characters in one read or search response (1,000 to 100,000). |

Pass a `TimeProvider` to the store constructor to control expiry in tests.

## Scope and isolation

A handle is only meaningful to the store that issued it. Handles are random (96 bits), not sequential. Create one store
per conversation or agent session so one session can never read another's results. For run-scoped handles, call
`Clear()` when each run ends. `Remove(handle)` deletes one result; `Count` and `TotalCharacters` report usage.

## Reading stored results from the host

Your application can read a stored result directly, for example to log it, show it in a UI or hand it to another
process:

```csharp
if (results.TryGetResult(handle, out StoredToolResult? stored))
    Console.WriteLine($"{stored.ToolName} ({stored.Format}, stored {stored.StoredAt:u}): {stored.Text.Length} characters");
```

`Text` is the complete stored text exactly as stored, including its original line breaks: indented JSON when
`Format` is `json`, otherwise the tool's text. Unlike the agent tools, `TryGetResult` does not extend a result's
lifetime, so host reads never keep a result alive that the model has stopped using. It returns false for unknown,
expired, evicted or removed handles. Take the handle from the `resultHandle` the model received, for example from the
`FunctionResultContent` in the agent's response messages.

The store holds results in process memory. It is not persisted with an agent session and does not survive a restart;
after either, the model receives `result_unavailable` and can call the original tool again.

## Approval-required tools

Wrapping an `ApprovalRequiredAIFunction` returns a new `ApprovalRequiredAIFunction` around the limited function, so
`FunctionInvokingChatClient` still requests approval before the tool runs. Companion tools returned by
`StoredResultTools.Create` are never wrapped, even when passed through `WithResultLimits`.

## Compatibility

- .NET 10, `Microsoft.Extensions.AI.Abstractions` 10.11.0.
- Works anywhere an `AIFunction` is invoked, including `ChatClientAgent` and `FunctionInvokingChatClient`.
- AOT-compatible: results and tool responses use source-generated JSON metadata.

## License

MIT. See the repository's `LICENSE` file.
