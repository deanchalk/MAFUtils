# MAFUtils

Independent .NET utility packages for Microsoft Agent Framework and ordinary .NET applications.
This guide is for developers contributing to or maintaining the repository. For installation and application usage,
use the individual package guides below; they require no knowledge of this repository's implementation.

## Packages

| Package | Purpose | Documentation |
| --- | --- | --- |
| `MAFUtils.SqlServer` | Session-scoped SQL Server metadata catalogs, schema discovery and dependency analysis. | [SQL Server package README](MAFUtils/MAFUtils.SqlServer/README.md) |
| `MAFUtils.Tools` | Offline inspection of tool names, descriptions and input schemas. | [Tools package README](MAFUtils/MAFUtils.Tools/README.md) |
| `MAFUtils.ToolResults` | Stores oversized tool results and gives the model a preview plus search and read tools. | [ToolResults package README](MAFUtils/MAFUtils.ToolResults/README.md) |

Each project folder contains the README for consumers of that project's NuGet package, including installation,
quick-start examples, configuration and limitations. Each package's NuGet website/project link points directly to
its README; its repository link points to the source repository. Library dependencies do not connect the packages.
Pushing a release tag publishes every package with the same tagged version after validation succeeds.

## Repository layout

- `MAFUtils/MAFUtils.SqlServer`: SQL Server library and package documentation.
- `MAFUtils/MAFUtils.SqlServer.Tests`: SQL Server unit and integration tests.
- `MAFUtils/MAFUtils.Tools`: contract inspection library and package documentation.
- `MAFUtils/MAFUtils.Tools.Tests`: offline inspector tests and SQL tool contract consumer tests.
- `MAFUtils/MAFUtils.ToolResults`: oversized tool result storage library and package documentation.
- `MAFUtils/MAFUtils.ToolResults.Tests`: offline store, tool and scripted Agent Framework tests.
- `MAFUtils/MAFUtils.slnx`: solution containing the libraries and tests.
- `scripts/Build-Package.ps1`: release validation and separate consumer smoke tests for every package.
- `.github/workflows/nuget.yml`: Windows validation and tag-triggered publication of every package.
- `.github/dependabot.yml`: monthly updates for the commit-SHA-pinned GitHub Actions.
- `global.json`: SDK selection, starting at .NET SDK 10.0.100 and rolling forward to the latest installed feature band.
- `artifacts`: local package and validation output; generated files are not source.

The root README describes the repository. The root `LICENSE` applies across the repository; projects declare the
MIT license in package metadata. Each package includes its own project-local README rather than this overview.

## Architecture and project boundaries

`MAFUtils.SqlServer` separates host-facing operations (`SqlServerMetadataService`), SQL catalog extraction
(`SqlServerCatalogLoader`), immutable snapshots and graph queries (`DatabaseCatalog`), metadata records and policy
options, and the AI function adapter (`SqlServerDiscoveryTools`). One service owns a policy-scoped snapshot.
Loading/refreshing performs database reads; subsequent queries use memory. Incoming/outgoing dependency indexes
are built at capture time. Traversal results are cached by target, direction and transitivity with a 128-entry
eviction threshold. The adapter converts selected exceptions to structured tool responses.

`MAFUtils.Tools` separates inspection orchestration (`ToolContractInspector`), iterative JSON traversal with optional budgets
(`SchemaInspector`), immutable configuration (`ToolInspectionOptions`), and diagnostics/metrics/text formatting
(`ToolInspectionReport`). It references only `Microsoft.Extensions.AI.Abstractions` directly. It must inspect
metadata without invoking tools, reading return schemas or contacting providers. Schema rules apply only at supported
schema positions; annotation payloads remain data. Numeric thresholds and budgets default to null; callers opt into
limits. Unsupported constructs and exhausted configured budgets mark partial analysis. Traversal and diagnostic path
formatting are iterative so unrestricted depth does not consume the call stack.

`MAFUtils.ToolResults` separates the session store (`ToolResultStore`: sliding expiry, least-recently-used eviction
and capacity limits behind one lock), the function wrapper (`ToolResultLimits`, a private `DelegatingAIFunction` that
classifies, measures and stores results), immutable stored text with a line index (`StoredText`), and the companion tools
(`StoredResultTools`). It references only `Microsoft.Extensions.AI.Abstractions` directly. Wrapping must not change a
tool's name, description or input schema, and must leave exceptions, cancellation and non-text content untouched.
Companion tools are marked internally so they are never wrapped themselves.

The Tools test project references the SqlServer and Tools libraries to check real function contracts offline. This is a test dependency,
not a dependency between the distributed packages. Add a shared library only when independently useful packages
actually need common abstractions.

All libraries set `IsAotCompatible` and must not rely on reflection-based System.Text.Json. `SqlServerDiscoveryTools`
and `StoredResultTools` append a source-generated context (`SqlServerJsonContext`, `ToolResultsJsonContext`) after the
Microsoft.Extensions.AI default resolver: add every new tool parameter or result type to the matching context.
SqlServer and Tools enable package validation against the published 0.6.0 baseline; update
`PackageValidationBaselineVersion` after each release, and add it to ToolResults after its first release.

## Development prerequisites

Use a .NET 10 SDK compatible with `global.json` and an IDE that supports the target framework and `.slnx` solution
format, or use the CLI. Package restore requires access to the configured NuGet feeds. The validation script also
requires PowerShell 7. SQL integration tests need a reachable SQL Server instance; the CI workflow uses Windows LocalDB.
No library's tests require a live model provider.

## Build and test

Repository contributors need the .NET 10 SDK. From the repository root:

```powershell
dotnet restore MAFUtils/MAFUtils.slnx
dotnet build MAFUtils/MAFUtils.slnx -c Release
dotnet test MAFUtils/MAFUtils.slnx -c Release
```

To run the Tools or ToolResults tests independently:

```powershell
dotnet test MAFUtils/MAFUtils.Tools.Tests/MAFUtils.Tools.Tests.csproj -c Release
dotnet test MAFUtils/MAFUtils.ToolResults.Tests/MAFUtils.ToolResults.Tests.csproj -c Release
```

The Tools and ToolResults tests run offline without provider credentials or a SQL Server instance. The Tools tests
inspect the existing SQL Server function contracts without opening a database connection.

To run the SQL Server unit tests without its database integration fixtures:

```powershell
dotnet test MAFUtils/MAFUtils.SqlServer.Tests/MAFUtils.SqlServer.Tests.csproj -c Release --filter "FullyQualifiedName~MAFUtils.SqlServer.Tests.ToolTests"
```

SQL integration tests use `MAFUTILS_TEST_SQLSERVER`, or the existing `MAFUtils/local.runsettings` file selected by the
SQL test project. Without either, SQL tests are skipped. The fixture creates and drops its own
`MAFUtilsTests_<guid>` database and requires a test-server identity allowed to do so. It never uses the configured
database as its fixture database.

For a development LocalDB instance, a PowerShell session can configure the test connection as follows:

```powershell
$env:MAFUTILS_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test MAFUtils/MAFUtils.SqlServer.Tests/MAFUtils.SqlServer.Tests.csproj -c Release
```

This example assumes LocalDB is installed and running. Use a dedicated development/test server with the required
create/drop-database permissions. Keep real credentials and local runsettings out of commits. A passing run with
skipped SQL tests is not evidence that the database integration paths were validated.

SQL test coverage includes metadata visibility, complete inventories, cached details, explicit refresh, policy
filters, dependency traversal and invocation through a scripted Agent Framework client. Tools test coverage includes
contract rules, schema shapes, analysis budgets, safe reports, deterministic ordering and concurrent inspection.
ToolResults test coverage includes result classification, size boundaries, expiry and eviction, read and search
paging, surrogate-safe splitting, approval preservation and a scripted Agent Framework run that searches and reads
a stored result.

## Package builds

Build each package independently:

```powershell
dotnet pack MAFUtils/MAFUtils.SqlServer/MAFUtils.SqlServer.csproj -c Release -o artifacts
dotnet pack MAFUtils/MAFUtils.Tools/MAFUtils.Tools.csproj -c Release -o artifacts
dotnet pack MAFUtils/MAFUtils.ToolResults/MAFUtils.ToolResults.csproj -c Release -o artifacts
```

These commands create local packages; they do not publish them. For full release validation of every package, run
`./scripts/Build-Package.ps1 -Version 0.5.0-rc.1` with the SQL integration test connection configured. Omitting the version
uses each project's own default version. See [CHANGELOG.md](CHANGELOG.md) for release notes and
[PUBLISHING.md](PUBLISHING.md) for the shared tag-release process and account setup.

## CI and release validation

The current NuGet workflow runs on main-branch pushes, pull requests, manual dispatch and `v*` tags. Validation
starts LocalDB and calls `scripts/Build-Package.ps1`, which builds the solution, runs each test project with separate
TRX results, rejects skips/failures, packs every library and symbols (validating their public API against the
baseline release), checks package metadata and project README contents, and runs each package in a separate fresh
consumer/cache with reflection-based JSON disabled. Publishing runs only for matching pushed version tags,
after validation, using the configured NuGet environment and trusted publishing credentials. Ordinary package builds
and branch validation do not publish.

For example, pushing `v0.5.0` supplies version `0.5.0` to every library, regardless of its project default.
Each validation run has an isolated `artifacts/nuget/<run-id>` directory; only its validated files are uploaded.
The publish job checks the expected package names, versions and symbol packages before pushing any of them.
NuGet publication is not atomic across packages. Duplicate skipping supports retrying the same validated release
if one push succeeds and another fails; use a new version for different contents and never move a release tag.
The NuGet trusted-publishing policy must authorize every package ID, including first publication of ToolResults.
See [PUBLISHING.md](PUBLISHING.md) for setup and recovery details.

## Making changes and adding packages

Keep runtime changes within the owning library and add focused tests in its test project. Preserve the existing
nullable annotations and useful XML API documentation. Test contract rules by diagnostic code, severity and location;
keep deterministic text-format checks separate. SQL behavior changes need appropriate integration coverage, while
inspector changes must retain offline operation, unrestricted traversal and enforcement of configured budgets. Run the affected tests and a Release solution
build before proposing a change; report any integration coverage that was skipped.

For a new independent package, add its library and test project to the solution, keep dependencies scoped to its
own functionality, and supply its own consumer README. Set `PackageId`, version, description, author, license expression,
repository URL and the README's direct GitHub URL in `PackageProjectUrl`. Include that local README using
`PackageReadmeFile` and `None Update="README.md" Pack="true" PackagePath="/"`. Do not link the root README or license
into the project as package content. Add a package entry above and extend validation/release automation explicitly;
adding a project to the solution does not create an independent release process.

## Documentation responsibilities

Package READMEs are complete instructions for a first-time NuGet consumer: explain the problem solved, prerequisites,
installation, working examples, all supported functionality, configuration/defaults, results, failures and limitations.
Keep each guide independent of sibling packages. Do not put checkout, contribution, internal architecture or publishing
instructions in those guides. Keep consumer migration notes with the affected package and maintainer/repository
guidance here or in the release documents. Check README examples and defaults against the actual public API whenever
behavior changes. NuGet archives should contain exactly the same README as the owning project's GitHub folder.

## License

MAFUtils is distributed under the [MIT license](LICENSE).
