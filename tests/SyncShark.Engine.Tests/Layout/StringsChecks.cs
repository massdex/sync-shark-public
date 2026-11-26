using Massdex.SyncShark.Engine.Storage;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Loads the real text catalog; missing, repeated or unknown keys are errors.</summary>
public static class StringsChecks
{
    public static Task RunAsync()
    {
        Check.Require(Text.All.Count == Enum.GetValues<TextKey>().Length, "Incomplete text registry");
        Check.Require(Sql.All.Count == Enum.GetValues<SqlKey>().Length, "Incomplete SQL registry");
        Check.Require(Text.Get(TextKey.InfoLinks, (TextArg.Count, "2"), (TextArg.Folders, "Top")) ==
            "2 links or shortcuts are not followed or synced, in: Top.", "Links notice placeholders");
        return Task.CompletedTask;
    }
    /// <summary>Every key a refusal or failure is created with renders with the values its creation site passes, leaving no
    /// {Name}; a failure cause (ItemFailedException, a classified Dropbox refusal) is rendered with only Path and Message.</summary>
    public static Task ThrownPlaceholdersAsync(CheckArguments input)
    {
        int seen=0; var unfilled=new List<string>();
        var files=new[]{"src","apps"}.SelectMany(folder=>Directory.EnumerateFiles(Path.Combine(input.Root,folder),"*.cs",SearchOption.AllDirectories))
            .Where(f=>!Path.GetRelativePath(input.Root,f).Split(Path.DirectorySeparatorChar).Any(s=>s is "bin" or "obj"));
        foreach(var file in files)
            foreach(var creation in CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                // A target-typed new takes its type from the enclosing method's return type.
                var type=(creation is ObjectCreationExpressionSyntax {Type:var named} ? named.ToString() : creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ReturnType.ToString())?.Split('.')[^1];
                if(type is not ("EngineRequestException" or "ItemFailedException" or "Classified") || creation.ArgumentList is not {} arguments) continue;
                string[] Named(string owner)=>arguments.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(m=>m.Expression is IdentifierNameSyntax {Identifier.ValueText:var id} && id==owner).Select(m=>m.Name.Identifier.ValueText).ToArray();
                var values=Named(nameof(TextArg)).Select(n=>Enum.Parse<TextArg>(n)).Concat(type=="EngineRequestException" ? Array.Empty<TextArg>() : new[]{TextArg.Path,TextArg.Message}).Select(name=>(name,"x")).ToArray();
                foreach(var key in Named(nameof(TextKey)).Select(n=>Enum.Parse<TextKey>(n)))
                {
                    ++seen;
                    if(System.Text.RegularExpressions.Regex.IsMatch(Text.Get(key,values),@"\{\w+\}")) unfilled.Add($"{Path.GetFileName(file)}:{creation.GetLocation().GetLineSpan().StartLinePosition.Line+1} {key}");
                }
            }
        Check.Require(seen!=0 && unfilled.Count==0,"Unfilled placeholders: "+string.Join(", ",unfilled));
        return Task.CompletedTask;
    }
    /// <summary>Wire limits belong to the provider data, not the transport or its callers.</summary>
    public static Task ProviderLimitsAsync()
    {
        using var stream=typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Routes) ?? throw new InvalidDataException(ResourceNames.Routes);
        using var doc=System.Text.Json.JsonDocument.Parse(stream);var root=doc.RootElement;
        var actual=root.GetProperty("routes").EnumerateArray().Where(row=>row.TryGetProperty("maxEntries",out _)).ToDictionary(row=>row.GetProperty("name").GetString()!,row=>row.GetProperty("maxEntries").GetInt32());
        (string Name,int Max)[] expected=[("files/delete_batch",1000),("files/create_folder_batch",10000),("files/move_batch_v2",1000),("files/upload_session/finish_batch_v2",1000)];
        Check.Require(actual.Count==expected.Length && expected.All(x=>actual.GetValueOrDefault(x.Name)==x.Max),"Incorrect route limit (D05/D07)");
        var limits=root.GetProperty("limits");Check.Require(limits.GetProperty("contentRequestMiB").GetInt64()==150 && limits.GetProperty("relocationFiles").GetInt32()==10000,"Incorrect content or relocation limit (D06/D07)");
        return Task.CompletedTask;
    }
}
