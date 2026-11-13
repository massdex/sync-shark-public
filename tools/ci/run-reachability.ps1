$ErrorActionPreference = 'Stop'
$root = $env:GITHUB_WORKSPACE
$output = Join-Path $env:RUNNER_TEMP 'reachability'
New-Item -ItemType Directory -Force $output | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$buildRoot = Join-Path $root 'build/ci'
$properties = @("-p:Configuration=$env:CONFIGURATION", "-p:SyncSharkBuildRoot=$buildRoot", "-p:SyncSharkZigPath=$env:ZIG_EXE")
# Restore may update build/ci assets/packages; tool outputs stay in runner temp.
# This runs after binary uploads, so downstream artifacts cannot be modified.
$projects = @([regex]::Matches([IO.File]::ReadAllText((Join-Path $root 'SyncShark.sln')), '"([^"\r\n]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value })
$projects += 'tests/SyncShark.FilesOnDemand.Tests/SyncShark.FilesOnDemand.Tests.csproj'
foreach ($project in $projects) {
    dotnet restore (Join-Path $root $project) @properties --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Workspace restore failed: $project" }
}
$toolRoot = Join-Path $env:RUNNER_TEMP 'reachability-tool'
dotnet build (Join-Path $root 'tools/ci/Reachability/Reachability.csproj') -c $env:CONFIGURATION "-p:SyncSharkBuildRoot=$toolRoot" -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Reachability tool compilation failed.' }
$assembly = Join-Path $toolRoot "bin/Reachability/$env:CONFIGURATION/net8.0-windows/Reachability.dll"
dotnet $assembly $root $output
$code = $LASTEXITCODE
@{ elapsedSeconds = $watch.Elapsed.TotalSeconds; scope = 'restore, tool build and analysis; peak memory in summary is analyzer process only' } | ConvertTo-Json | Set-Content (Join-Path $output 'ci-time.json')
if ($code -ne 0) { exit $code }
Get-Content (Join-Path $output 'summary.txt') | Add-Content $env:GITHUB_STEP_SUMMARY
