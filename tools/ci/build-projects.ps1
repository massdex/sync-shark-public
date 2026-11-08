# One owner of the project build switch, shared by the parallel build jobs.
# The select job's `build` output names the projects; Target narrows this job to
# its own group so the engine lane can start without waiting for the app/UI build.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('core', 'engine', 'app')][string]$Target,
    [Parameter(Mandatory)][string]$Keys
)
$ErrorActionPreference = 'Stop'
$targetKeys = switch -CaseSensitive ($Target) {
    'core' { @('core', 'core-tests', 'fod-tests') }
    'engine' { @('engine-tests') }
    'app' { @('app', 'ui-fixture') }
}
$selected = @($Keys | ConvertFrom-Json)
if ($selected.Count -eq 0) { throw 'The select job reported no build projects.' }
$keys = @($selected | Where-Object { $targetKeys -ccontains $_ })
if ($keys.Count -eq 0) {
    Write-Host "No selected project belongs to build target '$Target'."
    exit 0
}
$buildRoot = Join-Path $env:GITHUB_WORKSPACE 'build\ci'
foreach ($key in $keys) {
    Write-Host "::group::BUILD $key"
    $code = 1
    switch -CaseSensitive ($key) {
        'core' {
            dotnet build 'src/SyncShark.Core/SyncShark.Core.csproj' -c $env:CONFIGURATION --nologo
            $code = $LASTEXITCODE
        }
        # The test project targets net8.0 (not net8.0-windows) but naturally uses
        # Windows identity APIs on the Windows-only host checks. Keep the one
        # platform-compatibility rule quiet so the runner's full analyzer set does
        # not turn its Windows-only calls into errors.
        'core-tests' {
            dotnet build 'tests/SyncShark.Core.Tests/SyncShark.Core.Tests.csproj' -c $env:CONFIGURATION --nologo -p:NoWarn=CA1416
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                & "tests/SyncShark.Core.Tests/bin/$env:CONFIGURATION/net8.0/SyncShark.Core.Tests.exe" --engine-layout-checks
                $code = $LASTEXITCODE
            }
        }
        # The standalone Files On-Demand project is the native acceptance runner
        # and compiles a different subset of files than the main test project.
        # Build it (compile only, its tests never run in CI) without NoWarn=CA1416
        # so drift the main project hides fails here instead of on native-acceptance day.
        'fod-tests' {
            dotnet build 'tests/SyncShark.FilesOnDemand.Tests/SyncShark.FilesOnDemand.Tests.csproj' -c $env:CONFIGURATION --nologo
            $code = $LASTEXITCODE
        }
        'engine-tests' {
            dotnet build 'tests/SyncShark.Engine.Tests/SyncShark.Engine.Tests.csproj' -c $env:CONFIGURATION --nologo -p:NoWarn=CA1416
            $code = $LASTEXITCODE
        }
        # The product build owns its output allocation through SyncSharkBuildRoot;
        # the native setup script refuses an output outside it.
        'app' {
            dotnet build 'apps/SyncShark/SyncShark.csproj' -c $env:CONFIGURATION --nologo `
                -p:SyncSharkBuildRoot=$buildRoot `
                -p:SyncSharkZigPath=$env:ZIG_EXE
            $code = $LASTEXITCODE
        }
        # The GUI/app fixture is a separate WPF executable that references the app
        # project. Build it into the same allocation so its output sits beside the
        # app's and the copied native Explorer/Discovery helpers come along to the
        # ui jobs.
        'ui-fixture' {
            dotnet build 'tests/SyncShark.UiFixture/SyncShark.UiFixture.csproj' -c $env:CONFIGURATION --nologo `
                -p:SyncSharkBuildRoot=$buildRoot `
                -p:SyncSharkZigPath=$env:ZIG_EXE
            $code = $LASTEXITCODE
        }
        default { throw "Unknown build project '$key'." }
    }
    Write-Host "::endgroup::"
    if ($code -ne 0) { exit $code }
}
