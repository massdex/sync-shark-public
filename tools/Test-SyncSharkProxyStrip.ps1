$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# B-16: on PowerShell 7.6 [Environment]::SetEnvironmentVariable($name, $null, 'Process') stores ''
# instead of deleting the variable. Proxy stripping must use Remove-Item Env:<name>.
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File -Recurse |
    Where-Object { $_.FullName -ne $PSCommandPath })
$bad = @($scripts | Where-Object {
    (Get-Content -LiteralPath $_.FullName -Raw) -match '\[Environment\]::SetEnvironmentVariable\([^,]+,\s*\$null\s*,'
})
if ($bad.Count) {
    throw ('B-16: use Remove-Item Env:<name>; null SetEnvironmentVariable in: ' + (($bad | ForEach-Object FullName) -join ', '))
}
# B-31: a build started from pwsh 7 hands its module paths to the Windows PowerShell 5.1 signing child
# (apps/SyncShark/SyncShark.csproj). A module auto-load restores PSModulePath, so every restore/build
# needs its own delete as the immediately preceding executable line.
$launch = '(?im)^\s*(?:\$[A-Za-z_]\w*\s*=\s*)?&\s*(?:''[^''\r\n]*\bdotnet\.exe''|\$[A-Za-z_]\w*)\s+[^\r\n]*\b(?:build|restore)\b|^\s*dotnet(?:\.exe)?\s+[^\r\n]*\b(?:build|restore)\b|^\s*msbuild(?:\.exe)?\b'
$deletion = '(?i)^\s*Remove-Item\s+env:PSModulePath\s+-ErrorAction\s+SilentlyContinue\s*(?:#.*)?$'
$launches = 0
$missing = @()
foreach ($script in $scripts) {
    $lines = @(Get-Content -LiteralPath $script.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch $launch) { continue }
        $launches++
        $j = $i - 1
        while ($j -ge 0 -and ($lines[$j].Trim() -eq '' -or $lines[$j].TrimStart().StartsWith('#'))) { $j-- }
        if ($j -lt 0 -or $lines[$j] -notmatch $deletion) {
            $missing += ('{0}:{1}' -f $script.FullName, ($i + 1))
        }
    }
}
if ($missing.Count) {
    throw ('B-31: each restore/build needs Remove-Item env:PSModulePath immediately before it; missing at: ' + ($missing -join ', '))
}
# The replacement must delete the variable on this host: an empty value still counts as present (B-16).
# Save the caller's values so the probe leaves the process as it found it.
$names = @('HTTPS_PROXY', 'HTTP_PROXY', 'ALL_PROXY')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'b16-probe', 'Process') }
    foreach ($name in $names) { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    foreach ($name in $names) {
        if ($null -ne [Environment]::GetEnvironmentVariable($name, 'Process')) {
            throw "B-16: proxy variable survived the strip: $name"
        }
    }
} finally {
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}
[pscustomobject]@{ ok = $true; scanned = $scripts.Count; launches = $launches; stripped = $names } | ConvertTo-Json -Compress
