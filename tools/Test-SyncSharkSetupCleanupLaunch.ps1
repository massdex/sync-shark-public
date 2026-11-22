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
        $script:SetupHost = 'Sync Shark.exe'
        $script:SetupLayout = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('["Sync Shark.exe","fixture.txt"]'))
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
        try { Invoke-SetupInstall $archive $installRoot } catch {
            if (-not $_.Exception.Message.Contains([Massdex.SyncShark.SetupMessages]::Text('version.occupied',[object[]]@()))) { throw }
        }
        $stages = @([IO.Directory]::EnumerateDirectories((Join-Path $installRoot 'versions/.staging')))
        if ($stages.Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $stages[0] 'fixture.txt')) -cne 'fixture' -or
            [IO.File]::ReadAllText((Join-Path $installRoot 'active/fixture.txt')) -cne 'fixture') { throw 'Published or retained payload was lost.' }
        $issues = @()
        # A 0.3.95 predecessor has only the old host name. The candidate must still use its new name.
        try {
            $legacy = Join-Path $case 'legacy'; [void][IO.Directory]::CreateDirectory($legacy)
            Copy-Item (Join-Path $payload 'Sync Shark.exe') (Join-Path $legacy 'SyncShark.exe')
            Assert-SetupUpgrade ([version]'1.2.4') $legacy
            try { Assert-SetupUpgrade ([version]'1.2.2') $legacy; throw 'Legacy downgrade accepted.' } catch { if (-not $_.Exception.Message.Contains([Massdex.SyncShark.SetupMessages]::Text('version.upgrade',[object[]]@()))) { throw } }
        } catch { $issues += "Legacy predecessor upgrade: $_" }
        # Keep the fixture's ready 1.2.3 host; interrupt 1.2.4 immediately before switching active.
        $candidate = Join-Path $case 'candidate'; [void][IO.Directory]::CreateDirectory($candidate)
        [IO.File]::WriteAllText($source,[IO.File]::ReadAllText($source).Replace('1.2.3','1.2.4'))
        & $compiler /nologo /target:exe ("/out:" + (Join-Path $candidate 'Sync Shark.exe')) $source
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic candidate compilation failed.' }
        [IO.File]::WriteAllText((Join-Path $candidate 'fixture.txt'),'candidate')
        $upgradeZip = Join-Path $case 'candidate.zip'; Compress-Archive -Path (Join-Path $candidate '*') -DestinationPath $upgradeZip
        foreach ($fault in 'extra','missing','interrupted') {
            $trial = Join-Path $case $fault; Invoke-SetupInstall $archive $trial
            function Expand-Archive($LiteralPath,$DestinationPath) {
                if ($fault -eq 'interrupted') {
                    [IO.File]::WriteAllText((Join-Path $DestinationPath 'partial.txt'),'retained'); throw 'Extraction interrupted.'
                }
                Microsoft.PowerShell.Archive\Expand-Archive -LiteralPath $LiteralPath -DestinationPath $DestinationPath
                if ($fault -eq 'extra') { [IO.File]::WriteAllText((Join-Path $DestinationPath 'other.txt'),'unowned') }
                else { [IO.File]::Delete((Join-Path $DestinationPath 'fixture.txt')) }
            }
            $refused=$false
            try { Invoke-SetupInstall $upgradeZip $trial } catch { $refused=$true }
            finally { Remove-Item Function:Expand-Archive }
            if (-not $refused -or [IO.File]::ReadAllText((Join-Path $trial 'active/fixture.txt')) -cne 'fixture') {
                $issues += "Staged $fault did not preserve the predecessor."; continue
            }
            & (Join-Path $trial 'active/Sync Shark.exe'); if ($LASTEXITCODE) { throw 'Predecessor cannot run after refused staging.' }
            if ($fault -eq 'interrupted') {
                Invoke-SetupInstall $upgradeZip $trial
                $partial=@(Get-ChildItem -LiteralPath (Join-Path $trial 'versions/.staging') -File -Recurse -Filter partial.txt)
                if ($partial.Count -ne 1 -or [IO.File]::ReadAllText($partial[0].FullName) -cne 'retained' -or [IO.File]::ReadAllText((Join-Path $trial 'active/fixture.txt')) -cne 'candidate') { throw 'Partial preparation was reused or lost.' }
            }
        }
        $variant = Join-Path $case 'interruption'; [void][IO.Directory]::CreateDirectory($variant)
        foreach ($name in 'SetupFileSystem.cs','SetupMessages.cs','setup-strings.xml') { Copy-Item (Join-Path $installer $name) $variant }
        $before = [IO.File]::ReadAllText((Join-Path $installer 'setup.ps1')); $boundary = '$active.Retarget('
        $index = $before.LastIndexOf($boundary,[StringComparison]::Ordinal)
        if ($index -lt 0) { throw 'Pointer switch interruption did not bind.' }
        $after = $before.Insert($index,'[Environment]::Exit(23); ').Replace('@HOST@',$script:SetupHost).Replace('@LAYOUT@',$script:SetupLayout)
        [IO.File]::WriteAllText((Join-Path $variant 'setup.ps1'),$after)
        $childScript = Join-Path $variant 'invoke.ps1'
        [IO.File]::WriteAllText($childScript, 'param($Zip,$Root) . (Join-Path $PSScriptRoot setup.ps1) -FunctionsOnly; Invoke-SetupInstall $Zip $Root')
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childScript $upgradeZip $installRoot
        if ($LASTEXITCODE -ne 23 -or [IO.File]::ReadAllText((Join-Path $installRoot 'active/fixture.txt')) -cne 'fixture') { throw 'Interrupted setup disturbed predecessor.' }
        & (Join-Path $installRoot 'active/Sync Shark.exe'); if ($LASTEXITCODE -ne 0) { throw 'Predecessor no longer runnable.' }
        if (Test-Path -LiteralPath (Join-Path $installRoot 'versions/setup-transaction.jsonl')) { $issues += 'Interrupted install created a switch journal.' }
        Invoke-SetupInstall $upgradeZip $installRoot
        if ([IO.File]::ReadAllText((Join-Path $installRoot 'active/fixture.txt')) -cne 'candidate') { throw 'Rerun failed to publish the complete version.' }
        # Package and uninstall the artifact actually emitted by the house builder. The OS registration is a separate boundary.
        $repository = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { Split-Path $PSScriptRoot }
        $revision = (& git -C $repository rev-parse HEAD).Trim(); if ($LASTEXITCODE) { throw 'Cannot bind fixture publish to source.' }
        $publish = Join-Path $case 'published'
        foreach ($project in 'SyncShark','SyncShark.Cli') {
            & dotnet publish (Join-Path $repository "apps/$project/$project.csproj") -c Release -r win-x64 --self-contained false --no-restore "-p:SyncSharkSourceRevision=$revision" -o $publish
            if ($LASTEXITCODE) { throw "Fixture publish failed: $project" }
        }
        [IO.File]::WriteAllText((Join-Path $publish 'unrelated.txt'),'must not ship')
        $built = Join-Path $case 'built'; & pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Build-SyncSharkSetup.ps1') -PublishedApp $publish -OutputDirectory $built
        if ($LASTEXITCODE) { throw 'House builder failed.' }
        $artifacts = @(Get-ChildItem -LiteralPath $built -Filter '*.exe' -File); if ($artifacts.Count -ne 1) { throw 'Expected one setup.' }
        & (Join-Path $PSScriptRoot 'Test-SyncSharkSetupCleanupLaunch.ps1') -Setup $artifacts[0].FullName
        $shell = Start-Process $artifacts[0].FullName -ArgumentList '--fixture-invalid-argument' -PassThru -Wait -RedirectStandardError (Join-Path $case 'shell.stderr')
        try {
            if ($shell.ExitCode -ne 1) { throw 'Standalone setup shell accepted unknown arguments.' }
            if (-not [IO.File]::ReadAllText((Join-Path $case 'shell.stderr')).Contains([Massdex.SyncShark.SetupMessages]::Text('shell.arguments'))) { throw 'Standalone setup shell lost its embedded message resource.' }
        } finally { $shell.Dispose() }
        $artifact = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($artifacts[0].FullName))
        $resources=Join-Path $case 'resources'; [void][IO.Directory]::CreateDirectory($resources)
        foreach ($name in $artifact.GetManifestResourceNames()) {
            $stream=$artifact.GetManifestResourceStream($name); $dest=[IO.File]::Create((Join-Path $resources $name))
            try { $stream.CopyTo($dest) } finally { $dest.Dispose(); $stream.Dispose() }
        }
        . (Join-Path $resources 'setup.ps1') -FunctionsOnly
        $isolated = Join-Path $case 'installed-package'; $zipPath=Join-Path $resources 'payload.zip'; Invoke-SetupInstall $zipPath $isolated
        if (Test-Path -LiteralPath (Join-Path $isolated 'active/unrelated.txt')) { throw 'Unrelated publish input shipped.' }
        & (Join-Path $PSScriptRoot 'Test-SyncSharkUninstall.ps1') -Root $isolated -Archive $zipPath
        # Windows may deny a file disposition after it has opened and hashed successfully.
        $retryRoot=Join-Path $case 'retry-removal'; Invoke-SetupInstall $zipPath $retryRoot
        $retryEntry=Join-Path $retryRoot 'active/tools/SyncShark.Uninstall.ps1'
        $readonly=Join-Path $retryRoot 'active/SyncShark.Engine.dll'; $previous=[IO.File]::GetAttributes($readonly)
        [IO.File]::SetAttributes($readonly,($previous -bor [IO.FileAttributes]::ReadOnly))
        . $retryEntry
        $calls=[Collections.Generic.List[string]]::new(); $refused=$false
        try { Remove-SyncSharkPackage $retryRoot { param($v) $calls.Add([string]$v) } } catch { $refused=$true }
        if (-not $refused -or $calls.Count -or -not [IO.File]::Exists($retryEntry)) { throw 'Failed file removal lost its registered retry entry.' }
        [IO.File]::SetAttributes($readonly,$previous)
        & (Join-Path $PSScriptRoot 'Test-SyncSharkUninstall.ps1') -Root $retryRoot -Archive $zipPath
        Write-Host 'PASS refused file removal retained the uninstall entry and retry completed.'
        # A removal that leaves one runtime DLL must fail the independent archive oracle.
        $mutatedRoot=Join-Path $case 'incomplete-removal'; Invoke-SetupInstall $zipPath $mutatedRoot
        $entry=Join-Path $mutatedRoot 'active/tools/SyncShark.Uninstall.ps1'; $original=[IO.File]::ReadAllText($entry)
        $needle='foreach ($file in $files) { $file.Delete() }'
        $mutation=$original.Replace($needle,'foreach ($file in $files) { if ([IO.Path]::GetFileName($file.Path) -cne "SyncShark.Engine.dll") { $file.Delete() } }')
        if ($mutation -ceq $original) { throw 'Partial-removal fixture did not bind.' }
        [IO.File]::WriteAllText($entry,$mutation,[Text.UTF8Encoding]::new($false))
        try { & (Join-Path $PSScriptRoot 'Test-SyncSharkUninstall.ps1') -Root $mutatedRoot -Archive $zipPath; throw 'Partial removal passed.' }
        catch { if ($_.Exception.Message -notlike 'Runtime member remains: *\SyncShark.Engine.dll') { throw } }
        if (-not [IO.File]::Exists($entry)) { throw 'Finalization lost its retry entry.' }
        Write-Host 'PASS finalization refused a retained runtime DLL before unregistering.'
        # Independently exercise the archive-wide oracle with a leaked support file after the finalization boundary.
        $leakRoot=Join-Path $case 'incomplete-support-removal'; Invoke-SetupInstall $zipPath $leakRoot
        $entry=Join-Path $leakRoot 'active/tools/SyncShark.Uninstall.ps1'; $original=[IO.File]::ReadAllText($entry)
        $needle='foreach ($file in $support) { $file.Delete() }'
        $mutation=$original.Replace($needle,'foreach ($file in $support) { if ([IO.Path]::GetFileName($file.Path) -cne "SetupMessages.cs") { $file.Delete() } }')
        if ($mutation -ceq $original) { throw 'Partial support removal fixture did not bind.' }
        [IO.File]::WriteAllText($entry,$mutation,[Text.UTF8Encoding]::new($false))
        try { & (Join-Path $PSScriptRoot 'Test-SyncSharkUninstall.ps1') -Root $leakRoot -Archive $zipPath; throw 'Partial support removal passed.' }
        catch { if ($_.Exception.Message -cne 'Uninstall left archive member: tools/SetupMessages.cs') { throw } }
        Write-Host 'PASS independent removal oracle rejected a retained support file.'
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
