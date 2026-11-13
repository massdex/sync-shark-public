#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

// One deterministic serializer serves release preparation and the protected
// publisher. These bytes are data for the existing Discovery transaction, never
// code loaded from the unelevated staging area.
public static class SyncSharkUpdaterManifest
{
    [DataContract] private sealed class Member
    {
        [DataMember(Name="path",Order=0)] public string Path;
        [DataMember(Name="sha256",Order=1)] public string Sha256;
    }
    [DataContract] private sealed class Manifest
    {
        [DataMember(Name="schemaVersion",Order=0)] public int SchemaVersion=1;
        [DataMember(Name="version",Order=1)] public string Version;
        [DataMember(Name="userSid",Order=2)] public string UserSid;
        [DataMember(Name="appDirectory",Order=3)] public string AppDirectory;
        [DataMember(Name="workerSha256",Order=4)] public string WorkerSha256;
        [DataMember(Name="members",Order=5)] public Member[] Members;
    }
    internal static byte[] Discovery(SyncSharkUpdaterContract contract)
    {
        var members=contract.Members;
        var manifest=new Manifest {Version=contract.ProductVersion,UserSid=contract.UserSid,AppDirectory=contract.Destination,
            WorkerSha256=members.Single(m=>m.Path=="SyncSharkDiscovery.exe").Sha256,
            Members=members.OrderBy(m=>m.Path,StringComparer.Ordinal).Select(m=>new Member {Path=m.Path.Replace('/','\\'),Sha256=m.Sha256}).ToArray()};
        using(var stream=new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(Manifest)).WriteObject(stream,manifest);
            string text=Encoding.UTF8.GetString(stream.ToArray()).Replace("\\/","/");
            var ascii=new StringBuilder(text.Length);
            foreach(char character in text)
            {if(character>127)ascii.Append("\\u").Append(((int)character).ToString("x4"));else ascii.Append(character);}
            return new UTF8Encoding(false).GetBytes(ascii.ToString());
        }
    }

    // Build-time data derivation only. The caller supplies a provisional header;
    // this does not authenticate it or grant publication authority. Runtime uses
    // Discovery only after independent signature and complete contract admission.
    public static byte[] FromReleaseHeader(string header,string version,string revision)
    {return Discovery(SyncSharkUpdaterData.ReadSignedHeader(header,new SyncSharkUpdaterData.PendingPlan(version,revision,new string('0',64))));}
}
