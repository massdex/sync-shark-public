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
        # The same payload must publish once. A second attempt may not discard its unproved staging contents.
        $case = Join-Path ([IO.Path]::GetTempPath()) ('setup-ownership-' + [guid]::NewGuid().ToString('N'))
        $payload = Join-Path $case 'payload'; $installRoot = Join-Path $case 'installation'
        [IO.Directory]::CreateDirectory($payload) | Out-Null
        $source = Join-Path $case 'fixture.cs'
        [IO.File]::WriteAllText($source, 'using System.Reflection; [assembly:AssemblyInformationalVersion("1.2.3")] class Program { static void Main() {} }')
        $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        & $compiler /nologo /target:exe ("/out:" + (Join-Path $payload 'Sync Shark.exe')) $source
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic setup member did not compile.' }
        [IO.File]::WriteAllText((Join-Path $payload 'fixture.txt'),'fixture')
        $archive = Join-Path $case 'fixture.zip'; Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
        Invoke-SetupInstall $archive $installRoot
        try { Invoke-SetupInstall $archive $installRoot; throw 'Duplicate installation unexpectedly succeeded.' }
        catch { if (-not $_.Exception.Message.Contains([Massdex.SyncShark.SetupMessages]::Text('version.occupied',[object[]]@()))) { throw } }
        $stages = @([IO.Directory]::EnumerateDirectories((Join-Path $installRoot 'versions/.staging')))
        if ($stages.Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $stages[0] 'fixture.txt')) -cne 'fixture' -or
            [IO.File]::ReadAllText((Join-Path $installRoot 'active/fixture.txt')) -cne 'fixture') { throw 'Published or retained payload was lost.' }
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
