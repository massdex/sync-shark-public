# Requires Windows, PowerShell 7.4+ (.NET 8+), and the .NET Framework C# compiler.
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedApp,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$StageOnly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$PublishedApp = [IO.Path]::GetFullPath($PublishedApp).TrimEnd('\','/')
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $PublishedApp 'SyncShark.Engine.dll')))
$names = $assembly.GetType('Massdex.SyncShark.Engine.ResourceNames',$true)
$files = [string[]]$names::InstalledFiles
$uninstallPath = @($files | Where-Object { $_.EndsWith('.ps1',[StringComparison]::Ordinal) })
$registration = @($files | Where-Object { $_.EndsWith('.psm1',[StringComparison]::Ordinal) })
if ($uninstallPath.Count -ne 1 -or $registration.Count -ne 1) { throw 'Expected one uninstall entry and one registration module.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishedApp $names::HostExecutable)).ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The published application has no stable version.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$work = if ($StageOnly) { $OutputDirectory } else { Join-Path $OutputDirectory ('setup-build-' + [guid]::NewGuid().ToString('N')) }
$payload = Join-Path $work 'app'; [void](New-Item -ItemType Directory -Path $payload -ErrorAction Stop)
$members = @(foreach ($name in $files) {
    $target = Join-Path $payload $name
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    if ($name -ceq $uninstallPath[0]) { continue }
    $source = if ($name -ceq $registration[0]) { Join-Path $repository $name }
        elseif ($name.StartsWith('tools/',[StringComparison]::Ordinal)) { Join-Path (Join-Path $repository 'Installer') ([IO.Path]::GetFileName($name)) }
        else { Join-Path $PublishedApp $name }
    [IO.File]::Copy($source,$target)
    [ordered]@{Path=$name;Hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
})
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $members -Compress)))
$uninstall = [IO.File]::ReadAllText((Join-Path $repository $uninstallPath[0])).Replace('@PUBLISHED_MEMBERS@',$encoded).Replace('@PUBLISHED_VERSION@',$version).Replace('@UNINSTALL@',$uninstallPath[0]).Replace('@CLI@',$names::CliExecutable).Replace('@HOST@',$names::HostExecutable).Replace('@PRODUCT@',$names::ProductFolder).Replace('@REGISTRATION@',$registration[0])
[IO.File]::WriteAllText((Join-Path $payload $uninstallPath[0]),$uninstall,[Text.UTF8Encoding]::new($false))
$paths = [Collections.Generic.HashSet[string]]::new($files,[StringComparer]::Ordinal)
foreach ($name in $files) {
    for ($parent=[IO.Path]::GetDirectoryName($name); $parent; $parent=[IO.Path]::GetDirectoryName($parent)) { [void]$paths.Add($parent.Replace('\','/') + '/') }
}
foreach ($item in Get-ChildItem -LiteralPath $payload -Force -Recurse) {
    $path = [IO.Path]::GetRelativePath($payload,$item.FullName).Replace('\','/')
    if ($item.PSIsContainer) { $path += '/' }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not $paths.Remove($path)) { throw "Unexpected staged member: $path" }
}
if ($paths.Count) { throw "Missing staged members: $($paths -join ', ')" }
if ($StageOnly) { return }
$layout = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $files -Compress)))
$install = [IO.File]::ReadAllText((Join-Path $repository 'Installer/setup.ps1')).Replace('@UNINSTALL@',$uninstallPath[0]).Replace('@LAYOUT@',$layout).Replace('@HOST@',$names::HostExecutable).Replace('@PRODUCT@',$names::ProductFolder).Replace('@REGISTRATION@',$registration[0])
[IO.File]::WriteAllText((Join-Path $work 'setup.ps1'),$install,[Text.UTF8Encoding]::new($false))
$archive = Join-Path $work 'payload.zip'
$zip = [IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try { foreach ($name in $files) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $payload $name),$name) } }
finally { $zip.Dispose() }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$setup = Join-Path $OutputDirectory "SyncShark-Setup-v$version.exe"
if (Test-Path -LiteralPath $setup) { throw 'Setup output already exists; use a new output directory.' }
& $compiler /nologo /target:exe /optimize+ /reference:System.Xml.dll "/out:$setup" "/resource:$archive,payload.zip" "/resource:$(Join-Path $work 'setup.ps1'),setup.ps1" "/resource:$(Join-Path $repository 'Installer/SetupFileSystem.cs'),SetupFileSystem.cs" "/resource:$(Join-Path $repository 'Installer/SetupMessages.cs'),SetupMessages.cs" "/resource:$(Join-Path $repository 'Installer/setup-strings.xml'),setup-strings.xml" "/win32manifest:$(Join-Path $repository 'Installer/setup.manifest')" (Join-Path $repository 'Installer/SyncSharkSetup.cs') (Join-Path $repository 'Installer/SetupMessages.cs')
if ($LASTEXITCODE -ne 0) { throw "Setup compilation failed: $LASTEXITCODE" }
Write-Host "Unsigned setup: $setup. Owner signs this final executable separately. Retained build inputs: $work"
