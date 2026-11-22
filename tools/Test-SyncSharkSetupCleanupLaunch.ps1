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
        # Run the real shell with a harmless embedded setup script. Its marker is not owned by the shell.
        $case = Join-Path ([IO.Path]::GetTempPath()) ('setup-shell-' + [guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($case)
        $probe = Join-Path $case 'setup.ps1'; $record = Join-Path $case 'extracted.txt'
        [IO.File]::WriteAllText($probe, 'param($Payload) if ([IO.File]::ReadAllText($Payload) -cne "fixture") {exit 3}; [IO.File]::WriteAllText((Join-Path $PSScriptRoot "foreign.txt"),"preserved"); [IO.File]::WriteAllText($env:SETUP_FIXTURE_RECORD,$PSScriptRoot); exit 23')
        $archive = Join-Path $case 'payload.zip'; [IO.File]::WriteAllText($archive,'fixture')
        $exe = Join-Path $case 'setup.exe'; $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        $resources = @("/resource:$archive,payload.zip", "/resource:$probe,setup.ps1")
        foreach ($name in 'SetupFileSystem.cs','SetupMessages.cs','setup-strings.xml') { $resources += ("/resource:" + (Join-Path $installer $name) + ',' + $name) }
        & $compiler /nologo /target:exe /reference:System.Xml.dll ("/out:" + $exe) @resources (Join-Path $installer 'SyncSharkSetup.cs') (Join-Path $installer 'SetupMessages.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Fixture shell compilation failed.' }
        $saved = $env:SETUP_FIXTURE_RECORD
        try { $env:SETUP_FIXTURE_RECORD = $record; $child = Start-Process $exe -PassThru -Wait; try { $code = $child.ExitCode } finally { $child.Dispose() } }
        finally { $env:SETUP_FIXTURE_RECORD = $saved }
        if ($code -ne 23 -or [IO.File]::ReadAllText((Join-Path ([IO.File]::ReadAllText($record)) 'foreign.txt')) -cne 'preserved') { throw 'Shell did not run its extracted setup and retain unowned content.' }
    } else {
        . (Join-Path $installer 'setup.ps1') -FunctionsOnly
        foreach ($command in 'Invoke-SetupInstall','Restore-SetupTransaction') {
            $remove = (Get-Command $command).ScriptBlock.Ast.Find({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Remove-SetupTree' },$true)
            if ($null -ne $remove) { throw 'Installer promotes a post-extraction directory census to cleanup ownership.' }
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
        $issues = @()
        # The same journal handle must remain exclusive from creation through publication.
        try {
            $journal = [SetupFileSystem]::CreateJournal((Join-Path $case 'new.journal'))
            try {
                $journal.Append([byte[]](41,42)); $journal.Rename((Join-Path $case 'published.journal'))
                if (-not [SetupFileSystem]::Equal($journal.Read(),[byte[]](41,42))) { throw 'Journal content changed.' }
                $journal.Delete()
            } finally { $journal.Dispose() }
            [IO.File]::WriteAllText((Join-Path $case 'occupied.journal'),'keep')
            try { $unexpected = [SetupFileSystem]::CreateJournal((Join-Path $case 'occupied.journal')); $unexpected.Dispose(); throw 'Occupied journal was admitted.' }
            catch [Management.Automation.MethodInvocationException] { }
            if ([IO.File]::ReadAllText((Join-Path $case 'occupied.journal')) -cne 'keep') { throw 'Occupied journal changed.' }
        } catch { $issues += "Exclusive journal creation: $_" }
        # A 0.3.95 predecessor has only the old host name. The candidate must still use its new name.
        try {
            $legacy = Join-Path $case 'legacy'; [void][IO.Directory]::CreateDirectory($legacy)
            Copy-Item (Join-Path $payload 'Sync Shark.exe') (Join-Path $legacy 'SyncShark.exe')
            Assert-SetupUpgrade ([version]'1.2.4') $legacy
            try { Assert-SetupUpgrade ([version]'1.2.2') $legacy; throw 'Legacy downgrade accepted.' } catch { if (-not $_.Exception.Message.Contains([Massdex.SyncShark.SetupMessages]::Text('version.upgrade',[object[]]@()))) { throw } }
        } catch { $issues += "Legacy predecessor upgrade: $_" }
        if ($issues.Count) { throw ($issues -join '; ') }
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
