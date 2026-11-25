# Requires PowerShell 7. The hosted lane runs the entire offline registry, not a group matrix.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workflow = Get-Content -Raw -LiteralPath (Join-Path $root '.github/workflows/sync-shark-ci.yml')
$runner = Get-Content -Raw -LiteralPath (Join-Path $root 'tests/SyncShark.Engine.Tests/Program.cs')
if ($workflow -notmatch 'SyncShark.Engine.Tests.dll \$env:GITHUB_WORKSPACE \$inventory' -or $runner -notmatch 'CheckRegistry\.All\(') { throw 'Hosted CI must invoke every registered offline check.' }
if ($workflow -match '\.ci-lane\.json|Invoke-V2LiveTests|SYNCSHARK_LIVE_CONNECTION') { throw 'Hosted checks must not use selectors or access the live route.' }
