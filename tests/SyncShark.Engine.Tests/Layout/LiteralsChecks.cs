using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Checks settings defaults, provider URLs and user-visible messages at their source owners.</summary>
public static class LiteralsChecks
{
    public static Task RunAsync(CheckArguments input)
    {
        Check.Require(Offends("class A { void M(string path) => throw new IOException(\"Cannot open \" + path); }","A.cs"),"Concatenated exception text escaped");
        Check.Require(Offends("class A { void Fail() => throw new Exception(\"A failure occurred.\"); }", "A.cs"), "Unowned message not detected");
        Check.Require(!Offends("class A { void Fail() => throw new Exception(nameof(A)); }", "A.cs"), "Name expression rejected");
        using var data=typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Cli) ?? throw new InvalidDataException(ResourceNames.Cli);
        using var catalog=System.Text.Json.JsonDocument.Parse(data);
        var commands=catalog.RootElement.GetProperty("commands").EnumerateArray();
        var words=commands.SelectMany(row=>System.Text.RegularExpressions.Regex.Replace(row.GetProperty("usage").GetString()!,"<[^>]*>",string.Empty)
            .Split(new[]{' ','[',']','|'},StringSplitOptions.RemoveEmptyEntries))
            .Concat(catalog.RootElement.GetProperty("choices").EnumerateObject().Select(v=>v.Value.GetString()!)).ToHashSet(StringComparer.Ordinal);
        var context=CSharpCompilation.Create("Literals",[CSharpSyntaxTree.ParseText("global using System; global using System.Linq; global using Massdex.SyncShark.Engine;")],References());
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
        Check.Require(Offends("class A { void M() => throw new Exception(\"Unavailable\"); }","A.cs"),"Single-word failure escaped");
        Check.Require(Offends("class A { void M() => writer.WriteAsync(\"Retry\"); }","A.cs",words,context) && Offends("class A { void M() => writer.WriteLineAsync(\"Retry\"); }","A.cs",words,context),"Async output escaped");
        Check.Require(!Offends("class A { void M() => writer.WriteLineAsync(Text.Get(key)); }","A.cs",words,context),"Catalog-backed async output rejected");
        var schemaText="namespace Example; static class Schema { public const string Create=\"CREATE TABLE item(id INTEGER)\"; }";
        var schemaPath="src/SyncShark.Engine/Storage/Schema.cs";
        // Storage SQL is permitted only by its destination (R124): the right side of CommandText directly, or an argument
        // bound to a helper parameter, reported as that sink for a reviewed sink receipt; the real StorageCommand is
        // compiled with each fixture.
        const string CommandFile="src/SyncShark.Engine/Storage/StorageCommand.cs";
        var storageCommand=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(input.Root,"src","SyncShark.Engine","Storage","StorageCommand.cs")));
        // Each site row as the artifact carries it; a sink is [path, offset, symbol, parameter].
        JsonElement[] Rows(string code,string path)
        {
            var tree=SiteTree(code);
            var flow=new SqlFlow(context.AddSyntaxTrees(tree,storageCommand),new Dictionary<SyntaxTree,string>{[tree]=path,[storageCommand]=CommandFile});
            return Sites(tree,code,path,flow).Select(x=>JsonSerializer.SerializeToElement(x)).ToArray();
        }
        (bool Permitted,string? Sink)[] Sited(string code,string path)=>Rows(code,path).Select(x=>(x.GetProperty("permitted").GetBoolean(),
            x.GetProperty("sink") is {ValueKind:JsonValueKind.Array} sink ? sink[0].GetString()+":"+sink[2].GetString()!.Split('(')[0].Split('.').Last()+"."+sink[3].GetString() : null)).ToArray();
        bool[] Permitted(string code,string path)=>Sited(code,path).Select(x=>x.Permitted).ToArray();
        bool Permits(string code,string path)=>Permitted(code,path).Single();
        const string Sql="using System.Collections.Generic; using Microsoft.Data.Sqlite; namespace Massdex.SyncShark.Engine.Storage; ";
        const string Store="src/SyncShark.Engine/Storage/A.cs";
        const string Create=CommandFile+":Create.sql";
        Check.Require(Permits(schemaText,schemaPath) && !Permits(schemaText,"src/SyncShark.Engine/Run/Schema.cs") && !Permits(schemaText.Replace("CREATE TABLE item(id INTEGER)","Retry this file"),schemaPath),"DDL owner or content scope was lost");
        var spliced=Sql+"class A { SqliteCommand M(SqliteConnection db,string c) => StorageCommand.Create(db,null,$\"SELECT {c} FROM item WHERE id=$id\"); }";
        Check.Require(Sited(spliced,Store).Single()==(false,Create) && Sited(spliced,"src/SyncShark.Engine/Run/A.cs").Single()==(false,null),"Storage SQL sink or its folder scope was lost");
        foreach(var sql in new[]{
            Sql+"class A { void M(SqliteCommand cmd) { cmd.CommandText = \"PRAGMA foreign_keys=OFF\"; } }",
            Sql+"class A { void M(SqliteCommand cmd, string order) { cmd.CommandText = \"SELECT id FROM item WHERE pair_id=$p\" + order; } }",  // concatenated in place
            Sql+"class A { void M(SqliteCommand cmd, string c) { cmd.CommandText = $\"SELECT {c} FROM item WHERE id=$id\"; } }"})  // interpolated in place
            Check.Require(Permitted(sql,Store) is {Length:>0} sites && sites.All(x=>x),"Storage SQL destination not recognised: "+sql);
        foreach(var (sql,sink) in new[]{
            (Sql+"class A { SqliteCommand M(SqliteConnection db) => StorageCommand.Create(db, null, \"select id from item\"); }",Create),  // lowercase
            (Sql+"class A { static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; } void M(SqliteConnection db) => Execute(db, \"SELECT CAST(x AS TEXT) FROM issue WHERE kind='InfoLinks'\"); }",Store+":Execute.sql"),
            // Sol r6 R6-2: a helper parameter that is display text or SQL by mode is only reported; no receipt, so it counts.
            (Sql+"class A { static void Run(SqliteCommand cmd, bool inspect, System.Action<string> present, string text) { if (inspect) present(text); else cmd.CommandText = text; } void M(SqliteCommand cmd, System.Action<string> show) => Run(cmd, true, show, \"Please inspect the saved item.\"); }",Store+":Run.text")})
            Check.Require(Sited(sql,Store) is [(false,var reported)] && reported==sink,"Storage helper argument not reported as its sink: "+sql);
        // R127 (R7-3): a local function has no symbol ID, so its argument reports no sink and counts.
        Check.Require(Sited(Sql+"class A { void M(SqliteConnection db) { void Run(string sql) { } Run(\"SELECT id FROM item\"); } }",Store) is [(false,null)],"Local-function argument reported as a sink");
        // Sol r7 R7-3: an unreviewed overload declared on the reviewed method's closing line is its own symbol and offset.
        var overloads=Rows(Sql+"class A { static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; } static void Execute(SqliteConnection db, string sql, int n) { System.Console.WriteLine(sql); }\n"+
            "void M(SqliteConnection db) { Execute(db, \"SELECT id FROM item\"); Execute(db, \"SELECT id FROM item\", 1); } }",Store).Select(x=>x.GetProperty("sink")).ToArray();
        Check.Require(overloads is [{ValueKind:JsonValueKind.Array} reviewedSink,{ValueKind:JsonValueKind.Array} sameLine] &&
            reviewedSink[2].GetString()!.EndsWith(",System.String)",StringComparison.Ordinal) && sameLine[2].GetString()!.EndsWith(",System.Int32)",StringComparison.Ordinal) &&
            reviewedSink[1].GetInt32()<sameLine[1].GetInt32(),"Same-line overload shares the reviewed sink identity");
        foreach(var text in new[]{
            "class A { void M(System.IO.TextWriter writer) { var detail = \"path_key missing\"; writer.WriteLine(detail); } }",  // written out, not SQL
            "class A { void M() { var sql=\"SELECT id FROM item WHERE id=$id\"; } }",  // no destination
            Sql+"class A { static void Show(System.IO.TextWriter w, string sql) => w.WriteLine(sql); void M(System.IO.TextWriter w) => Show(w, \"SELECT id FROM item\"); }",
            Sql+"class Command { public string CommandText { get; set; } void M() => new Command().CommandText = \"select id from item\"; }",  // not a DbCommand
            "class A { void M() => throw new InvalidOperationException(\"Store row missing\"); }","class A { void M() => writer.WriteLine(\"Store row missing\"); }",
            "class A { void M() => _ = Text.Get(Lookup(\"Store row missing\")); }","class A { RunEvent M() => new RunEvent(kind, what, path, \"Store row missing\", outcome); }",
            Sql+"class A { void M(System.IO.TextWriter writer, SqliteCommand cmd, string query) { string text = \"Please inspect the saved item.\"; writer.WriteLine(text); text = query; cmd.CommandText = text; } }",  // overwritten before the SQL read
            Sql+"class A { void M(System.IO.TextWriter writer, SqliteCommand cmd, string query) { string text = \"Please inspect the saved item.\"; writer.WriteLine(text); ref string slot = ref text; slot = query; cmd.CommandText = text; } }",  // overwritten through a ref alias
            Sql+"class A { readonly string text = \"Please inspect the saved item.\"; A(System.IO.TextWriter writer, string query) { writer.WriteLine(text); text = query; } void M(SqliteCommand cmd) { cmd.CommandText = text; } }",  // readonly field rewritten by its constructor
            // R123: no local, collection, branch or result flow, so SQL built away from its destination counts too.
            Sql+"class A { SqliteCommand M(SqliteConnection db,string c) { var sql=$\"SELECT {c} FROM item WHERE id=$id\"; return StorageCommand.Create(db,null,sql); } }",
            Sql+"class A { SqliteCommand M(SqliteConnection db, bool all, string union) { var parts = new List<string>(); string Part(string column) => \"SELECT \" + column + \" FROM item\"; parts.Add(Part(all ? \"id\" : \"path_key\")); return StorageCommand.Create(db, null, string.Join(union, parts)); } }",
            Sql+"class A { void M(System.IO.TextWriter writer, SqliteCommand cmd, string query) { var parts = new List<string> { query }; var note = \"Please inspect the saved item.\"; writer.WriteLine(note); parts.Add(note); parts.RemoveAt(parts.Count - 1); cmd.CommandText = string.Join(string.Empty, parts); } }",  // removed before the SQL read
            Sql+"class A { SqliteCommand M(SqliteConnection db, bool all) => StorageCommand.Create(db, null, all ? \"SELECT id FROM item\" : \"SELECT path_key FROM item\"); }",  // a branch is not a destination position
            // R124: a const, string.Join or a candidate of a failed bind proves no destination, so these count too.
            Sql+"class A { const string Range=\" AND local_key >= $localLow AND local_key < $localHigh\"; SqliteCommand M(SqliteConnection db) => StorageCommand.Create(db, null, \"SELECT id FROM item WHERE pair_id=$p\" + Range); }",
            Sql+"class A { const string Clause=\" WHERE id=$id AND kind=$kind\"; void M(SqliteCommand cmd, System.IO.TextWriter writer, string query) { cmd.CommandText = query + Clause; writer.WriteLine(Clause); } }",  // Sol r6: a const used as SQL and as display text
            Sql+"class A { SqliteCommand M(SqliteConnection db, string[] parts) => StorageCommand.Create(db, null, string.Join(\" UNION ALL \", parts)); }",
            Sql+"class A { void M(SqliteConnection db) => StorageCommand.Create(db, \"SELECT id FROM item\"); }",
            // Sol r7 R7-5: a user-defined + takes the text to a status callback and returns independent SQL.
            Sql+"struct Part { public static System.Action<string>? Status; public static string operator +(Part part, string text) { Status?.Invoke(text); return \"SELECT id FROM item\"; } } class A { void M(SqliteCommand cmd, Part part) { cmd.CommandText = part + \"Please inspect this item.\"; } }",
            "class A { string Reason() => \"Please inspect the saved item.\"; }","class A { void M() { var note=\"saved item was not found\"; Show(note); } }"})
            Check.Require(!Permitted(text,Store).Any(x=>x),"Storage text permitted as SQL: "+text);
        Check.Require(!Permits(schemaText.Replace("Create=","Extra="),schemaPath) && !Permits(schemaText.Replace("CREATE TABLE item(id INTEGER)","SELECT id FROM item"),schemaPath),
            "Unlisted Schema member or non-DDL Schema text permitted");
        var names="namespace Example; static class ResourceNames { public const string HostAssembly=\"Sync Shark\"; }";
        var namesPath="src/SyncShark.Engine/ResourceNames.cs";
        Check.Require(Permits(names,namesPath) && !Permits(names.Replace("HostAssembly","HostTitle"),namesPath),"Spaced resource name permission is not limited to HostAssembly");
        var sibling=context.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Labels { public static readonly string Retry=\"Retry this item\"; }"));
        Check.Require(Offends("class A { void M() => Console.WriteLine(Labels.Retry); }","A.cs",words,sibling),"Sibling constant escaped");
        var trees=new[]{"src","apps"}.SelectMany(folder=>Directory.EnumerateFiles(Path.Combine(input.Root,folder),"*.cs",SearchOption.AllDirectories))
            .Where(f=>!Path.GetRelativePath(input.Root,f).Split(Path.DirectorySeparatorChar).Any(s=>s is "bin" or "obj"))
            .Select(f=>CSharpSyntaxTree.ParseText(File.ReadAllText(f),path:f)).ToArray();
        var production=context.AddSyntaxTrees(trees);
        foreach(var tree in trees) Check.Require(!Offends(string.Empty,Path.GetFileName(tree.FilePath),words,production,tree),tree.FilePath);
        return Task.CompletedTask;
    }
    private static bool Offends(string source, string file, ISet<string>? words=null,CSharpCompilation? context=null,SyntaxTree? existing=null)
    {
        var tree = existing ?? CSharpSyntaxTree.ParseText(source);var root=tree.GetRoot(); SemanticModel? model=null;
        static bool Emits(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText is "Write" or "WriteLine" or "WriteAsync" or "WriteLineAsync" or "LogInformation" or "LogWarning" or "LogError";
        bool Message(ExpressionSyntax expr)
        {
            foreach(var node in expr.DescendantNodesAndSelf())
            {
                if(node is InterpolatedStringTextSyntax part && part.TextToken.ValueText.Any(char.IsLetter) || node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.Any(char.IsLetter)) return true;
                if(node is not IdentifierNameSyntax id || context==null) continue;
                model??=(existing==null ? context.AddSyntaxTrees(tree) : context).GetSemanticModel(tree);
                var symbol=model.GetSymbolInfo(id).Symbol;
                if(symbol is ILocalSymbol { IsConst:true,ConstantValue:string local } && local.Any(char.IsLetter) || symbol is IFieldSymbol { IsConst:true,ConstantValue:string field } && field.Any(char.IsLetter)) return true;
                if(symbol is IFieldSymbol { IsStatic:true,IsReadOnly:true } stored && stored.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax {Initializer.Value:var value} && value!=null && model.Compilation.GetSemanticModel(value.SyntaxTree).GetConstantValue(value) is {HasValue:true,Value:string text} && text.Any(char.IsLetter)) return true;
            }
            return false;
        }
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
                model??=(existing==null ? context.AddSyntaxTrees(tree) : context).GetSemanticModel(tree);
                target=model.GetTypeInfo(obj).ConvertedType?.ToDisplayString();
            }
            if(target?.Split('.').Last()=="RunEvent" && obj.ArgumentList?.Arguments.Any(a=>Message(a.Expression))==true) return true;
        }
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is string text && (text.StartsWith("https://", StringComparison.Ordinal) ||
                literal.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(creation=>creation.Type.ToString().EndsWith("Exception",StringComparison.Ordinal) && creation.Type.ToString().Split('.').Last() is not ("ArgumentNullException" or "ArgumentOutOfRangeException" or "ObjectDisposedException") &&
                    (text.Any(char.IsWhiteSpace) || text.Any(char.IsLetter) && literal.Parent is ArgumentSyntax arg && creation.ArgumentList?.Arguments.FirstOrDefault()==arg)))) return true;
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
    private const string DropboxEntriesFile = "src/SyncShark.Engine/Dropbox/DropboxEntries.cs";
    private const string EngineNamespace = "Massdex.SyncShark.Engine";
    private const string StorageFolder = "src/SyncShark.Engine/Storage/";

    /// <summary>Where a Storage literal's value lands, by compiler-resolved symbols (R124, R127): directly on the right
    /// side of a DbCommand's CommandText (inside an interpolation, parentheses or a + the compiler binds to the built-in
    /// string concatenation there) is SQL by role; an argument bound to a by-value parameter of a method declared in
    /// source is reported as that sink [path, offset, symbol, parameter] (the code-point offset of the method's
    /// identifier and its documentation ID), SQL only through a reviewed sink receipt bound to that whole method. Nothing
    /// else is SQL: no const, local, field, collection, string.Join/Concat, branch, result or local-function flow, no
    /// user-defined or unbound +, and no proof from a helper's body or from a candidate symbol of a failed bind.</summary>
    internal sealed class SqlFlow(CSharpCompilation compilation, IReadOnlyDictionary<SyntaxTree, string> paths)
    {
        private readonly Dictionary<SyntaxTree, SemanticModel> models = new();

        private SemanticModel Model(SyntaxNode node) =>
            models.TryGetValue(node.SyntaxTree, out var model) ? model : models[node.SyntaxTree] = compilation.GetSemanticModel(node.SyntaxTree);

        // R127 (R7-5): a + composes SQL only when it binds string's built-in concatenation, never a user-defined operator.
        private bool Composes(SyntaxNode parent) =>
            parent is InterpolatedStringExpressionSyntax or InterpolationSyntax or ParenthesizedExpressionSyntax ||
            parent.IsKind(SyntaxKind.AddExpression) && Model(parent).GetSymbolInfo(parent).Symbol is IMethodSymbol
                { MethodKind: MethodKind.BuiltinOperator, ContainingType.SpecialType: SpecialType.System_String };

        public bool Reaches(SyntaxNode node) => Destination(node).Direct;

        public (bool Direct, object[]? Sink) Destination(SyntaxNode node)
        {
            for (var parent = node.Parent; parent != null; node = parent, parent = node.Parent)
            {
                if (Composes(parent)) continue;
                return parent switch
                {
                    ArgumentSyntax argument => (false, Sink(argument)),
                    AssignmentExpressionSyntax assignment => (assignment.Right == node && CommandText(assignment.Left), null),
                    _ => (false, null),
                };
            }
            return (false, null);
        }

        private object[]? Sink(ArgumentSyntax argument)
        {
            if (argument.Parent is not BaseArgumentListSyntax list || list.Parent is not { } call ||
                Model(call).GetSymbolInfo(call).Symbol is not IMethodSymbol method) return null;
            var index = list.Arguments.IndexOf(argument);
            var parameter = argument.NameColon is { } name ? method.Parameters.FirstOrDefault(p => p.Name == name.Name.Identifier.ValueText) :
                index < method.Parameters.Length ? method.Parameters[index] : null;
            // R127 (R7-3): the sink is the method symbol itself, never a line or a name; a local function has no symbol ID.
            var symbol = (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentId();
            if (parameter is not { RefKind: RefKind.None, IsParams: false } || symbol is null ||
                parameter.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().Parent?.Parent is not
                    MethodDeclarationSyntax { Identifier: var token } || !paths.TryGetValue(token.SyntaxTree!, out var path)) return null;
            var text = token.SyntaxTree!.GetText().ToString();
            return [path, token.SpanStart - text.Take(token.SpanStart).Count(char.IsLowSurrogate), symbol, parameter.Name];
        }

        private bool CommandText(ExpressionSyntax target)
        {
            if (Model(target).GetSymbolInfo(target).Symbol is IPropertySymbol { Name: "CommandText" } property)
                for (var type = property.ContainingType; type != null; type = type.BaseType)
                    if (type.Name == "DbCommand" && type.ContainingNamespace.ToDisplayString() == "System.Data.Common") return true;
            return false;
        }
    }

    internal static string? StringViolation(SyntaxToken token, string? relativePath, ref SemanticModel? grammarModel,
        Func<SyntaxNode, bool>? reachesSql = null)
    {
        // SQL lives only in Storage (std 1.1): a Storage literal whose value reaches a SQL destination is owned there.
        // Schema.cs keeps its own member rule below, so an unlisted Schema member stays counted.
        if (SqlScope(relativePath) &&
            (token.IsKind(SyntaxKind.InterpolatedStringTextToken) || IsStringLiteral(token)) && reachesSql?.Invoke(token.Parent!) == true) return null;
        if (token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            return token.Value is string interpolated && interpolated.Length > 0 ? "interpolated text " + token.Text : null;
        if (!IsStringLiteral(token) ||
            token.Value is not string text || text.Length == 0) return null;
        // Schema SQL owns its const fragments; other owners cannot hide sentences.
        var owner = OwnerOf(token.Parent, relativePath);
        if (owner == "Schema")
        {
            var sql=text.TrimStart();
            if (MemberOf(token.Parent)=="Pragmas" ? sql.StartsWith("PRAGMA ",StringComparison.OrdinalIgnoreCase) : sql.StartsWith("CREATE TABLE ",StringComparison.OrdinalIgnoreCase)) return null;
        }
        else if (owner is not null && (owner == "StateSchema" || !text.Contains(' ') ||
            owner == "ResourceNames" && MemberOf(token.Parent) == nameof(ResourceNames.HostAssembly))) return null;
        if (relativePath == DropboxEntriesFile &&
            token.Parent is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.Utf8StringLiteralExpression) &&
            literal.Parent is ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax })
        {
            grammarModel ??= GrammarModel(token.SyntaxTree!);
            if (IsNamedUtf8Grammar(token, grammarModel)) return null;
        }
        return "text literal " + token.Text;
    }

    private static bool SqlScope(string? relativePath) =>
        relativePath?.StartsWith(StorageFolder, StringComparison.Ordinal) == true && relativePath != OwnerFiles["Schema"];

    internal static bool IsStringLiteral(SyntaxToken token) =>
        token.Kind().ToString().Contains("StringLiteral", StringComparison.Ordinal);

    /// <summary>The classes allowed to hold named literals, each for its own kind, and the file each must be declared
    /// in (design section 10).</summary>
    private static readonly Dictionary<string, string> OwnerFiles = new(StringComparer.Ordinal)
    {
        ["EngineSettings"] = "src/SyncShark.Engine/EngineSettings.cs",
        ["SettingsParser"] = "src/SyncShark.Engine/SettingsParser.cs",
        ["DropboxListing"] = "src/SyncShark.Engine/Dropbox/DropboxListing.cs",
        ["Schema"] = "src/SyncShark.Engine/Storage/Schema.cs",
        ["StateSchema"] = "src/SyncShark.Engine/Storage/StateSchema.cs",
        ["DropboxProtocol"] = "src/SyncShark.Engine/Dropbox/DropboxProtocol.cs",
        ["DropboxEntries"] = DropboxEntriesFile,
        ["VolumeSpace"] = "src/SyncShark.Engine/Local/VolumeSpace.cs",
        ["DownloadSpace"] = "src/SyncShark.Engine/Local/DownloadSpace.cs",
        ["LocalBin"] = "src/SyncShark.Engine/Local/LocalBin.cs",
        ["LocalSide"] = "src/SyncShark.Engine/Local/LocalSide.cs",
        ["LocalRemoval"] = "src/SyncShark.Engine/Local/LocalRemoval.cs",
        ["LocalListing"] = "src/SyncShark.Engine/Local/LocalListing.cs",
        ["LocalErrors"] = "src/SyncShark.Engine/Local/LocalErrors.cs",
        ["ResourceFailures"] = "src/SyncShark.Engine/ResourceFailures.cs",
        ["ResourceNames"] = "src/SyncShark.Engine/ResourceNames.cs",
        ["PathNames"] = "src/SyncShark.Engine/PathNames.cs",
        ["PathLocks"] = "src/SyncShark.Engine/PathLocks.cs",
        ["StageNames"] = "src/SyncShark.Engine/Local/StageNames.cs",
        ["RunLog"] = "src/SyncShark.Engine/Run/RunLog.cs",
        ["AdaptiveLimiter"] = "src/SyncShark.Engine/Dropbox/AdaptiveLimiter.cs",
        ["TimeRules"] = "src/SyncShark.Engine/Planner/TimeRules.cs",
        ["ReplaceTurn"] = "src/SyncShark.Engine/ReplaceTurn.cs",
        ["EngineHost"] = "src/SyncShark.Engine/Run/EngineHost.cs",
        ["Monitor"] = "src/SyncShark.Engine/Run/Monitor.cs",
        ["PairRuntimes"] = "src/SyncShark.Engine/PairRuntimes.cs",
        ["FolderMoveRunner"] = "src/SyncShark.Engine/Executor/FolderMoveRunner.cs",
        ["EngineCliCatalog"] = "src/SyncShark.Core/Engine/EngineCliCatalog.cs",
    };

    // Narrow exemptions follow each member's owner; other constants there remain offences.
    private static readonly Dictionary<string, IReadOnlySet<string>> MemberOwners = new(StringComparer.Ordinal)
    {
        ["Schema"] = new HashSet<string>(StringComparer.Ordinal) { "Pragmas", "Create", "CreateScan" },
        ["EngineSettings"] = new HashSet<string>(StringComparer.Ordinal) { "AtomicReplaceDelayMultiplier" },
        ["SettingsParser"] = new HashSet<string>(StringComparer.Ordinal)
            { "MaximumAtomicReplaceAttempts", "MaximumAtomicReplaceInitialDelayMilliseconds" },
        ["DropboxListing"] = new HashSet<string>(StringComparer.Ordinal) { "SpellingQueued", "SpellingReleased" },
        ["EngineHost"] = new HashSet<string>(StringComparer.Ordinal)
            { "SingleHolder", "LatestStatusCapacity" },
        ["Monitor"] = new HashSet<string>(StringComparer.Ordinal) { "QuietSeconds" },
        ["PairRuntimes"] = new HashSet<string>(StringComparer.Ordinal) { "FolderMovePrefix" },
        ["FolderMoveRunner"] = new HashSet<string>(StringComparer.Ordinal) { "Halves" },
        ["ResourceFailures"] = new HashSet<string>(StringComparer.Ordinal)
            { "ErrorDiskFull", "ErrorHandleDiskFull", "DiskFullHResult", "HandleDiskFullHResult" },
    };

    // The owner class of a const field, or null when the field is not a const, its class is not an owner, the
    // declaration is not in that owner's exact repo-relative file (a nested path is not the owner's file, R6), or
    // the owner's exemption names specific members and this is not one of them.
    private static string? OwnerOf(SyntaxNode? node, string? relativePath)
    {
        var field = ConstField(node);
        if (field?.Parent is not TypeDeclarationSyntax type ||
            type.Parent is not (BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)) return null;
        var name = type.Identifier.ValueText;
        if (!OwnerFiles.TryGetValue(name, out var file) || relativePath != file) return null;
        return !MemberOwners.TryGetValue(name, out var members) || members.Contains(MemberOf(node)) ? name : null;
    }

    private static string MemberOf(SyntaxNode? node) =>
        node?.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";

    private static FieldDeclarationSyntax? ConstField(SyntaxNode? node) =>
        node?.Ancestors().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(field => field.Modifiers.Any(SyntaxKind.ConstKeyword));

    private static SemanticModel GrammarModel(SyntaxTree tree) =>
        CSharpCompilation.Create("Grammar", [tree,
            CSharpSyntaxTree.ParseText("global using System;", new CSharpParseOptions(LanguageVersion.CSharp12))],
            [MetadataReference.CreateFromFile(typeof(ReadOnlySpan<byte>).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);

    private static bool IsNamedUtf8Grammar(SyntaxToken token, SemanticModel model)
    {
        if (token.Parent?.Parent is not ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } ||
            property.ExpressionBody?.Expression != token.Parent || property.Initializer is not null ||
            property.AccessorList is not null ||
            property.Parent is not ClassDeclarationSyntax replyTokens || replyTokens.Identifier.ValueText != "ReplyTokens" ||
            replyTokens.Parent is not ClassDeclarationSyntax outer || outer.Identifier.ValueText != "DropboxEntries" ||
            outer.Parent is not BaseNamespaceDeclarationSyntax || NamespaceOf(outer) != EngineNamespace + ".Dropbox" ||
            model.GetDeclaredSymbol(property) is not IPropertySymbol
            {
                IsStatic: true, DeclaredAccessibility: Accessibility.Private, IsIndexer: false,
                ReturnsByRef: false, ReturnsByRefReadonly: false, SetMethod: null, GetMethod: not null,
                Type: INamedTypeSymbol span
            } symbol || symbol.ContainingType.Name != "ReplyTokens" ||
            symbol.ContainingType.ContainingType is not { Name: "DropboxEntries", ContainingType: null } ||
            symbol.ContainingNamespace.ToDisplayString() != EngineNamespace + ".Dropbox") return false;
        var framework = (IAssemblySymbol)model.Compilation.GetAssemblyOrModuleSymbol(model.Compilation.References.Single())!;
        if (!SymbolEqualityComparer.Default.Equals(span.OriginalDefinition,
                framework.GetTypeByMetadataName("System.ReadOnlySpan`1")) ||
            span.TypeArguments.Single().SpecialType != SpecialType.System_Byte) return false;
        return token.ValueText == (property.Identifier.ValueText switch
        {
            "TrueToken" => "true",
            "FalseToken" => "false",
            "NullToken" => "null",
            "EmptyStringToken" => "\"\"",
            "PropertyToken" => "\"p\":",
            "NumberToken" => "0 ",
            _ => null,
        });
    }

    private static string NamespaceOf(SyntaxNode node) => string.Join(".",
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

    internal static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RunEvent).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));

    // The SDK's implicit usings for the product projects (ImplicitUsings=enable).
    private static readonly SyntaxTree ImplicitUsings = CSharpSyntaxTree.ParseText("global using System; global using System.IO; " +
        "global using System.Collections.Generic; global using System.Linq; global using System.Net.Http; " +
        "global using System.Threading; global using System.Threading.Tasks;", new CSharpParseOptions(LanguageVersion.CSharp12));

    internal static void WriteLiteralSites(string candidate, string release, string output)
    {
        using var stream = new StreamWriter(output, false, new UTF8Encoding(false));
        foreach (var (tree, root) in new[] { ("candidate", candidate), ("release", release) })
        {
            // One compilation per tree, so a SQL destination resolves across that tree's files.
            var files = Files(Path.Combine(root, "src")).Select(file =>
            {
                var source = File.ReadAllText(file).Replace("\r\n", "\n").Replace('\r', '\n');
                return (path: Path.GetRelativePath(root, file).Replace('\\', '/'), source, syntax: SiteTree(source));
            }).ToList();
            var flow = new SqlFlow(CSharpCompilation.Create("Sites", files.Select(f => f.syntax).Append(ImplicitUsings), References()),
                files.ToDictionary(f => f.syntax, f => f.path));
            foreach (var (path, source, syntax) in files)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
                stream.WriteLine(JsonSerializer.Serialize(new { tree, path, hash, sites = Sites(syntax, source, path, flow) }));
            }
        }
    }

    private static IEnumerable<string> Files(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("literal artifact: source symlink");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetFileName(entry) is not ("bin" or "obj" or ".git"))
                    foreach (var child in Files(entry)) yield return child;
            }
            else if (Path.GetExtension(entry) == ".cs") yield return entry;
        }
    }

    // The lexical inventory includes inactive branches, but not directive lines.
    internal static SyntaxTree SiteTree(string source)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp12);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        if (!tree.GetRoot().ContainsDirectives) return tree;
        var parsed = source.ToCharArray();
        foreach (var directive in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.IsDirective))
            for (var i = directive.SpanStart; i < directive.Span.End; i++)
                if (parsed[i] != '\n') parsed[i] = ' ';
        return CSharpSyntaxTree.ParseText(new string(parsed), options);
    }

    /// <summary>Each string literal site with its permission and, for a Storage site the owner does not permit, the helper
    /// parameter it is bound to (a sink the ratchet's reviewed sink receipts decide).</summary>
    internal static IEnumerable<object> Sites(SyntaxTree tree, string source, string path, SqlFlow flow)
    {
        var root = tree.GetRoot();
        SemanticModel? model = null;
        var forbidden = new HashSet<int>();
        foreach (var token in root.DescendantTokens())
            if (StringViolation(token, path, ref model, flow.Reaches) is not null) forbidden.Add(token.SpanStart);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var line = 1;
        var previous = 0;
        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>().Where(node =>
            node is InterpolatedStringExpressionSyntax || node is LiteralExpressionSyntax literal &&
            IsStringLiteral(literal.Token)))
        {
            for (var i = previous; i < expression.SpanStart; i++)
                if (source[i] == '\n') { line++; ordinals.Clear(); }
            previous = expression.SpanStart;
            var text = source.Substring(expression.SpanStart, expression.Span.Length);
            var ordinal = ordinals.GetValueOrDefault(text);
            ordinals[text] = ordinal + 1;
            var permitted = !expression.DescendantTokens().Any(token => forbidden.Contains(token.SpanStart));
            var sink = permitted || !SqlScope(path) ? null : flow.Destination(expression).Sink;
            yield return new { line, text, ordinal, permitted, sink };
        }
    }

}
