# MAFUtils.SqlServer

SQL Server discovery tools for **Microsoft Agent Framework** agents.
Use the [MAFUtils.SqlServer NuGet package](https://www.nuget.org/packages/MAFUtils.SqlServer) to give your agent
ready-made functions for exploring database structure, describing objects and tracing dependencies. Your application
configures the database connection and permitted scope, then registers the tools with its agent.

The tools let an agent answer questions such as "Which schemas contain tables?", "What columns and relationships
does this table have?" and "Which procedures depend on this table, including through views?" without generating
or executing SQL. Metadata means information about database objects, rather than the business records stored in tables.
Application data rows are never loaded.

The package captures permitted metadata in a session-scoped in-memory catalog. After that initial load, tool calls
query the captured catalog without further database round trips. The snapshot stays stable across follow-up questions
until explicitly refreshed. Only requested results are returned to the agent; the full catalog stays in your application.

Start with the Agent Framework integration below. The same APIs also support
[ordinary .NET applications](#secondary-use-ordinary-net-applications) as a secondary use case.

## Install for Microsoft Agent Framework

From your .NET 10 agent application's directory:

```powershell
dotnet add package MAFUtils.SqlServer
dotnet add package Microsoft.Agents.AI
```

Your host application also needs a configured model provider exposing an `IChatClient` with function-calling support.
The package supplies the database tools; your application supplies the model connection and credentials.

Set `SQLSERVER_CONNECTION_STRING` in your application's environment. The SQL identity must be able to read the
required metadata, including database `VIEW DEFINITION` and `SELECT` on `sys.sql_expression_dependencies`.
See [Captured metadata and visibility](#captured-metadata-and-visibility) for scope and permission details.
Review the [release notes](https://github.com/deanchalk/MAFUtils/blob/main/CHANGELOG.md) when upgrading.

## Quick start: give your agent database tools

The example assumes your application has created `chatClient`, your model provider's configured `IChatClient`.
It reads the database connection from the environment and uses `CancellationToken.None`; supply your application's
cancellation token when integrating it into a request or conversation handler.

```csharp
using MAFUtils.SqlServer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var connectionString = Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set SQLSERVER_CONNECTION_STRING first.");
var cancellationToken = CancellationToken.None;

var metadata = new SqlServerMetadataService(connectionString, new SqlServerDiscoveryOptions
{
    AllowedSchemas = [], // Null or empty: all visible schemas. Use exact names to restrict scope.
    IncludeDefinitions = true, // Eagerly cache complete SQL module text; optional.
    CommandTimeoutSeconds = 30,
    MaxPageSize = 100
});

// Recommended: load before the first chat message so initialization has visible progress.
var catalog = await metadata.GetCatalogAsync(cancellationToken);
Console.WriteLine($"Loaded {catalog.Status.ObjectCount} objects at {catalog.Status.CapturedAt:O}");

AIAgent agent = chatClient.AsAIAgent(
    name: "DatabaseExplorer",
    instructions: SqlServerDiscoveryTools.RecommendedInstructions,
    tools: [.. SqlServerDiscoveryTools.Create(metadata)]);

// Retain both the metadata service and the framework conversation session for follow-up questions.
var session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync(
    "Give me a grid containing all schemas and their tables", session));
Console.WriteLine(await agent.RunAsync(
    "Which procedures depend on sales.Customers, including through views or functions?", session));
```

`SqlServerDiscoveryTools.Create(metadata)` creates the function tools backed by your service. Pass the collection
to `AsAIAgent` so the agent can select and invoke them while answering questions. `RecommendedInstructions` provides
guidance on choosing inventory, search and dependency tools, following pages and reporting limitations. Include it
alongside your own application instructions. Tool descriptions explain the individual functions to the model;
the model's choice of tools and wording of answers still depend on your provider and prompts.

The second question illustrates a follow-up in the same conversation. Replace `sales.Customers` with an object
from your database. You can also ask the agent to describe an object, find procedures, explain relationships or
retrieve a module definition when definitions are enabled.

### Keep the catalog and conversation together

Retain both the metadata service and the Agent Framework conversation session for follow-up questions. Create a
service per exploration session and keep its database, identity and allow lists stable. Use a separately scoped
service and tool collection when another user or conversation needs a different database or visibility policy.

First use loads lazily if `GetCatalogAsync` was not called. Concurrent first callers share one successful load.
Canceled/failed initialization is not published and can be retried. No background refresh or time-based expiry occurs.
Call `RefreshCatalogAsync` explicitly after schema changes. It builds a replacement before publishing it atomically;
existing readers keep their previous snapshot, and failed/canceled refresh leaves the previous snapshot usable.
A refresh discards the old snapshot's cached dependency traversals once no references to it remain.

## Agent tools

| Tool | Purpose |
| --- | --- |
| `GetDatabaseInventory` | Complete grouped inventory, including empty permitted schemas. Optional Table/View/Procedure/Function filter. One call answers schema/table grids; no model-controlled enumeration loop. |
| `ListDatabaseObjects` | Literal name search, optional schema/kind filters, paged results. Omitted schema means all permitted schemas. |
| `DescribeObject` | Complete captured columns, parameters, indexes, constraints, expressions and incoming/outgoing foreign keys for one table/view/routine. Module text is separate. |
| `QueryDependencies` | Direct/transitive incoming/outgoing references, exact total count within the known graph, optional shortest paths and paged matches. |
| `GetCatalogStatus` | Snapshot ID, capture interval, object/edge counts and coverage limitations. |
| `RefreshCatalog` | Explicit reload; not needed between ordinary questions. Omitted when `AllowAgentRefresh` is false. |
| `GetObjectDefinition` | Only registered when definitions are enabled. Character slices over fully cached module text, with continuation. |

Tools return `DiscoveryToolResult<T>` with `Data`, or `ErrorCode`/`Error`. Errors are not empty results. SQL exception
details and connection credentials are not returned to the model. Cancellation and unexpected host/programming
errors propagate. Successful results carry snapshot provenance directly or in a `CatalogAnswer<T>` envelope.

Only requested results go to the model; the complete catalog stays in application memory. Inventory intentionally
returns every matching name/kind without truncation, and descriptions contain all captured structural details.
For huge databases, render inventories directly as grids/downloads or narrow the request before model delivery.
`IsComplete` means complete within the snapshot's stated visibility scope, not proof of unrestricted database access.
A host rendering `DatabaseInventory.Schemas` directly does not depend on the model reproducing every row faithfully.

The tools carry their own source-generated JSON metadata, so they also work where reflection-based System.Text.Json
serialization is disabled, such as trimmed applications and .NET 10 file-based apps (`dotnet run app.cs`).

### Definitions, paging and refresh

`IncludeDefinitions = true` captures available SQL module text and adds `GetObjectDefinition` to the registered
tools. Its arguments include `schema`, `name`, `offset` and `characterCount` (default 16,000, maximum 100,000).
The response supplies `Text`, `TotalCharacters`, `NextOffset` and `Availability`; follow `NextOffset` for additional
slices. Null text means unavailable, such as an encrypted module or an object without a SQL module definition.
Slices never split a UTF-16 surrogate pair (for example an emoji): a slice ending inside one stops a character early,
and a one-character slice starting at one returns the whole pair. Always continue from the returned `NextOffset`.

Search and dependency tools return pages with `Items`, `HasMore` and `NextOffset`. The agent should follow the
continuation when it needs more results. For complete schema/table inventories, use `GetDatabaseInventory`, which
returns all matching objects in one call. A page of dependency matches does not reduce the reported `TotalCount`.

`RefreshCatalog` reloads metadata when the user requests fresh information or a database deployment has changed
the schema. It is not needed for each chat message. `GetCatalogStatus` reports the snapshot identity, capture times,
counts and limitations so the agent can explain which capture its answers describe.

## Dependency questions your agent can answer

For example, ask "Which procedures reference sales.ProcessOrder directly?", "Which procedures ultimately depend
on sales.Customers?" or "What does sales.ProcessOrder depend on?". The `QueryDependencies` tool supports incoming
and outgoing direction, direct or transitive traversal, an optional object-kind filter and optional paths.

The catalog is a directed graph, not a tree: shared dependencies, recursion and cycles are normal. Traversal is
cycle-safe, excludes the starting object, deduplicates matches and records a shortest path. Kind filtering happens
**after** traversal so a procedure can be discovered through an intermediate view/function. Both endpoints and every
intermediate node must be in the permitted catalog. Paths follow reference direction, from caller to referenced object.
`TotalCount` covers all matching nodes regardless of paging; `Matches.NextOffset` retrieves further names/paths.
`DependencyMatch.Depth` counts reference edges from the target (1 is a direct match). `Path` is empty unless
`includePaths` is true. Incoming queries answer "what references this object?"; outgoing queries answer "what does
this object reference?". `transitive: true` follows multiple steps rather than stopping at direct references.

Edges come from [sys.sql_expression_dependencies](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql).
They represent known persisted SQL expression references, not proof of runtime execution. Dynamic SQL, unresolved or
caller-dependent references, cross-database/server references and unsupported/excluded objects are not traversed.
Foreign keys are modeled separately. Empty dependency results do not prove that an object is unused. These limitations
are included in dependency responses. No regex inference or procedure execution is used to invent missing edges.

## Configure the service

Configure `SqlServerDiscoveryOptions` in your host application before creating the agent's tools. These settings
control the catalog and responses available through that tool collection. Omitted settings use these defaults:

| Option | Default | Meaning and allowed values |
| --- | --- | --- |
| `CommandTimeoutSeconds` | `30` | Database command timeout, 1–300 seconds. Applies to loading/refreshing metadata. |
| `MaxPageSize` | `100` | Maximum service/agent page size, 1–1000 rows. |
| `MaxDetailRows` | `200` | Maximum rows per type-specific detail section, 1–2000. |
| `MaxTextLength` | `2_000` | Maximum description length in bounded detail responses, 1–16,000 characters. |
| `MaxDefinitionLength` | `16_000` | Maximum text length returned by `GetObjectDefinitionAsync`, 1–100,000 characters. |
| `IncludeDefinitions` | `false` | Capture complete available module text and expose the definition agent tool. |
| `IncludeBuiltInSchemas` | `false` | Also list empty built-in schemas (`guest`, `INFORMATION_SCHEMA`, `sys`, `db_owner` and other fixed role schemas). |
| `AllowAgentRefresh` | `true` | Register the `RefreshCatalog` agent tool. Set false so only your application can reload metadata. |
| `AllowedSchemas` | `null` | Null or empty permits all visible schemas; otherwise restrict to exact schema names. |
| `AllowedObjects` | `null` | Null permits all visible objects; an empty collection permits none; otherwise restrict to exact schema/name pairs. |

For example, restrict a session to two objects (using the `connectionString` from the quick start):

```csharp
var metadata = new SqlServerMetadataService(connectionString, new SqlServerDiscoveryOptions
{
    AllowedSchemas = ["sales"],
    AllowedObjects = [new("sales", "Customers"), new("sales", "Orders")],
    IncludeDefinitions = false,
    MaxPageSize = 50
});
```

Schema and object policies intersect: an object must satisfy both. Allow lists are copied at construction;
create a new service to change the scope. Names must be unquoted, nonblank identifiers of 1–128 characters,
not SQL expressions or bracketed identifiers. Policy comparisons are case-sensitive and culture-independent.
The combined allow-list limit is 500 names, with each object using two names. Invalid options or identifiers throw
argument exceptions.

Page and detail limits control returned responses, not how much metadata the catalog captures. Complete inventories
and `DescribeObjectAsync` are not truncated by these limits. The type-specific detail methods return
`MetadataSection<T>` with `Items` and `IsTruncated`; bounded descriptions and definitions use `MetadataText` with
`Value` and `IsTruncated`. Check those flags rather than assuming a bounded response is complete.

### Connections and authentication

The string constructor accepts a normal `Microsoft.Data.SqlClient` connection string. If your application needs to
configure each connection, use the factory constructor instead:

```csharp
using Microsoft.Data.SqlClient;
using MAFUtils.SqlServer;

var metadata = new SqlServerMetadataService(() => new SqlConnection(connectionString));
```

Return a fresh, unopened connection each time; the service opens and disposes it during loading. Configure your
application's required authentication on that connection, keeping the database and identity stable for the session.
Keep connection strings in application configuration rather than tool arguments. Construction does not load the
database; first use or an explicit `GetCatalogAsync` call does.

## Captured metadata and visibility

Bulk extraction uses one connection and a set-based batch, not one query per object. Response row/text limits never
truncate the underlying catalog. Supported metadata includes:

- User tables, views, SQL/CLR procedures and functions, with schema-qualified names and descriptions.
- Columns, SQL types, nullability, identity/computed flags, computed/default expressions and descriptions.
- Routine parameters, scalar return metadata, table-valued function columns.
- Indexes, index columns, primary/unique keys, filters, check/default constraints and foreign-key column pairs.
- Resolved expression dependencies between supported objects, including names qualified with the current database.
  Functions referenced from CHECK/DEFAULT constraints count as dependencies of the constraint's table.
- Complete available SQL module definitions when `IncludeDefinitions` is enabled.

This is a structural discovery catalog, not a full database backup or DDL scripting engine. Synonyms, triggers,
sequences, table-type members, permissions and physical/storage configuration are not modeled. Procedure result sets
and T-SQL parameter defaults are not inferred. Catalog lengths are bytes (`nvarchar(80)` is 160); -1 means MAX.
Encrypted/inaccessible definitions and objects without SQL modules have null definition text. Tables have no module DDL.

`AllowedSchemas`: null or empty means no restriction. A non-empty collection applies to **both schemas and objects**.
`AllowedObjects`: null means unrestricted; empty means none. When set, only schemas containing permitted supported
objects appear. Active policies intersect and are copied on construction. They use exact ordinal names, independently
of SQL collation, with a combined limit of 500 configured names (each object counts as two).
Lookups use exact names returned by the catalog; name substring search is ordinal case-insensitive and treats `%`/`_`
literally. The in-memory matching semantics do not follow database collation.

Built-in schemas (`guest`, `INFORMATION_SCHEMA`, `sys` and the fixed database-role schemas such as `db_owner`) are
listed only when they contain captured objects, unless `IncludeBuiltInSchemas` is true. `dbo` is always listed.
The allow lists still apply to built-in schemas.

SQL identity permissions still determine visibility. Complete dependency extraction requires database `VIEW DEFINITION`
and `SELECT` on `sys.sql_expression_dependencies`, even when module text is disabled. Missing catalog permissions may
fail the entire load rather than publish a partial snapshot. Other permission-limited metadata can appear absent;
see [SQL Server metadata visibility](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration).
Definitions, expressions, descriptions and type names can themselves mention objects outside the allow list; their text
is not redacted. Treat all database text as untrusted data.

A published snapshot is stable, but its extraction spans multiple catalog reads and is **not** a transactional DDL
snapshot. Concurrent schema deployment can affect capture. Load during a stable period and refresh after deployment;
status includes start/end times. All captured metadata consumes application memory; provision for database size and
concurrent sessions. Definitions can be disabled to reduce memory use.

## Errors and troubleshooting

Agent tools convert selected failures into `DiscoveryToolResult<T>`:

| `ErrorCode` | Meaning | What to check |
| --- | --- | --- |
| `invalid_argument` | An argument failed validation. | Exact unquoted identifiers, object kind, direction, offsets and page limits. |
| `object_unavailable` | The object is absent from the permitted snapshot. | Inventory, case-sensitive names, SQL visibility and configured allow lists. |
| `database_error` | Loading or refreshing metadata failed. | Host-side connectivity, catalog permissions and timeout settings. |

On success, `ErrorCode` is null and `Data` contains the result. On these failures, `Data` is absent; do not interpret
that as an empty database. Unexpected errors and cancellation still propagate. `CatalogAnswer<T>` wraps a value with
snapshot identity, capture time and scope; inventory and dependency results carry their own provenance fields.

If a schema change is missing, explicitly refresh the service. If a dependency is missing, check the limitations
returned with the result, particularly dynamic SQL and excluded intermediate objects. If an inventory is empty,
check the selected kind and the intersection of SQL permissions and allow lists before concluding that no objects exist.

Host-side calls such as the quick start's `GetCatalogAsync` throw exceptions rather than returning tool error
envelopes. Handle connection and permission failures in your application's startup or session-creation flow.
A failed initial load can be retried; a failed refresh leaves the prior snapshot usable. Cancellation propagates
as cancellation rather than an empty response.

## Secondary use: ordinary .NET applications

The same catalog and query APIs can also be used directly in a non-AI application, such as a database browser or
reporting utility. Install only `MAFUtils.SqlServer` for this use; no Agent Framework host or model provider is required.

```powershell
dotnet add package MAFUtils.SqlServer
```

Direct service calls throw argument exceptions for invalid options/inputs, `KeyNotFoundException` for unavailable
objects or mismatched type-specific describe calls, and `SqlException` for database failures. They do not use the
agent tools' `DiscoveryToolResult<T>` error envelopes. Cancellation propagates to the caller.

### List schemas and tables

Set `SQLSERVER_CONNECTION_STRING` in your application's environment, then use:

```csharp
using MAFUtils.SqlServer;

var connectionString = Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set SQLSERVER_CONNECTION_STRING first.");
var metadata = new SqlServerMetadataService(connectionString);

// One call returns every visible, permitted schema and its tables, including empty schemas.
var inventory = await metadata.GetDatabaseInventoryAsync(kind: SqlObjectKind.Table);
foreach (var schema in inventory.Schemas)
{
    var tables = schema.Objects.Count == 0
        ? "(no visible tables)"
        : string.Join(", ", schema.Objects.Select(table => table.Name));
    Console.WriteLine($"{schema.Schema}: {tables}");
}
```

Keep the service for subsequent questions; it reuses the loaded catalog. Call
`await metadata.RefreshCatalogAsync()` when you want to capture database schema changes.

The following examples that use `metadata` continue with this service instance. Replace names such as
`sales.Customers` with exact names returned from your own inventory. No agent or model is needed for these calls.

### Service API reference

| Service member | Use |
| --- | --- |
| `GetCatalogAsync` | Load once and obtain a stable `DatabaseCatalog` snapshot. |
| `RefreshCatalogAsync` | Capture fresh metadata and return the replacement snapshot. |
| `GetDatabaseInventoryAsync` | Return all matching objects grouped by schema; `kind: null` includes all supported kinds. |
| `ListSchemasAsync` | Search and page schema names. |
| `ListDatabaseObjectsAsync` | Search and page objects by name substring, optional schema and kind. |
| `DescribeObjectAsync` | Return complete captured structural details for one object. |
| `QueryDependenciesAsync` | Find incoming or outgoing, direct or transitive dependency matches. |
| `ListReferencingObjectsAsync` | Return a page of direct incoming references without traversal paths. |
| `DescribeTableAsync`, `DescribeViewAsync` | Return type-specific, bounded column, index-column and relationship sections. |
| `DescribeProcedureAsync`, `DescribeFunctionAsync` | Return type-specific, bounded parameter and result-column sections. |
| `GetObjectDefinitionAsync` | Return captured module text, limited by `MaxDefinitionLength`; requires definitions to be enabled. |
| `DefinitionsEnabled` | Check whether this service was configured to capture module text. |

`SqlObjectKind` supports `Table`, `View`, `Procedure` and `Function`. Optional kind filters accept null for all kinds.
All asynchronous service operations accept a `cancellationToken` argument.

### Describe an object

```csharp
var details = await metadata.DescribeObjectAsync("sales", "Customers");
foreach (var column in details.Columns)
    Console.WriteLine($"{column.Name}: {column.Type.Name}, nullable={column.IsNullable}");
foreach (var relationship in details.Relationships)
    Console.WriteLine($"{relationship.FromObject} -> {relationship.ToObject}");
```

`CatalogObjectDetails` contains object identity, columns, routine parameters, indexes, index columns,
foreign-key relationships, constraints, column expressions and definition metadata. Collections contain captured
details relevant to that object. For routine parameters, ordinal zero represents a function return value.
Procedure result sets are not inferred.

### Search and follow pages

```csharp
int offset = 0;
while (true)
{
    var page = await metadata.ListDatabaseObjectsAsync(
        search: "Customer", kind: SqlObjectKind.Table, offset: offset, pageSize: 25);
    foreach (var item in page.Items)
        Console.WriteLine($"{item.Object.Schema}.{item.Object.Name}");
    if (!page.HasMore) break;
    offset = page.NextOffset!.Value;
}
```

`MetadataPage<T>` supplies `Items`, `HasMore` and `NextOffset`. Continue using the returned offset while `HasMore`
is true. Omitted page size defaults to the smaller of 50 and `MaxPageSize`. Omitted schema searches all permitted
schemas. Search is a literal name substring of at most 128 characters; `%` and `_` are not wildcards.
For a complete schema/object grid, use `GetDatabaseInventoryAsync` instead of manually joining pages.

### Work with a stable snapshot

```csharp
var snapshot = await metadata.GetCatalogAsync();
Console.WriteLine($"Snapshot {snapshot.Status.SnapshotId}, captured {snapshot.Status.CapturedAt:O}");
var inventory = snapshot.GetInventory(kind: SqlObjectKind.Table);
var details = snapshot.Describe(new SqlObjectName("sales", "Customers"));
```

`DatabaseCatalog` exposes `Schemas`, `Objects`, `Dependencies` and `Status`, plus synchronous `GetInventory`,
`Search`, `Describe` and `QueryDependencies` methods. These operations use the captured snapshot and never open
a connection. `Search` and `QueryDependencies` default to page size 50 and accept sizes from 1 to 1000; the service's
`MaxPageSize` setting applies to service/agent calls, not these direct snapshot methods.

`Status` includes snapshot identity, capture start/end times, schema/object/dependency counts, whether definitions
were included and coverage limitations. If you retain a snapshot while the service refreshes, it continues to
represent the old capture. Obtain the returned replacement to work with refreshed data.

### Query dependencies directly

These calls use the `metadata` service created above. Replace `CancellationToken.None` with your application's
cancellation token when appropriate.

```csharp
// Procedures directly referencing (normally calling) a procedure.
var cancellationToken = CancellationToken.None;
var callers = await metadata.QueryDependenciesAsync("sales", "ProcessOrder",
    kind: SqlObjectKind.Procedure, cancellationToken: cancellationToken);
Console.WriteLine(callers.TotalCount); // Already counts all matches, not just this page.

// Procedures ultimately depending on a table, through views, functions or other procedures.
var dependants = await metadata.QueryDependenciesAsync("sales", "Customers",
    transitive: true, kind: SqlObjectKind.Procedure, includePaths: true,
    cancellationToken: cancellationToken);

// Everything that a routine ultimately references.
var dependencies = await metadata.QueryDependenciesAsync("sales", "ProcessOrder",
    direction: DependencyDirection.Outgoing, transitive: true,
    cancellationToken: cancellationToken);
```

### Retrieve SQL module definitions

Enable `IncludeDefinitions` when constructing the service, then retrieve a captured definition:

```csharp
var metadata = new SqlServerMetadataService(connectionString,
    new SqlServerDiscoveryOptions { IncludeDefinitions = true });
var definition = await metadata.GetObjectDefinitionAsync("sales", "ProcessOrder");
if (definition.Definition is { } text)
    Console.WriteLine($"{text.Value}\nTruncated: {text.IsTruncated}");
else
    Console.WriteLine("No definition is available for this object.");
```

Calling this method with definitions disabled throws `InvalidOperationException`. Null text means unavailable,
for example an encrypted module or an object without a SQL module definition. For full captured text, use
`(await metadata.DescribeObjectAsync(schema, name)).Definition` on a service with definitions enabled.
The agent definition tool instead returns slices using `offset`, `characterCount` (default 16,000, maximum 100,000)
and `NextOffset`. Follow that continuation to retrieve the remaining text.

## Compatibility with 0.5.x

Empty built-in schemas no longer appear in schema lists or inventories; set `IncludeBuiltInSchemas = true` to restore
them. Dependency counts can increase because current-database-qualified references and CHECK/DEFAULT constraint
function references are now captured. Existing APIs, tool names, arguments and result shapes are unchanged.
The package now depends on `Microsoft.Extensions.AI.Abstractions` rather than `Microsoft.Extensions.AI`; add a direct
reference to `Microsoft.Extensions.AI` if your application used it only through this package.

## Compatibility with 0.3.x

Existing `SqlServerMetadataService` constructors and list/describe methods remain available, but now read from the session
snapshot. Calls no longer automatically observe schema changes. `ListSchemasAsync` now respects the same schema policy
as object queries. Search/lookup semantics are explicit in-memory semantics as described above.

Legacy `DescribeTableAsync`, `DescribeViewAsync`, `DescribeProcedureAsync`, `DescribeFunctionAsync` and
`GetObjectDefinitionAsync` retain their bounded response behaviour and truncation flags. `MaxDetailRows`, `MaxTextLength`
and `MaxDefinitionLength` control those compatibility responses, not the loader. Use `DescribeObjectAsync` or
`catalog.Describe(...)` for complete details. Agent `DescribeObject` returns complete structure, and the definition tool
supports slices instead of irreversible truncation. Default list pages are min(50, MaxPageSize); the default host maximum
is 100 and configured values may be at most 1000.

The agent tool surface has changed: `GetDatabaseInventory` replaces schema-by-schema discovery, `DescribeObject` replaces
four type-specific describe tools, and `QueryDependencies` replaces the direct-only reference tool. Recreate your agent's
tool collection and update prompts to `RecommendedInstructions`. No application row query tool has been introduced.

## License

MAFUtils is distributed under the [MIT license](https://github.com/deanchalk/MAFUtils/blob/main/LICENSE).
