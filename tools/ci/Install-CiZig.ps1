$ErrorActionPreference = 'Stop'
$zipPath = Join-Path $env:RUNNER_TEMP 'zig-0.16.0.zip'
Invoke-WebRequest -Uri 'https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip' -OutFile $zipPath
$expected = $env:ZIG_ARCHIVE_SHA256
$actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Zig archive checksum mismatch: $actual" }
$extractRoot = Join-Path $env:RUNNER_TEMP 'zig'
Expand-Archive -Path $zipPath -DestinationPath $extractRoot -Force
$zig = Get-ChildItem -Path $extractRoot -Filter 'zig.exe' -Recurse -File | Select-Object -First 1
if ($null -eq $zig) { throw 'zig.exe was not found in the extracted toolchain.' }
"ZIG_EXE=$($zig.FullName)" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
& $zig.FullName version
