#if UPDATER_CONTRACT_STANDALONE
#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Standalone no-UI fixture: invokes the production admission predicate. Synthetic
// observations are inputs, not evidence of Windows signature/task/ACL verification.
internal static class SyncSharkUpdaterContractChecks
{
    private const string Sid="S-1-5-21-123-456-789-1001";
    private static readonly string Hash=new string('a',64), Other=new string('b',64), Revision=new string('c',40);
    private static int checks;
    private static void Check(bool condition,string message)
    {if(!condition)throw new InvalidDataException(message);checks++;}
    private static void Reject(Action operation,string message)
    {try{operation();}catch(InvalidDataException){checks++;return;}throw new InvalidDataException("Accepted: "+message);}
    private static SyncSharkUpdaterContract.Member[] Members()
    {return SyncSharkProtectedRuntimeTransaction.Members.Select(p=>new SyncSharkUpdaterContract.Member(p,Hash,100)).ToArray();}
    private static SyncSharkUpdaterContract.Member Worker()
    {return new SyncSharkUpdaterContract.Member("SyncSharkUpdater.exe",Other,1000);}
    private static SyncSharkUpdaterContract Contract(bool discovery=false,string version="0.3.49",string previous="0.3.48")
    {return new SyncSharkUpdaterContract(version,Revision,previous+"+"+Revision,Hash,Sid,Hash,Members(),
        discovery?new SyncSharkUpdaterContract.DiscoveryBinding(true,Other,Other,Hash,Hash,"restore-exact-preimage"):
        new SyncSharkUpdaterContract.DiscoveryBinding(false,null,null,null,null,"leave-disabled"),Worker());}
    private static SyncSharkUpdaterObservation Observation(bool discovery=false)
    {return new SyncSharkUpdaterObservation{
        ElevatedSplitToken=true,FixedWorkerIdentity=true,ProtectedAclAndPaths=true,ExactTaskIdentity=true,
        AuthenticatedInstaller=true,AuthenticatedRuntime=true,SourceShapeAndHashes=true,PredecessorShapeAndHashes=true,
        JournalIdentity=true,JournalCompatible=true,PublicationStepPending=true,RollbackAvailable=true,ProductStopped=true,
        UserSid=Sid,PreviousProductVersion="0.3.48+"+Revision,PreviousTree=Hash,HighestPublishedVersion="0.3.48",
        DiscoveryEnabled=discovery,DiscoveryWorker=discovery?Other:null,DiscoveryManifest=discovery?Other:null};}
    private static string Pending()
    {return "{\"schemaVersion\":1,\"version\":\"0.3.49\",\"sourceRevision\":\""+Revision+"\",\"installerSha256\":\""+Hash+"\"}";}
    private static string Header()
    {
        string members=String.Join(",",Members().Select(m=>"{\"path\":\""+m.Path+"\",\"bytes\":100,\"sha256\":\""+Hash+"\"}"));
        return SyncSharkUpdaterContract.HeaderPrefix+"{\"product\":\"Massdex.SyncShark\",\"version\":\"0.3.49\",\"sourceRevision\":\""+Revision+
            "\",\"previousVersion\":\"0.3.48+"+Revision+"\",\"moduleSha256\":\""+Hash+"\",\"manifestSha256\":\""+Hash+
            "\",\"signedCoreSha256\":\""+Hash+"\",\"signedUiSha256\":\""+Hash+"\",\"signedExeSha256\":\""+Hash+
            "\",\"signedExplorerSha256\":\""+Hash+"\",\"signedDiscoverySha256\":\""+Hash+"\",\"signedIdentityPackageSha256\":\""+Hash+
            "\",\"protectedRuntimeRoot\":\"C:\\\\Program Files\\\\Massdex\\\\SyncShark\\\\versions\\\\0.3.49\",\"runtimeMembers\":["+members+
            "],\"protectedUpdater\":{\"schemaVersion\":1,\"userSid\":\""+Sid+"\",\"previousTreeSha256\":\""+Hash+
            "\",\"workerSha256\":\""+Other+"\",\"workerBytes\":1000,\"discovery\":{\"enabled\":false,\"beforeWorkerSha256\":null,\"beforeManifestSha256\":null,\"afterWorkerSha256\":null,\"afterManifestSha256\":null,\"recovery\":\"leave-disabled\"}}}";
    }
    private static void DataChecks()
    {
        byte[] originalManifest=SyncSharkUpdaterManifest.Discovery(Contract(true));string manifestHash;
        using(var sha=System.Security.Cryptography.SHA256.Create())manifestHash=SyncSharkUpdaterFiles.Hex(sha.ComputeHash(originalManifest));
        string original="{\"manifestBase64\":\""+Convert.ToBase64String(originalManifest)+"\",\"manifestSha256\":\""+manifestHash+"\",\"treeSha256\":\""+Hash+"\"}";
        var originalPair=SyncSharkOriginalDiscovery.ReadPair(SyncSharkUpdaterData.Read(original,16384),Sid);
        Check(originalPair.Members.Length==16 && originalPair.Version=="0.3.49+"+Revision && originalPair.Manifest.SequenceEqual(originalManifest),"Original restoration preserves exact manifest bytes and derives one fixed retained runtime.");
        foreach(string bad in new[]{original.Replace(manifestHash,Other),original.Replace("{","{\"path\":\"C:\\\\other\","),
            original.Replace("{","{\"treeSha256\":\""+Hash+"\","),original.Replace("\"treeSha256\":\""+Hash+"\"","\"treeSha256\":null"),original+"{}"})
            Reject(()=>SyncSharkOriginalDiscovery.ReadPair(SyncSharkUpdaterData.Read(bad,16384),Sid),"Malformed original-pair restoration data");
        Reject(()=>SyncSharkOriginalDiscovery.ReadPair(SyncSharkUpdaterData.Read(original,16384),Sid+"1"),"Original restoration account mismatch");
        var plan=SyncSharkUpdaterData.ReadPendingPlan(Pending());
        Check(plan.TransactionName=="0.3.49-ccccccc","Pending selector derives its transaction; it accepts no caller path.");
        foreach(string bad in new[]{Pending().Replace("{","{\"version\":\"0.3.50\","),Pending().Replace("{","{\"path\":\"C:\\\\Windows\","),
            Pending().Replace("\"schemaVersion\":1","\"schemaVersion\":1.0"),Pending().Replace("\"schemaVersion\":1","\"schemaVersion\":true"),
            Pending()+"{}",Pending().Replace("0.3.49","../0.3.49"),new string(' ',1025)})
            Reject(()=>SyncSharkUpdaterData.ReadPendingPlan(bad),"malformed pending plan: "+bad);
        var contract=SyncSharkUpdaterData.ReadSignedHeader(Header(),plan);
        Check(contract.Members.Length==16 && contract.PreviousTree==Hash && contract.UserSid==Sid && contract.UpdaterWorker.Sha256==Other &&
            contract.UpdaterWorker.Bytes==1000,"Signed data preserves runtime and fixed updater authority bindings separately.");
        foreach(string bad in new[]{Header().Replace("\"product\":","\"product\":\"other\",\"product\":"),
            Header().Replace("\"product\":","\"run\":\"calc.exe\",\"product\":"),
            Header().Replace("\"bytes\":100","\"bytes\":100.5"),Header().Replace("\"enabled\":false","\"enabled\":\"false\""),
            Header().Replace("\"previousTreeSha256\":\""+Hash+"\"","\"previousTreeSha256\":null"),
            Header().Replace("\"workerBytes\":1000","\"workerBytes\":0"),Header().Replace("\"workerBytes\":1000","\"workerBytes\":16777217"),
            Header().Replace("\"workerSha256\":\""+Other+"\"","\"workerSha256\":null"),
            Header().Replace("\"signedCoreSha256\":\""+Hash+"\"","\"signedCoreSha256\":\""+Other+"\""),
            Header().Replace("versions\\\\0.3.49","versions\\\\0.3.50"),Header().Replace("0.3.49","0.3.50"),
            Header().Replace("\"schemaVersion\":1","\"schemaVersion\":2"),Header()+"{}",Header()+new string(' ',16384)})
            Reject(()=>SyncSharkUpdaterData.ReadSignedHeader(bad,plan),"malformed signed header");
        string steps="[\"stage-package\",\"publish-runtime\",\"backup-state\",\"create-next-link\",\"retain-predecessor\",\"publish-successor\",\"register-explorer-package\",\"verify-complete\"]";
        string journal="{\"schema\":1,\"operation\":\"install\",\"version\":\"0.3.49\",\"commit\":\""+Revision+
            "\",\"product_root\":\"test-root\",\"status\":\"active\",\"pending\":\"publish-runtime\",\"completed\":[\"stage-package\"],\"allowed_steps\":"+steps+",\"artifacts\":{\"state_predecessor_sha256\":null}}";
        Check(SyncSharkUpdaterData.PublicationStepPending(journal,contract,"test-root"),"The real installer publication phase is accepted.");
        foreach(string bad in new[]{journal.Replace("test-root","foreign-root"),journal.Replace("\"install\"","\"rollback\""),
            journal.Replace("\"pending\":\"publish-runtime\"","\"pending\":null"),journal.Replace("\"completed\":[\"stage-package\"]","\"completed\":[]"),
            journal.Replace("\"completed\":[\"stage-package\"]","\"completed\":[\"publish-runtime\",\"stage-package\"]"),
            journal.Replace("\"status\":\"active\"","\"status\":\"complete\"")})
            Reject(()=>SyncSharkUpdaterData.PublicationStepPending(bad,contract,"test-root"),"foreign/incompatible journal");
        string complete=journal.Replace("\"status\":\"active\"","\"status\":\"complete\"").Replace("\"pending\":\"publish-runtime\"","\"pending\":null")
            .Replace("\"completed\":[\"stage-package\"]","\"completed\":"+steps).Replace("\"state_predecessor_sha256\":null","\"state_predecessor_sha256\":\""+Hash+"\"");
        Check(!SyncSharkUpdaterData.PublicationStepPending(complete,contract,"test-root"),"A completed exact journal permits readback only.");
        string uninstall="{\"schema\":1,\"operation\":\"uninstall\",\"version\":\"0.3.49\",\"commit\":\""+Revision+
            "\",\"product_root\":\"test-root\",\"status\":\"complete\",\"pending\":null,\"completed\":[\"retain-active-pointer\",\"retain-runtime\",\"verify-uninstalled\"],"+
            "\"allowed_steps\":[\"retain-active-pointer\",\"retain-runtime\",\"verify-uninstalled\"],\"artifacts\":{\"runtime_sha256\":\""+Hash+
            "\",\"settings_retained\":true,\"recovery_retained\":true,\"restored_utc\":null,\"previous_undo_receipt\":null}}";
        SyncSharkUpdaterData.UninstallRestoration(uninstall,contract,"test-root",Hash);Check(true,"Completed uninstall permits only the exact retained runtime restoration.");
        string interrupted=uninstall.Replace("\"status\":\"complete\"","\"status\":\"active\"").Replace("\"pending\":null","\"pending\":\"retain-runtime\"")
            .Replace("\"completed\":[\"retain-active-pointer\",\"retain-runtime\",\"verify-uninstalled\"]","\"completed\":[\"retain-active-pointer\"]");
        SyncSharkUpdaterData.UninstallRestoration(interrupted,contract,"test-root",Hash);Check(true,"Exact failed uninstall runtime phase permits restoration without inventing an update.");
        foreach(string bad in new[]{uninstall.Replace("test-root","foreign"),uninstall.Replace("0.3.49","0.3.50"),
            uninstall.Replace(Hash,Other),uninstall.Replace("\"restored_utc\":null","\"restored_utc\":\"already restored\""),
            uninstall.Replace("\"settings_retained\":true","\"settings_retained\":false"),interrupted.Replace("\"pending\":\"retain-runtime\"","\"pending\":null"),
            uninstall.Replace("\"operation\":\"uninstall\"","\"operation\":\"install\""),uninstall.Replace("{\"schema\":1","{\"schema\":1,\"schema\":1")})
            Reject(()=>SyncSharkUpdaterData.UninstallRestoration(bad,contract,"test-root",Hash),"foreign or previously completed uninstall restoration");
    }
    internal static int Run()
    {
        checks=0;DataChecks();var contract=Contract();var observation=Observation();
        Reject(()=>SyncSharkUpdaterSetup.Prepare("UNBOUND"),"initial setup without exact frozen identity");
        var manifest=SyncSharkUpdaterManifest.Discovery(contract);
        var reordered=new SyncSharkUpdaterContract(contract.Version,contract.SourceRevision,contract.PreviousVersion,contract.PreviousTree,
            contract.UserSid,contract.InstallerSha256,contract.Members.Reverse().ToArray(),contract.Discovery,contract.UpdaterWorker);
        Check(manifest.SequenceEqual(SyncSharkUpdaterManifest.Discovery(reordered)),"Discovery postimage is independent of input inventory ordering.");
        Check(!manifest.Take(3).SequenceEqual(new byte[]{239,187,191}) && manifest.All(b=>b<128),"Discovery postimage uses exact BOM-free ASCII JSON.");
        string xml=SyncSharkUpdaterTask.Xml(Sid),sddl=SyncSharkUpdaterTask.Security(Sid);
        SyncSharkUpdaterTask.Validate(xml,sddl,Sid);Check(true,"Exact fixed task policy accepted.");
        string normalized=xml.Replace("</Settings>","<DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>"+
            "<UseUnifiedSchedulingEngine>false</UseUnifiedSchedulingEngine><Priority>7</Priority></Settings>")
            .Replace("</RegistrationInfo>","<URI>\\SyncSharkUpdater</URI></RegistrationInfo>")
            .Replace("</IdleSettings>","<Duration>PT10M</Duration><WaitTimeout>PT1H</WaitTimeout></IdleSettings>");
        SyncSharkUpdaterTask.Validate(normalized,sddl,Sid);Check(true,"Only schema-default normalization fields and the exact registered URI are admitted.");
        foreach(string bad in new[]{xml.Replace("</Task>","<Data>unexamined</Data></Task>"),
            xml.Replace("</Principal>","<RequiredPrivileges><Privilege>SeDebugPrivilege</Privilege></RequiredPrivileges></Principal>"),
            xml.Replace("</Principal>","<ProcessTokenSidType>Unrestricted</ProcessTokenSidType></Principal>"),
            xml.Replace("</Settings>","<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>"),
            xml.Replace("</IdleSettings>","<Unknown>false</Unknown></IdleSettings>"),
            xml.Replace("<Settings>","<Settings extra=\"false\">"),xml.Replace("<UserId>","<UserId extra=\"false\">"),
            xml.Replace("<Task version=", "<Task changed=\"true\" version="),
            xml.Replace("<Triggers />","<Triggers>unexpected</Triggers>"),
            normalized.Replace("<Priority>7","<Priority>6"),
            normalized.Replace("<UseUnifiedSchedulingEngine>false","<UseUnifiedSchedulingEngine>true"),
            normalized.Replace("<DisallowStartOnRemoteAppSession>false","<DisallowStartOnRemoteAppSession>true"),
            normalized.Replace("<Duration>PT10M","<Duration>PT1M"),normalized.Replace("<WaitTimeout>PT1H","<WaitTimeout>PT2H"),
            normalized.Replace("<URI>\\SyncSharkUpdater","<URI>\\OtherTask")})
            Reject(()=>SyncSharkUpdaterTask.Validate(bad,sddl,Sid),"unexamined or changed normalized task policy");
        foreach(string bad in new[]{xml.Replace("<Triggers />","<Triggers><TimeTrigger /></Triggers>"),xml.Replace("HighestAvailable","LeastPrivilege"),
            xml.Replace("<Exec>","<Exec><Arguments>run-command</Arguments>"),xml.Replace("SyncSharkUpdater.exe","powershell.exe"),
            xml.Replace("<Exec>","<Exec><Arguments> </Arguments>"),
            xml.Replace("<Hidden>true","<Hidden>false"),xml.Replace("PT10M","PT0S"),xml.Replace("IgnoreNew","Parallel"),
            xml.Replace("<RunOnlyIfIdle>false","<RunOnlyIfIdle>true"),xml.Replace("<AllowHardTerminate>false","<AllowHardTerminate>true"),
            xml.Replace("<Exec>","<Exec><Exec />"),xml.Replace("<UserId>","<UserId>"+Sid+"</UserId><UserId>")})
            Reject(()=>SyncSharkUpdaterTask.Validate(bad,sddl,Sid),"task execution drift");
        foreach(string bad in new[]{sddl.Replace("GRGX","GA"),sddl.Replace("O:BA","O:"+Sid),sddl.Replace("D:P","D:"),
            sddl+"(A;;GR;;;BU)",sddl.Replace(Sid,"S-1-5-21-123-456-789-1002")})
            Reject(()=>SyncSharkUpdaterTask.Validate(xml,bad,Sid),"task security drift");
        string discoveryXml=xml.Replace("SyncSharkUpdater.exe","SyncSharkDiscovery.exe").Replace("PT10M","PT31M")
            .Replace("<Hidden>true","<Hidden>false").Replace("<AllowHardTerminate>false","<AllowHardTerminate>true")
            .Replace("Sync Shark verified protected runtime publication","Sync Shark read-only NTFS discovery");
        Check(SyncSharkUpdaterTask.ValidateDiscovery(discoveryXml,sddl,Sid,"0.3.48+"+Revision),"Exact enabled Discovery task admitted before publication.");
        string compactDiscovery=discoveryXml;
        foreach(string name in new[]{"StartWhenAvailable","RunOnlyIfNetworkAvailable","Hidden","RunOnlyIfIdle","WakeToRun"})compactDiscovery=compactDiscovery.Replace("<"+name+">false</"+name+">","");
        foreach(string name in new[]{"AllowHardTerminate","AllowStartOnDemand","Enabled"})compactDiscovery=compactDiscovery.Replace("<"+name+">true</"+name+">","");
        compactDiscovery=compactDiscovery.Replace("</Settings>","<UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine></Settings>");
        Check(SyncSharkUpdaterTask.ValidateDiscovery(compactDiscovery,sddl,Sid,"0.3.48+"+Revision),"Observed registered Discovery serialization preserves the same effective settings.");
        Reject(()=>SyncSharkUpdaterTask.ValidateDiscovery(compactDiscovery.Replace("</Settings>","<AllowStartOnDemand>false</AllowStartOnDemand></Settings>"),sddl,Sid,"0.3.48+"+Revision),"Explicit false does not inherit the omitted true default");
        Reject(()=>SyncSharkUpdaterTask.Validate(xml.Replace("<AllowHardTerminate>false</AllowHardTerminate>",""),sddl,Sid),"Updater cannot omit its required nondefault no-hard-termination policy");
        foreach(string name in new[]{"DisallowStartIfOnBatteries","StopIfGoingOnBatteries"})
            Reject(()=>SyncSharkUpdaterTask.Validate(xml.Replace("<"+name+">false</"+name+">",""),sddl,Sid),"Missing battery policy retains Windows true default: "+name);
        Reject(()=>SyncSharkUpdaterTask.Validate(xml.Replace("<Hidden>true</Hidden>",""),sddl,Sid),"Updater cannot omit its required hidden setting");
        Check(!SyncSharkUpdaterTask.ValidateDiscovery(discoveryXml.Replace("<Enabled>true","<Enabled>false"),sddl,Sid,"0.3.48+"+Revision),
            "Disabled Discovery task is validated but grants no rebind.");
        Check(SyncSharkUpdaterTask.ValidateDiscovery(discoveryXml.Replace("PT31M","PT2M"),sddl,Sid,"0.3.31+"+Revision),"Retained worker requires its original task limit.");
        foreach(string bad in new[]{discoveryXml.Replace("PT31M","PT2M"),discoveryXml.Replace("<Exec>","<Exec><Arguments>--install</Arguments>"),
            discoveryXml.Replace("SyncSharkDiscovery.exe","SyncSharkUpdater.exe"),discoveryXml.Replace("<RestartOnIdle>false","<RestartOnIdle>true")})
            Reject(()=>SyncSharkUpdaterTask.ValidateDiscovery(bad,sddl,Sid,"0.3.48+"+Revision),"Discovery task drift before protected publication");
        bool staging;
        Check(SyncSharkUpdaterFiles.VersionEntry("0.3.49.installing","0.3.49",out staging)=="0.3.49" && staging,"Exact interrupted staging is not published high-water.");
        Check(SyncSharkUpdaterFiles.VersionEntry("0.3.50.uninstalled","0.3.49",out staging)=="0.3.50" && !staging,"Retired later release remains high-water.");
        Check(SyncSharkUpdaterFiles.VersionEntry("0.3.48","0.3.49",out staging)=="0.3.48" && !staging,"Published predecessor remains high-water.");
        foreach(string bad in new[]{"0.3.50.installing","0.3.49.installing.uninstalled","0.3.49.INSTALLING","0.3.49.extra","../0.3.49"})
            Reject(()=>{bool ignored;SyncSharkUpdaterFiles.VersionEntry(bad,"0.3.49",out ignored);},"foreign or malformed protected staging");
        var admission=SyncSharkUpdaterAdmission.Evaluate(contract,observation);
        Check(admission.PublishRuntime && !admission.RebindDiscovery,"Disabled Discovery stays disabled on a valid publication.");
        foreach(var field in typeof(SyncSharkUpdaterObservation).GetFields(BindingFlags.Instance|BindingFlags.NonPublic)
            .Where(f=>f.FieldType==typeof(bool) && (bool)f.GetValue(Observation())))
        {
            var broken=Observation();field.SetValue(broken,false);
            Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,broken),field.Name);
        }
        foreach(string version in new[]{"0.3.48","0.3.47","01.3.49","0.3.49.0","0.3.49\n","..\\0.3.49","65536.0.0"})
            Reject(()=>Contract(false,version),"invalid/equal/downgrade version "+version);
        var copy=contract.Members;copy[0]=new SyncSharkUpdaterContract.Member("bad",Other,1);
        Check(contract.Members[0].Path==SyncSharkProtectedRuntimeTransaction.Members[0],"Contract inventory cannot be replaced after admission.");
        Action<SyncSharkUpdaterContract.Member[]> rejectMembers=members=>Reject(()=>new SyncSharkUpdaterContract("0.3.49",Revision,
            "0.3.48+"+Revision,Hash,Sid,Hash,members,contract.Discovery,Worker()),"malformed member inventory");
        rejectMembers(Members().Skip(1).ToArray());
        var changed=Members();changed[0]=changed[1];rejectMembers(changed);
        changed=Members();changed[0]=new SyncSharkUpdaterContract.Member("../SyncShark.exe",Hash,100);rejectMembers(changed);
        changed=Members();changed[0]=new SyncSharkUpdaterContract.Member(changed[0].Path.ToUpperInvariant(),Hash,100);rejectMembers(changed);
        foreach(long length in new long[]{0,-1,536870913}) {changed=Members();changed[0]=new SyncSharkUpdaterContract.Member(changed[0].Path,Hash,length);rejectMembers(changed);}
        changed=Members().Select(m=>new SyncSharkUpdaterContract.Member(m.Path,Hash,536870912)).ToArray();rejectMembers(changed);
        changed=Members();changed[0]=new SyncSharkUpdaterContract.Member(changed[0].Path,Hash.ToUpperInvariant(),100);rejectMembers(changed);
        foreach(string field in new[]{"UserSid","PreviousProductVersion","PreviousTree","HighestPublishedVersion"})
        {var bad=Observation();typeof(SyncSharkUpdaterObservation).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bad,"wrong");
            Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,bad),field);}
        var stale=Observation();stale.HighestPublishedVersion="0.3.50";Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,stale),"stale replay");
        var replay=Observation();replay.HighestPublishedVersion="0.3.49";
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,replay),"equal version without exact published tree");
        replay.TargetExists=true;Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,replay),"conflicting published bytes");
        replay.ExactTarget=true;admission=SyncSharkUpdaterAdmission.Evaluate(contract,replay);
        Check(!admission.PublishRuntime && !admission.RebindDiscovery,"Exact replay does not repeat protected publication.");
        var enabled=Contract(true);var enabledState=Observation(true);
        admission=SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState);Check(admission.RebindDiscovery,"Enabled Discovery gets the exact rebind.");
        enabledState.DiscoveryWorker=Hash;Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState),"mixed Discovery pair");
        enabledState.DiscoveryManifest=Hash;Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState),"postimage without published runtime");
        enabledState.TargetExists=true;enabledState.ExactTarget=true;enabledState.HighestPublishedVersion="0.3.49";
        admission=SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState);
        Check(!admission.PublishRuntime && !admission.RebindDiscovery,"Completed exact replay has no repeated Discovery transition.");
        enabledState.DiscoveryPending=true;Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState),"unresolved protected recovery");
        enabledState.DiscoveryRecoveryBound=true;enabledState.DiscoveryEnabled=false;
        admission=SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState);
        Check(admission.RebindDiscovery && !admission.PublishRuntime,"Exact signed pending recovery may restore previously enabled Discovery without republishing runtime.");
        enabledState.PublicationStepPending=false;
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState),"completed transaction cannot authorise recovery effects");
        enabledState.PublicationStepPending=true;enabledState.TargetExists=false;
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,enabledState),"recovery without already-published runtime");
        var disabledRecovery=Observation();disabledRecovery.TargetExists=true;disabledRecovery.ExactTarget=true;
        disabledRecovery.DiscoveryPending=true;disabledRecovery.DiscoveryRecoveryBound=true;
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,disabledRecovery),"disabled signed plan cannot grant recovery");
        SyncSharkUpdaterTask.ValidateRemoval(xml.Replace("<Enabled>true","<Enabled>false"),sddl,Sid,true);
        Check(true,"Exact pending removal can resume a disabled otherwise unchanged task.");
        Reject(()=>SyncSharkUpdaterTask.ValidateRemoval(xml.Replace("<Enabled>true","<Enabled>false"),sddl,Sid,false),"unbound disabled removal task");
        Reject(()=>SyncSharkUpdaterTask.ValidateRemoval(xml.Replace("<Enabled>true","<Enabled>false").Replace("PT10M","PT1M"),sddl,Sid,true),"pending removal cannot relax task policy");
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(enabled,Observation()),"Discovery disabled after signing");
        Reject(()=>SyncSharkUpdaterAdmission.Evaluate(contract,Observation(true)),"Discovery enabled after signing");
        Reject(()=>new SyncSharkUpdaterContract("0.3.49",Revision,"0.3.48+"+Revision,Hash,Sid,Hash,Members(),
            new SyncSharkUpdaterContract.DiscoveryBinding(false,Hash,null,null,null,"leave-disabled"),Worker()),"disabled rebind authority");
        Reject(()=>new SyncSharkUpdaterContract("0.3.49",Revision,"0.3.48+"+Revision,Hash,Sid,Hash,Members(),
            new SyncSharkUpdaterContract.DiscoveryBinding(true,Other,Other,Other,Hash,"restore-exact-preimage"),Worker()),"wrong Discovery successor worker");
        Reject(()=>new SyncSharkUpdaterContract("0.3.49",Revision,"0.3.48+"+Revision,Hash,Sid,Hash,Members(),
            new SyncSharkUpdaterContract.DiscoveryBinding(true,Other,Other,Hash,Hash,"run-script"),Worker()),"arbitrary recovery action");
        foreach(var worker in new[]{null,new SyncSharkUpdaterContract.Member("other.exe",Other,1000),new SyncSharkUpdaterContract.Member("SyncSharkUpdater.exe",Hash.ToUpperInvariant(),1000)})
            Reject(()=>new SyncSharkUpdaterContract("0.3.49",Revision,"0.3.48+"+Revision,Hash,Sid,Hash,Members(),contract.Discovery,worker),"missing, redirected or malformed fixed worker binding");
        Console.WriteLine("Updater admission: "+checks+" checks passed. Synthetic inputs; no Windows task, signature, protected write, UI or install was exercised.");
        return 0;
    }
    private static int ReceiptChecks(string output)
    {
        string allocation=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TestData","Projects","sync-shark","build")+Path.DirectorySeparatorChar;
        string parent=Path.GetFullPath(output);
        Check(parent.StartsWith(allocation,StringComparison.OrdinalIgnoreCase) && Directory.Exists(parent),"Receipt fixture stays in the existing build allocation.");
        string root=Path.Combine(parent,"receipt-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string receipt=Path.Combine(root,"updater-publication.json"),temporary=receipt+".new";
        string first="{\"fixture\":\"first\"}",second="{\"fixture\":\"second\"}";
        var utf8=new System.Text.UTF8Encoding(false);int start=checks;
        try
        {
            SyncSharkUpdaterFiles.ValidateReceiptDestination(root,first,IntPtr.Zero,false);
            Check(!File.Exists(receipt) && !File.Exists(temporary),"Read-only receipt admission creates no evidence files.");
            SyncSharkUpdaterFiles.PublishReceipt(root,first,IntPtr.Zero,false);
            Check(File.ReadAllText(receipt)==first && !File.Exists(temporary),"Actual receipt publication completes with no temporary.");
            var stamp=new DateTime(2001,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(receipt,stamp);
            SyncSharkUpdaterFiles.PublishReceipt(root,first,IntPtr.Zero,false);
            Check(File.GetLastWriteTimeUtc(receipt)==stamp,"Exact receipt replay does not rewrite its destination.");
            File.WriteAllText(temporary,second,utf8);
            SyncSharkUpdaterFiles.ValidateReceiptDestination(root,second,IntPtr.Zero,false);
            Check(File.ReadAllText(receipt)==first && File.ReadAllText(temporary)==second,"Matching staged evidence remains unchanged during admission.");
            SyncSharkUpdaterFiles.PublishReceipt(root,second,IntPtr.Zero,false);
            Check(File.ReadAllText(receipt)==second && !File.Exists(temporary),"Exact pre-rename temporary is reconciled through the production publisher.");
            File.SetLastWriteTimeUtc(receipt,stamp);File.WriteAllText(temporary,second,utf8);
            SyncSharkUpdaterFiles.PublishReceipt(root,second,IntPtr.Zero,false);
            Check(File.GetLastWriteTimeUtc(receipt)==stamp && !File.Exists(temporary),"Matching completed receipt cleans only its exact temporary.");
            foreach(byte[] foreign in new[]{utf8.GetBytes("foreign"),utf8.GetBytes(second.Substring(0,4)),System.Text.Encoding.Unicode.GetBytes(second)})
            {
                File.WriteAllBytes(temporary,foreign);
                Reject(()=>SyncSharkUpdaterFiles.ValidateReceiptDestination(root,second,IntPtr.Zero,false),"foreign evidence rejected by pre-effect admission");
                Check(File.ReadAllText(receipt)==second && File.ReadAllBytes(temporary).SequenceEqual(foreign),"Pre-effect refusal preserves both evidence files.");
                Reject(()=>SyncSharkUpdaterFiles.PublishReceipt(root,second,IntPtr.Zero,false),"foreign, torn or differently encoded receipt staging");
                Check(File.ReadAllText(receipt)==second && File.ReadAllBytes(temporary).SequenceEqual(foreign),"Rejected staging and completed evidence are both preserved.");
                File.Delete(temporary);
            }
            Directory.CreateDirectory(temporary);
            Reject(()=>SyncSharkUpdaterFiles.ValidateReceiptDestination(root,second,IntPtr.Zero,false),"directory at evidence staging rejected before effects");
            Reject(()=>SyncSharkUpdaterFiles.PublishReceipt(root,second,IntPtr.Zero,false),"directory at receipt staging");
            Check(Directory.Exists(temporary) && File.ReadAllText(receipt)==second,"Unexpected directory is preserved on refusal.");
            Directory.Delete(temporary,false);
            Console.WriteLine("Updater receipt: "+(checks-start)+" isolated real filesystem assertions; no protected path, task or runtime publication.");
            return 0;
        }
        finally
        {
            foreach(string path in Directory.GetFileSystemEntries(root))
            {
                if(path!=receipt && path!=temporary || (File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
                    throw new IOException("Unexpected receipt fixture member preserved.");
            }
            foreach(string path in Directory.GetFiles(root))File.Delete(path);
            Directory.Delete(root,false);
        }
    }
    private sealed class Interrupted : Exception { }
    private static string Digest(string path)
    {using(var stream=File.OpenRead(path))using(var sha=System.Security.Cryptography.SHA256.Create())return SyncSharkUpdaterFiles.Hex(sha.ComputeHash(stream));}
    private static void RejectIo(Action operation,string message)
    {try{operation();}catch(IOException){checks++;return;}throw new IOException("Accepted: "+message);}
    private static int RecoveryChecks(string output)
    {
        string allocation=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TestData","Projects","sync-shark","build")+Path.DirectorySeparatorChar;
        string parent=Path.GetFullPath(output);
        Check(parent.StartsWith(allocation,StringComparison.OrdinalIgnoreCase) && Directory.Exists(parent),"Recovery fixture allocation is exact.");
        string suite=Path.Combine(parent,"recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(suite);
        string candidate=Path.Combine(suite,"candidate.exe");File.WriteAllText(candidate,"inert fixture updater worker");
        string expected=Digest(candidate);int start=checks;
        try
        {
            int index=0;
            foreach(bool removing in new[]{false,true})
            foreach(string point in new[]{"journal-written-Preparing","journal-published-Preparing","stage-created","journal-written-Ready","journal-published-Ready",removing?"member-deleted-0":"member-published-0","journal-written-Committed","journal-published-Committed","stage-removed"})
            {
                string root=Path.Combine(suite,"lifecycle-"+(index++));Directory.CreateDirectory(root);
                string worker=Path.Combine(root,"SyncSharkUpdater.exe");if(removing)File.Copy(candidate,worker);
                try
                {
                    using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,at=>{if(at==point)throw new Interrupted();}))
                    {tx.ResumeUpdater(candidate,expected,removing);tx.CompleteUpdater();}
                    throw new IOException("Lifecycle checkpoint not reached: "+point);
                }
                catch(Interrupted){}
                Check(SyncSharkDiscoveryTransaction.InspectUpdater(root,Sid,expected,removing,false),"Real signed lifecycle can be inspected after "+point);
                using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,(Action<string>)null,true))
                {
                    RejectIo(()=>tx.Recover(),"Discovery recovery must not accidentally retire or restore an updater");
                    tx.ResumeUpdater(candidate,expected,removing);
                    Check(File.Exists(Path.Combine(root,"discovery-setup.json")),"Lifecycle fence remains until task-aware caller confirms its postcondition.");
                    tx.CompleteUpdater();
                    Check(!tx.HasPending && File.Exists(worker)!=removing && (removing || Digest(worker)==expected),"Lifecycle interruption reconciles exact bytes and closes after readback.");
                }
            }
            string[] names={"SyncSharkDiscovery.exe","discovery-manifest.json","SyncSharkDiscovery.exe.previous","discovery-manifest.json.previous"};
            byte[] manifest=System.Text.Encoding.UTF8.GetBytes("candidate manifest");string afterManifest;
            using(var sha=System.Security.Cryptography.SHA256.Create())afterManifest=SyncSharkUpdaterFiles.Hex(sha.ComputeHash(manifest));
            foreach(string point in new[]{"journal-written-Preparing","journal-published-Preparing","stage-created","journal-written-Ready","journal-published-Ready","member-published-0","member-published-1","journal-written-Committed","journal-published-Committed","cleanup-old-0"})
            {
                string root=Path.Combine(suite,"discovery-"+(index++));Directory.CreateDirectory(root);
                for(int i=0;i<4;i++)File.WriteAllText(Path.Combine(root,names[i]),"old-"+i);
                string beforeWorker=Digest(Path.Combine(root,names[0])),beforeManifest=Digest(Path.Combine(root,names[1]));
                try
                {
                    using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,at=>{if(at==point)throw new Interrupted();}))
                    {tx.Prepare("Configure",candidate,manifest);tx.Apply();tx.Complete();}
                    throw new IOException("Discovery checkpoint not reached: "+point);
                }
                catch(Interrupted){}
                Check(SyncSharkDiscoveryTransaction.InspectConfigure(root,Sid,beforeWorker,beforeManifest,expected,afterManifest,false),"Exact signed Discovery journal is admitted after "+point);
                using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,(Action<string>)null,true))
                {
                    tx.ValidateConfigure(beforeWorker,beforeManifest,expected,afterManifest);
                    tx.RecoverRetainingFence();
                    Check(tx.HasPending && File.Exists(Path.Combine(root,"discovery-setup.json")),"Recovery cannot expose a disabled task without its fence.");
                    bool committed=point=="journal-published-Committed" || point=="cleanup-old-0";
                    Check(Digest(Path.Combine(root,names[0]))==(committed?expected:beforeWorker),"Recovery obeys signed preimage and durable commit boundary.");
                    // Filesystem proof only. Production task enabled-state readback
                    // occurs before this call and requires the separate real proof.
                    tx.FinishRecovery();Check(!tx.HasPending,"Fence closes only on explicit task-aware completion.");
                }
            }
            string originalWorker=Path.Combine(suite,"original.exe"),originalPreviousWorker=Path.Combine(suite,"original-previous.exe");
            File.WriteAllText(originalWorker,"original P worker");File.WriteAllText(originalPreviousWorker,"original Q worker");
            string secondWorker=Path.Combine(suite,"second.exe");File.WriteAllText(secondWorker,"candidate B worker");
            byte[] secondManifest=System.Text.Encoding.UTF8.GetBytes("candidate B manifest");
            byte[] originalManifest=System.Text.Encoding.UTF8.GetBytes("original P manifest"),originalPreviousManifest=System.Text.Encoding.UTF8.GetBytes("original Q manifest");
            string[] original={Digest(originalWorker),null,Digest(originalPreviousWorker),null};
            using(var sha=System.Security.Cryptography.SHA256.Create())
            {original[1]=SyncSharkUpdaterFiles.Hex(sha.ComputeHash(originalManifest));original[3]=SyncSharkUpdaterFiles.Hex(sha.ComputeHash(originalPreviousManifest));}
            foreach(string point in new[]{"none","journal-written-Preparing","journal-published-Preparing","stage-created","candidate-previous-worker","candidate-previous-manifest","journal-written-Ready","journal-published-Ready","member-published-0","member-published-1","member-published-2","member-published-3","journal-written-Committed","journal-published-Committed","cleanup-old-0"})
            {
                string root=Path.Combine(suite,"original-pair-"+(index++));Directory.CreateDirectory(root);
                File.Copy(originalWorker,Path.Combine(root,names[0]));File.WriteAllBytes(Path.Combine(root,names[1]),originalManifest);
                File.Copy(originalPreviousWorker,Path.Combine(root,names[2]));File.WriteAllBytes(Path.Combine(root,names[3]),originalPreviousManifest);
                // Exercise the actual P/Q -> A/P -> B/A production transaction,
                // then restore P/Q, not the two-slot swap that loses Q.
                using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,(Action<string>)null))
                {tx.Prepare("Configure",candidate,manifest);tx.Apply();tx.Complete();
                 tx.Prepare("Configure",secondWorker,secondManifest);tx.Apply();tx.Complete();}
                string[] before=names.Select(n=>Digest(Path.Combine(root,n))).ToArray();
                using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,(Action<string>)null))
                {
                    var wrong=(string[])before.Clone();wrong[0]=new string('f',64);
                    RejectIo(()=>tx.Prepare("Restore",originalWorker,originalManifest,originalPreviousWorker,originalPreviousManifest,wrong),"Different signed restoration preimage rejected before journal publication");
                    Check(!tx.HasPending && !File.Exists(Path.Combine(root,"discovery-setup.json")) && names.Select(n=>Digest(Path.Combine(root,n))).SequenceEqual(before),"Refused restoration leaves B/A byte-exact.");
                }
                try
                {
                    using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,at=>{if(at==point)throw new Interrupted();}))
                    {tx.Prepare("Restore",originalWorker,originalManifest,originalPreviousWorker,originalPreviousManifest,before);tx.Apply();tx.Complete();}
                    if(point!="none")throw new IOException("Original-pair checkpoint not reached: "+point);
                }
                catch(Interrupted){}
                using(var tx=new SyncSharkDiscoveryTransaction(root,Sid,(Action<string>)null,true))
                {
                    if(tx.HasPending)
                    {
                        var wrong=(string[])original.Clone();wrong[3]=new string('f',64);
                        RejectIo(()=>tx.ValidateRestore(before,wrong),"Changed signed original pair cannot claim an existing restoration journal");
                        tx.ValidateRestore(before,original);tx.RecoverRetainingFence();
                        Check(tx.HasPending,"Original-pair recovery retains task-aware readback fence.");
                        tx.FinishRecovery();
                    }
                    if(!names.Select(n=>Digest(Path.Combine(root,n))).SequenceEqual(original))
                    {Check(names.Select(n=>Digest(Path.Combine(root,n))).SequenceEqual(before),"Interrupted restoration returns exact B/A, never a mixed pair.");
                     tx.Prepare("Restore",originalWorker,originalManifest,originalPreviousWorker,originalPreviousManifest,before);tx.Apply();tx.Complete();}
                    Check(names.Select(n=>Digest(Path.Combine(root,n))).SequenceEqual(original) && !tx.HasPending,"Two-version chain restores exact P/Q bytes after "+point);
                }
            }
            string foreign=Path.Combine(suite,"foreign");Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign,"SyncSharkUpdater.exe"),"foreign bytes");
            using(var tx=new SyncSharkDiscoveryTransaction(foreign,Sid,(Action<string>)null))
                RejectIo(()=>tx.PrepareUpdater(candidate,expected,false),"foreign fixed worker cannot be overwritten");
            Check(File.ReadAllText(Path.Combine(foreign,"SyncSharkUpdater.exe"))=="foreign bytes" && !File.Exists(Path.Combine(foreign,"discovery-setup.json")),"Foreign worker refusal has no publication effect.");
            string empty=Path.Combine(suite,"inspect-only");Directory.CreateDirectory(empty);
            Check(!SyncSharkDiscoveryTransaction.InspectConfigure(empty,Sid,expected,expected,expected,expected,false) && !Directory.EnumerateFileSystemEntries(empty).Any(),"Read-only journal inspection never creates a claim file.");
            File.WriteAllText(Path.Combine(empty,"discovery-setup.json.new"),"torn");
            RejectIo(()=>SyncSharkDiscoveryTransaction.InspectConfigure(empty,Sid,expected,expected,expected,expected,false),"unpublished foreign journal");
            Check(Directory.GetFileSystemEntries(empty).Length==1 && File.ReadAllText(Path.Combine(empty,"discovery-setup.json.new"))=="torn","Refused journal bytes and missing claim remain unchanged.");
            Console.WriteLine("Updater recovery/lifecycle: "+(checks-start)+" real isolated filesystem assertions; task/ACL/UAC proof remains separate.");
            return 0;
        }
        finally
        {
            // Only this generated random fixture; never traverse reparse entries.
            var pending=new System.Collections.Generic.Stack<string>();var directories=new System.Collections.Generic.List<string>();pending.Push(suite);
            while(pending.Count!=0)
            {
                string directory=pending.Pop();directories.Add(directory);
                foreach(string entry in Directory.GetFileSystemEntries(directory))
                {
                    var attributes=File.GetAttributes(entry);
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unexpected fixture reparse content preserved.");
                    if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else File.Delete(entry);
                }
            }
            for(int i=directories.Count-1;i>=0;i--)Directory.Delete(directories[i],false);
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==2 && args[0]=="--receipt-checks"){Run();ReceiptChecks(args[1]);return RecoveryChecks(args[1]);}
            if(args.Length==2 && args[0]=="--signed-input")
            {
                SyncSharkUpdaterSignature.Verify(args[1],Path.GetExtension(args[1])==".msix");
                Console.WriteLine("Pinned signer, file integrity and signature-bound RFC3161 timestamp verified; read-only.");return 0;
            }
            if(args.Length==2 && args[0]=="--tree-input")
            {using(var files=new SyncSharkUpdaterFiles(IntPtr.Zero)){Console.WriteLine(files.TreeIdentity(args[1],false));return 0;}}
            if(args.Length!=0)return 87;
            return Run();
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
#endif
