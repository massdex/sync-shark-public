Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The signer identity is the single source of truth for release trust and accepts
# a LIST per role so an old and a replacement key verify during the switch. The
# exact canonical JSON is carried verbatim here so the vendored/embedded copies of
# this module (the in-memory module in the app, the signed deploy scripts and the
# packaged scripts) resolve the same identity for either signer role without
# depending on a sibling file. tools/New-SyncSharkSignerIdentity.ps1 regenerates
# this block and the native/managed artefacts from the canonical JSON.
# BEGIN GENERATED SyncShark signer identity (tools/SyncShark.SignerIdentity.json)
$script:SyncSharkSignerIdentityJson = @'
{
  "roles": {
    "publisher": {
      "subject": "CN=Massdex, O=Massdex",
      "thumbprints": ["CB6F453160451567EC3902F2006FF43616F799CA", "0788C3CB08917F003ECE5E0CF8FCF7288249D83E"],
      "spki": ["1706cb48355e1441d7e6b389e5606ece2d84416984afee77c29eaa0b28bcc211", "4d74ee24923f5453f76aa885cacb936005f133ea5016dd1ae733c4130d0817bb"]
    },
    "package": {
      "subject": "CN=Massdex Sync Shark Code Signing v1, O=Massdex",
      "thumbprints": ["2274E4508318BECBDBDC1CC3473CD63729117BB5", "2F4762A30CFD85AEB0F3EFC253325208140DBA03"],
      "spki": ["1706cb48355e1441d7e6b389e5606ece2d84416984afee77c29eaa0b28bcc211", "e56a9bba2a0857850f0c427d2a1923aeb62b6f4339be5d54fc51fcb9c1cee6df"]
    }
  }
}
'@
# END GENERATED SyncShark signer identity

$script:SyncSharkSignerIdentity = $null
function Get-SyncSharkSignerIdentity {
    if ($null -ne $script:SyncSharkSignerIdentity) { return $script:SyncSharkSignerIdentity }
    $script:SyncSharkSignerIdentity = ($script:SyncSharkSignerIdentityJson | ConvertFrom-Json)
    $script:SyncSharkSignerIdentity
}

function Get-SyncSharkSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SyncSharkNormalizedPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-SyncSharkWithin([string]$Root, [string]$Path) {
    $rootPath = (Get-SyncSharkNormalizedPath $Root) + [IO.Path]::DirectorySeparatorChar
    $candidate = Get-SyncSharkNormalizedPath $Path
    if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the authorised Sync Shark root: $Path"
    }
}

function Assert-SyncSharkOrdinaryTree([string]$Root, [switch]$AllowMissing, [switch]$RootOnly) {
    if (-not (Test-Path -LiteralPath $Root)) {
        if ($AllowMissing) { return }
        throw "Missing tree: $Root"
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) { throw "Expected a directory: $Root" }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Mutable tree root is a reparse point: $Root"
    }
    if ($RootOnly) { return }
    $reparse = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($reparse) { throw "Mutable tree contains a reparse point: $($reparse.FullName)" }
}

function Assert-SyncSharkTransactionTree([string]$Root, [string[]]$AllowedReparsePaths) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-SyncSharkOrdinaryTree $Root -RootOnly
    $allowed = @($AllowedReparsePaths | ForEach-Object { Get-SyncSharkNormalizedPath $_ })
    $unexpected = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and
            (Get-SyncSharkNormalizedPath $_.FullName) -notin $allowed } | Select-Object -First 1
    if ($unexpected) { throw "Transaction tree contains an unexpected reparse point: $($unexpected.FullName)" }
}

function Get-SyncSharkTreeManifest([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $Root -Force -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            length = $_.Length
            sha256 = Get-SyncSharkSha256 $_.FullName
        }
    })
}

function Assert-SyncSharkTreeMatch([string]$ExpectedRoot, [string]$ActualRoot) {
    Assert-SyncSharkOrdinaryTree $ExpectedRoot
    Assert-SyncSharkOrdinaryTree $ActualRoot
    $expected = @(Get-SyncSharkTreeManifest $ExpectedRoot | ConvertTo-Json -Depth 5 -Compress)
    $actual = @(Get-SyncSharkTreeManifest $ActualRoot | ConvertTo-Json -Depth 5 -Compress)
    if (($expected -join '') -cne ($actual -join '')) { throw "Tree identity mismatch: $ActualRoot" }
}

function Get-SyncSharkTreeIdentity([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    $json = ConvertTo-Json -InputObject @(Get-SyncSharkTreeManifest $Root) -Depth 5 -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.UTF8Encoding]::new($false).GetBytes($json))).ToLowerInvariant()
}

function Get-SyncSharkJunctionTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType -cne 'Junction') { throw "Expected a junction: $Path" }
    Get-SyncSharkNormalizedPath ([string]$item.Target)
}

function Assert-SyncSharkJunction([string]$Path, [string]$Target) {
    $actual = Get-SyncSharkJunctionTarget $Path
    if ($null -eq $actual -or $actual -cne (Get-SyncSharkNormalizedPath $Target)) {
        throw "Junction target mismatch: $Path"
    }
}

function Resolve-SyncSharkProductRoot([string]$TestProductRoot) {
    if ([string]::IsNullOrWhiteSpace($TestProductRoot)) {
        $localApplicationData = [Environment]::GetFolderPath('LocalApplicationData')
        if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
            -not [IO.Path]::IsPathFullyQualified($localApplicationData)) {
            throw 'The current Windows user has no absolute LocalApplicationData folder.'
        }
        return Join-Path $localApplicationData 'Massdex\SyncShark'
    }
    if ($env:SYNC_SHARK_RELEASE_TEST_MODE -cne '1') {
        throw 'The test product-root seam is disabled.'
    }
    $testBase = Join-Path ([IO.Path]::GetTempPath()) 'sync-shark-release-tests'
    $base = (Get-SyncSharkNormalizedPath $testBase) + [IO.Path]::DirectorySeparatorChar
    $resolved = Get-SyncSharkNormalizedPath $TestProductRoot
    if (-not $resolved.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The test product root must be a child of the fixed Sync Shark release-test root.'
    }
    $resolved
}

function Resolve-SyncSharkRuntimeRoot([string]$Version,[string]$TestProductRoot='') {
    if($Version -cnotmatch '^\d+\.\d+\.\d+$'){throw 'An exact runtime version is required.'}
    if($TestProductRoot){return Join-Path (Resolve-SyncSharkProductRoot $TestProductRoot) ('versions\'+$Version)}
    Join-Path 'C:\Program Files\Massdex\SyncShark\versions' $Version
}

function Assert-SyncSharkProtectedRuntime([string]$Runtime) {
    $path=Get-SyncSharkNormalizedPath $Runtime
    if($path -ine (Resolve-SyncSharkRuntimeRoot ([IO.Path]::GetFileName($path)))){throw 'Protected runtime path changed.'}
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    function ProtectedAcl([string]$Item,[bool]$VolumeRoot=$false) {
        $entry=Get-Item -LiteralPath $Item -Force
        if(($entry.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0){throw 'Protected code contains a reparse or offline item.'}
        $acl=Get-Acl -LiteralPath $Item
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Protected code has an ordinary-user owner.'}
        $mask=0x500d0116L;if($entry.PSIsContainer){$mask=0x500d0156L};if($VolumeRoot){$mask=0x500d0040L}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if($rule.AccessControlType -eq 'Allow' -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
                ([long]$rule.FileSystemRights -band $mask) -ne 0 -and $rule.IdentityReference.Value -notin $trusted){throw 'Protected code permits ordinary-user replacement.'}
        }
    }
    for($directory=[IO.DirectoryInfo]$path;$null -ne $directory;$directory=$directory.Parent){ProtectedAcl $directory.FullName ($null -eq $directory.Parent)}
    Assert-SyncSharkOrdinaryTree $path
    foreach($item in Get-ChildItem -LiteralPath $path -Recurse -Force){ProtectedAcl $item.FullName}
}

function Invoke-SyncSharkProtectedRuntime {
    param([string]$SourceRuntime,[ValidateSet('RuntimeInstall','RuntimeRemove','RuntimeRollback')][string]$Operation,[switch]$RestoringUninstall)
    $source=Get-SyncSharkNormalizedPath $SourceRuntime;$version=[IO.Path]::GetFileName($source)
    $root=Resolve-SyncSharkProductRoot ''
    if($version -cnotmatch '^\d+\.\d+\.\d+$' -or $source -ine (Join-Path $root ('versions\'+$version))){throw 'The protected setup source is not the exact retained version.'}
    Assert-SyncSharkOrdinaryTree $source
    if($Operation -ceq 'RuntimeInstall') {
        $revision=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'SyncShark.exe')).ProductVersion
        if($revision -cnotmatch ('^'+[regex]::Escape($version)+'\+([0-9a-f]{40})$')){throw 'Frozen runtime revision required.'}
        $retained=Join-Path $root ('replacement-staging\transactions\'+$version+'-'+$Matches[1].Substring(0,7)+'\Install-SyncShark.ps1')
        $update=Read-SyncSharkUpdaterRelease $retained $revision
        if($null -ne $update) {
            $fixedWorker='C:\Program Files\Massdex\SyncShark\SyncSharkUpdater.exe'
            $service=New-Object -ComObject 'Schedule.Service';$folder=$null;$task=$null
            try {
                $service.Connect();$folder=$service.GetFolder('\')
                try {$task=$folder.GetTask('SyncSharkUpdater')} catch {
                    $failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException}
                    if($failure.HResult -ne -2147024894){throw}
                }
                $route=Get-SyncSharkUpdaterInvocationRoute $task $update $fixedWorker $RestoringUninstall.IsPresent
                if($route -ceq 'routine') {
                    Invoke-SyncSharkUnattendedRuntime $source $retained $update $task
                    return
                }
                # First enrollment or an exact partial enrollment reaches the
                # attended repair boundary. A failed routine invocation never
                # falls back to elevation. Protected setup revalidates all bytes.
            } finally {
                foreach($com in @($task,$folder,$service)){if($null -ne $com){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)}}
            }
        }
    }
    $helper=Join-Path $source 'SyncSharkDiscovery.exe'
    $held=[IO.File]::Open($helper,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $helper) `
            @($publisher.thumbprints) $publisher.subject $publisher.subject `
            @($publisher.spki)
        # This is the per-user bootstrap copy, not the protected destination.
        # Staging moves retain an owner-only ACL; Windows consent must be able
        # to read its embedded icon before the elevated helper is started.
        # Use the existing public-runtime-only SYSTEM RX boundary for every
        # install/remove/rollback caller; never broaden private state access.
        Set-SyncSharkRuntimeSystemReadAccess $source
        # The helper authenticates the fixed retained signed installer as data;
        # no caller-supplied path/script/hash can authorize an elevated effect.
        $process=Start-Process -FilePath $helper -ArgumentList @($Operation,[string]$PID) -Verb RunAs -WindowStyle Hidden -PassThru
        try {
            if(-not $process.WaitForExit(240000)){throw 'Protected setup has not completed. Preserve its exact state; do not replay an unresolved operation.'}
            if($process.ExitCode -ne 0){throw ('Protected setup failed with code 0x'+$process.ExitCode.ToString('X8')+'. Existing state is retained for recovery.')}
        } finally {$process.Dispose()}
    } finally {$held.Dispose()}
}

function Get-SyncSharkUpdaterInvocationRoute([object]$Task,[object]$Contract,[string]$Worker,[bool]$RestoringUninstall=$false) {
    if($Worker -ine 'C:\Program Files\Massdex\SyncShark\SyncSharkUpdater.exe'){throw 'Unexpected fixed updater path.'}
    $present=Test-Path -LiteralPath $Worker
    if($null -ne $Task){Assert-SyncSharkUpdaterTaskForInvocation $Task ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)}
    if($present) {
        $item=Get-Item -LiteralPath $Worker -Force
        if($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -or
            $item.Length -ne $Contract.protectedUpdater.workerBytes -or (Get-SyncSharkSha256 $Worker) -cne $Contract.protectedUpdater.workerSha256){throw 'Foreign updater bytes cannot request repair.'}
        $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $Worker) `
            @($publisher.thumbprints) $publisher.subject $publisher.subject `
            @($publisher.spki)
    }
    $journal='C:\Program Files\Massdex\SyncShark\discovery-setup.json'
    $enrollment=$false
    foreach($record in @($journal,($journal+'.new'))) {
        if(-not (Test-Path -LiteralPath $record)){continue}
        $item=Get-Item -LiteralPath $record -Force
        if($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -or $item.Length -gt 65536){throw 'Invalid protected lifecycle journal.'}
        $state=Get-Content -LiteralPath $record -Raw|ConvertFrom-Json
        # Routing only: the signed attended helper independently validates the
        # closed journal identity, exact hashes, source and current transaction.
        $enrollment=$enrollment -or $state.Mode -ceq 'EnrollUpdater'
    }
    # Undo is an existing attended operation, not a new install transaction.
    # Its switch selects the route only: protected setup still verifies the
    # exact uninstall journal, retained runtime and held inactive pointer.
    if($null -ne $Task -and $present -and -not $enrollment -and -not $RestoringUninstall){return 'routine'}
    return 'attended'
}

function Read-SyncSharkUpdaterRelease([string]$Installer,[string]$ProductVersion) {
    if((Get-Item -LiteralPath $Installer).Length -gt 1MB){throw 'Oversized signed updater contract.'}
    $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
    Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $Installer) `
        @($publisher.thumbprints) $publisher.subject $publisher.subject `
        @($publisher.spki)
    $header=Get-Content -LiteralPath $Installer -TotalCount 1
    $prefix='# SyncSharkUpdateContract: '
    if(-not $header.StartsWith($prefix,[StringComparison]::Ordinal) -or $header.Length -gt 16384){throw 'Missing signed updater contract.'}
    $contract=$header.Substring($prefix.Length)|ConvertFrom-Json -Depth 12
    if($contract.product -cne 'Massdex.SyncShark' -or ($contract.version+'+'+$contract.sourceRevision) -cne $ProductVersion){throw 'Signed updater release identity changed.'}
    if($null -eq $contract.PSObject.Properties['protectedUpdater']){return $null}
    $update=$contract.protectedUpdater
    if($update.schemaVersion -ne 1 -or $update.userSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
        $update.workerSha256 -cnotmatch '^[0-9a-f]{64}$' -or $update.workerBytes -lt 1 -or $update.workerBytes -gt 16MB -or
        $update.previousTreeSha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid signed updater bindings.'}
    return $contract
}

function Assert-SyncSharkUpdaterTaskForInvocation([object]$Task,[string]$UserSid) {
    # Caller-side checks prevent invoking a foreign task. The protected worker
    # independently validates its full policy, ACL and contract before any effect.
    $text=[string]$Task.Xml
    if($text.Length -gt 32768){throw 'Oversized updater task.'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create([IO.StringReader]::new($text),$settings)
    try {$xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null;$xml.PreserveWhitespace=$true;$xml.Load($reader)}finally{$reader.Dispose()}
    $ns=[Xml.XmlNamespaceManager]::new($xml.NameTable);$ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    function Value([string]$XPath) {
        $nodes=$xml.SelectNodes($XPath,$ns)
        if($nodes.Count -ne 1 -or $nodes[0].SelectNodes('*').Count){throw 'Ambiguous updater task value.'}
        $nodes[0].InnerText
    }
    $principal=Value '/t:Task/t:Principals/t:Principal/t:UserId'
    if($principal -notlike 'S-1-*'){$principal=([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value}
    if($principal -cne $UserSid -or (Value '/t:Task/t:Principals/t:Principal/t:LogonType') -cne 'InteractiveToken' -or
        (Value '/t:Task/t:Principals/t:Principal/t:RunLevel') -cne 'HighestAvailable' -or
        $xml.SelectNodes('/t:Task/t:Actions/*',$ns).Count -ne 1 -or
        (Value '/t:Task/t:Actions/t:Exec/t:Command') -ine 'C:\Program Files\Massdex\SyncShark\SyncSharkUpdater.exe' -or
        (Value '/t:Task/t:Actions/t:Exec/t:WorkingDirectory') -ine 'C:\Program Files\Massdex\SyncShark' -or
        $xml.SelectNodes('/t:Task/t:Actions/t:Exec/t:Arguments[string-length(.) != 0]',$ns).Count -ne 0 -or
        $xml.SelectNodes('/t:Task/t:Triggers/*',$ns).Count -ne 0 -or (Value '/t:Task/t:Settings/t:Enabled') -cne 'true'){
        throw 'Updater task identity, arguments or availability changed.'
    }
    $security=[Security.AccessControl.RawSecurityDescriptor]::new([string]$Task.GetSecurityDescriptor(7))
    if($security.Owner.Value -cne 'S-1-5-32-544' -or $security.Group.Value -cne 'S-1-5-32-544' -or
        ($security.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq 0 -or
        $security.DiscretionaryAcl.Count -ne 3){throw 'Updater task protection changed.'}
    $seen=@{}
    foreach($ace in $security.DiscretionaryAcl) {
        $sid=$ace.SecurityIdentifier.Value
        if($seen.ContainsKey($sid) -or $ace.AceQualifier -ne 'AccessAllowed' -or $ace.AceFlags -ne 'None' -or $ace.IsCallback){throw 'Unexpected updater task permission.'}
        $seen[$sid]=$true
        if($sid -cin @('S-1-5-18','S-1-5-32-544')){if($ace.AccessMask -notin @(268435456,2032127)){throw 'Administrative updater task permission changed.'}}
        elseif($sid -cne $UserSid -or $ace.AccessMask -notin @(-1610612736,1179817)){throw 'Updater task is not user read/execute only.'}
    }
}

function Write-SyncSharkPendingSelector([string]$Pending,[string]$Selector) {
    if([IO.Path]::GetFileName($Pending) -cne 'pending-update.json'){throw 'Unexpected pending selector destination.'}
    Assert-SyncSharkOrdinaryTree (Split-Path $Pending -Parent) -RootOnly
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($Selector)
    if($bytes.Length -lt 1 -or $bytes.Length -gt 1024){throw 'Invalid pending selector length.'}
    $temporary=$Pending+'.new'
    $matches=@{}
    foreach($path in @($Pending,$temporary)) {
        $matches[$path]=$false
        if(Test-Path -LiteralPath $path) {
            $item=Get-Item -LiteralPath $path -Force
            if($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline))){throw 'Unsafe pending selector object; preserved.'}
            $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                if($stream.Length -gt 1024){throw 'Oversized pending selector; preserved.'}
                $actual=[byte[]]::new([int]$stream.Length);$offset=0
                while($offset -lt $actual.Length){$read=$stream.Read($actual,$offset,$actual.Length-$offset);if($read -eq 0){throw 'Pending selector truncated.'};$offset+=$read}
                $matches[$path]=[Convert]::ToBase64String($actual) -ceq [Convert]::ToBase64String($bytes)
            }finally{$stream.Dispose()}
        }
    }
    $staged=Test-Path -LiteralPath $temporary
    if($staged -and -not $matches[$temporary]){throw 'Pending selector staging differs from this exact request; preserved.'}
    if($matches[$Pending]) {
        if($staged){[IO.File]::Delete($temporary)}
        return
    }
    if(-not $staged) {
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    }
    if(Test-Path -LiteralPath $Pending){[IO.File]::Replace($temporary,$Pending,[NullString]::Value)}else{[IO.File]::Move($temporary,$Pending)}
}

function Invoke-SyncSharkUnattendedRuntime([string]$Source,[string]$Installer,[object]$Contract,[object]$Task) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'The user installation transaction must remain unelevated.'
    }
    $root=Resolve-SyncSharkProductRoot '';$worker='C:\Program Files\Massdex\SyncShark\SyncSharkUpdater.exe'
    Assert-SyncSharkProtectedRuntime (Resolve-SyncSharkRuntimeRoot $Contract.previousVersion.Split('+')[0])
    Assert-SyncSharkUpdaterTaskForInvocation $Task $identity.User.Value
    $held=[IO.File]::Open($worker,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $running=$null
    try {
        if($held.Length -ne $Contract.protectedUpdater.workerBytes -or (Get-SyncSharkSha256 $worker) -cne $Contract.protectedUpdater.workerSha256){throw 'Accepted updater executable changed.'}
        $publisher=(Get-SyncSharkSignerIdentity).roles.publisher
        Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $worker) `
            @($publisher.thumbprints) $publisher.subject $publisher.subject `
            @($publisher.spki)
        $instances=$Task.GetInstances(0)
        try {if($instances.Count -ne 0){throw 'The updater is already running. Preserve the current transaction; do not replay it.'}}
        finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($instances)}
        $pending=Join-Path $root 'replacement-staging\pending-update.json'
        Assert-SyncSharkOrdinaryTree (Split-Path $pending -Parent) -RootOnly
        if(Test-Path -LiteralPath $pending){$item=Get-Item -LiteralPath $pending -Force;if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid pending update selector.'}}
        $selector=[ordered]@{schemaVersion=1;version=$Contract.version;sourceRevision=$Contract.sourceRevision;installerSha256=(Get-SyncSharkSha256 $Installer)}|ConvertTo-Json -Compress
        Write-SyncSharkPendingSelector $pending $selector
        Assert-SyncSharkUpdaterTaskForInvocation $Task $identity.User.Value
        $start=[DateTime]::Now;$running=$Task.Run($null)
        $instance=[string]$running.InstanceGuid
        if(-not $instance){throw 'The fixed updater returned no invocation identity; do not replay.'}
        $deadline=[DateTime]::UtcNow.AddMinutes(11)
        while($true) {
            $instances=$Task.GetInstances(0);$active=$false
            try {foreach($item in $instances){try {if([string]$item.InstanceGuid -ceq $instance){$active=$true}}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)}}}
            finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($instances)}
            if(-not $active){break}
            if([DateTime]::UtcNow -ge $deadline){throw 'Protected publication is still unresolved. It was not stopped or retried; keep the transaction for reconciliation.'}
            Start-Sleep -Milliseconds 250
        }
        if($Task.LastRunTime -lt $start.AddSeconds(-1) -or $Task.LastTaskResult -ne 0){throw ('Protected publication failed or has no correlated result: '+$Task.LastTaskResult+'. No user-state switch was performed.')}
        Assert-SyncSharkUpdaterTaskForInvocation $Task $identity.User.Value
        $target=Resolve-SyncSharkRuntimeRoot $Contract.version
        Assert-SyncSharkProtectedRuntime $target;Assert-SyncSharkTreeMatch $Source $target
        $receiptPath='C:\Program Files\Massdex\SyncShark\updater-publication.json'
        $receiptItem=Get-Item -LiteralPath $receiptPath -Force
        if($receiptItem.Length -gt 4096 -or ($receiptItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid protected publication readback.'}
        $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json -Depth 5
        if($receipt.schemaVersion -ne 1 -or $receipt.product -cne 'Massdex.SyncShark' -or
            $receipt.version -cne ($Contract.version+'+'+$Contract.sourceRevision) -or
            $receipt.installerSha256 -cne (Get-SyncSharkSha256 $Installer) -or
            $receipt.previousTreeSha256 -cne $Contract.protectedUpdater.previousTreeSha256 -or
            $receipt.publishedTreeSha256 -cne (Get-SyncSharkTreeIdentity $target)){throw 'Publication evidence does not match the authenticated release.'}
        if($Contract.protectedUpdater.discovery.enabled) {
            foreach($entry in @(@('SyncSharkDiscovery.exe','afterWorkerSha256'),@('discovery-manifest.json','afterManifestSha256'))){
                if((Get-SyncSharkSha256 (Join-Path 'C:\Program Files\Massdex\SyncShark' $entry[0])) -cne $Contract.protectedUpdater.discovery.($entry[1])){throw 'Discovery publication differs from its signed postimage.'}
            }
        }
    } finally {
        if($null -ne $running){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($running)}
        $held.Dispose()
    }
}

function Read-SyncSharkJournal([string]$Path, [string]$Operation, [string]$Version,
    [string]$Commit, [string]$ProductRoot, [string[]]$AllowedSteps) {
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary) {
        $temporaryItem = Get-Item -LiteralPath $temporary -Force
        if ($temporaryItem.PSIsContainer -or
            ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The interrupted journal write is not an ordinary temporary file.'
        }
        # The committed journal remains the sole durable boundary. A surviving
        # .new file is either partial or an uncommitted complete successor and
        # can always be discarded before replaying the idempotent pending step.
        Remove-Item -LiteralPath $temporary -Force
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { $journal = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 20 }
    catch { throw 'The deployment journal is truncated or corrupt.' }
    if ($journal.schema -ne 1 -or $journal.operation -cne $Operation -or
        $journal.version -cne $Version -or $journal.commit -cne $Commit -or
        (Get-SyncSharkNormalizedPath ([string]$journal.product_root)) -cne
            (Get-SyncSharkNormalizedPath $ProductRoot)) {
        throw 'The deployment journal identity or schema does not match this transaction.'
    }
    $completed = @($journal.completed)
    if (@($completed | Where-Object { $_ -notin $AllowedSteps }).Count -ne 0 -or
        ($journal.pending -and $journal.pending -notin $AllowedSteps) -or
        ($journal.status -notin @('active', 'complete'))) {
        throw 'The deployment journal contains an unknown phase or status.'
    }
    if ($completed.Count -ne @($completed | Select-Object -Unique).Count) {
        throw 'The deployment journal repeats a completed phase.'
    }
    $highest = -1
    foreach ($step in $completed) {
        $index = [Array]::IndexOf($AllowedSteps, [string]$step)
        if ($index -lt $highest) { throw 'The deployment journal phase order is contradictory.' }
        $highest = $index
    }
    if ($journal.pending) {
        $pendingIndex = [Array]::IndexOf($AllowedSteps, [string]$journal.pending)
        if ($pendingIndex -ne ($highest + 1)) { throw 'The pending journal phase contradicts completed history.' }
    }
    elseif ($journal.status -ceq 'active' -and $highest -ge 0 -and $highest -lt ($AllowedSteps.Count - 1)) {
        # A clean boundary between phases is valid; the next invocation continues at highest + 1.
    }
    if ($journal.status -ceq 'complete' -and $highest -ne ($AllowedSteps.Count - 1)) {
        throw 'A complete journal is missing one or more required phases.'
    }
    $journal
}

function Write-SyncSharkJournal([string]$Path, [object]$Journal,
    [string]$CrashAfter = '', [string]$CrashPrefix = 'journal') {
    $parent = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary) { throw 'A prior atomic journal write was not reconciled.' }
    $json = $Journal | ConvertTo-Json -Depth 20
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $firstLength = [Math]::Max(1, [int][Math]::Floor($bytes.Length / 2))
        $stream.Write($bytes, 0, $firstLength)
        $stream.Flush($true)
        Invoke-SyncSharkCrashPoint "$CrashPrefix-mid-write" $CrashAfter
        if ($firstLength -lt $bytes.Length) {
            $stream.Write($bytes, $firstLength, $bytes.Length - $firstLength)
        }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    Invoke-SyncSharkCrashPoint "$CrashPrefix-after-temp-write" $CrashAfter
    [IO.File]::Move($temporary, $Path, $true)
}

function Copy-SyncSharkTreeToStaging([string]$Source, [string]$Destination,
    [string]$CrashAfter = '', [string]$CrashPoint = '') {
    Assert-SyncSharkOrdinaryTree $Source
    if (Test-Path -LiteralPath $Destination) { throw "Staging destination already exists: $Destination" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($directory in @(Get-ChildItem -LiteralPath $Source -Force -Directory -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($Source, $directory.FullName)
        New-Item -ItemType Directory -Path (Join-Path $Destination $relative) -Force | Out-Null
    }
    $crashReached = $false
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -Force -File -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $buffer = [byte[]]::new(65536)
            $read = $input.Read($buffer, 0, $buffer.Length)
            if ($read -gt 0) {
                $firstLength = [Math]::Max(1, [int][Math]::Floor($read / 2))
                $output.Write($buffer, 0, $firstLength)
                $output.Flush($true)
                if (-not $crashReached -and $CrashPoint) {
                    $crashReached = $true
                    Invoke-SyncSharkCrashPoint $CrashPoint $CrashAfter
                }
                if ($firstLength -lt $read) { $output.Write($buffer, $firstLength, $read - $firstLength) }
            }
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) { $output.Write($buffer, 0, $read) }
            $output.Flush($true)
        } finally { $output.Dispose(); $input.Dispose() }
    }
    if (-not $crashReached -and $CrashPoint) { Invoke-SyncSharkCrashPoint $CrashPoint $CrashAfter }
    Assert-SyncSharkTreeMatch $Source $Destination
}

function Assert-SyncSharkRuntimeSystemReadAccess([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'SyncShark.exe') -PathType Leaf)) {
        throw 'Windows icon access applies only to a verified app runtime, never product state.'
    }
    $required = [int][Security.AccessControl.FileSystemRights]::ReadAndExecute
    foreach ($item in @((Get-Item -LiteralPath $Root)) + @(Get-ChildItem -LiteralPath $Root -Recurse -Force)) {
        $allowed = 0
        foreach ($rule in (Get-Acl -LiteralPath $item.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.IdentityReference.Value -cne 'S-1-5-18') { continue }
            if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
            $rights = [int]$rule.FileSystemRights
            if ($rule.AccessControlType -eq 'Deny' -and ($rights -band $required)) {
                throw 'An existing SYSTEM deny prevents Windows from reading the runtime icon.'
            }
            if ($rule.AccessControlType -eq 'Allow') { $allowed = $allowed -bor $rights }
        }
        if (($allowed -band $required) -ne $required) { throw 'Windows SYSTEM cannot read the installed runtime resources.' }
    }
}

function Set-SyncSharkRuntimeSystemReadAccess([string]$Root) {
    Assert-SyncSharkOrdinaryTree $Root
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'SyncShark.exe') -PathType Leaf)) {
        throw 'Windows icon access applies only to a verified app runtime, never product state.'
    }
    # A same-volume move preserves private staging ACLs. Windows consent runs
    # outside the user's token and must read the *published public binaries*.
    # Add read/execute for SYSTEM only. Do not reset ACLs, grant other users or
    # give SYSTEM new write rights; state/recovery/credential directories stay private.
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $directory = [IO.DirectoryInfo]::new($Root)
    $security = [IO.FileSystemAclExtensions]::GetAccessControl($directory,
        [Security.AccessControl.AccessControlSections]::Access)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    [IO.FileSystemAclExtensions]::SetAccessControl($directory, $security)
    Assert-SyncSharkRuntimeSystemReadAccess $Root
}

function Invoke-SyncSharkCrashPoint([string]$Name, [string]$CrashAfter) {
    if ($CrashAfter -ceq $Name) {
        [Diagnostics.Process]::GetCurrentProcess().Kill()
        Start-Sleep -Seconds 60
    }
}

function Invoke-SyncSharkJournalStep([object]$Journal, [string]$JournalPath, [string]$Step,
    [string]$CrashAfter, [scriptblock]$Effect, [scriptblock]$Verify) {
    if (@($Journal.completed) -contains $Step) {
        # A later phase is allowed to consume or replace an earlier phase's
        # intermediate artifact. Re-verify only the latest durable boundary;
        # when a later phase is pending, that phase's idempotent effect and
        # verifier perform reconciliation. A complete transaction reaches its
        # aggregate final verifier through the last completed step.
        $stepIndex = [Array]::IndexOf([string[]]$Journal.allowed_steps, $Step)
        $highestCompleted = -1
        foreach ($completedStep in @($Journal.completed)) {
            $completedIndex = [Array]::IndexOf([string[]]$Journal.allowed_steps, [string]$completedStep)
            if ($completedIndex -gt $highestCompleted) { $highestCompleted = $completedIndex }
        }
        if (-not $Journal.pending -and $stepIndex -eq $highestCompleted) { & $Verify }
        return
    }
    if ($Journal.pending -and $Journal.pending -cne $Step) {
        throw "Another deployment phase is pending: $($Journal.pending)"
    }
    $Journal.pending = $Step
    Write-SyncSharkJournal $JournalPath $Journal $CrashAfter "$Step-pending-journal"
    Invoke-SyncSharkCrashPoint "$Step-after-pending" $CrashAfter
    & $Effect
    Invoke-SyncSharkCrashPoint "$Step-after-effect" $CrashAfter
    & $Verify
    $Journal.completed = @($Journal.completed) + $Step
    $Journal.pending = $null
    if ($Step -ceq $Journal.allowed_steps[-1]) { $Journal.status = 'complete' }
    Write-SyncSharkJournal $JournalPath $Journal $CrashAfter "$Step-complete-journal"
    Invoke-SyncSharkCrashPoint "$Step-after-journal" $CrashAfter
}

function Assert-SyncSharkExplorerReplacement {
    param([object[]]$Existing,[string]$Runtime,[string]$ExpectedPreviousRuntime='')
    $version=[IO.Path]::GetFileName($Runtime)
    if($Existing.Count -gt 1 -or @($Existing | Where-Object {[string]$_.Version -ceq ($version+'.0')}).Count) {
        throw 'The existing package at this version failed exact identity verification. Nothing was changed.'
    }
    if(@($Existing | Where-Object {$_.Publisher -cne 'CN=Massdex Sync Shark Code Signing v1, O=Massdex'}).Count) {
        throw 'Another publisher owns the Explorer package identity. Nothing was changed.'
    }
    if($Existing.Count -eq 1) {
        if(-not $ExpectedPreviousRuntime){throw 'An existing Explorer version requires the exact signed installer predecessor. Nothing was changed.'}
        $previous=Get-SyncSharkNormalizedPath $ExpectedPreviousRuntime
        $previousVersion=[IO.Path]::GetFileName($previous)
        if($previousVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
            $previous -ine (Resolve-SyncSharkRuntimeRoot $previousVersion) -or $previous -ieq $Runtime -or
            [string]$Existing[0].Version -cne ($previousVersion+'.0')) {
            throw 'The existing Explorer package is not the exact admitted predecessor. Nothing was changed.'
        }
        # This read-only installed verifier checks the real WinRT external root,
        # publisher, protected payload and package bytes before Add-AppxPackage.
        Invoke-SyncSharkExplorerPackage $previous Verify
    }
}

# The installed application's own state\instance.lock is the release transaction's
# native exclusion object. The application holds it ReadWrite/ShareNone while it
# runs, so a maintenance read lease both proves that no application instance is
# live and refuses every later application start for the whole transaction. Read
# sharing keeps byte-exact backup and hashing of the non-authorizing process-id
# file possible, and does not infer quiescence from process enumeration.
$script:SyncSharkMaintenanceLease = $null

function Resolve-SyncSharkMaintenanceLeaseRoot([string]$ProductRoot) {
    if ([string]::IsNullOrWhiteSpace($ProductRoot)) { throw 'An exact product root is required for release maintenance.' }
    $resolved = Get-SyncSharkNormalizedPath $ProductRoot
    if ($resolved -ieq (Get-SyncSharkNormalizedPath (Resolve-SyncSharkProductRoot ''))) { return $resolved }
    # Any other root must be the exact isolated release-test root, which the
    # existing seam resolves or refuses; no caller-supplied substitute root.
    Resolve-SyncSharkProductRoot $resolved
}

function Get-SyncSharkLiveMaintenanceLease([string]$ProductRoot) {
    # The exact live module-owned lease for this same resolved product root, or
    # $null. Only the maintenance wrapper creates it, in module-private scope.
    $lease = $script:SyncSharkMaintenanceLease
    if ($null -eq $lease) { return $null }
    $resolved = Get-SyncSharkNormalizedPath $ProductRoot
    if ($lease.Root -ine $resolved) { return $null }
    if ($null -eq $lease.Stream -or $lease.Stream.SafeFileHandle.IsClosed -or -not $lease.Stream.CanRead) {
        throw 'The held maintenance lease is no longer readable. Nothing was changed.'
    }
    if ((Get-SyncSharkNormalizedPath ([string]$lease.LockPath)) -cne
        (Get-SyncSharkNormalizedPath (Join-Path $resolved 'state\instance.lock'))) {
        throw 'The held maintenance lease is bound to a different application state lock.'
    }
    $lease
}

function Open-SyncSharkInstanceExclusion([string]$ProductRoot) {
    # Reuse ONLY the exact live maintenance lease for this same resolved product
    # root. Otherwise take the current independent exclusive lock. A reused lease
    # stays owned by its wrapper scope and is never disposed here.
    $resolved = Get-SyncSharkNormalizedPath $ProductRoot
    $lease = Get-SyncSharkLiveMaintenanceLease $resolved
    if ($null -ne $lease) { return [pscustomobject]@{ Stream = $lease.Stream; Owned = $false } }
    [pscustomobject]@{ Owned = $true; Stream = [IO.File]::Open((Join-Path $resolved 'state\instance.lock'),
        [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
}

function Invoke-SyncSharkWithInstanceMaintenanceLease {
    param([string]$ProductRoot,[scriptblock]$Body)
    if ($null -eq $Body) { throw 'A release transaction body is required.' }
    $root = Resolve-SyncSharkMaintenanceLeaseRoot $ProductRoot
    Assert-SyncSharkOrdinaryTree $root -RootOnly
    $state = Join-Path $root 'state'
    Assert-SyncSharkOrdinaryTree $state -RootOnly
    if ($null -ne $script:SyncSharkMaintenanceLease) {
        throw 'A release maintenance lease is already held by this session; nested acquisition is refused.'
    }
    $lock = Join-Path $state 'instance.lock'
    $item = Get-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { throw 'The installed application state lock is missing. Nothing was changed.' }
    if ($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline))) {
        throw 'The installed application state lock is not an ordinary local file. Nothing was changed.'
    }
    $stream = [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if (-not $stream.CanRead -or $stream.SafeFileHandle.IsClosed) {
        $stream.Dispose()
        throw 'The installed application state lock could not be held for maintenance. Nothing was changed.'
    }
    $previous = $script:SyncSharkMaintenanceLease
    $script:SyncSharkMaintenanceLease = [pscustomobject]@{ Root = $root; LockPath = (Get-SyncSharkNormalizedPath $lock); Stream = $stream }
    try { & $Body }
    finally {
        $script:SyncSharkMaintenanceLease = $previous
        $stream.Dispose()
    }
}

function Invoke-SyncSharkExplorerPackage {
    param([string]$Runtime,[ValidateSet('Register','Remove','Verify','VerifyAbsent')][string]$Operation,
        [switch]$ImmediatePredecessor,[string]$ExpectedPreviousRuntime='')
    $root=Resolve-SyncSharkProductRoot ''
    $path=Get-SyncSharkNormalizedPath $Runtime
    $version=[IO.Path]::GetFileName($path)
    if($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $path -ine (Resolve-SyncSharkRuntimeRoot $version)) {
        throw 'Explorer lifecycle requires one exact installed product runtime.'
    }
    Assert-SyncSharkOrdinaryTree $path
    Assert-SyncSharkProtectedRuntime $path
    # Only the exact frozen predecessor may retain the old executable signer.
    # The MSIX container role is separate and does not grant executable trust.
    $coreRole=(Get-SyncSharkSignerIdentity).roles.publisher
    $coreThumb=@($coreRole.thumbprints)
    $coreSubject=$coreRole.subject
    if($ImmediatePredecessor) {
        throw 'The writable transition predecessor must not regain packaged COM registration.'
    }
    if($Operation -in @('Register','Remove')) {Assert-SyncSharkJunction (Join-Path $root 'active') $path}
    if($Operation -eq 'Register') {
        # Updating from the packaged app itself pins its old package in use.
        # Reconcile an already-completed effect first, otherwise let Windows'
        # existing per-user deployment cmdlet run from this un-packaged installer.
        try { Invoke-SyncSharkExplorerPackage $path Verify -ImmediatePredecessor:$ImmediatePredecessor; return }
        catch { } # A nonmatching registration must pass all input guards below.
        if(@(Get-Process -Name SyncShark -ErrorAction SilentlyContinue).Count) {
            throw 'Sync Shark must complete its safe Exit checkpoint before Explorer registration.'
        }
        # Reuse the live wrapper lease for this same resolved product root. A
        # caller outside the maintenance wrapper keeps the exclusive lock; a
        # reused lease is never disposed by this function.
        $exclusion=Open-SyncSharkInstanceExclusion $root
        $heldPackage=$null
        $heldRuntime=$null
        try {
            $corePath=Join-Path $path 'SyncShark.Core.dll'
            Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $corePath) `
                $coreThumb $coreSubject $coreSubject `
                @($coreRole.spki)
            if(-not ('Massdex.SyncShark.PilotDirectoryLease' -as [type])) {Add-Type -Path $corePath}
            $heldRuntime=[Massdex.SyncShark.PilotDirectoryLease]::OpenInstalledRuntime($path)
            $packagePath=Join-Path $path 'SyncShark.msix'
            $heldPackage=[IO.File]::Open($packagePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $packageRole=(Get-SyncSharkSignerIdentity).roles.package
            Assert-SyncSharkSignatureObservation (Get-SyncSharkSignatureObservation $packagePath) `
                @($packageRole.thumbprints) `
                $packageRole.subject `
                $packageRole.subject `
                @($packageRole.spki)
            $zip=[IO.Compression.ZipArchive]::new($heldPackage,[IO.Compression.ZipArchiveMode]::Read,$true)
            try {
                $entry=$zip.GetEntry('AppxManifest.xml')
                if($null -eq $entry -or $entry.Length -gt 65536){throw 'Explorer package manifest is missing or oversized.'}
                $xmlSettings=[Xml.XmlReaderSettings]::new()
                $xmlSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
                $xmlSettings.XmlResolver=$null;$xmlSettings.MaxCharactersInDocument=65536
                $input=$entry.Open();$reader=[Xml.XmlReader]::Create($input,$xmlSettings)
                try {
                    $document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)
                    $identity=$document.DocumentElement.SelectSingleNode('*[local-name()="Identity"]')
                    if($null -eq $identity -or $identity.GetAttribute('Name') -cne 'Massdex.SyncShark' -or
                        $identity.GetAttribute('Publisher') -cne 'CN=Massdex Sync Shark Code Signing v1, O=Massdex' -or
                        $identity.GetAttribute('Version') -cne ($version+'.0') -or
                        $identity.GetAttribute('ProcessorArchitecture') -cne 'x64') {
                        throw 'Explorer package identity does not match the exact installed product.'
                    }
                } finally {$reader.Dispose();$input.Dispose()}
            } finally {$zip.Dispose()}
            if($PSVersionTable.PSEdition -eq 'Core'){Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue}
            $existing=@(Get-AppxPackage -Name Massdex.SyncShark -ErrorAction Stop)
            Assert-SyncSharkExplorerReplacement $existing $path $ExpectedPreviousRuntime
            # No forced shutdown, deferred success, all-users, unsigned or trust option.
            $heldRuntime.VerifyBinding()
            Add-AppxPackage -Path $packagePath -ExternalLocation $path -ErrorAction Stop
            $heldRuntime.VerifyBinding()
        } finally {
            if($null -ne $heldPackage){$heldPackage.Dispose()}
            if($null -ne $heldRuntime){$heldRuntime.Dispose()}
            if($exclusion.Owned){$exclusion.Stream.Dispose()}
        }
        Invoke-SyncSharkExplorerPackage $path Verify -ImmediatePredecessor:$ImmediatePredecessor
        return
    }
    $argument=@{Register='--register-explorer-package';Remove='--remove-explorer-package';Verify='--check-explorer-package';VerifyAbsent='--check-no-explorer-package'}[$Operation]
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $path 'SyncShark.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardError=$true
    [void]$start.ArgumentList.Add($argument)
    $process=[Diagnostics.Process]::Start($start)
    try {
        $errors=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(120000)) {throw 'Windows Explorer package operation did not finish; its state remains unverified. Do not switch the active version.'}
        if($process.ExitCode -ne 0){throw ('Explorer package operation failed: '+$errors.GetAwaiter().GetResult().Trim())}
    } finally {$process.Dispose()}
}

function Initialize-SyncSharkSignatureInspector {
    if ('SyncShark.Release.SignatureInspector' -as [type]) { return }
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.Formats.Asn1
    $references = @(
        [System.Security.Cryptography.Pkcs.SignedCms].Assembly.Location,
        [System.Formats.Asn1.AsnReader].Assembly.Location,
        [System.Security.Cryptography.X509Certificates.X509Certificate2].Assembly.Location,
        [System.Collections.CollectionBase].Assembly.Location,
        [System.IO.Compression.ZipArchive].Assembly.Location,
        [System.Numerics.BigInteger].Assembly.Location
    )
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Formats.Asn1;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SyncShark.Release {
  public sealed class TimestampResult {
    public bool Present { get; set; }
    public bool CryptographicallyValid { get; set; }
    public bool IsRfc3161 { get; set; }
    public bool SignatureBound { get; set; }
    public bool ChainEvaluatedAtTokenTime { get; set; }
    public bool ChainValid { get; set; }
    public string TokenTimestampUtc { get; set; } = "";
    public string[] ChainStatuses { get; set; } = Array.Empty<string>();
  }
  public static class SignatureInspector {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct TrustFile {
      public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path;
      public IntPtr File, Subject;
    }
    [StructLayout(LayoutKind.Sequential)] struct TrustData {
      public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice;
      public IntPtr File; public uint Action; public IntPtr State, Url; public uint Flags, Context; public IntPtr Settings;
    }
    [DllImport("wintrust.dll", ExactSpelling=true)] static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static uint VerifyFileIntegrity(string path) {
      var file = new TrustFile { Size=(uint)Marshal.SizeOf<TrustFile>(), Path=path };
      IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
      Marshal.StructureToPtr(file,pointer,false);
      var data=new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(), Ui=2, Choice=1, File=pointer, Action=1, Flags=0x1000 };
      var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
      try { return WinVerifyTrust(new IntPtr(-1),ref action,ref data); }
      finally { data.Action=2; WinVerifyTrust(new IntPtr(-1),ref action,ref data); Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    const string Rfc3161Attribute = "1.3.6.1.4.1.311.3.3.1";
    const string TstInfoContent = "1.2.840.113549.1.9.16.1.4";
    const string TimestampingEku = "1.3.6.1.5.5.7.3.8";
    public static TimestampResult InspectTimestamp(string path) => InspectTimestamp(path, null);
    public static TimestampResult InspectTimestamp(string path, X509Certificate2 customTrustRoot) {
      var result = new TimestampResult();
      try {
        byte[] cmsBytes = ReadAuthenticode(path);
        var cms = new SignedCms(); cms.Decode(cmsBytes); cms.CheckSignature(true);
        if (cms.SignerInfos.Count != 1) return result;
        var primary = cms.SignerInfos[0];
        CryptographicAttributeObject attr = null;
        foreach (CryptographicAttributeObject candidate in primary.UnsignedAttributes)
          if (candidate.Oid?.Value == Rfc3161Attribute) { if (attr != null) return result; attr = candidate; }
        if (attr == null || attr.Values.Count != 1) return result;
        return InspectTimestampToken(attr.Values[0].RawData, primary, customTrustRoot);
      } catch { return result; }
    }
    public static TimestampResult InspectTimestampTokenForFile(string path, byte[] encodedToken,
        X509Certificate2 customTrustRoot) {
      var result = new TimestampResult();
      try {
        byte[] cmsBytes = ReadAuthenticode(path);
        var cms = new SignedCms(); cms.Decode(cmsBytes); cms.CheckSignature(true);
        if (cms.SignerInfos.Count != 1) return result;
        return InspectTimestampToken(encodedToken, cms.SignerInfos[0], customTrustRoot);
      } catch { return result; }
    }
    static TimestampResult InspectTimestampToken(byte[] encodedToken, SignerInfo primary,
        X509Certificate2 customTrustRoot) {
      var result = new TimestampResult { Present = true };
      try {
        if (!Rfc3161TimestampToken.TryDecode(encodedToken, out Rfc3161TimestampToken token, out int consumed) ||
            token == null || consumed != encodedToken.Length) return result;
        var stamp = token.AsSignedCms(); stamp.CheckSignature(true);
        result.CryptographicallyValid = true;
        if (stamp.ContentInfo.ContentType?.Value != TstInfoContent || stamp.SignerInfos.Count != 1) return result;
        var tokenInfo = token.TokenInfo;
        if (tokenInfo.Version != 1 || tokenInfo.GetSerialNumber().Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(tokenInfo.Encode(), stamp.ContentInfo.Content)) return result;
        result.IsRfc3161 = true;
        result.TokenTimestampUtc = tokenInfo.Timestamp.UtcDateTime.ToString("O");
        if (!token.VerifySignatureForSignerInfo(primary, out X509Certificate2 cert, stamp.Certificates) || cert == null)
          return result;
        result.SignatureBound = true;
        if (!HasCriticalExclusiveTimestampingEku(cert)) return result;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = customTrustRoot == null ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimestampingEku));
        chain.ChainPolicy.VerificationTime = tokenInfo.Timestamp.UtcDateTime;
        if (chain.ChainPolicy.VerificationTimeIgnored) return result;
        result.ChainEvaluatedAtTokenTime = true;
        if (customTrustRoot != null) {
          chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
          chain.ChainPolicy.CustomTrustStore.Add(customTrustRoot);
        }
        foreach (X509Certificate2 embedded in stamp.Certificates)
          chain.ChainPolicy.ExtraStore.Add(embedded);
        result.ChainValid = chain.Build(cert);
        var statuses = new string[chain.ChainStatus.Length];
        for (int i = 0; i < chain.ChainStatus.Length; i++) statuses[i] = chain.ChainStatus[i].Status.ToString();
        result.ChainStatuses = statuses;
        return result;
      } catch { return result; }
    }
    static bool HasCriticalExclusiveTimestampingEku(X509Certificate2 cert) {
      X509EnhancedKeyUsageExtension timestampEku = null;
      foreach (X509Extension extension in cert.Extensions) {
        if (extension.Oid?.Value != "2.5.29.37") continue;
        if (timestampEku != null || !extension.Critical) return false;
        timestampEku = extension as X509EnhancedKeyUsageExtension ??
          new X509EnhancedKeyUsageExtension(extension, extension.Critical);
      }
      return timestampEku != null && timestampEku.EnhancedKeyUsages.Count == 1 &&
        timestampEku.EnhancedKeyUsages[0].Value == TimestampingEku;
    }
    static byte[] ReadAuthenticode(string path) {
      var extension = Path.GetExtension(path);
      if (string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase)) {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException();
        using var file=File.OpenRead(path); using var archive=new ZipArchive(file,ZipArchiveMode.Read);
        ZipArchiveEntry signature=null;
        foreach(var entry in archive.Entries) if (string.Equals(entry.FullName,"AppxSignature.p7x",StringComparison.OrdinalIgnoreCase)) {
          if(signature!=null || entry.FullName!="AppxSignature.p7x" || entry.Length<5 || entry.Length>1024*1024) throw new InvalidDataException();
          signature=entry;
        }
        if(signature==null) throw new InvalidDataException();
        using var input=signature.Open(); var bytes=new byte[checked((int)signature.Length)]; input.ReadExactly(bytes);
        if(bytes[0]!='P' || bytes[1]!='K' || bytes[2]!='C' || bytes[3]!='X') throw new InvalidDataException();
        return bytes[4..];
      }
      if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(extension, ".psm1", StringComparison.OrdinalIgnoreCase)) {
        var text = File.ReadAllText(path);
        const string begin = "# SIG # Begin signature block";
        const string end = "# SIG # End signature block";
        int start = text.LastIndexOf(begin, StringComparison.Ordinal), finish = text.LastIndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start || text.Substring(finish + end.Length).Trim().Length != 0) throw new InvalidDataException();
        var encoded = new System.Text.StringBuilder();
        foreach (var line in text.Substring(start + begin.Length, finish - start - begin.Length).Split('\n')) {
          var encodedLine = line.Trim(); if (encodedLine.Length == 0) continue;
          if (!encodedLine.StartsWith("# ", StringComparison.Ordinal)) throw new InvalidDataException();
          encoded.Append(encodedLine.Substring(2));
        }
        return Convert.FromBase64String(encoded.ToString());
      }
      byte[] pe = File.ReadAllBytes(path);
      if (pe.Length < 0x100 || BitConverter.ToUInt16(pe, 0) != 0x5a4d) throw new InvalidDataException();
      int header = BitConverter.ToInt32(pe, 0x3c);
      if (header < 0 || header + 24 >= pe.Length || BitConverter.ToUInt32(pe, header) != 0x00004550) throw new InvalidDataException();
      int optional = header + 24; ushort magic = BitConverter.ToUInt16(pe, optional);
      int directory = optional + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : throw new InvalidDataException());
      int security = directory + 8 * 4;
      int offset = checked((int)BitConverter.ToUInt32(pe, security));
      int size = checked((int)BitConverter.ToUInt32(pe, security + 4));
      if (offset <= 0 || size < 8 || offset + size > pe.Length) throw new InvalidDataException();
      int length = checked((int)BitConverter.ToUInt32(pe, offset));
      if (length < 8 || length > size || offset + length > pe.Length) throw new InvalidDataException();
      byte[] value = new byte[length - 8]; Buffer.BlockCopy(pe, offset + 8, value, 0, value.Length); return value;
    }
  }
}
'@
}

function Get-SyncSharkSignatureObservationCore([string]$Path,
    [Security.Cryptography.X509Certificates.X509Certificate2]$TimestampCustomTrustRoot) {
    Initialize-SyncSharkSignatureInspector
    # Check the file/package digest through the Windows SIP, separately from the
    # certificate chain and timestamp CMS. An unrelated UnknownError is never
    # accepted merely because this product's self-issued root is untrusted.
    $integrity = [SyncShark.Release.SignatureInspector]::VerifyFileIntegrity($Path)
    if ($integrity -ne 0 -and $integrity -ne [Convert]::ToUInt32('800b0109',16)) { throw ('Windows signature integrity verification failed: 0x{0:x8}' -f $integrity) }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $cert = $signature.SignerCertificate
    $signerStatuses = @()
    if ($cert) {
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $null = $chain.Build($cert)
            $signerStatuses = @($chain.ChainStatus | ForEach-Object { $_.Status.ToString() })
        } finally { $chain.Dispose() }
    }
    $timestamp = [SyncShark.Release.SignatureInspector]::InspectTimestamp($Path, $TimestampCustomTrustRoot)
    $rawStatus = $signature.Status.ToString()
    # PowerShell reports this self-issued package as UnknownError on this host
    # even though its independently built signer chain differs only by the
    # expected UntrustedRoot. Normalize that exact observed condition to the
    # explicit NotTrusted decision class; every injected/raw UnknownError is
    # otherwise rejected by the production policy below.
    $status = if ($rawStatus -ceq 'UnknownError' -and $signerStatuses.Count -eq 1 -and
        $signerStatuses[0] -ceq 'UntrustedRoot') { 'NotTrusted' } else { $rawStatus }
    [pscustomobject]@{
        Status = $status
        RawStatus = $rawStatus
        Thumbprint = if ($cert) { $cert.Thumbprint } else { $null }
        Subject = if ($cert) { $cert.Subject } else { $null }
        Issuer = if ($cert) { $cert.Issuer } else { $null }
        SpkiSha256 = if ($cert) {
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                $cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        } else { $null }
        SignerChainStatuses = $signerStatuses
        Rfc3161Present = $timestamp.Present
        Rfc3161CryptographicallyValid = $timestamp.CryptographicallyValid
        Rfc3161 = $timestamp.IsRfc3161
        TimestampSignatureBound = $timestamp.SignatureBound
        TimestampChainEvaluatedAtTokenTime = $timestamp.ChainEvaluatedAtTokenTime
        TimestampChainValid = $timestamp.ChainValid
        TimestampUtc = $timestamp.TokenTimestampUtc
        TimestampChainStatuses = @($timestamp.ChainStatuses)
    }
}

function Get-SyncSharkSignatureObservation([string]$Path) {
    Get-SyncSharkSignatureObservationCore $Path $null
}

function Assert-SyncSharkSignatureObservation([object]$Observation, [string[]]$ExpectedThumbprint,
    [string]$ExpectedSubject, [string]$ExpectedIssuer, [string[]]$ExpectedSpkiSha256) {
    if ($Observation.Thumbprint -cnotin $ExpectedThumbprint -or $Observation.Subject -cne $ExpectedSubject -or
        $Observation.Issuer -cne $ExpectedIssuer -or $Observation.SpkiSha256 -cnotin $ExpectedSpkiSha256) {
        throw 'Pinned signer identity mismatch.'
    }
    $signerStatuses = @($Observation.SignerChainStatuses)
    $allowed = $Observation.Status -ceq 'Valid' -and $signerStatuses.Count -eq 0
    if ($Observation.Status -ceq 'NotTrusted' -and $signerStatuses.Count -eq 1 -and
        $signerStatuses[0] -ceq 'UntrustedRoot') { $allowed = $true }
    if (-not $allowed) { throw "Authenticode status is not permitted: $($Observation.Status)" }
    if (-not $Observation.Rfc3161Present -or -not $Observation.Rfc3161CryptographicallyValid -or
        -not $Observation.Rfc3161 -or -not $Observation.TimestampSignatureBound -or
        -not $Observation.TimestampChainEvaluatedAtTokenTime -or
        -not $Observation.TimestampChainValid -or @($Observation.TimestampChainStatuses).Count -ne 0) {
        throw 'A cryptographically valid, signature-bound RFC3161 timestamp with a chain valid at the token time is required.'
    }
}

function Get-SyncSharkSignedMemberHashes([object]$Contract) {
    $fields = [ordered]@{
        signedCoreSha256 = 'SyncShark.Core.dll'
        signedUiSha256 = 'SyncShark.dll'
        signedExeSha256 = 'SyncShark.exe'
        signedExplorerSha256 = 'SyncShark.Explorer.dll'
        signedDiscoverySha256 = 'SyncSharkDiscovery.exe'
        signedIdentityPackageSha256 = 'SyncShark.msix'
    }
    $result = @{}
    foreach ($field in $fields.Keys) {
        $property = $Contract.PSObject.Properties[$field]
        if ($null -eq $property -and $field -in @('signedExplorerSha256', 'signedDiscoverySha256', 'signedIdentityPackageSha256')) { continue }
        if ($null -eq $property -or [string]$property.Value -cnotmatch '^[0-9a-f]{64}$') {
            throw "The signed update contract has an invalid $field."
        }
        $result[$fields[$field]] = [string]$property.Value
    }
    return $result
}

function Assert-SyncSharkExactPackage([string]$Root, [string]$ManifestPath, [string]$ExpectedManifestSha256,
    [string]$ExpectedVersion, [hashtable]$SignedHashes, [string[]]$ExpectedThumbprint,
    [string]$ExpectedSubject, [string]$ExpectedIssuer, [string[]]$ExpectedSpkiSha256,
    [string]$VerificationObservationPath, [bool]$AllowObservationInjection) {
    if ((Get-SyncSharkSha256 $ManifestPath) -cne $ExpectedManifestSha256) { throw 'Package manifest hash mismatch.' }
    Assert-SyncSharkOrdinaryTree $Root
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 20
    $versionedMembers = @('SyncShark.Core.dll', 'SyncShark.dll', 'SyncShark.exe')
    $nativeMembers = @('SyncShark.Explorer.dll', 'SyncSharkDiscovery.exe', 'SyncShark.msix')
    foreach ($name in $SignedHashes.Keys) {
        if ($name -cnotin ($versionedMembers + $nativeMembers) -or $SignedHashes[$name] -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Unexpected signed member or invalid signed-member hash.'
        }
    }
    foreach ($name in $versionedMembers) {
        if (-not $SignedHashes.ContainsKey($name)) { throw "Missing signed-member identity: $name" }
    }
    $expected = [ordered]@{}
    foreach ($member in $manifest.members) {
        $path = [string]$member.path
        if (-not $path -or [IO.Path]::IsPathRooted($path) -or $path.Contains('\') -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or $expected.Contains($path)) {
            throw 'The package manifest contains an invalid or duplicate member path.'
        }
        $expected[$path] = if ($SignedHashes.ContainsKey($path)) { $SignedHashes[$path] } else { [string]$member.sha256 }
    }
    foreach ($name in $nativeMembers) {
        if ($expected.Contains($name) -and -not $SignedHashes.ContainsKey($name)) {
            throw "The native component has no signed-member identity: $name"
        }
    }
    foreach ($name in $SignedHashes.Keys) {
        if (-not $expected.Contains($name)) { throw "Signed component is absent from the manifest: $name" }
    }
    $actual = @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse)
    if ($actual.Count -ne $expected.Count) { throw 'Package membership mismatch.' }
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if (-not $expected.Contains($relative) -or (Get-SyncSharkSha256 $file.FullName) -cne $expected[$relative]) {
            throw "Package identity mismatch: $relative"
        }
    }
    $injected = $null
    if ($VerificationObservationPath) {
        if (-not $AllowObservationInjection) { throw 'Signature observation injection is limited to the isolated release-test seam.' }
        $injected = Get-Content -LiteralPath $VerificationObservationPath -Raw | ConvertFrom-Json -Depth 20
    }
    foreach ($name in $SignedHashes.Keys) {
        $path = Join-Path $Root $name
        if ($name -cin $versionedMembers -and
            [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -cne $ExpectedVersion) {
            throw "Product version mismatch: $name"
        }
        $observation = if ($injected) { $injected.$name } else { Get-SyncSharkSignatureObservation $path }
        if($name -ceq 'SyncShark.msix') {
            # The existing package family is derived from this exact Publisher.
            # Never let the executable signer or a shared public key substitute.
            $packageRole=(Get-SyncSharkSignerIdentity).roles.package
            Assert-SyncSharkSignatureObservation $observation `
                @($packageRole.thumbprints) `
                $packageRole.subject `
                $packageRole.subject `
                @($packageRole.spki)
        } else {
            Assert-SyncSharkSignatureObservation $observation $ExpectedThumbprint $ExpectedSubject $ExpectedIssuer $ExpectedSpkiSha256
        }
    }
}

Export-ModuleMember -Function Get-SyncSharkSha256, Get-SyncSharkNormalizedPath,
    Get-SyncSharkSignerIdentity,
    Assert-SyncSharkWithin, Assert-SyncSharkOrdinaryTree, Assert-SyncSharkTransactionTree, Get-SyncSharkTreeManifest,
    Assert-SyncSharkTreeMatch, Get-SyncSharkTreeIdentity, Get-SyncSharkJunctionTarget, Assert-SyncSharkJunction,
    Resolve-SyncSharkProductRoot, Read-SyncSharkJournal, Write-SyncSharkJournal, Copy-SyncSharkTreeToStaging,
    Invoke-SyncSharkCrashPoint, Invoke-SyncSharkJournalStep, Get-SyncSharkSignatureObservation,
    Assert-SyncSharkSignatureObservation, Get-SyncSharkSignedMemberHashes, Assert-SyncSharkExactPackage,
    Invoke-SyncSharkExplorerPackage, Set-SyncSharkRuntimeSystemReadAccess, Assert-SyncSharkRuntimeSystemReadAccess
Export-ModuleMember -Function Resolve-SyncSharkRuntimeRoot, Assert-SyncSharkProtectedRuntime, Invoke-SyncSharkProtectedRuntime
Export-ModuleMember -Function Read-SyncSharkUpdaterRelease
Export-ModuleMember -Function Invoke-SyncSharkWithInstanceMaintenanceLease
