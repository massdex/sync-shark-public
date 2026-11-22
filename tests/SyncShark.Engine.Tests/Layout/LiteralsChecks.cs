using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Checks settings defaults, provider URLs and user-visible messages at their source owners.</summary>
public static class LiteralsChecks
{
    public static Task RunAsync(CheckArguments input)
    {
        Check.Require(Offends("class A { void Fail() => throw new Exception(\"A failure occurred.\"); }", "A.cs"), "Unowned message not detected");
        Check.Require(!Offends("class A { void Fail() => throw new Exception(nameof(A)); }", "A.cs"), "Name expression rejected");
        using var data=typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Cli) ?? throw new InvalidDataException(ResourceNames.Cli);
        using var catalog=System.Text.Json.JsonDocument.Parse(data);
        var words=catalog.RootElement.GetProperty("commands").EnumerateArray().SelectMany(r=>r.GetProperty("command").GetString()!.Split(' ')).ToHashSet(StringComparer.Ordinal);
        foreach(var probe in new[]{"switch (arg) { case \"repair\": break; }", "Console.WriteLine(\"Ready to sync\");", "writer.WriteLine($\"Retry {count} items\");", "await Task.Delay(300);", "var retryDelay = 5;"})
            Check.Require(Offends("class A { void M() { "+probe+" } }","A.cs",words),"Undetected hard-coded value: "+probe);
        Check.Require(!Offends("class A { void M() { writer.WriteLine(Text.Get(key)); } }","A.cs",words),"Catalog-backed message rejected");
        foreach (var folder in new[] { "src", "apps" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(input.Root, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetRelativePath(input.Root,file).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")) continue;
                Check.Require(!Offends(File.ReadAllText(file), Path.GetFileName(file),words), file);
            }
        return Task.CompletedTask;
    }
    private static bool Offends(string source, string file, ISet<string>? words=null)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        static bool Emits(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText is "Write" or "WriteLine" or "LogInformation" or "LogWarning" or "LogError";
        foreach(var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(Emits))
            foreach(var arg in call.ArgumentList.Arguments)
                if(arg.Expression.DescendantNodesAndSelf().Any(n=>n is InterpolatedStringTextSyntax part && part.TextToken.ValueText.Any(char.IsLetter) || n is LiteralExpressionSyntax literal && literal.Token.Value is string s && s.Any(char.IsLetter))) return true;
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is string text && (text.StartsWith("https://", StringComparison.Ordinal) ||
                text.Any(char.IsWhiteSpace) && literal.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(x => x.Type.ToString().EndsWith("Exception", StringComparison.Ordinal)))) return true;
            if(literal.Token.Value is string word && words?.Contains(word)==true && literal.Ancestors().Any(n=>n is CaseSwitchLabelSyntax or ConstantPatternSyntax or BinaryExpressionSyntax or SwitchExpressionArmSyntax)) return true;
            if(literal.Token.Value is int or long or float or double or decimal)
            {
                if(literal.Ancestors().OfType<InvocationExpressionSyntax>().Any(i=>i.Expression is MemberAccessExpressionSyntax a && a.Name.Identifier.ValueText is "Delay" or "Sleep" or "FromMilliseconds" or "FromSeconds" or "FromMinutes")) return true;
                var variable=literal.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;
                if(variable!=null && System.Text.RegularExpressions.Regex.IsMatch(variable,"retry|retries|delay|backoff|timeout",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
            }
            if (file == "EngineSettings.cs" && literal.Parent is EqualsValueClauseSyntax && literal.Token.Value is int or double) return true;
        }
        return false;
    }
}
