[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path "$PSScriptRoot/../..").Path,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $Output | Out-Null
$records = [Collections.Generic.List[object]]::new()
$files = @(& git -C $Root ls-files)
$clock = [Diagnostics.Stopwatch]::StartNew()
$timeouts = Get-Content "$PSScriptRoot/spec-inventory-timeouts.json" -Raw | ConvertFrom-Json
$processRunner = "$PSScriptRoot/Invoke-BoundedProcess.ps1"

function Invoke-InventoryTool($ToolName, $Version, $Format, $Reports, $Inputs, $Accepted, [scriptblock]$Run) {
    $exit = -1
    $failure = $null
    $toolClock = [Diagnostics.Stopwatch]::StartNew()
    $previousInventory, $previousOutput = $env:SpecInventory, $env:SpecInventoryOutput
    try {
        $env:SpecInventory, $env:SpecInventoryOutput = 'true', $Output
        $global:LASTEXITCODE = 0
        & $Run *> (Join-Path $Output "$ToolName.log")
        $exit = $global:LASTEXITCODE
    } catch {
        $failure = "$ToolName INCOMPLETE after $($toolClock.Elapsed.TotalSeconds) seconds: $($_.Exception.Message)"
        $failure | Add-Content (Join-Path $Output "$ToolName.log")
        $_.InvocationInfo | Format-List * -Force | Out-String | Add-Content (Join-Path $Output "$ToolName.log")
        $_.ScriptStackTrace | Add-Content (Join-Path $Output "$ToolName.log")
    } finally {
        $env:SpecInventory, $env:SpecInventoryOutput = $previousInventory, $previousOutput
    }
    $records.Add(@{ name = $ToolName; version = $Version; format = $Format; reports = @($Reports)
        inputs = @($Inputs); exit = $exit; acceptedExits = @($Accepted)
        failure = $failure; elapsedSeconds = $toolClock.Elapsed.TotalSeconds
        inputCoverage = $(if ($ToolName -in @('gitleaks', 'suppression-candidates')) { 'offered-only' } else { 'evaluated' })
        inputReport = $(if ($ToolName -match '^roslyn-(.+)$') { $Matches[1] + '.compile.json' } else { $null }) })
}

$packages = @{}
([xml](Get-Content "$Root/Directory.Build.props" -Raw)).Project.ItemGroup.PackageReference |
    Where-Object Include | ForEach-Object { $packages[$_.Include] = $_.Version }
$analyzerVersions = ($packages.GetEnumerator() |
    Sort-Object Key | ForEach-Object { "$($_.Key) $($_.Value)" }) -join '; '
$projects = @($files | Where-Object { $_ -like '*.csproj' })
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $buildArguments = @((Join-Path $Root $project), '-nologo', '-v:quiet', '-p:SpecInventory=true',
        "-p:SpecInventoryOutput=$Output", '-p:Configuration=Debug',
        '-p:EnableWindowsTargeting=true', "-p:SyncSharkZigPath=$env:ZIG_EXE",
        "-p:SyncSharkBuildRoot=$(Join-Path $env:GITHUB_WORKSPACE 'build/ci')")
    $buildSeconds = if ($name -eq 'SyncShark') { $timeouts.appBuildSeconds } else { $timeouts.buildSeconds }
    # Deliberately do not disable TreatWarningsAsErrors. Inventory captures reports even on build failure.
    Invoke-InventoryTool "roslyn-$name" $analyzerVersions sarif @("$name.sarif") @() @(0) {
        & $processRunner -Executable dotnet -Arguments (@('build') + $buildArguments) -OutputPath "$Output/$name.build.log" -TimeoutSeconds $buildSeconds
        $buildExit = $LASTEXITCODE
        & $processRunner -Executable dotnet -Arguments (@('msbuild') + $buildArguments + '-getItem:Compile') -OutputPath "$Output/$name.compile.json" -TimeoutSeconds $timeouts.buildSeconds
        if ($LASTEXITCODE -ne 0 -and $buildExit -eq 0) { $buildExit = $LASTEXITCODE }
        $global:LASTEXITCODE = $buildExit
    }

}

$ruffVersion = '0.8.6'
Invoke-InventoryTool ruff $ruffVersion sarif @('ruff.sarif') @($files | Where-Object { $_ -like '*.py' }) @(0, 1) {
    & python -I -m pip install --disable-pip-version-check "ruff==$ruffVersion"
    if ($LASTEXITCODE) { throw 'ruff installation failed' }
    $pythonFiles = @($files | Where-Object { $_ -like '*.py' } | ForEach-Object { Join-Path $Root $_ })
    & python -I -m ruff check @pythonFiles --no-cache --ignore-noqa --no-respect-gitignore --output-format sarif --output-file "$Output/ruff.sarif"
}
$pssaVersion = '1.23.0'
Invoke-InventoryTool PSScriptAnalyzer $pssaVersion powershell @('powershell.json') @($files | Where-Object { $_ -match '\.ps(m)?1$' }) @(0) {
    Install-Module PSScriptAnalyzer -RequiredVersion $pssaVersion -Scope CurrentUser -Force -Repository PSGallery
    Import-Module PSScriptAnalyzer -RequiredVersion $pssaVersion
    $rows = @(foreach ($file in $files | Where-Object { $_ -match '\.ps(m)?1$' }) {
        Invoke-ScriptAnalyzer -Path (Join-Path $Root $file) -IncludeSuppressed |
            Select-Object RuleName, Message, ScriptPath, Line, Severity, IsSuppressed
    })
    ConvertTo-Json -InputObject $rows -Depth 6 | Set-Content "$Output/powershell.json" -Encoding utf8
}
$gitleaksVersion = '8.24.2'
Invoke-InventoryTool gitleaks $gitleaksVersion sarif @('gitleaks.sarif') $files @(0, 1) {
    $download = Join-Path $Output 'gitleaks-download'
    New-Item -ItemType Directory $download | Out-Null
    $release = "https://github.com/gitleaks/gitleaks/releases/download/v$gitleaksVersion"
    $archive = "gitleaks_${gitleaksVersion}_windows_x64.zip"
    Invoke-WebRequest "$release/$archive" -OutFile "$download/package.zip"
    & "$PSScriptRoot/Assert-ArchiveDigest.ps1" -Path "$download/package.zip" -Expected $env:GITLEAKS_ARCHIVE_SHA256 -Name 'gitleaks'
    Expand-Archive "$download/package.zip" "$download/extracted"
    $snapshot = Join-Path $Output 'tracked-snapshot'
    foreach ($file in $files) {
        $destination = Join-Path $snapshot $file
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Root $file) -Destination $destination
    }
    & "$download/extracted/gitleaks.exe" dir $snapshot --redact=100 --report-format sarif --report-path "$Output/gitleaks.sarif" --exit-code 1
}
Invoke-InventoryTool suppression-candidates 'git-native' suppressions @('suppressions.txt') $files @(0, 1) {
    & git -C $Root grep -n -I -i -E 'NoWarn|WarningsNotAsErrors|pragma[[:space:]]+(warning|(GCC|clang)[[:space:]]+diagnostic)|nullable[[:space:]]+disable|SuppressMessage|noqa|pylint:[[:space:]]*disable|dotnet_(analyzer_)?diagnostic\..*severity[[:space:]]*=[[:space:]]*none|known.red|skip.*selector' -- . |
        Set-Content "$Output/suppressions.txt" -Encoding utf8
}
ConvertTo-Json -InputObject @($records) -Depth 8 | Set-Content "$Output/tools.json" -Encoding utf8
& python -I "$Root/tools/ci/spec_inventory.py" --root $Root --output $Output
if ($LASTEXITCODE) { throw 'Inventory assembly failed; original tool reports retained' }
$clock.Stop()
@{ elapsedSeconds = $clock.Elapsed.TotalSeconds } | ConvertTo-Json | Set-Content "$Output/runtime.json"
