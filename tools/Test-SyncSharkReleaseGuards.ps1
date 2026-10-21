param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$PredecessorRuntime,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [switch]$TimestampParserOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$predecessorRuntime = [IO.Path]::GetFullPath($PredecessorRuntime)
$evidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not (Test-Path -LiteralPath $evidenceDirectory)) { New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null }
$testBase = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
if (-not (Test-Path -LiteralPath $testBase)) { New-Item -ItemType Directory -Path $testBase | Out-Null }
$deploy = Join-Path $releaseRoot 'deploy-scripts'
$package = Join-Path $releaseRoot 'package\signed-candidate'
$manifest = Join-Path $releaseRoot 'package\unsigned-candidate-framework-manifest.json'
$install = Join-Path $deploy 'Install-SyncShark.ps1'
$rollback = Join-Path $deploy 'Rollback-SyncShark.ps1'
$module = Join-Path $deploy 'SyncShark.Release.psm1'
foreach ($path in @($install, $rollback, $module, $manifest)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release input: $path" }
}
$hashes = [ordered]@{
    ExpectedManifestSha256 = (Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedModuleSha256 = (Get-FileHash $module -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedCoreSha256 = (Get-FileHash (Join-Path $package 'SyncShark.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedUiSha256 = (Get-FileHash (Join-Path $package 'SyncShark.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ExpectedSignedExeSha256 = (Get-FileHash (Join-Path $package 'SyncShark.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$packageSigner = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -Raw | ConvertFrom-Json).roles.package
$pins = [ordered]@{
    Thumbprint = @($packageSigner.thumbprints)[0]
    Subject = $packageSigner.subject
    Issuer = $packageSigner.subject
    SpkiSha256 = @($packageSigner.spki)[0]
}
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$results = [Collections.Generic.List[object]]::new()

function New-ObservationSet {
    $all = [ordered]@{}
    foreach ($name in @('SyncShark.Core.dll', 'SyncShark.dll', 'SyncShark.exe')) {
        $all[$name] = [pscustomobject]@{
            Status = 'Valid'; RawStatus = 'Valid'; Thumbprint = $pins.Thumbprint
            Subject = $pins.Subject; Issuer = $pins.Issuer; SpkiSha256 = $pins.SpkiSha256
            SignerChainStatuses = @(); Rfc3161Present = $true
            Rfc3161CryptographicallyValid = $true; Rfc3161 = $true
            TimestampSignatureBound = $true; TimestampChainEvaluatedAtTokenTime = $true
            TimestampChainValid = $true; TimestampUtc = '2026-01-01T00:00:00.0000000Z'
            TimestampChainStatuses = @()
        }
    }
    $all
}

function Write-Observation([string]$Path, [scriptblock]$Mutate) {
    $all = New-ObservationSet
    if ($Mutate) { & $Mutate $all }
    [IO.File]::WriteAllText($Path, ($all | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

function New-TestRoot([string]$Name, [switch]$WithoutActive) {
    $root = Join-Path $testBase ($Name + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root 'versions\0.3.20'), (Join-Path $root 'state'), (Join-Path $root 'vNext\state') -Force | Out-Null
    Get-ChildItem -LiteralPath $predecessorRuntime -Force | Copy-Item -Destination (Join-Path $root 'versions\0.3.20') -Recurse
    [IO.File]::WriteAllText((Join-Path $root 'state\state.txt'), 'predecessor-state', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'vNext\state\state.txt'), 'predecessor-vnext-state', [Text.UTF8Encoding]::new($false))
    # The release transaction holds the application's own state lock for the whole
    # install, so the isolated root supplies the same lock file.
    [IO.File]::WriteAllText((Join-Path $root 'state\instance.lock'), '4242', [Text.UTF8Encoding]::new($false))
    if (-not $WithoutActive) { New-Item -ItemType Junction -Path (Join-Path $root 'active') -Target (Join-Path $root 'versions\0.3.20') | Out-Null }
    $observation = Join-Path $testBase ($Name + '-' + [guid]::NewGuid().ToString('N') + '.verification.json')
    Write-Observation $observation {}
    [pscustomobject]@{ Root = $root; Observation = $observation }
}

function Get-RootManifest([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push([IO.Path]::GetFullPath($Root))
    $rows = [Collections.Generic.List[object]]::new()
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $rows.Add([ordered]@{ path = $relative; kind = 'junction'; target = [string]$item.Target })
            }
            elseif ($item.PSIsContainer) { $rows.Add([ordered]@{ path = $relative; kind = 'directory' }); $pending.Push($item.FullName) }
            else { $rows.Add([ordered]@{ path = $relative; kind = 'file'; length = $item.Length; sha256 = (Get-FileHash $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }) }
        }
    }
    @($rows | Sort-Object { [string]$_.path }) | ConvertTo-Json -Depth 8 -Compress
}

function Get-PeAuthenticodeInfo([string]$Path) {
    $pe = [IO.File]::ReadAllBytes($Path)
    $header = [BitConverter]::ToInt32($pe, 0x3c)
    $optional = $header + 24
    $magic = [BitConverter]::ToUInt16($pe, $optional)
    $directory = $optional + $(if ($magic -eq 0x20b) { 112 } elseif ($magic -eq 0x10b) { 96 } else { throw 'Unsupported PE optional header.' })
    $security = $directory + 32
    $offset = [BitConverter]::ToInt32($pe, $security)
    $size = [BitConverter]::ToInt32($pe, $security + 4)
    $length = [BitConverter]::ToInt32($pe, $offset)
    if ($offset -le 0 -or $size -lt 8 -or $length -lt 8 -or $offset + $size -gt $pe.Length -or $length -gt $size) {
        throw 'Invalid source Authenticode table.'
    }
    $cms = [byte[]]::new($length - 8)
    [Buffer]::BlockCopy($pe, $offset + 8, $cms, 0, $cms.Length)
    [pscustomobject]@{ Bytes = $pe; Security = $security; Offset = $offset; Size = $size; Cms = $cms }
}

function Write-PeAuthenticodeCms([object]$Info, [byte[]]$Cms, [string]$Path) {
    $length = $Cms.Length + 8
    $size = [int](($length + 7) -band -8)
    $tailOffset = $Info.Offset + $Info.Size
    $tailLength = $Info.Bytes.Length - $tailOffset
    $bytes = [byte[]]::new($Info.Offset + $size + $tailLength)
    [Buffer]::BlockCopy($Info.Bytes, 0, $bytes, 0, $Info.Offset)
    [Buffer]::BlockCopy([BitConverter]::GetBytes($length), 0, $bytes, $Info.Offset, 4)
    [Buffer]::BlockCopy($Info.Bytes, $Info.Offset + 4, $bytes, $Info.Offset + 4, 4)
    [Buffer]::BlockCopy($Cms, 0, $bytes, $Info.Offset + 8, $Cms.Length)
    if ($tailLength -gt 0) { [Buffer]::BlockCopy($Info.Bytes, $tailOffset, $bytes, $Info.Offset + $size, $tailLength) }
    [Buffer]::BlockCopy([BitConverter]::GetBytes($Info.Offset), 0, $bytes, $Info.Security, 4)
    [Buffer]::BlockCopy([BitConverter]::GetBytes($size), 0, $bytes, $Info.Security + 4, 4)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function New-TestTimestampCms([byte[]]$Imprint, [string]$ContentOid, [string]$Mode) {
    $timestamp = [DateTimeOffset]::UtcNow
    $writer = [Formats.Asn1.AsnWriter]::new([Formats.Asn1.AsnEncodingRules]::DER)
    [void]$writer.PushSequence()
    if ($Mode -cne 'missing-version') { $writer.WriteInteger($(if ($Mode -ceq 'wrong-version') { 2 } else { 1 })) }
    $writer.WriteObjectIdentifier('1.2.3.4')
    [void]$writer.PushSequence()
    [void]$writer.PushSequence()
    $writer.WriteObjectIdentifier('2.16.840.1.101.3.4.2.1')
    $writer.WriteNull()
    $writer.PopSequence()
    $writer.WriteOctetString($Imprint)
    $writer.PopSequence()
    if ($Mode -cne 'missing-serial') { $writer.WriteInteger(1) }
    if ($Mode -cne 'missing-gentime') { $writer.WriteGeneralizedTime($timestamp) }
    if ($Mode -ceq 'trailing-tstinfo') { $writer.WriteNull() }
    $writer.PopSequence()
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Sync Shark Timestamp Parser Fixture', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $ekuOids = [Security.Cryptography.OidCollection]::new()
        if ($Mode -cne 'absent-eku') {
            if ($Mode -ceq 'wrong-eku') { [void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) }
            else {
                [void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.8'))
                if ($Mode -ceq 'multiple-eku') { [void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) }
            }
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                    $ekuOids, $Mode -cne 'noncritical-eku'))
        }
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $content = [Security.Cryptography.Pkcs.ContentInfo]::new([Security.Cryptography.Oid]::new($ContentOid), $writer.Encode())
            $stamp = [Security.Cryptography.Pkcs.SignedCms]::new($content, $false)
            $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
            $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
            $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
            if ($Mode -cne 'absent-ess') {
                $certificateHash = [Security.Cryptography.SHA256]::HashData($certificate.RawData)
                if ($Mode -ceq 'ess-mismatch') { $certificateHash[0] = $certificateHash[0] -bxor 0xff }
                $ess = [Formats.Asn1.AsnWriter]::new([Formats.Asn1.AsnEncodingRules]::DER)
                [void]$ess.PushSequence()
                [void]$ess.PushSequence()
                [void]$ess.PushSequence()
                $ess.WriteOctetString($certificateHash)
                $ess.PopSequence()
                $ess.PopSequence()
                $ess.PopSequence()
                [void]$signer.SignedAttributes.Add([Security.Cryptography.AsnEncodedData]::new(
                    [Security.Cryptography.Oid]::new('1.2.840.113549.1.9.16.2.47'), $ess.Encode()))
            }
            $stamp.ComputeSignature($signer)
            $stamp.Encode()
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
}

function New-TimestampParserFixture([string]$Name, [string]$Mode, [string]$Directory) {
    $rfcOid = '1.3.6.1.4.1.311.3.3.1'
    $tstOid = '1.2.840.113549.1.9.16.1.4'
    $source = Join-Path $predecessorRuntime 'SyncShark.exe'
    $info = Get-PeAuthenticodeInfo $source
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($info.Cms); $cms.CheckSignature($true)
    $primary = $cms.SignerInfos[0]
    foreach ($attribute in @($primary.UnsignedAttributes | Where-Object { $_.Oid.Value -eq $rfcOid })) {
        foreach ($value in @($attribute.Values)) { $primary.RemoveUnsignedAttribute($value) }
    }
    if ($Mode -cne 'missing') {
        $imprint = [Security.Cryptography.SHA256]::HashData($primary.GetSignature())
        if ($Mode -ceq 'binding') { $imprint[0] = $imprint[0] -bxor 0xff }
        $stampBytes = if ($Mode -ceq 'malformed') { [byte[]](0x30, 0x01, 0x00) }
            else { New-TestTimestampCms $imprint $(if ($Mode -ceq 'non-rfc3161') { '1.2.840.113549.1.7.1' } else { $tstOid }) $Mode }
        if ($Mode -ceq 'tampered') { $stampBytes[$stampBytes.Length - 1] = $stampBytes[$stampBytes.Length - 1] -bxor 0xff }
        $primary.AddUnsignedAttribute([Security.Cryptography.AsnEncodedData]::new(
            [Security.Cryptography.Oid]::new($rfcOid), [byte[]]$stampBytes))
    }
    if (-not (Test-Path -LiteralPath $Directory)) { New-Item -ItemType Directory -Path $Directory | Out-Null }
    $path = Join-Path $Directory ($Name + '.exe')
    Write-PeAuthenticodeCms $info $cms.Encode() $path
    $rewritten = [Security.Cryptography.Pkcs.SignedCms]::new()
    $rewritten.Decode((Get-PeAuthenticodeInfo $path).Cms); $rewritten.CheckSignature($true)
    $path
}

function New-ChainedTimestampParserFixture([string]$Name, [DateTimeOffset]$TokenTime,
    [DateTimeOffset]$LeafNotBefore, [DateTimeOffset]$LeafNotAfter, [bool]$MultipleEku,
    [bool]$AddSecondTimestampValue, [string]$Directory) {
    $rfcOid = '1.3.6.1.4.1.311.3.3.1'
    $tstOid = '1.2.840.113549.1.9.16.1.4'
    $source = Join-Path $predecessorRuntime 'SyncShark.exe'
    $info = Get-PeAuthenticodeInfo $source
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($info.Cms); $cms.CheckSignature($true)
    $primary = $cms.SignerInfos[0]
    foreach ($attribute in @($primary.UnsignedAttributes | Where-Object { $_.Oid.Value -eq $rfcOid })) {
        foreach ($value in @($attribute.Values)) { $primary.RemoveUnsignedAttribute($value) }
    }
    $imprint = [Security.Cryptography.SHA256]::HashData($primary.GetSignature())

    $rootKey = [Security.Cryptography.RSA]::Create(2048)
    $leafKey = [Security.Cryptography.RSA]::Create(2048)
    $root = $null; $leafWithoutKey = $null; $leaf = $null
    try {
        $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Sync Shark Isolated Timestamp Root', $rootKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $rootRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $rootRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
        $rootRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey, $false))
        $root = $rootRequest.CreateSelfSigned(
            [DateTimeOffset]::new(2018, 1, 1, 0, 0, 0, [TimeSpan]::Zero),
            [DateTimeOffset]::new(2035, 1, 1, 0, 0, 0, [TimeSpan]::Zero))

        $leafRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Sync Shark Isolated Timestamp Signer', $leafKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $leafRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $leafRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $ekuOids = [Security.Cryptography.OidCollection]::new()
        [void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.8'))
        if ($MultipleEku) { [void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) }
        $leafRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $true))
        $leafRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($leafRequest.PublicKey, $false))
        $serial = [byte[]]::new(16); [Security.Cryptography.RandomNumberGenerator]::Fill($serial); $serial[0] = $serial[0] -band 0x7f
        $leafWithoutKey = $leafRequest.Create($root, $LeafNotBefore, $LeafNotAfter, $serial)
        $leaf = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
            $leafWithoutKey, $leafKey)

        $tstInfo = [Formats.Asn1.AsnWriter]::new([Formats.Asn1.AsnEncodingRules]::DER)
        [void]$tstInfo.PushSequence(); $tstInfo.WriteInteger(1); $tstInfo.WriteObjectIdentifier('1.2.3.4')
        [void]$tstInfo.PushSequence(); [void]$tstInfo.PushSequence()
        $tstInfo.WriteObjectIdentifier('2.16.840.1.101.3.4.2.1'); $tstInfo.WriteNull(); $tstInfo.PopSequence()
        $tstInfo.WriteOctetString($imprint); $tstInfo.PopSequence(); $tstInfo.WriteInteger(1)
        $tstInfo.WriteGeneralizedTime($TokenTime); $tstInfo.PopSequence()
        $content = [Security.Cryptography.Pkcs.ContentInfo]::new(
            [Security.Cryptography.Oid]::new($tstOid), $tstInfo.Encode())
        $stamp = [Security.Cryptography.Pkcs.SignedCms]::new($content, $false)
        $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($leaf)
        $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
        $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
        $certificateHash = [Security.Cryptography.SHA256]::HashData($leaf.RawData)
        $ess = [Formats.Asn1.AsnWriter]::new([Formats.Asn1.AsnEncodingRules]::DER)
        [void]$ess.PushSequence(); [void]$ess.PushSequence(); [void]$ess.PushSequence()
        $ess.WriteOctetString($certificateHash); $ess.PopSequence(); $ess.PopSequence(); $ess.PopSequence()
        [void]$signer.SignedAttributes.Add([Security.Cryptography.AsnEncodedData]::new(
            [Security.Cryptography.Oid]::new('1.2.840.113549.1.9.16.2.47'), $ess.Encode()))
        $stamp.ComputeSignature($signer); $stamp.AddCertificate($root)
        $tokenBytes = $stamp.Encode()
        $primary.AddUnsignedAttribute([Security.Cryptography.AsnEncodedData]::new(
            [Security.Cryptography.Oid]::new($rfcOid), [byte[]]$tokenBytes))
        if ($AddSecondTimestampValue) {
            $primary.AddUnsignedAttribute([Security.Cryptography.AsnEncodedData]::new(
                [Security.Cryptography.Oid]::new($rfcOid), [byte[]](0x05, 0x00)))
        }
        if (-not (Test-Path -LiteralPath $Directory)) { New-Item -ItemType Directory -Path $Directory | Out-Null }
        $path = Join-Path $Directory ($Name + '.exe')
        Write-PeAuthenticodeCms $info $cms.Encode() $path
        [pscustomobject]@{
            Path = $path; Root = $root; Leaf = $leaf; RootKey = $rootKey; LeafKey = $leafKey
            TokenBytes = [byte[]]$tokenBytes; TokenTime = $TokenTime
        }
        $root = $null; $leaf = $null; $rootKey = $null; $leafKey = $null
    }
    finally {
        if ($leaf) { $leaf.Dispose() }; if ($leafWithoutKey) { $leafWithoutKey.Dispose() }
        if ($root) { $root.Dispose() }; if ($leafKey) { $leafKey.Dispose() }; if ($rootKey) { $rootKey.Dispose() }
    }
}

function Invoke-Installer([string]$Root, [string]$Observation, [hashtable]$HashOverride = @{}) {
    $arguments = @('-NoLogo', '-NoProfile', '-File', $install)
    foreach ($key in $hashes.Keys) {
        $value = if ($HashOverride.ContainsKey($key)) { $HashOverride[$key] } else { $hashes[$key] }
        $arguments += ('-' + $key)
        $arguments += $value
    }
    $arguments += @('-TestProductRoot', $Root, '-VerificationObservationPath', $Observation)
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($item in $arguments) { $start.ArgumentList.Add([string]$item) }
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['SYNC_SHARK_RELEASE_TEST_MODE'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { try { $process.Kill($true) } catch {}; throw 'Installer child timed out.' }
    [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdoutTask.GetAwaiter().GetResult(); Stderr = $stderrTask.GetAwaiter().GetResult() }
}

function Invoke-Rollback([string]$Root, [string]$Observation) {
    $arguments = @('-NoLogo', '-NoProfile', '-File', $rollback)
    foreach ($key in $hashes.Keys) { $arguments += ('-' + $key); $arguments += $hashes[$key] }
    $arguments += @('-TestProductRoot', $Root, '-VerificationObservationPath', $Observation)
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($item in $arguments) { $start.ArgumentList.Add([string]$item) }
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['SYNC_SHARK_RELEASE_TEST_MODE'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { try { $process.Kill($true) } catch {}; throw 'Rollback child timed out.' }
    [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdoutTask.GetAwaiter().GetResult(); Stderr = $stderrTask.GetAwaiter().GetResult() }
}

function Assert-RejectedUnchanged([string]$Case, [object]$Fixture, [hashtable]$HashOverride = @{}) {
    $before = Get-RootManifest $Fixture.Root
    $run = Invoke-Installer $Fixture.Root $Fixture.Observation $HashOverride
    if ($run.ExitCode -eq 0) { throw "Rejected case was accepted: $Case" }
    $after = Get-RootManifest $Fixture.Root
    if ($after -cne $before) { throw "Rejected case mutated the product root: $Case" }
    $beforeBytes = [Text.UTF8Encoding]::new($false).GetBytes($before)
    $afterBytes = [Text.UTF8Encoding]::new($false).GetBytes($after)
    $results.Add([ordered]@{
        case = $Case
        result = 'rejected-without-mutation'
        error = ($run.Stderr.Trim() -split "`r?`n")[-1]
        before_manifest = $before
        before_manifest_length = $beforeBytes.Length
        before_manifest_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($beforeBytes)).ToLowerInvariant()
        after_manifest = $after
        after_manifest_length = $afterBytes.Length
        after_manifest_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($afterBytes)).ToLowerInvariant()
    })
}

function New-RollbackTestRoot([string]$Name) {
    $fixture = New-TestRoot $Name
    $installRun = Invoke-Installer $fixture.Root $fixture.Observation
    if ($installRun.ExitCode -ne 0) { throw "Rollback fixture install failed: $($installRun.Stderr)" }
    [IO.File]::WriteAllText((Join-Path $fixture.Root 'state\state.txt'), 'successor-state', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixture.Root 'vNext\state\state.txt'), 'successor-vnext-state', [Text.UTF8Encoding]::new($false))
    $fixture
}

function Assert-RollbackRejectedUnchanged([string]$Case, [object]$Fixture) {
    $before = Get-RootManifest $Fixture.Root
    $run = Invoke-Rollback $Fixture.Root $Fixture.Observation
    if ($run.ExitCode -eq 0) { throw "Rejected rollback case was accepted: $Case" }
    $after = Get-RootManifest $Fixture.Root
    if ($after -cne $before) { throw "Rejected rollback case mutated the product root: $Case" }
    $beforeBytes = [Text.UTF8Encoding]::new($false).GetBytes($before)
    $afterBytes = [Text.UTF8Encoding]::new($false).GetBytes($after)
    $results.Add([ordered]@{
        case = $Case
        result = 'rejected-without-mutation'
        error = ($run.Stderr.Trim() -split "`r?`n")[-1]
        before_manifest = $before
        before_manifest_length = $beforeBytes.Length
        before_manifest_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($beforeBytes)).ToLowerInvariant()
        after_manifest = $after
        after_manifest_length = $afterBytes.Length
        after_manifest_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($afterBytes)).ToLowerInvariant()
    })
}

$releaseModule = Import-Module $module -Force -PassThru
function Assert-Decision([string]$Case, [scriptblock]$Mutate, [bool]$ExpectedAllowed) {
    $all = New-ObservationSet; if ($Mutate) { & $Mutate $all }
    $allowed = $true; $errorText = $null
    try { Assert-SyncSharkSignatureObservation $all['SyncShark.exe'] $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256 }
    catch { $allowed = $false; $errorText = $_.Exception.Message }
    if ($allowed -ne $ExpectedAllowed) { throw "Signature decision mismatch for $Case." }
    $results.Add([ordered]@{ case = "signature-decision/$Case"; result = if ($allowed) { 'allowed' } else { 'rejected' }; error = $errorText })
}

Assert-Decision 'valid' {} $true
Assert-Decision 'nottrusted-isolated-untrustedroot' { param($x); $x.Values | ForEach-Object { $_.Status = 'NotTrusted'; $_.RawStatus = 'UnknownError'; $_.SignerChainStatuses = @('UntrustedRoot') } } $true
foreach ($status in @('UnknownError', 'NotSigned', 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible')) {
    Assert-Decision ("status-$status") { param($x); $x.Values | ForEach-Object { $_.Status = $status } }.GetNewClosure() $false
}
Assert-Decision 'nottrusted-no-chain-status' { param($x); $x.Values | ForEach-Object { $_.Status = 'NotTrusted'; $_.SignerChainStatuses = @() } } $false
Assert-Decision 'nottrusted-additional-chain-status' { param($x); $x.Values | ForEach-Object { $_.Status = 'NotTrusted'; $_.SignerChainStatuses = @('UntrustedRoot', 'NotTimeValid') } } $false
foreach ($property in @('Thumbprint', 'Subject', 'Issuer', 'SpkiSha256')) {
    Assert-Decision ("pin-$property") { param($x); $x.Values | ForEach-Object { $_.$property = 'mismatch' } }.GetNewClosure() $false
}
foreach ($property in @('Rfc3161Present', 'Rfc3161CryptographicallyValid', 'Rfc3161', 'TimestampSignatureBound', 'TimestampChainEvaluatedAtTokenTime', 'TimestampChainValid')) {
    Assert-Decision ("timestamp-$property-false") { param($x); $x.Values | ForEach-Object { $_.$property = $false } }.GetNewClosure() $false
}
Assert-Decision 'timestamp-additional-chain-status' { param($x); $x.Values | ForEach-Object { $_.TimestampChainStatuses = @('NotTimeValid') } } $false

$actual = [ordered]@{}
foreach ($name in @('SyncShark.Core.dll', 'SyncShark.dll', 'SyncShark.exe')) {
    $observation = Get-SyncSharkSignatureObservation (Join-Path $predecessorRuntime $name)
    Assert-SyncSharkSignatureObservation $observation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256
    $actual[$name] = $observation
}
$results.Add([ordered]@{ case = 'signature-parser/installed-0.3.20'; result = 'allowed'; observations = $actual })

$parserFixtureDirectory = Join-Path $evidenceDirectory 'timestamp-parser-fixtures'
if (Test-Path -LiteralPath $parserFixtureDirectory) { Remove-Item -LiteralPath $parserFixtureDirectory -Recurse -Force }
$parserCases = @(
    [pscustomobject]@{ Name='missing'; Present=$false; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='malformed'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='tampered'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='non-rfc3161'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='missing-version'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='wrong-version'; Present=$true; Crypto=$true; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='missing-serial'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='missing-gentime'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='trailing-tstinfo'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='absent-ess'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='ess-mismatch'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='absent-eku'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='wrong-eku'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='noncritical-eku'; Present=$true; Crypto=$false; Rfc=$false; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='multiple-eku'; Present=$true; Crypto=$true; Rfc=$true; Bound=$true; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='binding'; Present=$true; Crypto=$true; Rfc=$true; Bound=$false; AtTime=$false; Chain=$false },
    [pscustomobject]@{ Name='chain'; Present=$true; Crypto=$true; Rfc=$true; Bound=$true; AtTime=$true; Chain=$false }
)
foreach ($parserCase in $parserCases) {
    $fixturePath = New-TimestampParserFixture $parserCase.Name $parserCase.Name $parserFixtureDirectory
    $observation = Get-SyncSharkSignatureObservation $fixturePath
    if ([bool]$observation.Rfc3161Present -ne $parserCase.Present -or
        [bool]$observation.Rfc3161CryptographicallyValid -ne $parserCase.Crypto -or
        [bool]$observation.Rfc3161 -ne $parserCase.Rfc -or
        [bool]$observation.TimestampSignatureBound -ne $parserCase.Bound -or
        [bool]$observation.TimestampChainEvaluatedAtTokenTime -ne $parserCase.AtTime -or
        [bool]$observation.TimestampChainValid -ne $parserCase.Chain) {
        throw "Actual timestamp parser outcome mismatch: $($parserCase.Name)"
    }
    if ($parserCase.Name -ceq 'chain' -and @($observation.TimestampChainStatuses).Count -eq 0) {
        throw 'The actual timestamp parser chain-negative fixture did not expose a chain status.'
    }
    $results.Add([ordered]@{
        case = 'signature-parser/' + $parserCase.Name
        result = 'actual-parser-rejected'
        fixture_path = $fixturePath
        fixture_sha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        source_fixture_sha256 = (Get-FileHash -LiteralPath (Join-Path $predecessorRuntime 'SyncShark.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        observation = $observation
    })
}

$historicalTime = [DateTimeOffset]::new(2019, 6, 1, 12, 0, 0, [TimeSpan]::Zero)
$historicalValid = New-ChainedTimestampParserFixture 'valid-at-token-time-expired-now' $historicalTime `
    ([DateTimeOffset]::new(2019, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) `
    ([DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) $false $false $parserFixtureDirectory
$historicalInvalid = New-ChainedTimestampParserFixture 'not-valid-at-token-time' $historicalTime `
    ([DateTimeOffset]::new(2020, 1, 2, 0, 0, 0, [TimeSpan]::Zero)) `
    ([DateTimeOffset]::new(2021, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) $false $false $parserFixtureDirectory
$multipleEku = New-ChainedTimestampParserFixture 'multiple-eku-custom-trust' ([DateTimeOffset]::UtcNow) `
    ([DateTimeOffset]::UtcNow.AddDays(-1)) ([DateTimeOffset]::UtcNow.AddDays(1)) $true $false $parserFixtureDirectory
$outerExtraValuePe = New-ChainedTimestampParserFixture 'outer-extra-value-pe' $historicalTime `
    ([DateTimeOffset]::new(2019, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) `
    ([DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) $false $true $parserFixtureDirectory
try {
    $validObservation = & $releaseModule { param($path, $root) Get-SyncSharkSignatureObservationCore $path $root } $historicalValid.Path $historicalValid.Root
    Assert-SyncSharkSignatureObservation $validObservation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256
    if (-not $validObservation.TimestampChainEvaluatedAtTokenTime -or
        $validObservation.TimestampUtc -cne $historicalTime.UtcDateTime.ToString('O')) {
        throw 'The historical valid chain was not evaluated at the exact token time.'
    }
    $results.Add([ordered]@{ case='signature-parser/generated-valid-expired-now'; result='actual-parser-allowed'; fixture_path=$historicalValid.Path; fixture_sha256=(Get-FileHash $historicalValid.Path -Algorithm SHA256).Hash.ToLowerInvariant(); observation=$validObservation })

    $invalidObservation = & $releaseModule { param($path, $root) Get-SyncSharkSignatureObservationCore $path $root } $historicalInvalid.Path $historicalInvalid.Root
    $invalidAllowed = $true; try { Assert-SyncSharkSignatureObservation $invalidObservation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256 } catch { $invalidAllowed = $false }
    if ($invalidAllowed -or $invalidObservation.TimestampSignatureBound) { throw 'The TSA signer not valid at token time was not rejected at binding.' }
    $results.Add([ordered]@{ case='signature-parser/generated-not-valid-at-token-time'; result='actual-parser-rejected'; fixture_path=$historicalInvalid.Path; fixture_sha256=(Get-FileHash $historicalInvalid.Path -Algorithm SHA256).Hash.ToLowerInvariant(); observation=$invalidObservation })

    $multipleObservation = & $releaseModule { param($path, $root) Get-SyncSharkSignatureObservationCore $path $root } $multipleEku.Path $multipleEku.Root
    $multipleAllowed = $true; try { Assert-SyncSharkSignatureObservation $multipleObservation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256 } catch { $multipleAllowed = $false }
    if ($multipleAllowed -or @($multipleObservation.TimestampChainStatuses).Count -ne 0 -or
        $multipleObservation.TimestampChainEvaluatedAtTokenTime) {
        throw 'The multiple-EKU fixture did not reject before chain evaluation.'
    }
    $results.Add([ordered]@{ case='signature-parser/generated-multiple-eku-custom-trust'; result='actual-parser-rejected-before-chain'; fixture_path=$multipleEku.Path; fixture_sha256=(Get-FileHash $multipleEku.Path -Algorithm SHA256).Hash.ToLowerInvariant(); observation=$multipleObservation })

    $baseRaw = [SyncShark.Release.SignatureInspector]::InspectTimestampTokenForFile(
        $historicalValid.Path, $historicalValid.TokenBytes, $historicalValid.Root)
    if (-not $baseRaw.Present -or -not $baseRaw.ChainValid -or -not $baseRaw.ChainEvaluatedAtTokenTime) {
        throw 'The raw-token positive control did not pass the shared production parser.'
    }
    $trailingBytes = [byte[]]::new($historicalValid.TokenBytes.Length + 2)
    [Buffer]::BlockCopy($historicalValid.TokenBytes, 0, $trailingBytes, 0, $historicalValid.TokenBytes.Length)
    $trailingBytes[$trailingBytes.Length - 2] = 0x05; $trailingBytes[$trailingBytes.Length - 1] = 0x00
    $trailingRaw = [SyncShark.Release.SignatureInspector]::InspectTimestampTokenForFile(
        $historicalValid.Path, $trailingBytes, $historicalValid.Root)
    if (-not $trailingRaw.Present -or $trailingRaw.CryptographicallyValid -or $trailingRaw.IsRfc3161 -or
        $trailingRaw.SignatureBound -or $trailingRaw.ChainValid) {
        throw 'The otherwise-valid RFC3161 token with trailing outer bytes was not rejected at complete consumption.'
    }
    $results.Add([ordered]@{ case='signature-parser/generated-valid-raw-token'; result='shared-production-parser-allowed'; fixture_path=$historicalValid.Path; token_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($historicalValid.TokenBytes)).ToLowerInvariant(); observation=$baseRaw })
    $results.Add([ordered]@{ case='signature-parser/generated-outer-trailing-bytes'; result='shared-production-parser-rejected'; fixture_path=$historicalValid.Path; token_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($trailingBytes)).ToLowerInvariant(); base_token_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($historicalValid.TokenBytes)).ToLowerInvariant(); observation=$trailingRaw })

    $outerExtraValueObservation = & $releaseModule { param($path, $root) Get-SyncSharkSignatureObservationCore $path $root } $outerExtraValuePe.Path $outerExtraValuePe.Root
    $outerExtraValueAllowed = $true; try { Assert-SyncSharkSignatureObservation $outerExtraValueObservation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256 } catch { $outerExtraValueAllowed = $false }
    if ($outerExtraValueAllowed -or $outerExtraValueObservation.Rfc3161Present -or
        $outerExtraValueObservation.Rfc3161CryptographicallyValid -or $outerExtraValueObservation.Rfc3161) {
        throw 'The PE fixture containing a valid token plus an additional timestamp value did not fail at attribute cardinality.'
    }
    $results.Add([ordered]@{ case='signature-parser/generated-valid-token-plus-extra-value-pe'; result='actual-pe-parser-rejected-at-cardinality'; fixture_path=$outerExtraValuePe.Path; fixture_sha256=(Get-FileHash $outerExtraValuePe.Path -Algorithm SHA256).Hash.ToLowerInvariant(); base_token_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($outerExtraValuePe.TokenBytes)).ToLowerInvariant(); observation=$outerExtraValueObservation })
}
finally {
    foreach ($material in @($historicalValid, $historicalInvalid, $multipleEku, $outerExtraValuePe)) {
        if ($material) { $material.Leaf.Dispose(); $material.Root.Dispose(); $material.LeafKey.Dispose(); $material.RootKey.Dispose() }
    }
}

$consumerFixture = New-TestRoot 'timestamp-time-consumer-gate'
try {
    Write-Observation $consumerFixture.Observation {
        param($x)
        $x.Values | ForEach-Object { $_.TimestampChainEvaluatedAtTokenTime = $false }
    }
    $beforeInstall = Get-RootManifest $consumerFixture.Root
    $installRejected = Invoke-Installer $consumerFixture.Root $consumerFixture.Observation
    $afterInstall = Get-RootManifest $consumerFixture.Root
    if ($installRejected.ExitCode -eq 0 -or $beforeInstall -cne $afterInstall) {
        throw 'The exact installer did not reject the missing token-time chain proof before mutation.'
    }
    $results.Add([ordered]@{
        case='consumer/install-rejects-missing-token-time-before-mutation'; result='exact-entry-point-rejected-without-mutation'
        script=$install; script_sha256=(Get-FileHash $install -Algorithm SHA256).Hash.ToLowerInvariant()
        module_sha256=$hashes.ExpectedModuleSha256; before_manifest_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($beforeInstall))).ToLowerInvariant()
        after_manifest_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($afterInstall))).ToLowerInvariant()
    })

    Write-Observation $consumerFixture.Observation {}
    $installAllowed = Invoke-Installer $consumerFixture.Root $consumerFixture.Observation
    if ($installAllowed.ExitCode -ne 0) { throw "Could not establish the isolated rollback fixture: $($installAllowed.Stderr)" }
    Write-Observation $consumerFixture.Observation {
        param($x)
        $x.Values | ForEach-Object { $_.TimestampChainEvaluatedAtTokenTime = $false }
    }
    $beforeRollback = Get-RootManifest $consumerFixture.Root
    $rollbackRejected = Invoke-Rollback $consumerFixture.Root $consumerFixture.Observation
    $afterRollback = Get-RootManifest $consumerFixture.Root
    if ($rollbackRejected.ExitCode -eq 0 -or $beforeRollback -cne $afterRollback) {
        throw 'The exact rollback did not reject the missing token-time chain proof before mutation.'
    }
    $results.Add([ordered]@{
        case='consumer/rollback-rejects-missing-token-time-before-mutation'; result='exact-entry-point-rejected-without-mutation'
        script=$rollback; script_sha256=(Get-FileHash $rollback -Algorithm SHA256).Hash.ToLowerInvariant()
        module_sha256=$hashes.ExpectedModuleSha256; before_manifest_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($beforeRollback))).ToLowerInvariant()
        after_manifest_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($afterRollback))).ToLowerInvariant()
    })
}
finally {
    $resolvedTestBase = [IO.Path]::GetFullPath($testBase).TrimEnd('\') + '\'
    $resolvedFixture = [IO.Path]::GetFullPath($consumerFixture.Root)
    if (-not $resolvedFixture.StartsWith($resolvedTestBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refused to clean a focused consumer fixture outside the fixed test root.'
    }
    if (Test-Path -LiteralPath $consumerFixture.Root) { Remove-Item -LiteralPath $consumerFixture.Root -Recurse -Force }
    if (Test-Path -LiteralPath $consumerFixture.Observation) { Remove-Item -LiteralPath $consumerFixture.Observation -Force }
}

if ($TimestampParserOnly) {
    $receipt = [ordered]@{
        result = 'PASS'; source_commit = $SourceCommit; focused_scope = 'rfc3161-timestamp-parser-and-tsa-policy'
        entry_points = @('Get-SyncSharkSignatureObservation actual PE/CMS parser', 'Assert-SyncSharkSignatureObservation decision gate')
        command_shape = 'pwsh -File Test-SyncSharkReleaseGuards.ps1 -TimestampParserOnly -ReleaseRoot <exact-candidate> -PredecessorRuntime <signed-0.3.20> -SourceCommit <commit> -EvidenceDirectory <bounded-output>'
        input_identity = [ordered]@{ predecessor_runtime=$predecessorRuntime; predecessor_exe_sha256=(Get-FileHash -LiteralPath (Join-Path $predecessorRuntime 'SyncShark.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); module_sha256=$hashes.ExpectedModuleSha256; install_script=$install; install_sha256=(Get-FileHash $install -Algorithm SHA256).Hash.ToLowerInvariant(); rollback_script=$rollback; rollback_sha256=(Get-FileHash $rollback -Algorithm SHA256).Hash.ToLowerInvariant(); parser_fixture_directory=$parserFixtureDirectory }
        oracle = 'genuine installed timestamps and a generated isolated chain valid at token time must pass; a signer invalid at token time plus malformed/incomplete TSTInfo, outer trailing bytes, missing/mismatched ESS, invalid imprint, absent/wrong/noncritical/nonexclusive TSA EKU, invalid signature/content and invalid chain must fail closed'
        case_count = $results.Count; cases = $results
    }
    $receiptPath = Join-Path $evidenceDirectory 'release-guard-matrix.json'
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $receipt | ConvertTo-Json -Depth 5
    return
}

foreach ($allowedCase in @(
    [pscustomobject]@{ Name = 'valid'; Mutate = {} },
    [pscustomobject]@{ Name = 'nottrusted-isolated-untrustedroot'; Mutate = { param($x); $x.Values | ForEach-Object { $_.Status = 'NotTrusted'; $_.RawStatus = 'UnknownError'; $_.SignerChainStatuses = @('UntrustedRoot') } } }
)) {
    $fixture = New-TestRoot ('allow-' + $allowedCase.Name)
    Write-Observation $fixture.Observation $allowedCase.Mutate
    $run = Invoke-Installer $fixture.Root $fixture.Observation
    if ($run.ExitCode -ne 0) { throw "Permitted installer case failed: $($allowedCase.Name): $($run.Stderr)" }
    $active = Get-Item -LiteralPath (Join-Path $fixture.Root 'active') -Force
    if ([IO.Path]::GetFullPath([string]$active.Target) -cne [IO.Path]::GetFullPath((Join-Path $fixture.Root 'versions\0.3.21'))) { throw 'Permitted installer did not publish the successor.' }
    $results.Add([ordered]@{ case = 'installer-allow/' + $allowedCase.Name; result = 'installed-isolated-successor' })
}

$rejections = [Collections.Generic.List[object]]::new()
foreach ($status in @('UnknownError', 'NotSigned', 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible')) {
    $rejections.Add([pscustomobject]@{ Name = "status-$status"; Mutate = { param($x); $x.Values | ForEach-Object { $_.Status = $status } }.GetNewClosure() })
}
$rejections.Add([pscustomobject]@{ Name = 'nottrusted-additional-status'; Mutate = { param($x); $x.Values | ForEach-Object { $_.Status = 'NotTrusted'; $_.SignerChainStatuses = @('UntrustedRoot', 'NotTimeValid') } } })
foreach ($property in @('Thumbprint', 'Subject', 'Issuer', 'SpkiSha256')) {
    $rejections.Add([pscustomobject]@{ Name = "pin-$property"; Mutate = { param($x); $x.Values | ForEach-Object { $_.$property = 'mismatch' } }.GetNewClosure() })
}
foreach ($property in @('Rfc3161Present', 'Rfc3161CryptographicallyValid', 'Rfc3161', 'TimestampSignatureBound', 'TimestampChainValid')) {
    $rejections.Add([pscustomobject]@{ Name = "timestamp-$property-false"; Mutate = { param($x); $x.Values | ForEach-Object { $_.$property = $false } }.GetNewClosure() })
}
$rejections.Add([pscustomobject]@{ Name = 'timestamp-chain-status'; Mutate = { param($x); $x.Values | ForEach-Object { $_.TimestampChainStatuses = @('NotTimeValid') } } })
foreach ($case in $rejections) {
    $fixture = New-TestRoot ('reject-' + $case.Name)
    Write-Observation $fixture.Observation $case.Mutate
    Assert-RejectedUnchanged ('installer-reject/' + $case.Name) $fixture
}

$badHashFixture = New-TestRoot 'reject-package-hash'
Assert-RejectedUnchanged 'installer-reject/package-hash-mismatch' $badHashFixture @{ ExpectedSignedExeSha256 = ('0' * 64) }

foreach ($member in @('SyncShark.exe', 'SyncShark.Core.dll')) {
    $fixture = New-TestRoot ('reject-predecessor-' + $member.Replace('.', '-'))
    [IO.File]::AppendAllText((Join-Path $fixture.Root ('versions\0.3.20\' + $member)), 'tamper')
    Assert-RejectedUnchanged ('installer-reject/predecessor-tree-' + $member) $fixture
}

function Write-Journal([string]$Root, [object]$Value) {
    $transaction = Join-Path $Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
    New-Item -ItemType Directory -Path $transaction -Force | Out-Null
    $path = Join-Path $transaction 'install-journal.json'
    if ($Value -is [string]) { [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false)) }
    else { [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false)) }
    [pscustomobject]@{ Root = $transaction; Path = $path }
}
function New-Journal([string]$Root, [string[]]$Completed = @(), [string]$Pending = $null) {
    [pscustomobject]@{
        schema = 1; operation = 'install'; version = '0.3.21'; commit = $SourceCommit
        product_root = [IO.Path]::GetFullPath($Root); status = 'active'; pending = $Pending
        completed = $Completed; allowed_steps = @('stage-package', 'publish-runtime', 'backup-state', 'create-next-link', 'retain-predecessor', 'publish-successor', 'verify-complete')
        artifacts = [pscustomobject]@{ state_predecessor_sha256 = $null }
    }
}

function Write-RollbackJournal([string]$Root, [object]$Value) {
    $transaction = Join-Path $Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
    $path = Join-Path $transaction 'rollback-journal.json'
    if ($Value -is [string]) { [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false)) }
    else { [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false)) }
    [pscustomobject]@{ Root = $transaction; Path = $path }
}
function New-RollbackJournal([string]$Root, [string[]]$Completed = @(), [string]$Pending = $null) {
    $transaction = Join-Path $Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
    $installJournal = Get-Content -LiteralPath (Join-Path $transaction 'install-journal.json') -Raw | ConvertFrom-Json -Depth 20
    [pscustomobject]@{
        schema = 1; operation = 'rollback'; version = '0.3.21'; commit = $SourceCommit
        product_root = [IO.Path]::GetFullPath($Root); status = 'active'; pending = $Pending
        completed = $Completed; allowed_steps = @('preserve-state', 'preserve-vnext-state', 'restore-state', 'restore-vnext-state', 'retain-successor', 'publish-predecessor', 'verify-complete')
        artifacts = [pscustomobject]@{ predecessor_state_sha256 = [string]$installJournal.artifacts.state_predecessor_sha256; successor_state_sha256 = $null; pending_successor_state_sha256 = $null }
    }
}

foreach ($badJournal in @(
    [pscustomobject]@{ Name = 'truncated'; Mutate = { param($j); '{bad' } },
    [pscustomobject]@{ Name = 'unknown-schema'; Mutate = { param($j); $j.schema = 2; $j } },
    [pscustomobject]@{ Name = 'version-mismatch'; Mutate = { param($j); $j.version = '9.9.9'; $j } },
    [pscustomobject]@{ Name = 'commit-mismatch'; Mutate = { param($j); $j.commit = ('0' * 40); $j } },
    [pscustomobject]@{ Name = 'escaped-product-root'; Mutate = { param($j); $j.product_root = 'C:\Windows'; $j } },
    [pscustomobject]@{ Name = 'unknown-phase'; Mutate = { param($j); $j.pending = 'unknown'; $j } },
    [pscustomobject]@{ Name = 'unknown-status'; Mutate = { param($j); $j.status = 'unknown'; $j } },
    [pscustomobject]@{ Name = 'contradictory-order'; Mutate = { param($j); $j.completed = @('publish-runtime', 'stage-package'); $j } }
)) {
    $fixture = New-TestRoot ('journal-' + $badJournal.Name)
    $journal = New-Journal $fixture.Root
    $value = & $badJournal.Mutate $journal
    Write-Journal $fixture.Root $value | Out-Null
    Assert-RejectedUnchanged ('journal-reject/' + $badJournal.Name) $fixture
}

$fixture = New-TestRoot 'reparse-transaction'
$tx = Write-Journal $fixture.Root (New-Journal $fixture.Root)
$outside = Join-Path $testBase ('outside-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $outside | Out-Null
New-Item -ItemType Junction -Path (Join-Path $tx.Root 'unexpected') -Target $outside | Out-Null
Assert-RejectedUnchanged 'journal-reject/unexpected-transaction-reparse' $fixture

$fixture = New-TestRoot 'active-target' -WithoutActive
New-Item -ItemType Directory -Path (Join-Path $fixture.Root 'versions\rogue') | Out-Null
New-Item -ItemType Junction -Path (Join-Path $fixture.Root 'active') -Target (Join-Path $fixture.Root 'versions\rogue') | Out-Null
Assert-RejectedUnchanged 'identity-reject/unexpected-active-target' $fixture

$fixture = New-TestRoot 'unexpected-rollback'
$txPath = Join-Path $fixture.Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
New-Item -ItemType Directory -Path $txPath -Force | Out-Null
New-Item -ItemType Junction -Path (Join-Path $txPath 'predecessor.rollback') -Target (Join-Path $fixture.Root 'versions\0.3.20') | Out-Null
Assert-RejectedUnchanged 'identity-reject/unexpected-rollback-artifact' $fixture

$fixture = New-TestRoot 'missing-publish-artifact'
Write-Journal $fixture.Root (New-Journal $fixture.Root @('stage-package') 'publish-runtime') | Out-Null
Assert-RejectedUnchanged 'journal-reject/missing-nonreconcilable-staging' $fixture

$fixture = New-TestRoot 'contradictory-publish-artifacts'
$tx = Write-Journal $fixture.Root (New-Journal $fixture.Root @('stage-package') 'publish-runtime')
Copy-Item -LiteralPath $package -Destination (Join-Path $tx.Root 'runtime.installing') -Recurse
Copy-Item -LiteralPath $package -Destination (Join-Path $fixture.Root 'versions\0.3.21') -Recurse
Assert-RejectedUnchanged 'journal-reject/contradictory-publish-artifacts' $fixture

$fixture = New-TestRoot 'runtime-hash-mismatch'
$tx = Write-Journal $fixture.Root (New-Journal $fixture.Root @('stage-package', 'publish-runtime'))
Copy-Item -LiteralPath $package -Destination (Join-Path $fixture.Root 'versions\0.3.21') -Recurse
[IO.File]::AppendAllText((Join-Path $fixture.Root 'versions\0.3.21\SyncShark.dll'), 'tamper')
Assert-RejectedUnchanged 'journal-reject/runtime-hash-mismatch' $fixture

$fixture = New-TestRoot 'state-backup-mismatch'
$badStateJournal = New-Journal $fixture.Root @('stage-package', 'publish-runtime', 'backup-state')
$badStateJournal.artifacts.state_predecessor_sha256 = ('0' * 64)
$tx = Write-Journal $fixture.Root $badStateJournal
Copy-Item -LiteralPath $package -Destination (Join-Path $fixture.Root 'versions\0.3.21') -Recurse
New-Item -ItemType Directory -Path (Join-Path $tx.Root 'state.predecessor\state'), (Join-Path $tx.Root 'state.predecessor\vNext\state') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $tx.Root 'state.predecessor\state\state.txt'), 'wrong', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $tx.Root 'state.predecessor\vNext\state\state.txt'), 'predecessor-vnext-state', [Text.UTF8Encoding]::new($false))
Assert-RejectedUnchanged 'journal-reject/state-backup-hash-mismatch' $fixture

foreach ($badJournal in @(
    [pscustomobject]@{ Name = 'truncated'; Mutate = { param($j); '{bad' } },
    [pscustomobject]@{ Name = 'unknown-schema'; Mutate = { param($j); $j.schema = 2; $j } },
    [pscustomobject]@{ Name = 'version-mismatch'; Mutate = { param($j); $j.version = '9.9.9'; $j } },
    [pscustomobject]@{ Name = 'commit-mismatch'; Mutate = { param($j); $j.commit = ('0' * 40); $j } },
    [pscustomobject]@{ Name = 'escaped-product-root'; Mutate = { param($j); $j.product_root = 'C:\Windows'; $j } },
    [pscustomobject]@{ Name = 'unknown-phase'; Mutate = { param($j); $j.pending = 'unknown'; $j } },
    [pscustomobject]@{ Name = 'unknown-status'; Mutate = { param($j); $j.status = 'unknown'; $j } },
    [pscustomobject]@{ Name = 'contradictory-order'; Mutate = { param($j); $j.completed = @('restore-state', 'preserve-state'); $j } },
    [pscustomobject]@{ Name = 'predecessor-identity-mismatch'; Mutate = { param($j); $j.artifacts.predecessor_state_sha256 = ('0' * 64); $j } }
)) {
    $fixture = New-RollbackTestRoot ('rollback-journal-' + $badJournal.Name)
    $journal = New-RollbackJournal $fixture.Root
    $value = & $badJournal.Mutate $journal
    Write-RollbackJournal $fixture.Root $value | Out-Null
    Assert-RollbackRejectedUnchanged ('rollback-journal-reject/' + $badJournal.Name) $fixture
}

$fixture = New-RollbackTestRoot 'rollback-reparse-transaction'
$transaction = Join-Path $fixture.Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
$outsideRollback = Join-Path $testBase ('rollback-outside-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $outsideRollback | Out-Null
New-Item -ItemType Junction -Path (Join-Path $transaction 'unexpected-rollback-reparse') -Target $outsideRollback | Out-Null
Assert-RollbackRejectedUnchanged 'rollback-journal-reject/unexpected-transaction-reparse' $fixture

foreach ($liveReparseCase in @(
    [pscustomobject]@{ Name = 'state-child'; Relative = 'state\unexpected-reparse'; ReplaceRoot = $false },
    [pscustomobject]@{ Name = 'state-root'; Relative = 'state'; ReplaceRoot = $true },
    [pscustomobject]@{ Name = 'vnext-state-child'; Relative = 'vNext\state\unexpected-reparse'; ReplaceRoot = $false }
)) {
    $fixture = New-RollbackTestRoot ('rollback-live-reparse-' + $liveReparseCase.Name)
    $outsideLive = Join-Path $testBase ('rollback-live-outside-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $outsideLive | Out-Null
    [IO.File]::WriteAllText((Join-Path $outsideLive 'outside.txt'), 'must-not-be-followed', [Text.UTF8Encoding]::new($false))
    $reparsePath = Join-Path $fixture.Root $liveReparseCase.Relative
    if ($liveReparseCase.ReplaceRoot) { Remove-Item -LiteralPath $reparsePath -Recurse -Force }
    New-Item -ItemType Junction -Path $reparsePath -Target $outsideLive | Out-Null
    Assert-RollbackRejectedUnchanged ('rollback-state-reject/live-' + $liveReparseCase.Name) $fixture
}

$fixture = New-RollbackTestRoot 'rollback-missing-state-backup'
$transaction = Join-Path $fixture.Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
Remove-Item -LiteralPath (Join-Path $transaction 'state.predecessor') -Recurse -Force
Assert-RollbackRejectedUnchanged 'rollback-journal-reject/missing-predecessor-state-backup' $fixture

foreach ($member in @('SyncShark.exe', 'SyncShark.Core.dll')) {
    $fixture = New-RollbackTestRoot ('rollback-reject-predecessor-' + $member.Replace('.', '-'))
    [IO.File]::AppendAllText((Join-Path $fixture.Root ('versions\0.3.20\' + $member)), 'tamper')
    Assert-RollbackRejectedUnchanged ('rollback-reject/predecessor-tree-' + $member) $fixture
}

$fixture = New-RollbackTestRoot 'rollback-state-backup-mismatch'
$transaction = Join-Path $fixture.Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
[IO.File]::AppendAllText((Join-Path $transaction 'state.predecessor\state\state.txt'), 'tamper')
Assert-RollbackRejectedUnchanged 'rollback-journal-reject/predecessor-state-backup-mismatch' $fixture

$fixture = New-RollbackTestRoot 'rollback-unexpected-successor-artifact'
$transaction = Join-Path $fixture.Root ('replacement-staging\transactions\0.3.21-' + $SourceCommit.Substring(0, 7))
New-Item -ItemType Directory -Path (Join-Path $transaction 'state.successor') | Out-Null
Assert-RollbackRejectedUnchanged 'rollback-journal-reject/unexpected-successor-state-artifact' $fixture

$escapeRoot = Join-Path $testBase ('escape-source-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $escapeRoot | Out-Null
$escapeObservation = Join-Path $testBase ('escape-' + [guid]::NewGuid().ToString('N') + '.json'); Write-Observation $escapeObservation {}
$escapeRun = Invoke-Installer 'C:\Windows' $escapeObservation
if ($escapeRun.ExitCode -eq 0) { throw 'The isolated test-root seam accepted a path outside its fixed temporary allocation.' }
$results.Add([ordered]@{ case = 'isolation-seam/reject-outside-fixed-test-root'; result = 'rejected' })

$receipt = [ordered]@{
    result = 'PASS'; source_commit = $SourceCommit
    entry_points = @('Get-SyncSharkSignatureObservation actual PE/CMS parser', 'Assert-SyncSharkSignatureObservation decision gate', 'exact packaged installer', 'exact packaged rollback')
    command_shape = 'pwsh -File Test-SyncSharkReleaseGuards.ps1 -ReleaseRoot <exact-candidate> -PredecessorRuntime <signed-0.3.20> -SourceCommit <commit> -EvidenceDirectory <bounded-output>'
    input_identity = [ordered]@{ predecessor_runtime=$predecessorRuntime; predecessor_exe_sha256=(Get-FileHash -LiteralPath (Join-Path $predecessorRuntime 'SyncShark.exe') -Algorithm SHA256).Hash.ToLowerInvariant(); package_manifest=$manifest; package_manifest_sha256=$hashes.ExpectedManifestSha256; parser_fixture_directory=$parserFixtureDirectory }
    oracle = 'actual parser flags must match each constructed PE/CMS negative; only exact permitted decision states may install; every installer and rollback rejection must preserve an identical product-root manifest'
    install_script = $install; install_sha256 = (Get-FileHash $install -Algorithm SHA256).Hash.ToLowerInvariant()
    rollback_script = $rollback; rollback_sha256 = (Get-FileHash $rollback -Algorithm SHA256).Hash.ToLowerInvariant()
    module_sha256 = $hashes.ExpectedModuleSha256; case_count = $results.Count; cases = $results
}
$receiptPath = Join-Path $evidenceDirectory 'release-guard-matrix.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$receipt | ConvertTo-Json -Depth 5
