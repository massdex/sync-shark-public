param([string]$Root)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
if (-not $Root) { $Root = Join-Path $env:RUNNER_TEMP ('setup-mutants-' + [guid]::NewGuid().ToString('N')) }
$mutants = @(
    @('downgrade', 'setup.ps1', 'Assert-SetupUpgrade $version $active.JunctionTarget()', '$null = $version', 'Direct downgrade was accepted.'),
    @('preparation-drain', 'setup.ps1', 'Clear-SetupPreparation $Root', '[IO.Directory]::CreateDirectory((Join-Path $Root ''versions\.staging'')) | Out-Null', 'Next setup did not report interrupted preparation cleanup.'),
    @('payload-proof', 'setup.ps1', 'if ($item.Identity -cne $entry.Identity -or (-not $entry.Directory -and $item.Hash() -cne $entry.Hash)) {', 'if ($false) {', 'Changed payload was admitted for deletion.'),
    @('publication-proof', 'setup.ps1', '$publishedTree = Open-SetupTree $destination $manifest -RequireComplete', '$publishedTree = $null', 'Changed payload was published.'),
    @('first-rollback', 'setup.ps1', '$active.Delete(); $active = $null', '$active.Dispose(); $active = $null', 'First-install rollback retained the launcher.'),
    @('upgrade-rollback', 'setup.ps1', '$active.Retarget($candidate, $previous)', '$active.ReadJunction() | Out-Null', 'Interrupted switch lost the active launcher.'),
    @('cleanup-report', 'cleanup.ps1', 'exit 1', 'exit 0', 'Detached cleanup hid a retained download.'),
    @('obstruction-journal', 'setup.ps1', 'catch { throw ([Massdex.SyncShark.SetupMessages]::Text(''install.rollback''', 'catch { $journal.Delete(); throw ([Massdex.SyncShark.SetupMessages]::Text(''install.rollback''', 'Persistent obstruction discarded its recovery journal.'),
    @('obstruction-report', 'setup-strings.xml', 'run setup again after correcting the reported obstruction.', 'exit setup.', 'Missing retry trigger left an unreported automatic wait.'),
    @('invoker', 'setup.manifest', 'asInvoker', 'requireAdministrator', 'Setup elevation contract changed.'),
    @('process-policy', 'SyncSharkSetup.cs', '-ExecutionPolicy Bypass', '-ExecutionPolicy RemoteSigned', 'Setup lost process-scoped policy.'),
    @('torn-frame', 'setup.ps1', '.GetString($bytes, 0, $end)', '.GetString($bytes)', 'Torn UTF-8 tail hid the durable journal frame.'),
    @('ancestor-hold', 'SetupFileSystem.cs', 'uint access = ReadAttributes | GenericRead;', 'uint access = ReadAttributes | GenericRead; if (isDirectory && !mutable && !junction) access = ReadAttributes;', 'Held ancestry allowed a directory replacement.'),
    @('rename-termination', 'SetupFileSystem.cs', '            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);', '', 'Native rename did not publish the exact target name.'),
    @('rename-parent-sharing', 'SetupFileSystem.cs', 'uint share = isDirectory && !junction ? 3U : 1U;', 'uint share = 1U;', 'Held-parent cross-directory rename failed with 0x80070020.'),
    @('post-rename-conversion', 'SetupFileSystem.cs', '            Verify();', '', 'Post-rename conversion was not refused.'),
    @('preparation-conversion', 'setup.ps1', '$guard.Verify()', '', 'Converted staging deleted foreign evidence.'),
    @('held-directory-conversion', 'SetupFileSystem.cs', 'public void Verify() { Information(); }', 'public void Verify() { }', 'Held empty directory conversion was not refused.'),
    @('no-follow', 'SetupFileSystem.cs', '((result.Attributes & 0x400) != 0) != junction ||', '', 'Payload inventory followed a junction.'),
    @('hardlink', 'SetupFileSystem.cs', '(!isDirectory && result.Links != 1) ||', '', 'A hardlinked setup claim was accepted.'),
    @('exclusive-claim', 'SetupFileSystem.cs', 'claim ? 0U : share', 'claim ? 7U : share', 'Concurrent setup claim was accepted.')
)
$failures = [Collections.Generic.List[string]]::new()
foreach ($mutant in $mutants) {
    $case = Join-Path $Root $mutant[0]
    $installer = Join-Path $case 'Installer'
    $tools = Join-Path $case 'tools'
    [IO.Directory]::CreateDirectory($installer) | Out-Null
    [IO.Directory]::CreateDirectory($tools) | Out-Null
    foreach ($name in @('setup.ps1','SetupFileSystem.cs','SetupMessages.cs','setup-strings.xml','setup.manifest','SyncSharkSetup.cs','cleanup.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repository ('Installer/' + $name)) -Destination $installer
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Build-SyncSharkSetup.ps1') -Destination $tools
    $fixture = Join-Path $tools 'Test-SyncSharkSetup.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-SyncSharkSetup.ps1') -Destination $fixture
    $path = Join-Path $installer $mutant[1]
    $source = [IO.File]::ReadAllText($path)
    if ($source.IndexOf($mutant[2], [StringComparison]::Ordinal) -lt 0 -or
        $source.IndexOf($mutant[2], [StringComparison]::Ordinal) -ne $source.LastIndexOf($mutant[2], [StringComparison]::Ordinal)) {
        throw "Mutant did not bind exactly once: $($mutant[0])"
    }
    [IO.File]::WriteAllText($path, $source.Replace($mutant[2], $mutant[3]))
    $output = Join-Path $case 'stdout.log'; $errorLog = Join-Path $case 'stderr.log'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $fixture + '"'), '-Root', ('"' + (Join-Path $case 'evidence') + '"')) -PassThru -Wait -RedirectStandardOutput $output -RedirectStandardError $errorLog
    try {
        $text = [IO.File]::ReadAllText($output) + [IO.File]::ReadAllText($errorLog)
        if ($process.ExitCode -eq 0 -or -not $text.Contains($mutant[4])) {
            $failures.Add($mutant[0])
            Write-Host "Mutant failed to prove its named assertion: $($mutant[0]); exit=$($process.ExitCode); evidence=$case"
        } else { Write-Host "Mutant rejected by named assertion: $($mutant[0]); evidence=$case" }
    } finally { $process.Dispose() }
}
if ($failures.Count) { throw ('Unproved mutants: ' + ($failures -join ', ')) }
& cmd /c exit 0
