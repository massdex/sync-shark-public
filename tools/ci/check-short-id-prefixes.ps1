# ShortId.Make accepts only 1-4 lowercase letters. Check call sites before building,
# including multiline calls, so notice workers cannot hide a bad prefix in a catch.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$bad = @()
$count = 0
foreach ($tree in @('src', 'tests')) {
    foreach ($file in Get-ChildItem (Join-Path $root $tree) -Filter '*.cs' -Recurse -File) {
        if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
        $source = [IO.File]::ReadAllText($file.FullName)
        foreach ($call in [regex]::Matches($source, 'ShortId\s*\.\s*Make\s*\(\s*(?<argument>[^,\r\n]+)')) {
            $count++
            $argument = $call.Groups['argument'].Value.Trim()
            if ($argument -match '^"[a-z]{1,4}"$') { continue }
            # This existing negative test explicitly verifies the prefix validation.
            if ($tree -eq 'tests' -and $file.Name -eq 'StorageChecks.cs' -and $argument -eq '"BAD"') { continue }
            $line = 1 + ([regex]::Matches($source.Substring(0, $call.Index), "`n")).Count
            $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
            $bad += "${relative}:${line}: invalid or nonliteral ShortId.Make prefix $argument"
        }
    }
}
if ($bad.Count -ne 0) { throw ($bad -join [Environment]::NewLine) }
Write-Output "PASS ShortId.Make prefix contract: $count source and test calls checked."
