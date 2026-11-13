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

function Invoke-InventoryTool($ToolName, $Version, $Format, $Reports, $Inputs, $Accepted, [scriptblock]$Run) {
    $exit = -1
    $previousInventory, $previousOutput = $env:SpecInventory, $env:SpecInventoryOutput
    try {
        # Metrics starts its own MSBuild workspace; environment properties cross that boundary.
        $env:SpecInventory, $env:SpecInventoryOutput = 'true', $Output
        $global:LASTEXITCODE = 0
        & $Run *> (Join-Path $Output "$ToolName.log")
        $exit = $global:LASTEXITCODE
    } catch {
        $_ | Out-String | Add-Content (Join-Path $Output "$ToolName.log")
    } finally {
        $env:SpecInventory, $env:SpecInventoryOutput = $previousInventory, $previousOutput
    }
    $records.Add(@{ name = $ToolName; version = $Version; format = $Format; reports = @($Reports)
        inputs = @($Inputs); exit = $exit; acceptedExits = @($Accepted)
        inputReport = $(if ($ToolName -match '^(roslyn|metrics)-(.+)$') { $Matches[2] + '.compile.json' } else { $null }) })
}

# The config's rule list has one owner; production projects never import this copy.
$config = @(Get-Content "$Root/.editorconfig" | Where-Object { $_ -match '^dotnet_' })
@('is_global = true', 'global_level = 999') + $config |
    Set-Content "$Output/inventory.globalconfig" -Encoding utf8
$projects = @($files | Where-Object { $_ -like '*.csproj' })
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $buildArguments = @((Join-Path $Root $project), '-nologo', '-v:quiet', '-p:SpecInventory=true',
        "-p:SpecInventoryOutput=$Output", '-p:Configuration=Debug', '-p:RestoreLockedMode=false',
        '-p:EnableWindowsTargeting=true')
    # Deliberately do not disable TreatWarningsAsErrors. Inventory captures reports even on build failure.
    Invoke-InventoryTool "roslyn-$name" 'NetAnalyzers 8.0.0; StyleCop 1.2.0-beta.556; Threading 17.12.19; Roslynator 4.12.9' sarif @("$name.sarif") @() @(0) {
        & dotnet build @buildArguments
        $buildExit = $LASTEXITCODE
        & dotnet msbuild @buildArguments -getItem:Compile > "$Output/$name.compile.json"
        if ($LASTEXITCODE -ne 0 -and $buildExit -eq 0) { $buildExit = $LASTEXITCODE }
        $global:LASTEXITCODE = $buildExit
    }
    Invoke-InventoryTool "metrics-$name" '4.14.0' metrics @("$name.metrics.xml") @() @(0) {
        & dotnet msbuild @buildArguments /t:Metrics -v:normal
    }
}

Invoke-InventoryTool ruff '0.8.6' sarif @('ruff.sarif') @($files | Where-Object { $_ -like '*.py' }) @(0, 1) {
    & python -I -m pip install --disable-pip-version-check ruff==0.8.6
    if ($LASTEXITCODE) { throw 'ruff installation failed' }
    $pythonFiles = @($files | Where-Object { $_ -like '*.py' } | ForEach-Object { Join-Path $Root $_ })
    & python -I -m ruff check @pythonFiles --no-cache --ignore-noqa --no-respect-gitignore --output-format sarif --output-file "$Output/ruff.sarif"
}
Invoke-InventoryTool PSScriptAnalyzer '1.23.0' powershell @('powershell.json') @($files | Where-Object { $_ -match '\.ps(m)?1$' }) @(0) {
    Install-Module PSScriptAnalyzer -RequiredVersion 1.23.0 -Scope CurrentUser -Force -Repository PSGallery
    Import-Module PSScriptAnalyzer -RequiredVersion 1.23.0
    $rows = @(foreach ($file in $files | Where-Object { $_ -match '\.ps(m)?1$' }) {
        Invoke-ScriptAnalyzer -Path (Join-Path $Root $file) -IncludeSuppressed |
            Select-Object RuleName, Message, ScriptPath, Line, Severity, IsSuppressed
    })
    ConvertTo-Json -InputObject $rows -Depth 6 | Set-Content "$Output/powershell.json" -Encoding utf8
}
Invoke-InventoryTool gitleaks '8.24.2' sarif @('gitleaks.sarif') $files @(0, 1) {
    $download = Join-Path $Output 'gitleaks-download'
    New-Item -ItemType Directory $download | Out-Null
    $release = 'https://github.com/gitleaks/gitleaks/releases/download/v8.24.2'
    $archive = 'gitleaks_8.24.2_windows_x64.zip'
    Invoke-WebRequest "$release/$archive" -OutFile "$download/package.zip"
    Invoke-WebRequest "$release/gitleaks_8.24.2_checksums.txt" -OutFile "$download/checksums.txt"
    $expected = @(Get-Content "$download/checksums.txt" | Where-Object { $_ -match "  $([regex]::Escape($archive))$" })
    if ($expected.Count -ne 1 -or (Get-FileHash "$download/package.zip" -Algorithm SHA256).Hash -ine ($expected[0] -split '\s+')[0]) {
        throw 'gitleaks release checksum mismatch or missing archive entry'
    }
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
    & git -C $Root grep -n -I -E 'NoWarn|WarningsNotAsErrors|pragma[[:space:]]+warning|nullable[[:space:]]+disable|SuppressMessage|noqa|pylint:[[:space:]]*disable|dotnet_diagnostic\..*severity[[:space:]]*=[[:space:]]*none|known.red|skip.*selector' -- . |
        Set-Content "$Output/suppressions.txt" -Encoding utf8
}
ConvertTo-Json -InputObject @($records) -Depth 8 | Set-Content "$Output/tools.json" -Encoding utf8
& python -I "$Root/tools/ci/spec_inventory.py" --root $Root --output $Output
if ($LASTEXITCODE) { throw 'Inventory assembly failed; original tool reports retained' }
$clock.Stop()
@{ elapsedSeconds = $clock.Elapsed.TotalSeconds } | ConvertTo-Json | Set-Content "$Output/runtime.json"
