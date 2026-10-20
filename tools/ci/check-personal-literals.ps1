# Fail when a tracked file carries the owner's personal data or a real fixture name.
# docs/ and AGENTS.md are excluded (they are handled separately).
#
# Patterns are assembled from fragments so this guard never matches its own source.
[CmdletBinding()]
param(
    [switch]$Staged,
    [string]$ExportRoot,
    [switch]$NoExclusions
)

$ErrorActionPreference = 'Stop'
# Native executables returning a non-zero code (git grep "no match" is 1) must not throw.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Export mode deliberately scans every file and path, including hidden and binary files.
# No Git metadata, ignore rules or extension filters apply.
# Synthetic user-path placeholders only: You, Fixture, user, <user> (case-insensitive).
# SIDs with any 7+ digit domain component or the known private prefix are refused.
if ($ExportRoot) {
    if (-not $NoExclusions -or $Staged) { throw 'Export scans require -NoExclusions and cannot use -Staged.' }
    $patterns = @(
        ('yuv' + 'al'), ('Appl' + 'iances'), ('\bCod' + 'ex\b'),
        ('[A-Z]:[\\/]+Users(?>[\\/]+)(?!(?:You|Fixture|user|<user>)(?:[\\/]|$))[^\s"]+'),
        ('S-1-' + '5-21-(?:4004|(?:\d+-){0,3}\d{7,})'),
        ('[A-Z]:[\\/]+Bri' + 'dge(?:[\\/]|\b)'),
        ('[\\/]Documents[\\/]+(?:Cod' + 'ex|Bri' + 'dge)'),
        ('resources[\\/]+(?:evidence|references|worklists)'),
        ('[\\/]\.claude[\\/]'), ('[\\/]worktrees[\\/]')
    )
    $found = $false
    foreach ($file in Get-ChildItem -LiteralPath $ExportRoot -Recurse -File -Force) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $text = $file.FullName.Substring($ExportRoot.Length) + "`n" + [Text.Encoding]::UTF8.GetString($bytes) +
            "`n" + [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($pattern in $patterns) {
            if ([regex]::IsMatch($text, $pattern, 'IgnoreCase')) {
                Write-Host ("Personal literal match in " + $file.FullName.Substring($ExportRoot.Length))
                $found = $true
                break
            }
        }
    }
    if ($found) { throw 'check-personal-literals: exported tree refused (no exclusions).' }
    Write-Host 'check-personal-literals: exported tree clean (no exclusions).'
    exit 0
}
if ($NoExclusions) { throw '-NoExclusions requires -ExportRoot.' }

$root = (& git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
    Write-Host 'check-personal-literals: not inside a git work tree.'
    exit 1
}

Push-Location $root.Trim()
try {
    # The account name is assembled from fragments too, so this guard never
    # matches its own source now that the bare name is a pattern.
    $accountName = 'yuv' + 'al'
    $separator = '[\\/]+'
    # A one-character string delimiter avoids native quote marshalling differences.
    $quote = '.'
    $privateFolder = 'Cod' + 'ex'
    $patterns = @(
        ($accountName + '\.' + 'mass'),
        ($accountName + '_' + 'mass'),
        ('C:' + '\\+' + 'Users' + '\\+' + $accountName),
        ('S-1-5-21-' + '4004'),
        ($separator + 'Documents' + $separator + $privateFolder),
        ($privateFolder + $separator + 'Projects'),
        ($quote + $privateFolder + $quote + '[[:space:]]*,[[:space:]]*' + $quote + 'Projects' + $quote),
        ('F:' + $separator + 'Bri' + 'dge' + '([^[:alnum:]_]|$)'),
        ('Appl' + 'iances'),
        ($accountName)
    )
    # '.' keeps the search rooted at the work tree; the exclusions drop the
    # separately published material.
    $pathspecs = @('.', ':(exclude)docs/', ':(exclude)AGENTS.md')

    $found = $false
    foreach ($pattern in $patterns) {
        $arguments = @('grep', '-n', '-I', '-i', '-E')
        if ($Staged) { $arguments += '--cached' }
        $arguments += $pattern
        $arguments += '--'
        $arguments += $pathspecs

        $hits = & git @arguments
        $code = $LASTEXITCODE
        if ($code -eq 0 -and $hits) {
            $found = $true
            Write-Host "Personal literal matched: $pattern"
            $hits | ForEach-Object { Write-Host "  $_" }
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
