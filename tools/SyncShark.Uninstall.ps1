# Requires Windows, PowerShell 7.4+ for production registration; Windows PowerShell 5.1 for offline package fixtures.
# Per-user house setup: the package builder fills in its own version and file hashes.
# Nothing read from the installation is treated as a record of what setup owns.
$ErrorActionPreference = 'Stop'
$script:PackageMembers = '@PUBLISHED_MEMBERS@'
$script:PackageVersion = '@PUBLISHED_VERSION@'
$script:UninstallSource = $MyInvocation.MyCommand.ScriptBlock.Ast.Extent.Text

function Remove-SyncSharkPackage([string]$Root, [scriptblock]$Unregister) {
    $version = $script:PackageVersion
    if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Uninstall must run from its built package.' }
    $members = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:PackageMembers)))
    $target = [IO.Path]::GetFullPath((Join-Path $Root ('versions/' + $version)))
    $held = [Collections.Generic.List[IDisposable]]::new(); $files = [Collections.Generic.List[object]]::new()
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
            $file=[SetupFileSystem]::OpenMutableFile($path); $held.Add($file)
            if ($file.Hash() -cne $member.Hash) { throw "Installed member changed; nothing was removed: $path" }
            $files.Add($file)
        }
        & $Unregister $version
        $active.Delete()
        foreach ($file in $files) { $file.Delete() }
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
    $cli = Join-Path $activeRoot '@CLI@'
    if (@(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension('@HOST@')) -ErrorAction SilentlyContinue).Count) {
        & $cli host stop
        if ($LASTEXITCODE -ne 0) { throw 'The host did not confirm its safe stop.' }
    }
    Remove-SyncSharkPackage $root { param($v) Invoke-SyncSharkAppRegistration $v Remove -HostExecutable '@HOST@'; Invoke-SyncSharkAppRegistration $v VerifyAbsent -HostExecutable '@HOST@' }
}
