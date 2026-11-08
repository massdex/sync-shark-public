param([string]$Root)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Cleanup launch proof runs only on disposable CI runners.' }
$repository = Split-Path $PSScriptRoot
if (-not $Root) { $Root = Join-Path $env:RUNNER_TEMP ('setup-mutants-cleanup-' + [guid]::NewGuid().ToString('N')) }
$source = [IO.File]::ReadAllText((Join-Path $repository 'src/SyncShark.Core/SetupDownloadCleanup.cs')).Replace("`r`n", "`n")
$before = @'
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Report(directory, error, diagnostics);
        }
'@
$before = $before.Replace("`r`n", "`n")
$after = $before.Replace('Report(directory, error, diagnostics);', '// Isolated mutant restores the silent launch-failure behavior.')
if ($source.IndexOf($before, [StringComparison]::Ordinal) -lt 0 -or
    $source.IndexOf($before, [StringComparison]::Ordinal) -ne $source.LastIndexOf($before, [StringComparison]::Ordinal)) {
    throw 'Cleanup launch mutant did not bind exactly once.'
}
foreach ($variant in @('positive', 'silent-launch-mutant')) {
    $case = Join-Path $Root $variant
    [IO.Directory]::CreateDirectory($case) | Out-Null
    foreach ($member in @('Installer/SetupMessages.cs', 'Installer/setup-strings.xml', 'Installer/cleanup.ps1', 'tests/SyncShark.Core.Tests/SetupCleanupLaunchChecks.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $member) -Destination $case
    }
    $candidate = if ($variant -eq 'positive') { $source } else { $source.Replace($before, $after) }
    [IO.File]::WriteAllText((Join-Path $case 'SetupDownloadCleanup.cs'), $candidate)
    [IO.File]::WriteAllText((Join-Path $case 'Program.cs'), 'if (args is ["--setup-cleanup-launch-child", var root]) return SetupCleanupLaunchChecks.Child(root); SetupCleanupLaunchChecks.Run(); return 0;')
    $project = Join-Path $case 'CleanupLaunch.csproj'
    [IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="setup-strings.xml" LogicalName="setup-strings.xml" />
    <EmbeddedResource Include="cleanup.ps1" LogicalName="SyncShark.SetupCleanup.ps1" />
  </ItemGroup>
</Project>
'@)
    & dotnet build $project --configuration Release --output (Join-Path $case 'bin') *> (Join-Path $case 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Cleanup launch fixture build failed: $case" }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add((Join-Path $case 'bin/CleanupLaunch.dll'))
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $case 'result.log'), $text)
        if ($variant -eq 'positive') {
            if ($process.ExitCode -ne 0) { throw "Cleanup launch positive proof failed: $text" }
        } elseif ($process.ExitCode -eq 0 -or -not $text.Contains('Cleanup launch failure was not reported in output and log.')) {
            throw "Cleanup launch mutant did not fail its named assertion: $case"
        }
        Write-Host "Cleanup launch proof passed: $variant; evidence=$case"
    } finally { $process.Dispose() }
}
exit 0
