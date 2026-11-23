param(
    [Parameter(Mandatory=$true)][string]$JsonPath,
    [string]$HeaderOutput = '',
    [string]$ManagedOutput = '',
    [string]$ModulePath = '',
    [string]$DiscoverySetupPath = ''
)
# Generates every derived copy of the signer identity from the single canonical
# JSON source: the native trust header, the managed identity constants, and the
# marked embedded JSON blocks carried inside the vendored PowerShell scripts.
# Each file is written only when its content actually changes. No effect, no
# build and no signing happens here.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$nl = "`r`n"
$beginMarker = '# BEGIN GENERATED SyncShark signer identity (tools/SyncShark.SignerIdentity.json)'
$endMarker = '# END GENERATED SyncShark signer identity'

$source = [IO.Path]::GetFullPath($JsonPath)
$jsonText = [IO.File]::ReadAllText($source)
$jsonBody = $jsonText.Replace("`r`n", "`n").TrimEnd().Replace("`n", $nl)
$identity = $jsonText | ConvertFrom-Json

function Get-Role([string]$Name) {
    $role = $identity.roles.PSObject.Properties[$Name]
    if ($null -eq $role) { throw "The signer identity is missing the '$Name' role." }
    $role.Value
}
function Get-ThumbprintBytes([string]$Thumbprint) {
    if ($Thumbprint -cnotmatch '^[0-9a-fA-F]{40}$') { throw 'A thumbprint must be 40 hexadecimal digits.' }
    $bytes = for ($index = 0; $index -lt 40; $index += 2) { '0x' + $Thumbprint.Substring($index, 2).ToLowerInvariant() }
    ($bytes -join ',')
}
function Get-StringArray([string[]]$Values) {
    $quoted = @($Values | ForEach-Object { '"' + $_ + '"' })
    '{ ' + ($quoted -join ', ') + ' }'
}
function Write-IfChanged([string]$Path, [string]$Content) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($full) -and [IO.File]::ReadAllText($full) -ceq $Content) { return $false }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    [IO.File]::WriteAllText($full, $Content, [Text.UTF8Encoding]::new($false))
    return $true
}
function Get-GeneratedBlock {
    @(
        $beginMarker
        '$script:SyncSharkSignerIdentityJson = @'''
        $jsonBody
        "'@"
        $endMarker
    ) -join $nl
}
function Update-GeneratedBlock([string]$Path, [string]$Block) {
    $full = [IO.Path]::GetFullPath($Path)
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $text = $encoding.GetString([IO.File]::ReadAllBytes($full))
    $begin = $text.IndexOf($beginMarker)
    if ($begin -lt 0) { throw "The generated block start marker is absent: $full" }
    $end = $text.IndexOf($endMarker, $begin)
    if ($end -lt 0) { throw "The generated block end marker is absent: $full" }
    $end += $endMarker.Length
    $updated = $text.Substring(0, $begin) + $Block + $text.Substring($end)
    if ($updated -ceq $text) { return $false }
    [IO.File]::WriteAllBytes($full, $encoding.GetBytes($updated))
    return $true
}

$publisher = Get-Role 'publisher'
$package = Get-Role 'package'
$publisherThumbprints = @($publisher.thumbprints)
$packageThumbprints = @($package.thumbprints)
$changed = @()

if ($HeaderOutput) {
    $lines = @(
        '#ifndef SYNC_SHARK_SIGNER_IDENTITY_H'
        '#define SYNC_SHARK_SIGNER_IDENTITY_H'
        '/* Generated from tools/SyncShark.SignerIdentity.json. Do not edit by hand;'
        '   run tools/New-SyncSharkSignerIdentity.ps1 instead. The executable (publisher)'
        '   role accepts any listed thumbprint; the MSIX container role is separate. */'
        ('#define SSD_PUBLISHER_THUMBPRINT_COUNT ' + $publisherThumbprints.Count + 'u')
        'static const unsigned char ssd_publisher_thumbprints[SSD_PUBLISHER_THUMBPRINT_COUNT][20] = {'
    )
    foreach ($thumbprint in $publisherThumbprints) {
        $lines += ('    {' + (Get-ThumbprintBytes $thumbprint) + '},')
    }
    $lines += @('};', '#endif')
    if (Write-IfChanged $HeaderOutput (($lines -join $nl) + $nl)) { $changed += $HeaderOutput }
}

if ($ManagedOutput) {
    $lines = @(
        '#if NET8_0_OR_GREATER'
        '#nullable disable'
        '#endif'
        '// Generated from tools/SyncShark.SignerIdentity.json. Do not edit by hand;'
        '// run tools/New-SyncSharkSignerIdentity.ps1 instead.'
        'internal static class SyncSharkSignerIdentity'
        '{'
        '    internal const string PublisherSubject = "' + $publisher.subject + '";'
        '    internal static readonly string[] PublisherThumbprints = ' + (Get-StringArray $publisherThumbprints) + ';'
        '    internal static readonly string[] PublisherSpki = ' + (Get-StringArray @($publisher.spki)) + ';'
        '    internal const string PackageSubject = "' + $package.subject + '";'
        '    internal static readonly string[] PackageThumbprints = ' + (Get-StringArray $packageThumbprints) + ';'
        '    internal static readonly string[] PackageSpki = ' + (Get-StringArray @($package.spki)) + ';'
        '}'
    )
    if (Write-IfChanged $ManagedOutput (($lines -join $nl) + $nl)) { $changed += $ManagedOutput }
}

$block = Get-GeneratedBlock
foreach ($path in @($ModulePath, $DiscoverySetupPath)) {
    if ($path -and (Update-GeneratedBlock $path $block)) { $changed += $path }
}

if ($changed.Count) { Write-Output ('Updated: ' + ($changed -join ', ')) }
else { Write-Output 'Signer identity artefacts already current.' }
