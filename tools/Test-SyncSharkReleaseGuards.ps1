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
$module=Join-Path $PSScriptRoot 'SyncShark.Release.psm1'
$packageSigner = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SyncShark.SignerIdentity.json') -Raw | ConvertFrom-Json).roles.publisher
$pins = [ordered]@{
    Thumbprint = @($packageSigner.thumbprints)[0]
    Subject = $packageSigner.subject
    Issuer = $packageSigner.subject
    SpkiSha256 = @($packageSigner.spki)[0]
}
$results = [Collections.Generic.List[object]]::new()

function New-ObservationSet {
    $all = [ordered]@{}
    foreach ($name in @('SyncShark.exe')) {
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
foreach ($name in @('SyncShark.exe')) {
    $observation = Get-SyncSharkSignatureObservation (Join-Path $predecessorRuntime $name)
    Assert-SyncSharkSignatureObservation $observation $pins.Thumbprint $pins.Subject $pins.Issuer $pins.SpkiSha256
    $actual[$name] = $observation
}
$results.Add([ordered]@{ case = 'signature-parser/installed-predecessor'; result = 'allowed'; observations = $actual })

$parserFixtureDirectory = Join-Path $evidenceDirectory 'timestamp-parser-fixtures'
if (Test-Path -LiteralPath $parserFixtureDirectory) { throw 'Use a fresh evidence directory.' }
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
if (!$TimestampParserOnly) {
    & (Join-Path $PSScriptRoot 'Test-SyncSharkReleaseTransactions.ps1') -ReleaseRoot $ReleaseRoot -PredecessorRuntime $PredecessorRuntime -SourceCommit $SourceCommit
}
$results | ConvertTo-Json -Depth 10
