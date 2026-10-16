#Requires -Version 7.0
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'MAFUtils/MAFUtils.slnx'
$ids = @('MAFUtils.SqlServer', 'MAFUtils.Tools')
if ($Version -and $Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be major.minor.patch with an optional prerelease suffix, e.g. 0.5.0-rc.1.'
}
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }
# Isolated output prevents stale packages from entering a later release artifact.
$runId = [guid]::NewGuid().ToString('N')
$output = Join-Path $root "artifacts/nuget/$runId"
$validation = Join-Path $root "artifacts/validation/$runId"
New-Item -ItemType Directory -Force $output,$validation | Out-Null
Push-Location $root
try {
    Invoke-Dotnet restore $solution @versionArgs
    Invoke-Dotnet build $solution -c Release --no-restore @versionArgs
    foreach ($id in $ids) {
        $testProject = Join-Path $root "MAFUtils/$id.Tests/$id.Tests.csproj"
        $testOutput = Join-Path $validation "$id.Tests"
        Invoke-Dotnet test $testProject -c Release --no-build --no-restore --logger 'trx;LogFileName=tests.trx' --results-directory $testOutput
        [xml]$results = Get-Content -LiteralPath (Join-Path $testOutput 'tests.trx')
        $counts = $results.TestRun.ResultSummary.Counters
        if ([int]$counts.total -eq 0 -or [int]$counts.passed -ne [int]$counts.total) {
            throw "$id release validation requires every test to pass. Configure MAFUTILS_TEST_SQLSERVER or local.runsettings; skipped tests are not accepted."
        }
    }
    foreach ($id in $ids) {
        $project = Join-Path $root "MAFUtils/$id/$id.csproj"
        [xml]$projectXml = Get-Content -LiteralPath $project
        $packageVersion = if ($Version) { $Version } else { [string]$projectXml.Project.PropertyGroup.Version }
        Invoke-Dotnet pack $project -c Release --no-build --no-restore @versionArgs --output $output
        $package = Join-Path $output "$id.$packageVersion.nupkg"
        $symbols = Join-Path $output "$id.$packageVersion.snupkg"
        $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
        try {
            $names = @($zip.Entries.FullName)
            foreach ($required in @("$id.nuspec", 'README.md', "lib/net10.0/$id.dll", "lib/net10.0/$id.xml")) {
                if ($required -notin $names) { throw "$id package missing $required" }
            }
            if ($names.Where({ $_ -match '(?i)(\.runsettings$|\.cs$|\.csproj$|appsettings.*\.json$|\.env$)' }).Count -gt 0) {
                throw "Unexpected source/configuration files in $id package."
            }
            $reader = [System.IO.StreamReader]::new($zip.GetEntry("$id.nuspec").Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $manifest.package.metadata
            if ($metadata.id -ne $id -or $metadata.version -ne $packageVersion -or
                $metadata.authors -ne 'Dean Chalk' -or $metadata.license.InnerText -ne 'MIT' -or
                $metadata.repository.url -ne 'https://github.com/deanchalk/MAFUtils' -or
                $metadata.projectUrl -ne "https://github.com/deanchalk/MAFUtils/blob/main/MAFUtils/$id/README.md" -or
                $metadata.readme -ne 'README.md') {
                throw "$id package metadata is incorrect."
            }
            $reader = [System.IO.StreamReader]::new($zip.GetEntry('README.md').Open())
            try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($readme -cne (Get-Content -LiteralPath (Join-Path (Split-Path $project) 'README.md') -Raw)) {
                throw "$id package README differs from its project README."
            }
        } finally { $zip.Dispose() }
        $zip = [System.IO.Compression.ZipFile]::OpenRead($symbols)
        try {
            if ("lib/net10.0/$id.pdb" -notin $zip.Entries.FullName) { throw "$id portable symbols are missing." }
        } finally { $zip.Dispose() }
        # Consume each package separately so a sibling cannot hide missing declared dependencies.
        $consumer = Join-Path $validation "consumer-$id"
        New-Item -ItemType Directory $consumer | Out-Null
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="$id" Version="[$packageVersion]" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
        $program = if ($id -eq 'MAFUtils.SqlServer') {
            @'
using MAFUtils.SqlServer;
var service = new SqlServerMetadataService(() => throw new InvalidOperationException("Must not connect."));
var names = SqlServerDiscoveryTools.Create(service).Select(t => t.Name).ToArray();
if (!names.Contains("GetDatabaseInventory") || !names.Contains("QueryDependencies"))
    throw new InvalidOperationException("Expected public tools are missing.");
Console.WriteLine("SQL Server package consumer passed.");
'@
        } else {
            @'
using MAFUtils.Tools;
using Microsoft.Extensions.AI;
var function = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("Must not invoke.")), "Lookup", "Look up an item.");
var report = ToolContractInspector.Inspect([function, function], new ToolInspectionOptions { MaxSchemaDepth = null });
if (!report.HasErrors || report.Diagnostics.Count(d => d.Code == "MFT002") != 1)
    throw new InvalidOperationException("Contract inspection failed.");
Console.WriteLine("Tools package consumer passed.");
'@
        }
        $program | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
        $escapedOutput = [System.Security.SecurityElement]::Escape($output)
        @"
<configuration>
  <packageSources><clear /><add key="built" value="$escapedOutput" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="built"><package pattern="MAFUtils.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.Config')
        $consumerProject = Join-Path $consumer 'Consumer.csproj'
        Invoke-Dotnet restore $consumerProject --configfile (Join-Path $consumer 'NuGet.Config') --packages (Join-Path $consumer 'packages')
        Invoke-Dotnet run --project $consumerProject -c Release --no-restore
        Write-Host "Validated: $package and $symbols"
    }
    if ($env:GITHUB_OUTPUT) { "package-directory=$output" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    Write-Host "Validated release output: $output"
} finally { Pop-Location }
