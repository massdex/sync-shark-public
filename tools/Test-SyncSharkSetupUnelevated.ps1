$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true') { throw 'This disposable local-account fixture runs only on public CI.' }
$root = Join-Path $env:RUNNER_TEMP ('setup-unelevated-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$name = 'SetupFixture'
$password = ConvertTo-SecureString ([guid]::NewGuid().ToString('N') + 'aA1!') -AsPlainText -Force
$user = New-LocalUser -Name $name -Password $password -AccountNeverExpires
try {
    Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-545') -Member $user
    $account = $env:COMPUTERNAME + '\' + $name
    & icacls.exe $root /grant "${account}:(OI)(CI)F"
    if ($LASTEXITCODE) { throw 'Cannot grant the isolated fixture directory.' }
    $repository = Split-Path $PSScriptRoot
    & icacls.exe $repository /grant "${account}:(OI)(CI)RX" /T /Q
    if ($LASTEXITCODE) { throw 'Cannot grant read access to fixture sources.' }
    $credential = [pscredential]::new($account, $password)
    $script = Join-Path $PSScriptRoot 'Test-SyncSharkSetup.ps1'
    $process = Start-Process powershell.exe -Credential $credential -LoadUserProfile -WorkingDirectory $root -ArgumentList @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $script + '"'), '-RequireNonAdmin', '-Root', ('"' + $root + '"')
    ) -PassThru -Wait -RedirectStandardOutput (Join-Path $root 'stdout.log') -RedirectStandardError (Join-Path $root 'stderr.log')
    Get-Content -LiteralPath (Join-Path $root 'stdout.log')
    Get-Content -LiteralPath (Join-Path $root 'stderr.log')
    if ($process.ExitCode) { throw "Unelevated setup checks failed: $($process.ExitCode)" }
} finally { Remove-LocalUser -SID $user.SID }
