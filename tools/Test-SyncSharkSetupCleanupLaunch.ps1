# Verify the actual standalone launcher payload, not the retired in-app download helper.
[CmdletBinding(DefaultParameterSetName='Payload')]
param([Parameter(Mandatory,ParameterSetName='Payload')][string]$Setup,
    [Parameter(Mandatory,ParameterSetName='Ownership')][ValidateSet('Shell','Stage')][string]$OwnershipCheck)
$ErrorActionPreference = 'Stop'
if ($OwnershipCheck) {
    $installer = Join-Path (Split-Path $PSScriptRoot) 'Installer'
    if ($OwnershipCheck -eq 'Shell') {
        $shell = [IO.File]::ReadAllText((Join-Path $installer 'SyncSharkSetup.cs'))
        if ($shell -match 'Directory\.Delete\s*\([^,]+,\s*true\s*\)') { throw 'Shell cleanup selects unproved children for recursive removal.' }
    } else {
        . (Join-Path $installer 'setup.ps1') -FunctionsOnly
        $removers = 'Invoke-SetupInstall','Restore-SetupTransaction' | ForEach-Object {
            (Get-Command $_).ScriptBlock.Ast.Find({param($node) $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Remove-SetupTree' },$true)
        }
        if ($removers -ne $null) {
            throw 'Installer promotes a post-extraction directory census to cleanup ownership.'
        }
    }
    return
}
Add-Type -AssemblyName System.IO.Compression
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Setup))
$stream = $assembly.GetManifestResourceStream('payload.zip')
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $executables = @($zip.Entries | Where-Object { $_.FullName -like '*.exe' })
        if ($executables.Count -ne 2) { throw 'Only the host and CLI may ship as executable entry points.' }
        foreach ($name in @('Sync Shark.exe', 'syncshark.exe')) {
            $entry = $zip.GetEntry($name)
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "Standalone setup omitted executable: $name" }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
