#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

// Pure task policy, shared by attended setup, read-only admission and the
// unelevated invoker. No task registration or Run call occurs in this class.
internal static class SyncSharkUpdaterTask
{
    internal const string ExecutionLimit="PT10M";
    private static readonly XNamespace Task="http://schemas.microsoft.com/windows/2004/02/mit/task";
    internal static string Security(string user)
    {
        ValidateUser(user);
        return "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;"+user+")";
    }
    private static void ValidateUser(string user)
    {SyncSharkUpdaterContract.Require(user!=null && new SecurityIdentifier(user).Value==user && user.StartsWith("S-1-5-21-",StringComparison.Ordinal),"Invalid task user SID.");}
    internal static string Xml(string user)
    {
        ValidateUser(user);
        return "<Task version=\"1.4\" xmlns=\""+Task+"\"><RegistrationInfo><Description>Sync Shark verified protected runtime publication</Description></RegistrationInfo><Triggers />"+
            "<Principals><Principal id=\"User\"><UserId>"+user+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"+
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"+
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>"+
            "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>"+
            "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>"+
            "<ExecutionTimeLimit>"+ExecutionLimit+"</ExecutionTimeLimit></Settings><Actions Context=\"User\"><Exec><Command>"+SyncSharkUpdaterContract.WorkerPath+
            "</Command><WorkingDirectory>"+SyncSharkUpdaterContract.ProtectedRoot+"</WorkingDirectory></Exec></Actions></Task>";
    }
    internal static void Validate(string xml,string sddl,string expectedUser)
    {ValidateFixed(xml,sddl,expectedUser,SyncSharkUpdaterContract.WorkerPath,ExecutionLimit,false,true,true);}
    internal static void ValidateRemoval(string xml,string sddl,string expectedUser,bool exactPendingRemoval)
    {ValidateFixed(xml,sddl,expectedUser,SyncSharkUpdaterContract.WorkerPath,ExecutionLimit,false,true,exactPendingRemoval?(bool?)null:true);}
    internal static bool ValidateDiscovery(string xml,string sddl,string expectedUser,string workerVersion)
    {
        SyncSharkUpdaterContract.Require(SyncSharkUpdaterContract.IsProductVersion(workerVersion),"Discovery worker version is unbound.");
        string limit=Version.Parse(workerVersion.Split('+')[0])>=new Version(0,3,32)?"PT31M":"PT2M";
        return ValidateFixed(xml,sddl,expectedUser,SyncSharkUpdaterContract.ProtectedRoot+@"\SyncSharkDiscovery.exe",limit,true,false,null);
    }
    private static bool ValidateFixed(string xml,string sddl,string expectedUser,string worker,string limit,bool terminate,bool hidden,bool? enabled)
    {
        ValidateUser(expectedUser);
        SyncSharkUpdaterContract.Require(xml!=null && xml.Length<=32768 && sddl!=null && sddl.Length<=4096,"Oversized or absent updater task.");
        XElement root;
        try
        {
            var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32768};
            using(var reader=XmlReader.Create(new StringReader(xml),settings))root=XElement.Load(reader,LoadOptions.PreserveWhitespace);
        }
        catch(XmlException error){throw new InvalidDataException("Invalid updater task XML.",error);}
        SyncSharkUpdaterContract.Require(root.Name==Task+"Task","Unexpected task root.");
        Closed(root,new[]{"RegistrationInfo","Triggers","Principals","Settings","Actions"},"version");
        SyncSharkUpdaterContract.Require((string)root.Attribute("version")=="1.4","Unexpected task schema version.");
        foreach(var group in root.DescendantsAndSelf().GroupBy(e=>new {Parent=e.Parent,Name=e.Name}))
            SyncSharkUpdaterContract.Require(group.Count()==1,"Duplicate task element.");
        XElement registration=One(root,"RegistrationInfo");
        Closed(registration,new[]{"Description","URI"});
        string description=hidden?"Sync Shark verified protected runtime publication":"Sync Shark read-only NTFS discovery";
        SyncSharkUpdaterContract.Require(Value(registration,"Description")==description,"Task registration identity changed.");
        OptionalValue(registration,"URI",hidden?@"\SyncSharkUpdater":@"\SyncSharkDiscovery");
        XElement principals=One(root,"Principals"),principal=One(principals,"Principal");
        Closed(principals,new[]{"Principal"});
        Closed(principal,new[]{"UserId","LogonType","RunLevel"},"id");
        SyncSharkUpdaterContract.Require(principals.Elements().Count()==1 && (string)principal.Attribute("id")=="User","Unexpected task principal.");
        string user=Value(principal,"UserId");
        if(!user.StartsWith("S-1-",StringComparison.Ordinal))user=((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;
        SyncSharkUpdaterContract.Require(user==expectedUser && Value(principal,"LogonType")=="InteractiveToken" && Value(principal,"RunLevel")=="HighestAvailable",
            "Task user/logon/privilege changed.");
        Closed(One(root,"Triggers"),new string[0]);
        XElement actions=One(root,"Actions"),action=One(actions,"Exec");
        Closed(actions,new[]{"Exec"},"Context");
        Closed(action,new[]{"Command","WorkingDirectory","Arguments"});
        SyncSharkUpdaterContract.Require(actions.Elements().Count()==1 && (string)actions.Attribute("Context")=="User" &&
            String.Equals(Value(action,"Command"),worker,StringComparison.OrdinalIgnoreCase) && String.Equals(Value(action,"WorkingDirectory"),SyncSharkUpdaterContract.ProtectedRoot,StringComparison.OrdinalIgnoreCase),
            "Task executable or working directory changed.");
        var arguments=action.Element(Task+"Arguments");
        SyncSharkUpdaterContract.Require(arguments==null || Value(action,"Arguments").Length==0,"Updater task arguments are forbidden.");
        XElement policy=One(root,"Settings");
        // Registered task XML can omit defaults that NewTask.XmlText retains.
        // Keep exact effective values, not an assumed serialization shape.
        // Every other optional policy (network, restart, maintenance, token privileges,
        // expiry, etc.) is deliberately outside this fixed task's authority.
        Closed(policy,new[]{"MultipleInstancesPolicy","DisallowStartIfOnBatteries","StopIfGoingOnBatteries",
            "AllowHardTerminate","StartWhenAvailable","RunOnlyIfNetworkAvailable","IdleSettings",
            "AllowStartOnDemand","Enabled","Hidden","RunOnlyIfIdle","WakeToRun","ExecutionTimeLimit",
            "DisallowStartOnRemoteAppSession","UseUnifiedSchedulingEngine","Priority"});
        OptionalValue(policy,"DisallowStartOnRemoteAppSession","false");
        if(hidden)OptionalValue(policy,"UseUnifiedSchedulingEngine","false");
        else
        {
            // Existing Discovery registrations use either scheduler engine. The
            // engine does not grant a different action, trigger or privilege;
            // every effective execution/security setting remains checked below.
            string engine=DefaultValue(policy,"UseUnifiedSchedulingEngine","false");
            SyncSharkUpdaterContract.Require(engine=="true" || engine=="false","Invalid Discovery scheduling engine.");
        }
        OptionalValue(policy,"Priority","7");
        foreach(string name in new[]{"DisallowStartIfOnBatteries","StopIfGoingOnBatteries"})
            SyncSharkUpdaterContract.Require(DefaultValue(policy,name,"true")=="false","Task battery execution policy changed.");
        foreach(string name in new[]{"StartWhenAvailable","RunOnlyIfNetworkAvailable","RunOnlyIfIdle","WakeToRun"})
            SyncSharkUpdaterContract.Require(DefaultValue(policy,name,"false")=="false","Updater task execution policy changed.");
        string actualEnabled=DefaultValue(policy,"Enabled","true");
        SyncSharkUpdaterContract.Require((actualEnabled=="true" || actualEnabled=="false") && (!enabled.HasValue || actualEnabled==(enabled.Value?"true":"false")),"Task availability changed.");
        SyncSharkUpdaterContract.Require(DefaultValue(policy,"AllowStartOnDemand","true")=="true" && DefaultValue(policy,"Hidden","false")== (hidden?"true":"false") &&
            DefaultValue(policy,"AllowHardTerminate","true")== (terminate?"true":"false"),"Task execution controls changed.");
        XElement idle=One(policy,"IdleSettings");
        Closed(idle,new[]{"StopOnIdleEnd","RestartOnIdle","Duration","WaitTimeout"});
        OptionalValue(idle,"Duration","PT10M");
        OptionalValue(idle,"WaitTimeout","PT1H");
        SyncSharkUpdaterContract.Require(Value(idle,"StopOnIdleEnd")=="false" && Value(idle,"RestartOnIdle")=="false","Task idle policy changed.");
        SyncSharkUpdaterContract.Require(Value(policy,"ExecutionTimeLimit")==limit && Value(policy,"MultipleInstancesPolicy")=="IgnoreNew",
            "Updater task time or concurrency policy changed.");
        ValidateSecurity(sddl,expectedUser);
        return actualEnabled=="true";
    }
    private static XElement One(XElement parent,string name)
    {
        var values=parent.Elements(Task+name).ToArray();SyncSharkUpdaterContract.Require(values.Length==1,"Required task element missing or duplicated.");return values[0];
    }
    private static string Value(XElement parent,string name)
    {var value=One(parent,name);SyncSharkUpdaterContract.Require(!value.HasElements && !value.HasAttributes,"Task value must be literal text without attributes.");return value.Value;}
    private static void OptionalValue(XElement parent,string name,string expected)
    {if(parent.Element(Task+name)!=null)SyncSharkUpdaterContract.Require(Value(parent,name)==expected,"Optional task policy changed: "+name);}
    private static string DefaultValue(XElement parent,string name,string schemaDefault)
    {return parent.Element(Task+name)==null?schemaDefault:Value(parent,name);}
    private static void Closed(XElement element,string[] children,params string[] attributes)
    {
        SyncSharkUpdaterContract.Require(element.Elements().All(e=>e.Name.Namespace==Task && children.Contains(e.Name.LocalName)),
            "Unexpected task policy element under "+element.Name.LocalName+".");
        SyncSharkUpdaterContract.Require(element.Attributes().All(a=>
            a.IsNamespaceDeclaration ? element.Parent==null && a.Value==Task.NamespaceName :
            a.Name.Namespace==XNamespace.None && attributes.Contains(a.Name.LocalName)),"Unexpected task policy attribute.");
        SyncSharkUpdaterContract.Require(element.Nodes().All(n=>n is XElement || n is XText && String.IsNullOrWhiteSpace(((XText)n).Value)),
            "Unexpected task policy content.");
    }
    private static void ValidateSecurity(string sddl,string user)
    {
        RawSecurityDescriptor security;
        try{security=new RawSecurityDescriptor(sddl);}catch(ArgumentException error){throw new InvalidDataException("Invalid task security descriptor.",error);}
        SyncSharkUpdaterContract.Require(security.Owner!=null && security.Owner.Value=="S-1-5-32-544" && security.Group!=null && security.Group.Value=="S-1-5-32-544" &&
            (security.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0 && security.DiscretionaryAcl!=null && security.DiscretionaryAcl.Count==3,"Task ownership/permissions changed.");
        var seen=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach(GenericAce entry in security.DiscretionaryAcl)
        {
            var ace=entry as CommonAce;
            SyncSharkUpdaterContract.Require(ace!=null && ace.AceQualifier==AceQualifier.AccessAllowed && ace.AceFlags==AceFlags.None && !ace.IsCallback,
                "Unexpected updater task ACE.");
            string sid=ace.SecurityIdentifier.Value;
            SyncSharkUpdaterContract.Require(seen.Add(sid),"Duplicate updater task ACE.");
            if(sid=="S-1-5-18" || sid=="S-1-5-32-544")
                SyncSharkUpdaterContract.Require(ace.AccessMask==268435456 || ace.AccessMask==2032127,"Administrative task permission changed.");
            else SyncSharkUpdaterContract.Require(sid==user && (ace.AccessMask==unchecked((int)0xa0000000) || ace.AccessMask==1179817),"Ordinary-user task permission is not read/execute-only.");
        }
    }
}
