#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

// A strict, bounded data reader shared by staging and the fixed updater. The JSON
// reader exposes duplicate members as XML siblings, so none can silently override
// a previously authenticated decision. No deserialized type or path is executable.
internal static class SyncSharkUpdaterData
{
    internal sealed class PendingPlan
    {
        internal readonly string Version, Revision, InstallerSha256;
        internal PendingPlan(string version,string revision,string hash) {Version=version;Revision=revision;InstallerSha256=hash;}
        internal string TransactionName {get{return Version+"-"+Revision.Substring(0,7);}}
    }

    internal static XElement Read(string json,int limit)
    {
        SyncSharkUpdaterContract.Require(json!=null && Encoding.UTF8.GetByteCount(json)<=limit,"Missing or oversized updater data.");
        RequireSingleObject(json);
        var quotas=new XmlDictionaryReaderQuotas {MaxDepth=12,MaxStringContentLength=limit,MaxArrayLength=128,MaxBytesPerRead=4096,MaxNameTableCharCount=8192};
        try
        {
            using(var reader=JsonReaderWriterFactory.CreateJsonReader(new UTF8Encoding(false,true).GetBytes(json),quotas))
            {
                XElement result=XElement.Load(reader,LoadOptions.None);
                SyncSharkUpdaterContract.Require(reader.EOF || !reader.Read(),"Trailing updater data refused.");
                return result;
            }
        }
        catch(Exception error)
        {
            if(error is XmlException || error is ArgumentException || error is FormatException)
                throw new InvalidDataException("Malformed updater JSON.",error);
            throw;
        }
    }
    private static bool White(char value) {return value==' ' || value=='\t' || value=='\r' || value=='\n';}
    private static void RequireSingleObject(string json)
    {
        // JsonReaderWriterFactory otherwise stops at the first root and silently
        // ignores a second document. This envelope check does not interpret values;
        // the framework JSON reader below still validates their full syntax.
        int start=0;while(start<json.Length && White(json[start]))start++;
        SyncSharkUpdaterContract.Require(start<json.Length && json[start]=='{',"Expected one complete JSON object.");
        var containers=new Stack<char>();bool quoted=false,escaped=false;char previous='\0';
        for(int i=start;i<json.Length;i++)
        {
            char value=json[i];
            if(quoted)
            {
                SyncSharkUpdaterContract.Require(value>=32,"Unescaped JSON control character.");
                if(escaped){escaped=false;continue;}
                if(value=='\\'){escaped=true;continue;}
                if(value=='\"')quoted=false;
                continue;
            }
            if(value=='\"'){quoted=true;previous=value;continue;}
            if(value=='{' || value=='[')
            {containers.Push(value);SyncSharkUpdaterContract.Require(containers.Count<=12,"Updater nesting limit exceeded.");}
            else if(value=='}' || value==']')
            {
                SyncSharkUpdaterContract.Require(previous!=',' && containers.Count>0 && containers.Pop()==(value=='}'?'{':'['),"Malformed JSON container.");
                if(containers.Count==0)
                {
                    for(int tail=i+1;tail<json.Length;tail++)SyncSharkUpdaterContract.Require(White(json[tail]),"Trailing updater data refused.");
                    return;
                }
            }
            if(!White(value))previous=value;
        }
        throw new InvalidDataException("Incomplete updater JSON object.");
    }
    internal static Dictionary<string,XElement> Object(XElement element,params string[] names)
    {
        SyncSharkUpdaterContract.Require((string)element.Attribute("type")=="object","Expected an updater object.");
        var result=new Dictionary<string,XElement>(StringComparer.Ordinal);
        foreach(var child in element.Elements())
        {
            string name=child.Name.LocalName;
            SyncSharkUpdaterContract.Require(child.Name.NamespaceName.Length==0 && names.Contains(name,StringComparer.Ordinal) && !result.ContainsKey(name),
                "Unknown, aliased or duplicate updater field.");
            result.Add(name,child);
        }
        SyncSharkUpdaterContract.Require(result.Count==names.Length,"Missing updater field.");return result;
    }
    internal static string Text(XElement element)
    {
        SyncSharkUpdaterContract.Require((string)element.Attribute("type")=="string" && !element.HasElements,"Expected a literal updater string.");
        return element.Value;
    }
    private static string NullableText(XElement element)
    {if((string)element.Attribute("type")=="null" && !element.HasElements && element.Value.Length==0)return null;return Text(element);}
    internal static long Integer(XElement element)
    {
        long value;
        SyncSharkUpdaterContract.Require((string)element.Attribute("type")=="number" && !element.HasElements &&
            long.TryParse(element.Value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value),"Expected an integral updater value.");
        return long.Parse(element.Value,CultureInfo.InvariantCulture);
    }
    private static bool Boolean(XElement element)
    {
        SyncSharkUpdaterContract.Require((string)element.Attribute("type")=="boolean" && !element.HasElements &&
            (element.Value=="true" || element.Value=="false"),"Expected a literal updater boolean.");return element.Value=="true";
    }
    internal static XElement[] Array(XElement element,int maximum)
    {
        SyncSharkUpdaterContract.Require((string)element.Attribute("type")=="array","Expected an updater array.");
        var values=element.Elements().ToArray();
        SyncSharkUpdaterContract.Require(values.Length<=maximum && values.All(e=>e.Name=="item"),"Invalid or oversized updater array.");return values;
    }

    internal static PendingPlan ReadPendingPlan(string json)
    {
        var data=Object(Read(json,1024),"schemaVersion","version","sourceRevision","installerSha256");
        string version=Text(data["version"]),revision=Text(data["sourceRevision"]),hash=Text(data["installerSha256"]);
        SyncSharkUpdaterContract.Require(Integer(data["schemaVersion"])==1 && SyncSharkUpdaterContract.IsVersion(version) &&
            SyncSharkUpdaterContract.IsHex(revision,40) && SyncSharkUpdaterContract.IsHex(hash,64),"Invalid fixed pending-plan selector.");
        return new PendingPlan(version,revision,hash);
    }

    internal static SyncSharkUpdaterContract ReadSignedHeader(string header,PendingPlan pending)
    {
        SyncSharkUpdaterContract.Require(header!=null && header.Length<=16384 && header.StartsWith(SyncSharkUpdaterContract.HeaderPrefix,StringComparison.Ordinal),
            "Missing or oversized signed updater contract.");
        var data=Object(Read(header.Substring(SyncSharkUpdaterContract.HeaderPrefix.Length),16384),
            "product","version","sourceRevision","previousVersion","moduleSha256","manifestSha256",
            "signedCoreSha256","signedUiSha256","signedExeSha256","signedExplorerSha256","signedDiscoverySha256","signedIdentityPackageSha256",
            "protectedRuntimeRoot","runtimeMembers","protectedUpdater");
        var update=Object(data["protectedUpdater"],"schemaVersion","userSid","previousTreeSha256","workerSha256","workerBytes","discovery");
        var discovery=Object(update["discovery"],"enabled","beforeWorkerSha256","beforeManifestSha256","afterWorkerSha256","afterManifestSha256","recovery");
        SyncSharkUpdaterContract.Require(Text(data["product"])==SyncSharkUpdaterContract.Product && Integer(update["schemaVersion"])==1,
            "Unrecognised signed updater contract.");
        var members=Array(data["runtimeMembers"],16).Select(row=>
        {
            var member=Object(row,"path","bytes","sha256");
            return new SyncSharkUpdaterContract.Member(Text(member["path"]),Text(member["sha256"]),Integer(member["bytes"]));
        }).ToArray();
        var contract=new SyncSharkUpdaterContract(Text(data["version"]),Text(data["sourceRevision"]),Text(data["previousVersion"]),
            Text(update["previousTreeSha256"]),Text(update["userSid"]),pending.InstallerSha256,members,
            new SyncSharkUpdaterContract.DiscoveryBinding(Boolean(discovery["enabled"]),NullableText(discovery["beforeWorkerSha256"]),
                NullableText(discovery["beforeManifestSha256"]),NullableText(discovery["afterWorkerSha256"]),NullableText(discovery["afterManifestSha256"]),Text(discovery["recovery"])),
            new SyncSharkUpdaterContract.Member("SyncSharkUpdater.exe",Text(update["workerSha256"]),Integer(update["workerBytes"])));
        SyncSharkUpdaterContract.Require(contract.Version==pending.Version && contract.SourceRevision==pending.Revision &&
            Text(data["protectedRuntimeRoot"])==contract.Destination,"The fixed selector and signed release disagree.");
        var signables=new Dictionary<string,string> {
            {"SyncShark.Core.dll","signedCoreSha256"},{"SyncShark.dll","signedUiSha256"},{"SyncShark.exe","signedExeSha256"},
            {"SyncShark.Explorer.dll","signedExplorerSha256"},{"SyncSharkDiscovery.exe","signedDiscoverySha256"},{"SyncShark.msix","signedIdentityPackageSha256"}};
        foreach(var signed in signables)
            SyncSharkUpdaterContract.Require(members.Single(m=>m.Path==signed.Key).Sha256==Text(data[signed.Value]),"Runtime and signed-member contracts disagree.");
        foreach(string field in new[]{"moduleSha256","manifestSha256"})
            SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsHex(Text(data[field]),64),"Incomplete installer content binding.");
        return contract;
    }

    internal static void UninstallRestoration(string json,SyncSharkUpdaterContract contract,string productRoot,string exactRetainedTree)
    {
        var journal=Object(Read(json,65536),"schema","operation","version","commit","product_root","status","pending","completed","allowed_steps","artifacts");
        string[] steps={"retain-active-pointer","retain-runtime","verify-uninstalled"};
        var artifacts=Object(journal["artifacts"],"runtime_sha256","settings_retained","recovery_retained","restored_utc","previous_undo_receipt");
        SyncSharkUpdaterContract.Require(Integer(journal["schema"])==1 && Text(journal["operation"])=="uninstall" &&
            Text(journal["version"])==contract.Version && Text(journal["commit"])==contract.SourceRevision && Text(journal["product_root"])==productRoot &&
            Array(journal["allowed_steps"],3).Select(Text).SequenceEqual(steps) && Text(artifacts["runtime_sha256"])==exactRetainedTree &&
            SyncSharkUpdaterContract.IsHex(exactRetainedTree,64) && Boolean(artifacts["settings_retained"]) && Boolean(artifacts["recovery_retained"]) &&
            NullableText(artifacts["restored_utc"])==null,"Uninstall restoration identity or retained bytes differ.");
        var completed=Array(journal["completed"],3).Select(Text).ToArray();string status=Text(journal["status"]),pending=NullableText(journal["pending"]);
        bool complete=status=="complete" && pending==null && completed.SequenceEqual(steps);
        bool interrupted=status=="active" && pending=="retain-runtime" && completed.SequenceEqual(steps.Take(1));
        SyncSharkUpdaterContract.Require(complete || interrupted,"Uninstall restoration is outside the exact retained-runtime phase.");
        // Previous receipt is history, not authority; reject extra structure.
        var previous=artifacts["previous_undo_receipt"];
        if((string)previous.Attribute("type")!="null")
        {
            var prior=Object(previous,"sha256","restored_utc");
            SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsHex(Text(prior["sha256"]),64) && Text(prior["restored_utc"]).Length<=64,"Invalid prior uninstall receipt.");
        }
    }
    internal static bool PublicationStepPending(string json,SyncSharkUpdaterContract contract,string productRoot)
    {
        var journal=Object(Read(json,65536),"schema","operation","version","commit","product_root","status","pending","completed","allowed_steps","artifacts");
        string[] steps={"stage-package","publish-runtime","backup-state","create-next-link","retain-predecessor","publish-successor","register-explorer-package","verify-complete"};
        var allowed=Array(journal["allowed_steps"],8).Select(Text).ToArray();
        var completed=Array(journal["completed"],8).Select(Text).ToArray();
        var artifacts=Object(journal["artifacts"],"state_predecessor_sha256");
        string stateHash=NullableText(artifacts["state_predecessor_sha256"]);
        SyncSharkUpdaterContract.Require(Integer(journal["schema"])==1 && Text(journal["operation"])=="install" &&
            Text(journal["version"])==contract.Version && Text(journal["commit"])==contract.SourceRevision &&
            Text(journal["product_root"])==productRoot && allowed.SequenceEqual(steps),"Foreign installation journal.");
        SyncSharkUpdaterContract.Require(completed.SequenceEqual(steps.Take(completed.Length)) &&
            (stateHash==null || SyncSharkUpdaterContract.IsHex(stateHash,64)),"Incompatible installation journal.");
        string status=Text(journal["status"]),pending=NullableText(journal["pending"]);
        bool publication=status=="active" && pending=="publish-runtime" && completed.Length==1 && stateHash==null;
        bool complete=status=="complete" && pending==null && completed.Length==steps.Length && SyncSharkUpdaterContract.IsHex(stateHash,64);
        SyncSharkUpdaterContract.Require(publication || complete,"Publication request is outside its exact journal phase.");
        return publication;
    }
}
