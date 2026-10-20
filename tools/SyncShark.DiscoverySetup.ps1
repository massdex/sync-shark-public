# Fixed signed SyncSharkDiscovery.exe loads its own embedded assembly and this resource.
# Runtime remains argument-free; no certificate/trust mutation or runtime compilation.
# The signer identity is the single source of truth for release trust and accepts
# a LIST per role so an old and a replacement key verify during the switch. This
# embedded script runs from stdin with no $PSScriptRoot, so it carries the exact
# canonical JSON verbatim; tools/New-SyncSharkSignerIdentity.ps1 regenerates it.
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
$script:SyncSharkDiscoverySignerIdentity = $null
function Get-SyncSharkDiscoverySignerIdentity {
    if ($null -ne $script:SyncSharkDiscoverySignerIdentity) { return $script:SyncSharkDiscoverySignerIdentity }
    $script:SyncSharkDiscoverySignerIdentity = ($script:SyncSharkSignerIdentityJson | ConvertFrom-Json)
    $script:SyncSharkDiscoverySignerIdentity
}

function Get-SyncSharkDiscoverySetupFailureCode([Exception]$Failure) {
    # Return only a bounded phase number and numeric error. No exception text,
    # path, account information or user-controlled content crosses the boundary.
    $phase=0
    if(Test-Path Variable:script:DiscoverySetupPhase){$phase=[int]$script:DiscoverySetupPhase}
    if($phase -lt 0 -or $phase -gt 20){$phase=0}
    $cause=$Failure.GetBaseException()
    # COM can return NTSTATUS rather than HRESULT_FROM_WIN32. Its low word
    # alone is ambiguous, so retain all 32 bits in the existing exit code.
    if($cause -is [Runtime.InteropServices.COMException]){return [int]$cause.ErrorCode}
    $code=$cause.HResult -band 0xffff
    if($cause -is [ComponentModel.Win32Exception]){$code=$cause.NativeErrorCode -band 0xffff}
    return [int](0x20000000 -bor ($phase -shl 16) -bor $code)
}

function Get-SyncSharkDiscoveryTaskUserSid([string]$TaskUser) {
    # Task Scheduler canonicalizes a supplied SID to an account name. Compare
    # the Windows-resolved identity, not two different representations of it.
    if([string]::IsNullOrWhiteSpace($TaskUser)){throw 'The Discovery task has no Windows account.'}
    if($TaskUser.StartsWith('S-1-',[StringComparison]::OrdinalIgnoreCase)) {
        return [Security.Principal.SecurityIdentifier]::new($TaskUser).Value
    }
    return ([Security.Principal.NTAccount]::new($TaskUser)).Translate([Security.Principal.SecurityIdentifier]).Value
}

function Get-SyncSharkDiscoveryTaskSddl([string]$UserSid) {
    $sid=[Security.Principal.SecurityIdentifier]::new($UserSid).Value
    # The elevated split token can assign Administrators as owner. Its linked
    # medium token carries that SID as deny-only, not as an ownership grant.
    return 'O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;'+$sid+')'
}

function Get-SyncSharkDiscoveryTaskExecutionLimit([string]$BoundVersion) {
    if($BoundVersion -cnotmatch '^(\d+\.\d+\.\d+)\+[0-9a-f]{40}$'){throw 'The Discovery task version is unbound.'}
    # This is read only after the protected worker/manifest pair is verified.
    # Restoring an old worker restores its original task limit as well.
    if([version]$Matches[1] -ge [version]'0.3.32'){return 'PT31M'}
    return 'PT2M'
}

function Assert-SyncSharkDiscoveryTaskLimitBinding([string]$ExecutionLimit,[string]$BoundVersion,
    [bool]$WasEnabled,[bool]$Recovering) {
    if($ExecutionLimit -cnotin @('PT2M','PT31M')){throw 'The Discovery task execution limit changed.'}
    $expected=Get-SyncSharkDiscoveryTaskExecutionLimit $BoundVersion
    # A caught commit failure can restore the old files and close their journal
    # after the successor task was registered disabled. That exact, validated
    # disabled task is repairable; it must not strand all three setup verbs.
    # WasEnabled is captured before this invocation disables the task. An enabled
    # mismatch still fails closed unless recovering an actual pending transaction.
    if($ExecutionLimit -cne $expected -and $WasEnabled -and -not $Recovering){throw 'The task limit does not match its protected worker.'}
}

function Get-SyncSharkDiscoverySigner([string]$Binary,[string]$BinarySha256='',
    [string]$ManifestSha256='',[switch]$RetainedProtectedPair) {
    if($RetainedProtectedPair) {
        if($Binary -cnotin @('C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe',
            'C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe.previous')) {
            throw 'Legacy signer admission is limited to the exact protected Discovery pair.'
        }
        # Fixed transition preimage, not a general old/new signer allowlist.
        # ReadPair also holds both files and proves their protected ACLs.
        $legacyPairs=@{
            'd8a3faa5ef1c3f760e346d21b0925f420536e224070fa33c6a0ec1bdef05e256'='1ef7f297db09cc97d1231108789b2ecc38e9c431ef947966d28ca8af9e3e57b1'
            'c879d94e2b7fba765f06e6913c3273737af8a029fa918f329579e3a4ddfc5b20'='ebad10fd3214afc2fa9c46f2b07e5ba7bfefac2c90f499917f537472bfb2312a'
        }
        if($legacyPairs.ContainsKey($BinarySha256)) {
            if($legacyPairs[$BinarySha256] -cne $ManifestSha256) {throw 'The legacy Discovery manifest is not its exact retained preimage.'}
            $package=(Get-SyncSharkDiscoverySignerIdentity).roles.package
            return @{Thumbprints=@($package.thumbprints);Subject=$package.subject}
        }
    }
    $publisher=(Get-SyncSharkDiscoverySignerIdentity).roles.publisher
    return @{Thumbprints=@($publisher.thumbprints);Subject=$publisher.subject}
}

function Assert-SyncSharkDiscoverySetupRoots([string]$Mode) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator setup is required once. Ordinary sync remains available.'}
    $trustedOwners=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    function ProtectedDirectory([string]$Path,[bool]$VolumeRoot=$false) {
        $attributes=[IO.File]::GetAttributes($Path)
        if(($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::Directory) -eq 0){throw 'The setup path is not an ordinary protected directory.'}
        $acl=[IO.Directory]::GetAccessControl($Path)
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedOwners){throw 'The setup path has an untrusted owner.'}
        $writeMask=0x500d0156L;if($VolumeRoot){$writeMask=0x500d0040L}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0){continue}
            if(([long]$rule.FileSystemRights -band $writeMask) -ne 0 -and $rule.IdentityReference.Value -notin $trustedOwners){throw 'The setup path is writable by a non-administrator.'}
        }
    }
    ProtectedDirectory 'C:\' $true;ProtectedDirectory 'C:\Program Files'
    foreach($path in @('C:\Program Files\Massdex','C:\Program Files\Massdex\SyncShark')) {
        if(-not [IO.Directory]::Exists($path)) {
            if($Mode -ne 'Configure'){throw 'Fast Discovery is not installed.'}
            $acl=[Security.AccessControl.DirectorySecurity]::new()
            $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)')
            [void][IO.Directory]::CreateDirectory($path,$acl)
        }
        ProtectedDirectory $path
    }
}

function Invoke-SyncSharkDiscoverySetup {
    param([ValidateSet('Configure','Remove','Restore')][string]$Mode,
        [int]$ParentProcessId,[string]$AppDirectory,[switch]$RuntimeReleaseValidated,[object]$UpdaterSession=$null,[object]$OriginalDiscovery=$null)
    $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
    $script:DiscoverySetupPhase=1
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator setup is required once. Ordinary sync remains available.'
    }
    $user=$identity.User.Value
    if($null -ne $UpdaterSession -and ($Mode -cne 'Configure' -or -not $RuntimeReleaseValidated -or
        $UpdaterSession.GetType().FullName -cnotin @('SyncSharkUpdaterSession','SyncSharkUpdaterSetup'))){throw 'Unexpected protected updater transition.'}
    if($null -ne $OriginalDiscovery -and ($Mode -cne 'Restore' -or -not $RuntimeReleaseValidated -or
        $null -ne $UpdaterSession -or $OriginalDiscovery.GetType().FullName -cne 'SyncSharkOriginalDiscovery')){throw 'Unexpected original-pair restoration.'}
    $product=[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'Massdex','SyncShark')
    $app=[IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
    $version=[IO.Path]::GetFileName($app)
    if($version -cnotmatch '^\d+\.\d+\.\d+$' -or $app -ine [IO.Path]::Combine('C:\Program Files\Massdex\SyncShark','versions',$version)) { throw 'Setup requires one exact protected installed version.' }
    $parent=Get-Process -Id $ParentProcessId -ErrorAction Stop
    if($parent.SessionId -ne (Get-Process -Id $PID).SessionId -or (-not $RuntimeReleaseValidated -and
        ($parent.Path -ine [IO.Path]::Combine($app,'SyncShark.exe') -and $parent.Path -ine [IO.Path]::Combine($product,'active','SyncShark.exe')))) {
        throw 'The setup caller is not the installed interactive application.'
    }
    if(-not $RuntimeReleaseValidated) {
        $active=Get-Item -LiteralPath ([IO.Path]::Combine($product,'active')) -Force
        if($active.LinkType -cne 'Junction' -or [string]$active.Target -ine $app) { throw 'The active installed version changed.' }
    }
    $root='C:\Program Files\Massdex\SyncShark'
    $worker=[IO.Path]::Combine($root,'SyncSharkDiscovery.exe')
    $manifestPath=[IO.Path]::Combine($root,'discovery-manifest.json')
    $previousWorker=$worker+'.previous'; $previousManifest=$manifestPath+'.previous'
    $held=[Collections.Generic.List[IDisposable]]::new(); $medium=[IntPtr]::Zero
    $service=$null; $folder=$null; $task=$null; $registered=$null; $transaction=$null; $recoveryAllowed=$false
    $taskSddl=Get-SyncSharkDiscoveryTaskSddl $user
    function Hold([string]$Path,[bool]$Directory,[bool]$Protected) {
        $handle=[SyncSharkDiscoverySetupNative]::Hold($Path,$Directory); $held.Add($handle)
        if($Protected){[SyncSharkDiscoverySetupNative]::RequireProtected($handle,$medium,$Directory)}
    }
    function Sha([string]$Path) {
        $input=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $algorithm=[Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($algorithm.ComputeHash($input))).Replace('-','').ToLowerInvariant() }
        finally {$algorithm.Dispose();$input.Dispose()}
    }
    function ProductSignature([string]$Path,[string]$BinarySha256='',
        [string]$ManifestSha256='',[switch]$RetainedProtectedPair) {
        $expected=Get-SyncSharkDiscoverySigner $Path $BinarySha256 $ManifestSha256 -RetainedProtectedPair:$RetainedProtectedPair
        [SyncSharkDiscoverySetupNative]::RequireProductSignature($Path)
        $signature=Get-AuthenticodeSignature -LiteralPath $Path
        if($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cnotin $expected.Thumbprints -or
            $signature.SignerCertificate.Subject -cne $expected.Subject -or $signature.SignerCertificate.Issuer -cne $expected.Subject -or $null -eq $signature.TimeStamperCertificate) {
            throw 'Setup accepts only the registered timestamped Sync Shark signer.'
        }
    }
    function TaskIdentity([object]$Task) {
        $definition=$Task.Definition; $action=$definition.Actions.Item(1); $settings=$definition.Settings
        $security=[Security.AccessControl.RawSecurityDescriptor]::new($Task.GetSecurityDescriptor(7))
        if($security.Owner.Value -cne 'S-1-5-32-544' -or $security.Group.Value -cne 'S-1-5-32-544' -or
            ($security.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq 0 -or $security.DiscretionaryAcl.Count -ne 3) { throw 'The existing task permissions changed.' }
        $seen=@{}
        foreach($ace in $security.DiscretionaryAcl) {
            $sid=$ace.SecurityIdentifier.Value
            if($ace -isnot [Security.AccessControl.CommonAce] -or $seen.ContainsKey($sid) -or $ace.AceQualifier -ne 'AccessAllowed' -or $ace.AceFlags -ne 'None' -or $ace.IsCallback) {throw 'Unexpected task permission.'}
            $seen[$sid]=$true
            if($sid -in @('S-1-5-18','S-1-5-32-544')) {if($ace.AccessMask -notin @(268435456,2032127)){throw 'Administrator task permission changed.'}}
            elseif($sid -cne $user -or $ace.AccessMask -notin @(-1610612736,1179817)){throw 'Task is writable or belongs to another user.'}
        }
        if($definition.Actions.Count -ne 1 -or $action.Type -ne 0 -or $action.Path -ine $worker -or -not [string]::IsNullOrEmpty($action.Arguments) -or
            $action.WorkingDirectory -ine $root -or (Get-SyncSharkDiscoveryTaskUserSid $definition.Principal.UserId) -cne $user -or $definition.Principal.LogonType -ne 3 -or
            $definition.Principal.RunLevel -ne 1 -or $definition.Triggers.Count -ne 0 -or $settings.MultipleInstances -ne 2 -or
            $settings.ExecutionTimeLimit -notin @('PT2M','PT31M') -or -not $settings.AllowDemandStart -or $settings.RunOnlyIfIdle -or
            $settings.RunOnlyIfNetworkAvailable -or $settings.DisallowStartIfOnBatteries -or $settings.StopIfGoingOnBatteries) { throw 'The existing task action or execution limits changed.' }
    }
    function ReadPair([string]$Binary,[string]$Manifest) {
        if(-not [IO.File]::Exists($Binary) -or -not [IO.File]::Exists($Manifest)) { throw 'The protected component pair is incomplete; preserve it for recovery.' }
        foreach($path in @($Binary,$Manifest)) { Hold $path $false $true }
        if(([IO.FileInfo]$Manifest).Length -gt 524288){throw 'The protected manifest is oversized.'}
        $saved=[IO.File]::ReadAllText($Manifest)|ConvertFrom-Json
        $binaryHash=Sha $Binary
        if($saved.schemaVersion -ne 1 -or $saved.userSid -cne $user -or $saved.workerSha256 -cne $binaryHash) {throw 'The protected component identity changed.'}
        ProductSignature $Binary $binaryHash (Sha $Manifest) -RetainedProtectedPair
        return $saved
    }
    function CreateProtectedDirectory([string]$Path) {
        $acl=[Security.AccessControl.DirectorySecurity]::new()
        $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)')
        [void][IO.Directory]::CreateDirectory($Path,$acl)
    }
    function ReleaseFiles() { for($i=$held.Count-1;$i -ge 0;$i--){$held[$i].Dispose()};$held.Clear() }
    try {
        $script:DiscoverySetupPhase=2
        Assert-SyncSharkDiscoverySetupRoots $Mode
        $script:DiscoverySetupPhase=3
        $medium=[SyncSharkDiscoverySetupNative]::MediumToken()
        # Hold all source ancestors and members before verifying or copying them.
        $script:DiscoverySetupPhase=4
        $ancestors=[Collections.Generic.List[string]]::new();$current=$app
        while($current -and $current -ne [IO.Path]::GetPathRoot($current)){$ancestors.Add($current);$current=[IO.Path]::GetDirectoryName($current)}
        foreach($path in $ancestors){Hold $path $true $true}
        $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($app);$members=[Collections.Generic.List[object]]::new()
        while($queue.Count) {
            $directory=$queue.Dequeue()
            foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $attributes=[IO.File]::GetAttributes($entry)
                if(($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) -ne 0) {throw 'The installed source contains a reparse or offline item.'}
                if(($attributes -band [IO.FileAttributes]::Directory) -ne 0) {Hold $entry $true $true;$queue.Enqueue($entry);continue}
                if($members.Count -ge 512 -or ([IO.FileInfo]$entry).Length -gt 536870912){throw 'The installed source exceeds the bounded setup limit.'}
                Hold $entry $false $true
                $relative=$entry.Substring($app.Length+1)
                if($relative.Length -gt 255){throw 'An installed member name is too long.'}
                $members.Add([ordered]@{path=$relative;sha256=(Sha $entry)})
            }
        }
        $script:DiscoverySetupPhase=5
        foreach($name in @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll','SyncSharkDiscovery.exe','SyncShark.Explorer.dll')) {ProductSignature ([IO.Path]::Combine($app,$name))}
        $boundVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::Combine($app,'SyncShark.exe')).ProductVersion
        if($boundVersion -cnotmatch ('^'+[regex]::Escape($version)+'\+[0-9a-f]{40}$')) {throw 'The installed product version is unbound.'}
        foreach($name in @('SyncShark.dll','SyncShark.Core.dll')) {
            if([Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::Combine($app,$name)).ProductVersion -cne $boundVersion) {throw 'Installed product members belong to different revisions.'}
        }
        $script:DiscoverySetupPhase=6
        Hold 'C:\Program Files' $true $true
        $publisher=[IO.Path]::GetDirectoryName($root)
        if(-not [IO.Directory]::Exists($publisher)){CreateProtectedDirectory $publisher}
        Hold $publisher $true $true
        if(-not [IO.Directory]::Exists($root)) {
            if($Mode -ne 'Configure'){throw 'Fast Discovery is not installed.'}
            CreateProtectedDirectory $root
        }
        Hold $root $true $true
        # The protected file claim serializes every setup process and survives crashes.
        $script:DiscoverySetupPhase=7
        if($null -ne $UpdaterSession -or $null -ne $OriginalDiscovery){$transaction=[SyncSharkDiscoveryTransaction]::new($root,$user,$true)}
        else{$transaction=[SyncSharkDiscoveryTransaction]::new($root,$user)}
        $script:DiscoverySetupPhase=8
        $service=New-Object -ComObject 'Schedule.Service';$service.Connect();$folder=$service.GetFolder('\')
        $script:DiscoverySetupPhase=9
        try {$task=$folder.GetTask('SyncSharkDiscovery')} catch {
            $failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException}
            if($failure.HResult -ne -2147024894){throw}
        }
        $script:DiscoverySetupPhase=10
        $taskWasEnabled=$false
        if($task){TaskIdentity $task;$taskWasEnabled=[bool]$task.Enabled}
        $recovering=$transaction.HasPending
        if($null -ne $OriginalDiscovery) {
            if(-not $task){throw 'Original restoration cannot create a missing Discovery task.'}
            $alreadyRestored=$OriginalDiscovery.ValidateBefore($transaction)
            if($recovering) {
                $OriginalDiscovery.Recover($transaction)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($task)
                $task=$folder.GetTask('SyncSharkDiscovery');TaskIdentity $task
                $taskWasEnabled=[bool]$task.Enabled;$recovering=$false
                $alreadyRestored=$OriginalDiscovery.ValidateBefore($transaction)
            }
            if(-not $taskWasEnabled){throw 'Original restoration cannot enable an independently disabled task.'}
            if($alreadyRestored){return}
        }
        if($null -ne $UpdaterSession) {
            if(-not $task){throw 'Updater cannot create a missing Discovery task.'}
            if($recovering) {
                # Restore only this signed transition, retaining its fence until
                # the exact task and enabled preimage/postimage are read back.
                $UpdaterSession.RecoverDiscovery($transaction)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($task)
                $task=$folder.GetTask('SyncSharkDiscovery');TaskIdentity $task
                $taskWasEnabled=[bool]$task.Enabled;$recovering=$false
                if($UpdaterSession.IsDiscoveryPostimage()){return}
            }
            if(-not $taskWasEnabled){throw 'Updater cannot enable an independently disabled Discovery task.'}
            # Under the existing exclusive claim, before disabling/stopping the
            # task or touching any component, repeat the exact signed preimage.
            $UpdaterSession.VerifyDiscoveryPreimage()
        }
        if($task -and $null -eq $UpdaterSession -and $null -eq $OriginalDiscovery){$task.Enabled=$false;$task.Stop(0);$deadline=[DateTime]::UtcNow.AddSeconds(10);while($task.GetInstances(0).Count){if([DateTime]::UtcNow -ge $deadline){throw 'The existing Discovery worker did not stop.'};Start-Sleep -Milliseconds 100}}
        $recoveryAllowed=$true
        $script:DiscoverySetupPhase=11
        if($null -eq $OriginalDiscovery){$transaction.Recover()}
        if($recovering -and $task -and -not [IO.File]::Exists($worker) -and -not [IO.File]::Exists($manifestPath)) {
            $folder.DeleteTask('SyncSharkDiscovery',0)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($task);$task=$null
        }
        $script:DiscoverySetupPhase=12
        $old=$null
        if([IO.File]::Exists($worker) -or [IO.File]::Exists($manifestPath)){$old=ReadPair $worker $manifestPath}
        if([IO.File]::Exists($previousWorker) -or [IO.File]::Exists($previousManifest)){[void](ReadPair $previousWorker $previousManifest)}
        if($task -and -not $old){throw 'An existing task has no bound protected component.'}
        if($task){Assert-SyncSharkDiscoveryTaskLimitBinding $task.Definition.Settings.ExecutionTimeLimit $old.version $taskWasEnabled $recovering}
        $source=$null;$bytes=$null
        $script:DiscoverySetupPhase=13
        if($Mode -eq 'Configure') {
            $source=[IO.Path]::Combine($app,'SyncSharkDiscovery.exe')
            $manifest=[ordered]@{schemaVersion=1;version=$boundVersion;userSid=$user;appDirectory=$app;workerSha256=(Sha $source);members=@($members.ToArray())}
            $json=$manifest|ConvertTo-Json -Depth 6 -Compress
            $ascii=[Text.StringBuilder]::new()
            foreach($character in $json.ToCharArray()) {
                if([int]$character -gt 127){[void]$ascii.Append(('\u{0:x4}' -f [int]$character))}else{[void]$ascii.Append($character)}
            }
            $bytes=[Text.UTF8Encoding]::new($false).GetBytes($ascii.ToString())
            if($null -ne $UpdaterSession){$bytes=$UpdaterSession.DiscoveryCandidate()}
        }
        $parent.Refresh();if($parent.HasExited){throw 'The requesting application closed before setup publication.'}
        $script:DiscoverySetupPhase=14
        if($null -ne $OriginalDiscovery) {
            $transaction.Prepare('Restore',$OriginalDiscovery.CurrentWorker,$OriginalDiscovery.CurrentManifest,
                $OriginalDiscovery.PreviousWorker,$OriginalDiscovery.PreviousManifest,$OriginalDiscovery.ExpectedBefore)
        } else {$transaction.Prepare($Mode,$source,$bytes)}
        # For an unattended transition the durable, signed recovery fence must
        # exist before disabling the previously enabled task, including Stop errors.
        if($task -and ($null -ne $UpdaterSession -or $null -ne $OriginalDiscovery)){$task.Enabled=$false;$task.Stop(0);$deadline=[DateTime]::UtcNow.AddSeconds(10);while($task.GetInstances(0).Count){if([DateTime]::UtcNow -ge $deadline){throw 'The existing Discovery worker did not stop.'};Start-Sleep -Milliseconds 100}}
        ReleaseFiles
        # Worker admission rejects the journal fence throughout multi-file publication.
        # Complete publishes only after both exact pairs and task identity are verified.
        $script:DiscoverySetupPhase=15
        $transaction.Apply()
        if($Mode -eq 'Remove') {
            if($task){$folder.DeleteTask('SyncSharkDiscovery',0)}
            $transaction.Complete();return
        }
        $script:DiscoverySetupPhase=16
        $publishedPair=ReadPair $worker $manifestPath
        $executionLimit=Get-SyncSharkDiscoveryTaskExecutionLimit $publishedPair.version
        if([IO.File]::Exists($previousWorker)){[void](ReadPair $previousWorker $previousManifest)}
        ReleaseFiles
        $xml=@"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<RegistrationInfo><Description>Sync Shark read-only NTFS discovery</Description></RegistrationInfo><Triggers />
<Principals><Principal id="User"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>false</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>$executionLimit</ExecutionTimeLimit></Settings>
<Actions Context="User"><Exec><Command>$worker</Command><WorkingDirectory>$root</WorkingDirectory></Exec></Actions></Task>
"@
        # Keep the explicit read/run-only principal ACE; Task Scheduler must
        # not add a broader default ACE while registering or updating it.
        $flags=2 -bor 0x10;if($task){$flags=4 -bor 0x10}
        $script:DiscoverySetupPhase=17
        $registered=$folder.RegisterTask('SyncSharkDiscovery',$xml,$flags,$user,$null,3,$taskSddl)
        $script:DiscoverySetupPhase=18
        TaskIdentity $registered
        if($registered.Definition.Settings.ExecutionTimeLimit -cne $executionLimit){throw 'The registered task limit does not match its protected worker.'}
        $script:DiscoverySetupPhase=19
        $registered.Enabled=$true
        if(-not $registered.Enabled){throw 'Discovery task did not become enabled; retain its recovery fence.'}
        TaskIdentity $registered
        $script:DiscoverySetupPhase=20
        $transaction.Complete()
    }
    catch {
        ReleaseFiles
        if($null -ne $registered){$registered.Enabled=$false}
        if($null -ne $transaction -and $recoveryAllowed -and $null -eq $UpdaterSession -and $null -eq $OriginalDiscovery){$transaction.Recover()}
        if($null -ne $registered -and $null -eq $task){$folder.DeleteTask('SyncSharkDiscovery',0)}
        # Failure remains disabled; the durable transaction recovers on the next request.
        throw
    }
    finally {
        ReleaseFiles
        if($null -ne $transaction){$transaction.Dispose()}
        if($medium -ne [IntPtr]::Zero){[SyncSharkDiscoverySetupNative]::CloseToken($medium)}
        foreach($item in @($registered,$task,$folder,$service)){if($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)}}
    }
}

function Assert-SyncSharkProtectedRuntimeContract([object]$Contract,[string]$ProductVersion) {
    if($ProductVersion -cnotmatch '^(\d+\.\d+\.\d+)\+([0-9a-f]{40})$'){throw 'The protected runtime needs a frozen source version.'}
    $version=$Matches[1];$commit=$Matches[2]
    $target=[IO.Path]::Combine('C:\Program Files\Massdex\SyncShark\versions',$version)
    if($Contract.product -cne 'Massdex.SyncShark' -or $Contract.version -cne $version -or
        $Contract.sourceRevision -cne $commit -or $Contract.protectedRuntimeRoot -cne $target -or
        $Contract.previousVersion -cne '__EXPECTED_PREVIOUS_VERSION__') {
        throw 'The signed runtime contract does not bind this exact release and predecessor.'
    }
    $expected=[SyncSharkProtectedRuntimeTransaction]::Members
    if(@($Contract.runtimeMembers).Count -ne $expected.Length){throw 'The signed runtime inventory is incomplete.'}
    $seen=@{}
    foreach($member in $Contract.runtimeMembers) {
        if($member.path -cnotin $expected -or $seen.ContainsKey($member.path) -or
            $member.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $member.bytes -isnot [ValueType] -or
            [long]$member.bytes -lt 1 -or [long]$member.bytes -gt 536870912 -or [decimal]$member.bytes -ne [long]$member.bytes) {
            throw 'The signed runtime inventory contains an unexpected, duplicate or invalid member.'
        }
        $seen[$member.path]=$member
    }
    $signed=@{'SyncShark.exe'='signedExeSha256';'SyncShark.dll'='signedUiSha256';'SyncShark.Core.dll'='signedCoreSha256';
        'SyncSharkDiscovery.exe'='signedDiscoverySha256';'SyncShark.Explorer.dll'='signedExplorerSha256';'SyncShark.msix'='signedIdentityPackageSha256'}
    foreach($name in $signed.Keys){if($seen[$name].sha256 -cne $Contract.($signed[$name])){throw 'The runtime and release signing contracts disagree.'}}
    return $seen
}

function Invoke-SyncSharkProtectedRuntimeSetup {
    param([ValidateSet('RuntimeInstall','RuntimeRemove','RuntimeRollback')][string]$Mode,[int]$ParentProcessId,[string]$AppDirectory)
    $ErrorActionPreference='Stop';Set-StrictMode -Version Latest;$script:DiscoverySetupPhase=1
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Protected program-file installation requires administrator approval.'
    }
    $product=[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'Massdex','SyncShark')
    $app=[IO.Path]::GetFullPath($AppDirectory).TrimEnd('\');$version=[IO.Path]::GetFileName($app)
    if($version -cnotmatch '^\d+\.\d+\.\d+$' -or $app -ine [IO.Path]::Combine($product,'versions',$version)) {
        throw 'Protected setup accepts only its fixed retained signed release source.'
    }
    $parent=Get-Process -Id $ParentProcessId -ErrorAction Stop
    if($parent.SessionId -ne (Get-Process -Id $PID).SessionId){throw 'The setup requester left its original user session.'}
    $held=[Collections.Generic.List[IDisposable]]::new();$claim=$null;$medium=[IntPtr]::Zero;$updaterSetup=$null;$originalDiscovery=$null
    function HoldRuntime([string]$Path,[bool]$Directory,[bool]$Protected=$false) {
        $handle=[SyncSharkDiscoverySetupNative]::Hold($Path,$Directory);$held.Add($handle)
        if($Protected){[SyncSharkDiscoverySetupNative]::RequireProtected($handle,$medium,$Directory)}
    }
    function HashRuntime([string]$Path) {
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $sha=[Security.Cryptography.SHA256]::Create()
        try {return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
        finally {$sha.Dispose();$stream.Dispose()}
    }
    function SignedRuntime([string]$Path) {
        [SyncSharkDiscoverySetupNative]::RequireProductSignature($Path)
        $signature=Get-AuthenticodeSignature -LiteralPath $Path
        $publisher=(Get-SyncSharkDiscoverySignerIdentity).roles.publisher
        if($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cnotin @($publisher.thumbprints) -or
            $signature.SignerCertificate.Subject -cne $publisher.subject -or $signature.SignerCertificate.Issuer -cne $publisher.subject -or
            $null -eq $signature.TimeStamperCertificate){throw 'The fixed setup input is not signed by the exact executable publisher.'}
    }
    try {
        # No writable module or script is executed. The only retained script is
        # authenticated, then its bounded JSON header is parsed as data only.
        $medium=[SyncSharkDiscoverySetupNative]::MediumToken()
        for($directory=$app;$directory -and $directory -ne [IO.Path]::GetPathRoot($directory);$directory=[IO.Path]::GetDirectoryName($directory)) {
            HoldRuntime $directory $true
        }
        $helper=[IO.Path]::Combine($app,'SyncSharkDiscovery.exe');HoldRuntime $helper $false;SignedRuntime $helper
        $productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($helper).ProductVersion
        if($productVersion -cnotmatch ('^'+[regex]::Escape($version)+'\+([0-9a-f]{40})$')){throw 'The setup source version is not frozen.'}
        $commit=$Matches[1];$transaction=[IO.Path]::Combine($product,'replacement-staging','transactions',$version+'-'+$commit.Substring(0,7))
        for($directory=$transaction;$directory -and $directory -ne $product;$directory=[IO.Path]::GetDirectoryName($directory)){HoldRuntime $directory $true}
        $installer=[IO.Path]::Combine($transaction,'Install-SyncShark.ps1');HoldRuntime $installer $false
        if(([IO.FileInfo]$installer).Length -gt 1048576){throw 'Oversized signed runtime contract.'}
        SignedRuntime $installer
        $reader=[IO.File]::OpenText($installer)
        try {$header=$reader.ReadLine()} finally {$reader.Dispose()}
        $prefix='# SyncSharkUpdateContract: '
        if(-not $header.StartsWith($prefix,[StringComparison]::Ordinal) -or $header.Length -gt 16384){throw 'The signed runtime contract is missing.'}
        $contract=$header.Substring($prefix.Length)|ConvertFrom-Json
        $members=Assert-SyncSharkProtectedRuntimeContract $contract $productVersion
        HoldRuntime ([IO.Path]::Combine($app,'Assets')) $true
        $actual=@([IO.Directory]::EnumerateFileSystemEntries($app))
        if($actual.Count -ne [SyncSharkProtectedRuntimeTransaction]::Members.Length){throw 'Unexpected runtime source inventory.'}
        foreach($entry in $actual) {
            $name=[IO.Path]::GetFileName($entry)
            if($name -ceq 'Assets'){continue}
            if($name -cnotin [SyncSharkProtectedRuntimeTransaction]::Members){throw 'Unexpected runtime source directory or member.'}
            HoldRuntime $entry $false
        }
        $assets=@([IO.Directory]::EnumerateFileSystemEntries([IO.Path]::Combine($app,'Assets')))
        if($assets.Count -ne 1 -or [IO.Path]::GetFileName($assets[0]) -cne 'SyncShark-64.png'){throw 'Unexpected runtime source assets.'}
        foreach($name in [SyncSharkProtectedRuntimeTransaction]::Members) {
            $path=[IO.Path]::Combine($app,$name.Replace('/','\'));HoldRuntime $path $false
            if(([IO.FileInfo]$path).Length -ne $members[$name].bytes -or (HashRuntime $path) -cne $members[$name].sha256){throw 'A signed runtime source member changed.'}
        }
        foreach($name in @('SyncShark.exe','SyncShark.dll','SyncShark.Core.dll')) {
            $path=[IO.Path]::Combine($app,$name);SignedRuntime $path
            if([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -cne $productVersion){throw 'The runtime source revisions differ.'}
        }
        $parent.Refresh();if($parent.HasExited){throw 'The requesting process closed before protected setup.'}
        if(@(Get-Process -Name 'SyncShark' -ErrorAction SilentlyContinue).Count){throw 'Close Sync Shark before changing protected program files.'}
        if($Mode -eq 'RuntimeInstall' -and $null -ne $contract.PSObject.Properties['protectedUpdater']) {$updaterSetup=[SyncSharkUpdaterSetup]::Prepare($productVersion)}
        if($Mode -eq 'RuntimeRollback' -and $null -ne $contract.PSObject.Properties['protectedUpdater']) {$originalDiscovery=[SyncSharkOriginalDiscovery]::Prepare($productVersion)}
        Assert-SyncSharkDiscoverySetupRoots $(if($Mode -eq 'RuntimeInstall'){'Configure'}else{'Remove'})
        $protected='C:\Program Files\Massdex\SyncShark';$versions=[IO.Path]::Combine($protected,'versions')
        foreach($directory in @('C:\Program Files','C:\Program Files\Massdex',$protected)){HoldRuntime $directory $true $true}
        $claim=if($null -ne $updaterSetup -or $null -ne $originalDiscovery){[SyncSharkDiscoveryTransaction]::new($protected,$identity.User.Value,$true)}else{[SyncSharkDiscoveryTransaction]::new($protected,$identity.User.Value)}
        $originalAlreadyRestored=$false
        if($null -ne $originalDiscovery){$originalAlreadyRestored=$originalDiscovery.ValidateBefore($claim)}
        if(-not [IO.Directory]::Exists($versions)) {
            if($claim.HasPending){throw 'A pending protected recovery has no complete caller runtime.'}
            [SyncSharkDiscoverySetupNative]::CreateProtectedDirectory($versions)
        }
        HoldRuntime $versions $true $true
        $hashes=[string[]]@([SyncSharkProtectedRuntimeTransaction]::Members|ForEach-Object {$members[$_].sha256})
        $sizes=[long[]]@([SyncSharkProtectedRuntimeTransaction]::Members|ForEach-Object {$members[$_].bytes})
        $runtime=[SyncSharkProtectedRuntimeTransaction]::new($version,$hashes,$sizes)
        $discoveryCompleted=$false
        if($claim.HasPending -and -not $claim.UpdaterPending -and $null -eq $updaterSetup -and $null -eq $originalDiscovery) {
            # An interrupted integrated setup can recover only through the existing
            # task-aware transaction, and only after its complete protected caller
            # runtime is authenticated. Do not recover a journal by copying files.
            $runtime.Verify($contract.protectedRuntimeRoot)
            $claim.Dispose();$claim=$null
            $recoveryMode=@{RuntimeInstall='Configure';RuntimeRemove='Remove';RuntimeRollback='Restore'}[$Mode]
            Invoke-SyncSharkDiscoverySetup -Mode $recoveryMode -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated
            $claim=[SyncSharkDiscoveryTransaction]::new($protected,$identity.User.Value)
            if($claim.HasPending){throw 'The existing protected recovery did not complete.'}
            $discoveryCompleted=$true
        }
        if($Mode -eq 'RuntimeInstall'){$runtime.Install($app)}
        $claim.Dispose();$claim=$null
        if($null -ne $originalDiscovery) {
            if(-not $originalAlreadyRestored) {
                Invoke-SyncSharkDiscoverySetup -Mode Restore -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated -OriginalDiscovery $originalDiscovery
            }
            $discoveryCompleted=$true
        }
        if($Mode -in @('RuntimeRemove','RuntimeRollback') -and $null -ne $contract.PSObject.Properties['protectedUpdater']) {
            [SyncSharkUpdaterSetup]::Retire($productVersion)
        }
        # Rebind an already-enabled Discovery installation within this same
        # attended setup. No new task/service is introduced by runtime placement.
        if($null -ne $updaterSetup) {
            if($updaterSetup.NeedsDiscoveryRebind) {
                $updaterSetup.BeginDiscoveryRebind()
                Invoke-SyncSharkDiscoverySetup -Mode Configure -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated -UpdaterSession $updaterSetup
            }
            $updaterSetup.Publish()
        } elseif(-not $discoveryCompleted -and $Mode -eq 'RuntimeInstall' -and [IO.File]::Exists([IO.Path]::Combine($protected,'SyncSharkDiscovery.exe'))) {
            Invoke-SyncSharkDiscoverySetup -Mode Configure -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated
        }
        if(-not $discoveryCompleted -and $Mode -eq 'RuntimeRollback' -and [IO.File]::Exists([IO.Path]::Combine($protected,'SyncSharkDiscovery.exe'))) {
            $saved=[IO.File]::ReadAllText([IO.Path]::Combine($protected,'discovery-manifest.json'))|ConvertFrom-Json
            if($saved.appDirectory -ceq $contract.protectedRuntimeRoot) {
                Invoke-SyncSharkDiscoverySetup -Mode Restore -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated
            } else {
                $retainedWorker=[IO.Path]::Combine($protected,'SyncSharkDiscovery.exe')
                $retainedManifest=[IO.Path]::Combine($protected,'discovery-manifest.json')
                HoldRuntime $retainedWorker $false $true;HoldRuntime $retainedManifest $false $true
                $beforeWorker='b8bd8d2eb0cb17ebe2793d4a181321d213e98b7cf11fadd427e70526f6886994'
                $beforeManifest='ab7fe64bc0a2cf6992484b0f59ff9351a347a58c6e345d5f6544d34faa3fea7e'
                if($null -ne $contract.PSObject.Properties['protectedUpdater'] -and $contract.protectedUpdater.discovery.enabled) {
                    $beforeWorker=$contract.protectedUpdater.discovery.beforeWorkerSha256;$beforeManifest=$contract.protectedUpdater.discovery.beforeManifestSha256
                }
                if((HashRuntime $retainedWorker) -cne $beforeWorker -or (HashRuntime $retainedManifest) -cne $beforeManifest) {
                    throw 'Discovery is not bound to the candidate or its exact retained preimage.'
                }
                [SyncSharkDiscoverySetupNative]::RequireProductSignature($retainedWorker)
            }
        }
        if($Mode -eq 'RuntimeRemove') {
            if(-not $discoveryCompleted -and [IO.File]::Exists([IO.Path]::Combine($protected,'SyncSharkDiscovery.exe'))) {
                Invoke-SyncSharkDiscoverySetup -Mode Remove -ParentProcessId $ParentProcessId -AppDirectory $contract.protectedRuntimeRoot -RuntimeReleaseValidated
            }
            $claim=[SyncSharkDiscoveryTransaction]::new($protected,$identity.User.Value)
            if($claim.HasPending){throw 'Discovery removal did not finish; program files were retained.'}
            $runtime.Retire()
        }
    }
    finally {
        if($null -ne $claim){$claim.Dispose()}
        if($null -ne $updaterSetup){$updaterSetup.Dispose()}
        if($null -ne $originalDiscovery){$originalDiscovery.Dispose()}
        for($i=$held.Count-1;$i -ge 0;$i--){$held[$i].Dispose()}
        if($medium -ne [IntPtr]::Zero){[SyncSharkDiscoverySetupNative]::CloseToken($medium)}
    }
}
