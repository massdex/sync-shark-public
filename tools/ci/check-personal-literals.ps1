# Fail when a tracked file carries the owner's personal data or a real fixture name.
# Tracked mode excludes separately published documentation.
#
# Generic shapes only; private patterns come from outside the repository.
[CmdletBinding()]
param(
    [switch]$Staged,
    [string]$ExportRoot,
    [switch]$NoExclusions,
    [switch]$GetPatterns,
    [string[]]$PrivatePatterns,
    $Scan
)

$ErrorActionPreference = 'Stop'
# Native executables returning a non-zero code (git grep "no match" is 1) must not throw.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Export mode deliberately scans every file and path, including hidden and binary files.
# No Git metadata, ignore rules or extension filters apply.
$connectionPattern = '(?:gdrive-)?' + 'connection-' + '(?!0+\b)[0-9a-f]{16,}'
$shapes = @($connectionPattern, '\breview[\/]+[^\/\s]+-\d{8}[\/]+',
    '[A-Z]:[\\/]+Users(?>[\\/]+)(?!(?:You|Fixture|user|<user>)(?:[\\/]|$))[^\s"]+', 'S-1-5-21-(?:\d+-){0,3}\d{7,}')
if ($ExportRoot) {
    if (-not $NoExclusions -or $Staged) { throw 'Export scans require -NoExclusions and cannot use -Staged.' }
    if (-not $PrivatePatterns) { throw 'Export scans require the private literal list (SYNCSHARK_PRIVATE_LITERALS).' }
    $patterns = $shapes + $PrivatePatterns + @('resources[\\/]+(?:evidence|references|worklists)', '[\\/]\.claude[\\/]', '[\\/]worktrees[\\/]',
        'subagents?[\\/]', '\bD-\d{4}-\d+\b', '\b(?:product|process)-\d{4}\b|\bruling R?\d+\b', '"family"\s*:', '\b(?:AGENTS|CLAUDE)\.md\b')
    if ($GetPatterns) { return $patterns }
    $found = $false
    if ($Scan) {
        if ($Scan.Root -cne [IO.Path]::GetFullPath($ExportRoot).TrimEnd('\','/')) { throw 'Guard scan belongs to another export root.' }
        foreach ($relative in $Scan.PersonalPaths) { Write-Host ("Personal literal match in " + $relative); $found = $true }
    } else {
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PersonalLiteralScan.cs'))
        $scanner = Add-Type -TypeDefinition $source -PassThru
        $files = @(Get-ChildItem -LiteralPath $ExportRoot -Recurse -File -Force)
        foreach ($relative in $scanner::Scan($ExportRoot, $files, $patterns)) {
            Write-Host ("Personal literal match in " + $relative)
            $found = $true
        }
    }
    if ($found) { throw 'check-personal-literals: exported tree refused (no exclusions).' }
    Write-Host 'check-personal-literals: exported tree clean (no exclusions).'
    return
}
if ($NoExclusions) { throw '-NoExclusions requires -ExportRoot.' }

$root = (& git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
    Write-Host 'check-personal-literals: not inside a git work tree.'
    exit 1
}

Push-Location $root.Trim()
try {
    $private = @()
    if ($env:SYNCSHARK_PRIVATE_LITERALS) {
        $private = @(Get-Content -LiteralPath $env:SYNCSHARK_PRIVATE_LITERALS | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    } else { Write-Host 'private literal list not set; generic shapes only' }
    $patterns = $shapes + $private
    # '.' keeps the search rooted at the work tree; the exclusions drop the
    # separately published material.
    $pathspecs = @('.', ':(exclude)docs/', ':(exclude)[A]GENTS.md')

    $found = $false
    $index = 0
    foreach ($pattern in $patterns) {
        $index++
        $arguments = @('grep', '-l', '-I', '-i', '-P')
        if ($Staged) { $arguments += '--cached' }
        $arguments += $pattern
        $arguments += '--'
        $arguments += $pathspecs

        $hits = & git @arguments
        $code = $LASTEXITCODE
        if ($code -eq 0 -and $hits) {
            $found = $true
            Write-Host "Personal literal matched (pattern $index)."
        }
        elseif ($code -gt 1) {
            Write-Host "check-personal-literals: git grep failed with exit code $code."
            exit 1
        }
    }

    if ($found) {
        Write-Host ''
        Write-Host 'check-personal-literals: refusing personal data in tracked files.'
        Write-Host 'Use a neutral fixture (for example /Parent/Child or "Pair A") instead.'
        exit 1
    }

    if ($Staged) {
        Write-Host 'check-personal-literals: staged files are clean.'
    }
    else {
        Write-Host 'check-personal-literals: tracked files are clean.'
    }
    exit 0
}
finally {
    Pop-Location
}
