#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// The argument-free updater consumes an authenticated contract, never executable
// installer content. Admission is effect-free. Windows observation/signature code
// supplies facts; only the returned immutable decision can reach publication.
internal sealed class SyncSharkUpdaterContract
{
    internal const string Product = "Massdex.SyncShark";
    internal const string ProtectedRoot = @"C:\Program Files\Massdex\SyncShark";
    internal const string TaskPath = @"\SyncSharkUpdater";
    internal const string WorkerPath = ProtectedRoot + @"\SyncSharkUpdater.exe";
    internal const string PendingPlanRelativePath = @"replacement-staging\pending-update.json";
    internal const string HeaderPrefix = "# SyncSharkUpdateContract: ";

    internal sealed class Member
    {
        internal readonly string Path, Sha256;
        internal readonly long Bytes;
        internal Member(string path, string sha256, long bytes) { Path=path; Sha256=sha256; Bytes=bytes; }
    }

    internal sealed class DiscoveryBinding
    {
        internal readonly bool Enabled;
        internal readonly string BeforeWorker, BeforeManifest, AfterWorker, AfterManifest, Recovery;
        internal DiscoveryBinding(bool enabled, string beforeWorker, string beforeManifest,
            string afterWorker, string afterManifest, string recovery)
        {
            Enabled=enabled; BeforeWorker=beforeWorker; BeforeManifest=beforeManifest;
            AfterWorker=afterWorker; AfterManifest=afterManifest; Recovery=recovery;
        }
    }

    internal readonly string Version, SourceRevision, PreviousVersion, PreviousTree, UserSid, InstallerSha256;
    internal readonly DiscoveryBinding Discovery;
    internal readonly Member UpdaterWorker;
    private readonly Member[] members;
    internal Member[] Members { get { return (Member[])members.Clone(); } }
    internal string ProductVersion { get { return Version+"+"+SourceRevision; } }
    internal string Destination { get { return System.IO.Path.Combine(ProtectedRoot,"versions",Version); } }
    internal string TransactionName { get { return Version+"-"+SourceRevision.Substring(0,7); } }

    internal SyncSharkUpdaterContract(string version, string revision, string previousVersion,
        string previousTree, string userSid, string installerSha256, Member[] runtimeMembers, DiscoveryBinding discovery, Member updaterWorker)
    {
        Require(IsVersion(version), "Invalid successor version.");
        Require(IsHex(revision,40), "A frozen successor revision is required.");
        Require(IsProductVersion(previousVersion), "A frozen predecessor version is required.");
        Require(System.Version.Parse(version)>System.Version.Parse(previousVersion.Split('+')[0]), "Downgrade or equal-version update refused.");
        Require(IsHex(previousTree,64) && IsHex(installerSha256,64), "Missing predecessor or signed installer identity.");
        Require(userSid!=null && Regex.IsMatch(userSid,@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z"), "Exact interactive account required.");
        Require(updaterWorker!=null && updaterWorker.Path=="SyncSharkUpdater.exe" && IsHex(updaterWorker.Sha256,64) &&
            updaterWorker.Bytes>0 && updaterWorker.Bytes<=16777216, "Exact bounded fixed updater identity required.");
        string[] required=SyncSharkProtectedRuntimeTransaction.Members;
        Require(runtimeMembers!=null && runtimeMembers.Length==required.Length, "Complete runtime inventory required.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        long total=0;
        foreach(Member member in runtimeMembers)
        {
            Require(member!=null && required.Contains(member.Path,StringComparer.Ordinal) && seen.Add(member.Path), "Unexpected or duplicate runtime member.");
            Require(IsHex(member.Sha256,64) && member.Bytes>0 && member.Bytes<=536870912, "Invalid runtime member identity.");
            total=checked(total+member.Bytes);
        }
        Require(total<=1073741824, "Runtime exceeds the bounded publication size.");
        Require(discovery!=null, "Explicit Discovery disposition required.");
        if(discovery.Enabled)
        {
            Require(IsHex(discovery.BeforeWorker,64) && IsHex(discovery.BeforeManifest,64) &&
                IsHex(discovery.AfterWorker,64) && IsHex(discovery.AfterManifest,64), "Complete Discovery preimage/postimage required.");
            Require(discovery.AfterWorker==runtimeMembers.Single(m=>m.Path=="SyncSharkDiscovery.exe").Sha256,
                "Discovery postimage differs from the authenticated runtime.");
            Require(discovery.Recovery=="restore-exact-preimage", "Unsupported Discovery recovery disposition.");
        }
        else Require(discovery.BeforeWorker==null && discovery.BeforeManifest==null && discovery.AfterWorker==null &&
            discovery.AfterManifest==null && discovery.Recovery=="leave-disabled", "Disabled Discovery cannot grant a rebind.");
        Version=version; SourceRevision=revision; PreviousVersion=previousVersion; PreviousTree=previousTree;
        UserSid=userSid; InstallerSha256=installerSha256; Discovery=discovery; UpdaterWorker=updaterWorker; members=(Member[])runtimeMembers.Clone();
    }

    internal static bool IsVersion(string value)
    {
        if(value==null || !Regex.IsMatch(value,@"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z"))return false;
        System.Version parsed;return System.Version.TryParse(value,out parsed) && parsed.Major<=65535 && parsed.Minor<=65535 && parsed.Build<=65535;
    }
    internal static bool IsHex(string value,int length)
    { return value!=null && value.Length==length && value.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f')); }
    internal static bool IsProductVersion(string value)
    { if(value==null)return false;var pieces=value.Split('+');return pieces.Length==2 && IsVersion(pieces[0]) && IsHex(pieces[1],40); }
    internal static void Require(bool valid,string message) { if(!valid)throw new InvalidDataException(message); }
}

// No success-shaped defaults: every observation must be populated by the real
// Windows boundary while source/destination ancestors and member handles are held.
internal sealed class SyncSharkUpdaterObservation
{
    internal bool ElevatedSplitToken=false, FixedWorkerIdentity=false, ProtectedAclAndPaths=false, ExactTaskIdentity=false;
    internal bool AuthenticatedInstaller=false, AuthenticatedRuntime=false, SourceShapeAndHashes=false, PredecessorShapeAndHashes=false;
    internal bool JournalIdentity=false, JournalCompatible=false, PublicationStepPending=false, RollbackAvailable=false, ProductStopped=false;
    internal string UserSid=null, PreviousProductVersion=null, PreviousTree=null, HighestPublishedVersion=null;
    internal bool TargetExists=false, ExactTarget=false, DiscoveryEnabled=false, DiscoveryPending=false, DiscoveryRecoveryBound=false;
    internal string DiscoveryWorker=null, DiscoveryManifest=null;
}

internal sealed class SyncSharkUpdaterAdmission
{
    internal readonly SyncSharkUpdaterContract Contract;
    internal readonly bool PublishRuntime, RebindDiscovery;
    private SyncSharkUpdaterAdmission(SyncSharkUpdaterContract contract,bool publish,bool rebind)
    { Contract=contract;PublishRuntime=publish;RebindDiscovery=rebind; }

    internal static SyncSharkUpdaterAdmission Evaluate(SyncSharkUpdaterContract contract,SyncSharkUpdaterObservation actual)
    {
        SyncSharkUpdaterContract.Require(contract!=null && actual!=null,"Missing updater admission input.");
        SyncSharkUpdaterContract.Require(actual.ElevatedSplitToken && actual.FixedWorkerIdentity && actual.ProtectedAclAndPaths &&
            actual.ExactTaskIdentity,"Updater elevation, fixed identity or protected task/path boundary is unavailable.");
        SyncSharkUpdaterContract.Require(actual.AuthenticatedInstaller && actual.AuthenticatedRuntime && actual.SourceShapeAndHashes,
            "Signed timestamped release authority or held candidate bytes are invalid.");
        SyncSharkUpdaterContract.Require(actual.UserSid==contract.UserSid,"Updater account differs from the signed release.");
        SyncSharkUpdaterContract.Require(actual.PredecessorShapeAndHashes && actual.PreviousProductVersion==contract.PreviousVersion &&
            actual.PreviousTree==contract.PreviousTree,"Exact retained predecessor changed.");
        SyncSharkUpdaterContract.Require(actual.JournalIdentity && actual.JournalCompatible && actual.RollbackAvailable && actual.ProductStopped,
            "Foreign/incompatible transaction, unavailable rollback or running product.");
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsVersion(actual.HighestPublishedVersion),"Protected publication high-water version is unavailable.");
        var highest=System.Version.Parse(actual.HighestPublishedVersion);var target=System.Version.Parse(contract.Version);
        SyncSharkUpdaterContract.Require(highest<=target,"A later protected release already exists; stale publication refused.");
        SyncSharkUpdaterContract.Require(!actual.TargetExists || actual.ExactTarget,"Existing publication differs from this exact contract.");
        SyncSharkUpdaterContract.Require(actual.TargetExists || highest<target,"Equal-version publication is not an authenticated replay.");
        var discovery=contract.Discovery;
        bool recovery=actual.DiscoveryPending && actual.DiscoveryRecoveryBound && discovery.Enabled && actual.TargetExists && actual.PublicationStepPending;
        SyncSharkUpdaterContract.Require(!actual.DiscoveryPending || recovery,"Discovery recovery is not bound to this signed unfinished publication.");
        SyncSharkUpdaterContract.Require(actual.DiscoveryEnabled==discovery.Enabled || recovery,"Discovery enablement changed since signing.");
        bool rebind=recovery;
        if(discovery.Enabled && !recovery)
        {
            bool before=actual.DiscoveryWorker==discovery.BeforeWorker && actual.DiscoveryManifest==discovery.BeforeManifest;
            bool after=actual.DiscoveryWorker==discovery.AfterWorker && actual.DiscoveryManifest==discovery.AfterManifest;
            SyncSharkUpdaterContract.Require(before || (actual.TargetExists && after),"Discovery does not match the signed preimage/postimage.");
            rebind=before && !after;
        }
        SyncSharkUpdaterContract.Require(actual.PublicationStepPending || (actual.TargetExists && !rebind),
            "A completed user transaction cannot authorise another protected transition.");
        return new SyncSharkUpdaterAdmission(contract,!actual.TargetExists,rebind);
    }
}
