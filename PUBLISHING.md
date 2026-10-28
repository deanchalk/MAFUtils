# Publishing MAFUtils packages

A pushed version tag releases **MAFUtils.SqlServer**, **MAFUtils.Tools** and **MAFUtils.ToolResults** together, using
the tag's version for every package. They remain independently installable NuGet packages. The test projects are not
packaged. For example, `v0.5.0` produces version `0.5.0` of each library; `v0.5.0-rc.1` produces prerelease packages.
Branch pushes, pull requests and manual workflow runs validate only; they do not publish.

## Build and validate locally

Use the .NET 10 SDK and PowerShell 7. Configure `MAFUTILS_TEST_SQLSERVER` or the ignored
`MAFUtils/local.runsettings` for a dedicated SQL test server. Tests create and drop GUID-named databases and require
permission to do so. With LocalDB installed and started:

```powershell
$env:MAFUTILS_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
./scripts/Build-Package.ps1 -Version 0.5.0-rc.1
```

The script restores/builds the solution, runs each package's test project into a separate results directory, and
requires all tests to pass without skips. It then builds every package and symbol package, checks their identities,
versions, MIT license expressions, README contents and website/repository links, and runs a separate consumer of
each actual NuGet package. Consumers use separate fresh package caches and cannot resolve MAFUtils packages from
nuget.org. They run with reflection-based System.Text.Json disabled, as Native AOT and .NET 10 file-based apps do.
The SQL consumer never connects to a database; the Tools consumer verifies inspection without invocation; the
ToolResults consumer stores an oversized result and reads it back through the companion tools.

Packing also runs package validation against the `PackageValidationBaselineVersion` set in each project (currently
0.6.0, downloaded from nuget.org), so an accidental public API break fails the build. After publishing a release,
raise the baseline in each project to that version. MAFUtils.ToolResults has no baseline until its first release;
add `PackageValidationBaselineVersion` to its project once that version is published.

Each run creates `artifacts/nuget/<run-id>` containing:

- `MAFUtils.SqlServer.VERSION.nupkg` and `.snupkg`.
- `MAFUtils.Tools.VERSION.nupkg` and `.snupkg`.
- `MAFUtils.ToolResults.VERSION.nupkg` and `.snupkg`.

Test results and consumer projects go to `artifacts/validation/<run-id>`. Unique output directories prevent old
packages from entering a new release artifact. The script prints the validated output directory and, in Actions,
exposes it as the step's `package-directory` output. Only that directory is uploaded for publication.

With `-Version`, every package uses that version. Without it, local and ordinary branch builds use each project's
own `<Version>` value. Tag builds always supply the tag version, overriding every project default. Commit the
intended release sources before creating the release tag so source metadata refers to that revision.

Packages contain their own project README, library DLL and XML documentation; MIT is declared in metadata.
Portable PDBs are supplied in symbol packages. Root documentation, local settings and test fixtures are not shipped.

## Configure GitHub and NuGet.org

The workflow uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
to obtain a short-lived publishing key using GitHub OIDC.

1. Ensure `.github/workflows/nuget.yml` and the validation script are committed to `deanchalk/MAFUtils`.
2. In the GitHub repository, keep the **nuget** environment and permit deployment from tags matching **v***.
3. Set the environment secret **NUGET_USER** to **dchalk**, the NuGet.org profile username, not an email address or API key.
4. In that NuGet.org account's **Trusted Publishing** settings, configure the policy for:
   - Repository owner: `deanchalk`.
   - Repository: `MAFUtils`.
   - Workflow file: `nuget.yml` (filename only).
   - Environment: `nuget`.
   - Package scope covering `MAFUtils.SqlServer`, `MAFUtils.Tools` **and** `MAFUtils.ToolResults`. The glob
     `MAFUtils.*` covers all three and future packages under that prefix; choose it only if that broader scope is intended.
   - Permission to create new packages for the first ToolResults publication, and to publish new versions.
5. Confirm `dchalk` has publishing rights for the existing packages and that the ToolResults package ID is available
   or already owned by the appropriate account.

A policy listing only some package IDs will not authorize the others. Account policies are configured on nuget.org,
not by committing repository files. The username change does not change the GitHub repository owner `deanchalk`.

## Publish a shared version

Choose a version unused for every package ID, commit and push the intended source changes, then tag that commit:

```powershell
git tag v0.5.0
git push origin v0.5.0
```

The version above is an example, not a claim that it is currently available. Tags must have the form `vMAJOR.MINOR.PATCH`
with an optional prerelease suffix, such as `v0.5.0-rc.1`. Push the specific tag; a local tag does not start GitHub Actions.
Never move or reuse a release tag to publish different contents.

The Windows validation job starts LocalDB and validates every package before any can be published. The publish
job downloads only those artifacts, verifies that exactly the expected packages and their symbols match the
tag version, obtains the OIDC key, and pushes each package with its adjacent symbols. All use the same version,
even when only one library changed. No changes to the project default versions are required for a tagged release.

Publication of several packages is **not atomic**. One may succeed before another fails. The push uses `--skip-duplicate`
so rerunning the failed publish job with the same validated artifacts can finish the release without replacing an
existing package version. This flag does not compare existing package contents: always use a new version for a new
release, and retry only the original immutable tag/artifacts. If a symbol upload fails, check its status separately.

GitHub Actions → NuGet → Run workflow remains validation-only, including when a version override is supplied.
A successful push can precede NuGet indexing; check each package's status in your NuGet account. Each package's
embedded README changes only when a new version containing it is published.

## Optional manual publication

For recovery using already validated artifacts, supply a suitably scoped `NUGET_API_KEY` in your shell and use
explicit package paths from the validated output directory:

```powershell
dotnet nuget push ./artifacts/nuget/RUN-ID/MAFUtils.SqlServer.0.5.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
dotnet nuget push ./artifacts/nuget/RUN-ID/MAFUtils.Tools.0.5.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
dotnet nuget push ./artifacts/nuget/RUN-ID/MAFUtils.ToolResults.0.5.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
```

Keep each matching `.snupkg` beside its `.nupkg`; the push also submits symbols. Never commit credentials or put
literal API keys into documentation. Review [CHANGELOG.md](CHANGELOG.md) and package limitations before a release.
