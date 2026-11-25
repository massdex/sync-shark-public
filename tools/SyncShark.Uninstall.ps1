# Requires Windows PowerShell 5.1, the packaged registration module and setup resources.
# Per-user house setup: the package builder fills in its own version and file hashes.
# Nothing read from the installation is treated as a record of what setup owns.
$ErrorActionPreference = 'Stop'
$script:PackageMembers = '@PUBLISHED_MEMBERS@'
$script:PackageVersion = '@PUBLISHED_VERSION@'
$script:UninstallSource = $MyInvocation.MyCommand.ScriptBlock.Ast.Extent.Text

# Return the same no-follow handle used for the hash; read-only sharing permits launching the CLI.
function Open-PackageMember([string]$Path, [string]$Hash, [switch]$ForStop) {
    $file = if ($ForStop) { [SetupFileSystem]::OpenFile($Path) } else { [SetupFileSystem]::OpenMutableFile($Path) }
    try {
        if ($file.Hash() -cne $Hash) { throw "Installed member changed; nothing was removed: $Path" }
        return $file
    } catch { $file.Dispose(); throw }
}

function Remove-SyncSharkPackage([string]$Root, [scriptblock]$Unregister, [scriptblock]$Stop) {
    $version = $script:PackageVersion
    if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Uninstall must run from its built package.' }
    $members = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:PackageMembers)))
    $target = [IO.Path]::GetFullPath((Join-Path $Root ('versions/' + $version)))
    $held = [Collections.Generic.List[IDisposable]]::new(); $files = [Collections.Generic.List[object]]::new()
    $support = [Collections.Generic.List[object]]::new()
    $dirs = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        # Pin ordinary ancestors and share the setup claim through the last filesystem effect.
        $ancestry = [Collections.Generic.Stack[string]]::new()
        for ($dir=[IO.DirectoryInfo]::new((Join-Path $Root 'versions')); $dir; $dir=$dir.Parent) { $ancestry.Push($dir.FullName) }
        while ($ancestry.Count) { $held.Add([SetupFileSystem]::OpenDirectory($ancestry.Pop())) }
        $held.Add([SetupFileSystem]::OpenClaim((Join-Path $Root 'versions/setup.lock')))
        $active = [SetupFileSystem]::OpenJunction((Join-Path $Root 'active')); $held.Add($active)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals($active.JunctionTarget(),$target)) { throw 'The active version changed. Nothing was removed.' }
        $self = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:UninstallSource))).Replace('-','')
        if ($Stop) {
            $launch = [Collections.Generic.List[IDisposable]]::new()
            try {
                $launch.Add([SetupFileSystem]::OpenDirectory($target))
                # The declared runtime is at the version root. A retry after host removal needs no stop.
                if ([SetupFileSystem]::Exists((Join-Path $target '@HOST@'))) {
                    $launcher = $null
                    foreach ($member in $members) {
                        if ([IO.Path]::GetFileName($member.Path) -cne $member.Path) { continue }
                        $verified = Open-PackageMember (Join-Path $target $member.Path) $member.Hash -ForStop
                        $launch.Add($verified)
                        if ($member.Path -ceq '@CLI@') { $launcher = $verified.Path }
                    }
                    if (-not $launcher) { throw 'Missing CLI in the built package.' }
                    & $Stop $launcher
                }
            } finally { foreach ($handle in $launch) { $handle.Dispose() } }
        }
        $all = @($members) + @([pscustomobject]@{Path='@UNINSTALL@';Hash=$self})
        foreach ($member in $all) {
            $path = [IO.Path]::GetFullPath((Join-Path $target $member.Path))
            if (-not $path.StartsWith(($target + [IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
            $ancestry.Clear()
            for ($dir=[IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($path)); $dir -and -not $dirs.ContainsKey($dir.FullName); $dir=$dir.Parent) {
                $ancestry.Push($dir.FullName); if ($dir.FullName -ieq $target) { break }
            }
            while ($ancestry.Count) {
                $folder=$ancestry.Pop(); $handle=[SetupFileSystem]::OpenMutableDirectory($folder); $held.Add($handle); $dirs.Add($folder,$handle)
            }
            if (-not [SetupFileSystem]::Exists($path)) { continue }
            $file = Open-PackageMember $path $member.Hash; $held.Add($file)
            if ([IO.Path]::GetDirectoryName($member.Path) -ceq [IO.Path]::GetDirectoryName('@UNINSTALL@')) { $support.Add($file) }
            elseif ($member.Path -ceq '@HOST@') { $files.Insert(0,$file) }
            else { $files.Add($file) }
        }
        foreach ($file in $files) { $file.Delete() }
        # Refuse finalization while a declared runtime member or an unremovable support file remains.
        foreach ($file in $files) { if ([SetupFileSystem]::Exists($file.Path)) { throw "Runtime member remains: $($file.Path)" } }
        foreach ($file in $support) {
            if ([IO.File]::GetAttributes($file.Path) -band [IO.FileAttributes]::ReadOnly) { throw "Uninstall support is read-only: $($file.Path)" }
        }
        & $Unregister $version
        foreach ($file in $support) { $file.Delete() }
        $active.Delete()
        foreach ($folder in @($dirs.Keys | Sort-Object Length -Descending)) {
            # Foreign contents keep the directory. Deletion itself is handle-bound and non-recursive.
            try { $dirs[$folder].Delete() } catch {
                $errorCause = $_.Exception.GetBaseException()
                if ($errorCause -isnot [ComponentModel.Win32Exception] -or $errorCause.NativeErrorCode -ne 145) { throw }
            }
        }
    } finally {
        foreach ($item in $held) { $item.Dispose() }; $sha.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) '@PRODUCT@'
    $activeRoot = Join-Path $root 'active'
    Import-Module (Join-Path $activeRoot '@REGISTRATION@') -Force
    $sources = @('SetupFileSystem.cs','SetupMessages.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    Add-Type -Path $sources -ReferencedAssemblies ([System.Xml.XmlDocument].Assembly.Location)
    [Massdex.SyncShark.SetupMessages]::Load((Join-Path $PSScriptRoot 'setup-strings.xml'))
    Remove-SyncSharkPackage $root {
        param($v)
        Invoke-SyncSharkAppRegistration $v Remove -HostExecutable '@HOST@' -ProductRoot $root -UninstallEntry '@UNINSTALL@'
        Invoke-SyncSharkAppRegistration $v VerifyAbsent -HostExecutable '@HOST@' -ProductRoot $root -UninstallEntry '@UNINSTALL@'
    } {
        param($verifiedCli)
        & $verifiedCli host stop
        if ($LASTEXITCODE -ne 0) { throw 'The host did not confirm its safe stop.' }
    }
}
