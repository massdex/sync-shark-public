using Microsoft.CodeAnalysis;
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
        var commands=catalog.RootElement.GetProperty("commands").EnumerateArray();
        var words=commands.SelectMany(row=>System.Text.RegularExpressions.Regex.Replace(row.GetProperty("usage").GetString()!,"<[^>]*>",string.Empty)
            .Split(new[]{' ','[',']','|'},StringSplitOptions.RemoveEmptyEntries))
            .Concat(catalog.RootElement.GetProperty("choices").EnumerateArray().Select(v=>v.GetString()!)).ToHashSet(StringComparer.Ordinal);
        var references=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RunEvent).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).Select(path=>MetadataReference.CreateFromFile(path));
        var context=CSharpCompilation.Create("Literals",[CSharpSyntaxTree.ParseText("global using System; global using System.Linq; global using Massdex.SyncShark.Engine;")],references);
        foreach(var probe in new[]{"ListOptions opts = new(false,false); opts.Events?.Invoke(new(default,string.Empty,string.Empty,\"Retry this file\",string.Empty));", "if(args.Contains(\"--yes\")) Apply();", "if(attempt is >= 5) Stop();","switch (arg) { case \"repair\": break; }", "Console.WriteLine(\"Ready to sync\");", "writer.WriteLine($\"Retry {count} items\");", "await Task.Delay(300);", "var retryDelay = 5;", "if (attempt >= 5) Stop();", "switch (arg) { case \"--first-sync\": break; }", "if(arg==\"newest\") Stop();", "_ = new RunEvent(kind, what, path, \"Retry this file\", outcome);", "RunEvent report = new(kind, what, path, $\"Retry {count} items\", outcome);"})
            Check.Require(Offends("class A { void M() { "+probe+" } }","A.cs",words,context),"Undetected hard-coded value: "+probe);
        Check.Require(!Offends("class A { void M() { writer.WriteLine(Text.Get(key)); } }","A.cs",words,context),"Catalog-backed message rejected");
        foreach(var valid in new[]{"ListOptions opts = new(false,false); opts.Events?.Invoke(new(default,string.Empty,string.Empty,Text.Get(key),string.Empty));", "if(attempt is >= OwnedAttemptLimit) Stop();","if(attempt>=settings.Attempts) Stop();","if(backoff*(1+jitter)>limit) Stop();","if(parts.Length>=2) Read();","_ = new RunEvent(kind,what,path,Text.Get(key),outcome);"})
            Check.Require(!Offends("class A { void M() { "+valid+" } }","A.cs",words,context),"Owned or structural value rejected: "+valid);
        Check.Require(Offends("static class Render { static string Notice() => \"Retry this file\"; }","Render.cs",words,context),"Unowned renderer return was accepted");
        Check.Require(Offends("static class Render { static string Event() { return $\"Retry {count} items\"; } }","Render.cs",words,context),"Unowned renderer return block was accepted");
        Check.Require(!Offends("static class Render { static string Notice() => Text.Get(key); }","Render.cs",words,context),"Catalog-backed renderer return was rejected");
        Check.Require(Offends("static class LogText { static string Format() => \"Retry this file\"; }","LogText.cs",words,context),"Unowned log return was accepted");
        Check.Require(Offends("static class LogText { static string Format() { return $\"Retry {count} items\"; } }","LogText.cs",words,context),"Unowned log return block was accepted");
        Check.Require(!Offends("static class LogText { static string Format() => Text.Get(key); }","LogText.cs",words,context),"Catalog-backed log return was rejected");
        Check.Require(Offends("static class LogText { static string Format() { const string message=\"Retry this file\"; return message; } }","LogText.cs",words,context),"Named local log message escaped");
        Check.Require(Offends("class A { const string Message=\"Retry this file\"; void M() { Console.WriteLine(Message); } }","A.cs",words,context),"Named output message escaped");
        foreach (var folder in new[] { "src", "apps" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(input.Root, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetRelativePath(input.Root,file).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")) continue;
                Check.Require(!Offends(File.ReadAllText(file), Path.GetFileName(file),words,context), file);
            }
        return Task.CompletedTask;
    }
    private static bool Offends(string source, string file, ISet<string>? words=null,CSharpCompilation? context=null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);var root=tree.GetRoot(); SemanticModel? model=null;
        static bool Emits(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText is "Write" or "WriteLine" or "LogInformation" or "LogWarning" or "LogError";
        bool Message(ExpressionSyntax expr) => expr.DescendantNodesAndSelf().Any(n=>n is InterpolatedStringTextSyntax part && part.TextToken.ValueText.Any(char.IsLetter) || n is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.Any(char.IsLetter) || n is IdentifierNameSyntax id && context!=null && (model??=context.AddSyntaxTrees(tree).GetSemanticModel(tree)).GetSymbolInfo(id).Symbol is (ILocalSymbol { IsConst:true, ConstantValue:string } or IFieldSymbol { IsConst:true, ConstantValue:string }) && model.GetConstantValue(id).Value is string text && text.Any(char.IsLetter));
        if(file is "Render.cs" or "LogText.cs")
            foreach(var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>(file=="Render.cs" || m.Identifier.ValueText=="Format") && m.ReturnType is PredefinedTypeSyntax type && type.Keyword.IsKind(SyntaxKind.StringKeyword)))
                if(method.ExpressionBody is {} arrow && Message(arrow.Expression) || method.Body?.DescendantNodes().OfType<ReturnStatementSyntax>().Any(ret=>ret.Expression is {} expr && Message(expr))==true) return true;
        foreach(var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(Emits))
            if(call.ArgumentList.Arguments.Any(a=>Message(a.Expression))) return true;
        foreach(var obj in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var target=obj is ObjectCreationExpressionSyntax typed ? typed.Type.ToString() :
                obj.FirstAncestorOrSelf<VariableDeclarationSyntax>()?.Type.ToString() ?? obj.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.ReturnType.ToString();
            if(obj is ImplicitObjectCreationExpressionSyntax && obj.Parent is ArgumentSyntax && context!=null)
            {
                model??=context.AddSyntaxTrees(tree).GetSemanticModel(tree);
                target=model.GetTypeInfo(obj).ConvertedType?.ToDisplayString();
            }
            if(target?.Split('.').Last()=="RunEvent" && obj.ArgumentList?.Arguments.Any(a=>Message(a.Expression))==true) return true;
        }
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is string text && (text.StartsWith("https://", StringComparison.Ordinal) ||
                text.Any(char.IsWhiteSpace) && literal.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(x => x.Type.ToString().EndsWith("Exception", StringComparison.Ordinal)))) return true;
            if(literal.Token.Value is string word && words?.Contains(word)==true && (literal.Ancestors().Any(n=>n is CaseSwitchLabelSyntax or ConstantPatternSyntax or BinaryExpressionSyntax or SwitchExpressionArmSyntax) ||
                literal.Parent is ArgumentSyntax && literal.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Expression is MemberAccessExpressionSyntax lookup && lookup.Name.Identifier.ValueText is "Contains" or "IndexOf" or "TryGetValue" or "ContainsKey")) return true;
            if(literal.Token.Value is int or long or float or double or decimal)
            {
                if(literal.Ancestors().OfType<InvocationExpressionSyntax>().Any(i=>i.Expression is MemberAccessExpressionSyntax a && a.Name.Identifier.ValueText is "Delay" or "Sleep" or "FromMilliseconds" or "FromSeconds" or "FromMinutes")) return true;
                // Only a literal used as the threshold is an inline limit; a unit factor inside computed backoff is not.
                ExpressionSyntax operand=literal;
                while(operand.Parent is ParenthesizedExpressionSyntax paren) operand=paren;
                var comparison=operand.Parent as BinaryExpressionSyntax;
                if(comparison?.OperatorToken.Text is (">=" or ">" or "<=" or "<" or "==" or "!=") && comparison.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n=>System.Text.RegularExpressions.Regex.IsMatch(n.Identifier.ValueText,"attempt|retr(y|ies)|timeout|backoff|delay",System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return true;
                if(operand.Parent is RelationalPatternSyntax && operand.FirstAncestorOrSelf<IsPatternExpressionSyntax>()?.Expression is {} tested &&
                    tested.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n=>System.Text.RegularExpressions.Regex.IsMatch(n.Identifier.ValueText,"attempt|retr(y|ies)|timeout|backoff|delay",System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return true;
                var variable=literal.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;
                if(variable!=null && System.Text.RegularExpressions.Regex.IsMatch(variable,"retry|retries|delay|backoff|timeout",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
            }
            if (file == "EngineSettings.cs" && literal.Parent is EqualsValueClauseSyntax && literal.Token.Value is int or double) return true;
        }
        return false;
    }
}
