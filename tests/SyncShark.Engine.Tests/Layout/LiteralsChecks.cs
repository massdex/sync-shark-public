using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>The literals rule of design section 10 (`--engine-checks literals`): no text or number in engine code
/// outside its owner, and no default value in the settings record or parser.</summary>
public static class LiteralsChecks
{
    // Enforcement and its required passing/failing fixtures stay together so each permission has one owner.
    private const string StateSchemaFile = "src/SyncShark.Engine/Storage/StateSchema.cs";
    private const string DropboxProtocolFile = "src/SyncShark.Engine/Dropbox/DropboxProtocol.cs";
    private const string OtherStateSchemaFile = "src/SyncShark.Engine/Local/StateSchema.cs";
    private const string LocalErrorsFile = "src/SyncShark.Engine/Local/LocalErrors.cs";
    private const string DropboxEntriesFile = "src/SyncShark.Engine/Dropbox/DropboxEntries.cs";
    private const string EngineNamespace = "Massdex.SyncShark.Engine";

    public static void Run()
    {
        // Failing fixtures (design section 10).
        Fails("""static class C { static void M() { var retries = 3; } }""");
        Fails("""static class C { static void M() { var retries = 1; } }""");
        Fails("""static class C { static readonly System.Threading.SemaphoreSlim Gate = new(1, 1); }""");
        Fails("""static class C { static void M() => throw new System.InvalidOperationException("Something failed."); }""");
        Fails("""static class DropboxProtocol { public const string Broken = "a whole sentence"; }""", DropboxProtocolFile);
        // The new OS-code owner is file-bound like every other owner.
        Fails("""static class LocalErrors { private const int DiskFullHResult = unchecked((int)0x80070070); }""", "src/SyncShark.Engine/Local/OtherErrors.cs");
        Fails("""static class C { static string M(int attempts) => $"Something failed after {attempts} tries."; }""");
        // An owner class declared outside its own file is a counterfeit: it gets no exemption.
        Fails("""static class StateSchema { public const string Token = "token"; }""", OtherStateSchemaFile);
        Fails("""static class StateSchema { public const string Token = "token"; }""", "nested/" + StateSchemaFile);
        Fails("""public sealed record EngineSettings(int LogRetentionDays = 30);""");
        Fails("""static class C { private const int Retries = 5; }""");
        Fails("""static class C { private const string Name = "token"; }""");
        // The enum exemption covers only a member's own explicit value; the same initialiser shape elsewhere is a
        // bare number.
        Fails("""static class C { private static readonly int Chunk = 5; }""");

        // Passing fixtures (design section 10).
        Passes("""static class StateSchema { public const string Select = "SELECT 1 FROM pair WHERE pair_id = $p;"; }""",
            StateSchemaFile);
        Passes("""static class DropboxProtocol { public const string ModeAdd = "add"; }""", DropboxProtocolFile);
        Passes("""static class LocalErrors { private const int DiskFullHResult = unchecked((int)0x80070070); }""", LocalErrorsFile);
        Passes("""static class C { static int M(int i) => i + 1; }""");
        Passes("""static class C { static bool M(int count) => count == 0; }""");
        Passes("""static class C { static int M(int[] parts) => parts[0]; }""");
        Passes("""static class C { static string M(string path) => path[1..]; }""");
        Passes("""static class C { static int[] M(int[] path) => path[..^1]; }""");
        Passes("""public sealed record EngineSettings(int LogRetentionDays);""");
        // An enum member's explicit value is a frozen declaration: moving it would change persisted data.
        Passes("""enum E { A, B = 5 }""");

        OwnerFixtures();
        DefaultArithmeticFixtures();
        ZeroFixtures();
        GrammarFixtures();
        MigrationMessageChecks();
        UiMessageChecks();

        var root = FindRepoRoot();
        var offences = new List<string>();
        foreach (var file in ScannedFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            offences.AddRange(Violations(File.ReadAllText(file), relative)
                .Select(offence => $"{relative}:{offence.Line} {offence.What}"));
        }
        Check.Require(offences.Count == 0, "literals: " + string.Join("; ", offences));
    }

    private static void MigrationMessageChecks()
    {
        var root = FindRepoRoot();
        using var catalog = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/SyncShark.Core/V2/Adapters/edge-messages.json")));
        var templates = catalog.RootElement.GetProperty("messages");
        foreach (var expression in new[] { "MoveText(\"data-move-noop\")", "MoveText(\"data-move-precommit-failed\", (\"reason\", error.Message))", "MoveText(\"data-move-cleanup-incomplete\", (\"location\", result.DataLocation))" })
            Check.Require(!HasInlineText(SyntaxFactory.ParseExpression(expression), templates), "migration stable catalog expression passes: " + expression);
        foreach (var expression in new[] { "\"Failed.\"", "\"Data move failed.\"", "\"No-op\"", "\"Committed\"", "$\"Failed {p}\"", "MoveText(\"data-move-noop\") + \" appended\"", "MoveText(\"data-move-noop\") + suffix" })
            Check.Require(HasInlineText(SyntaxFactory.ParseExpression(expression), templates), "migration inline or appended text fails: " + expression);
        // Inline text cannot reach a return site through a local variable or a same-file helper.
        foreach (var (source, what) in new[]
        {
            ("""static partial class C { static string M() { var text = "Failed."; return text; } }""", "local variable"),
            ("""static partial class C { static string M() { string text; text = "Failed."; return text; } }""", "assigned local variable"),
            ("""static partial class C { static string M() => Helper(); static string Helper() => "Failed."; }""", "helper"),
        })
        {
            Check.Require(ReturnCarriesInlineText(source, templates), "migration inline text through a " + what + " fails: " + source);
            Check.Require(ReturnCarriesInlineText(source, null), "UI inline text through a " + what + " fails: " + source);
        }
        foreach (var (source, what) in new[]
        {
            ("""static partial class C { static string M() { var text = MoveText("data-move-noop"); return text; } }""", "local"),
            ("""static partial class C { static string M() => Helper(); static string Helper() => MoveText("data-move-noop"); }""", "helper"),
            // A repeated call in one expression is not recursion and must pass.
            ("""static partial class C { static string M() => Pick(Helper(), Helper()); static string Pick(string first, string second) => first; static string Helper() => MoveText("data-move-noop"); }""", "repeated helper"),
            ("""static partial class C { static string M() { var text = MoveText("data-move-noop"); return Pick(text, text); } static string Pick(string first, string second) => first; }""", "repeated local"),
        })
            Check.Require(!ReturnCarriesInlineText(source, templates), "migration resolved catalog call through a " + what + " passes: " + source);
        var migrationTree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/SyncShark.Core/SyncDataLocation.cs")));
        var migration = migrationTree.GetRoot();
        var migrationModel = MessageModel(migrationTree);
        foreach (var method in migration.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText is "MoveAsync" or "CleanupOldCopy"))
        foreach (var statement in method.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            var expression = statement.Expression!;
            if (method.Identifier.ValueText == "MoveAsync")
                expression = ((BaseObjectCreationExpressionSyntax)expression).ArgumentList!.Arguments[2].Expression;
            Check.Require(!HasInlineText(expression, templates, migrationModel), "migration outcome assembly: " + method.Identifier.ValueText);
        }
        var recovery = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/SyncShark.Core/SyncDataLocation.Recovery.cs"))).GetRoot();
        var formatter = recovery.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "MoveText");
        var expected = SyntaxFactory.ParseExpression("CliCatalogResource.Fill(V2.MessageCatalog.Default.Message(key), values)");
        Check.Require(SyntaxFactory.AreEquivalent(formatter.ExpressionBody!.Expression, expected), "migration formatter uses the existing catalog and one-pass filler");
        Check.Require(!SyntaxFactory.AreEquivalent(SyntaxFactory.ParseExpression("CliCatalogResource.Fill(V2.MessageCatalog.Default.Message(key), values) + suffix"), expected),
            "migration formatter append-after-lookup fails");
        var checks = new[] {
            ("src/SyncShark.Core/SyncDataLocation.cs", "DescribeMoveFailure", false),
            ("apps/SyncShark/App.DataLocation.cs", "MoveDataAndRestart", true),
            ("src/SyncShark.Core/SyncCliOperations.cs", "", true)
        };
        foreach (var (path, member, catchesOnly) in checks)
        {
            var fileTree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)));
            var syntax = fileTree.GetRoot();
            IEnumerable<SyntaxNode> nodes = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == member);
            if (path.EndsWith("SyncCliOperations.cs", StringComparison.Ordinal))
                nodes = syntax.DescendantNodes().OfType<CatchClauseSyntax>().Where(clause =>
                    clause.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                        invocation.Expression.ToString() == "SyncDataLocation.DescribeMoveFailure"));
            else if (catchesOnly) nodes = nodes.SelectMany(node => node.DescendantNodes().OfType<CatchClauseSyntax>())
                .Where(clause => clause.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.Expression.ToString() == "SyncDataLocation.DescribeMoveFailure"));
            var selected = nodes.ToArray();
            Check.Require(selected.Length == 1 && !HasInlineText(selected[0], templates, MessageModel(fileTree)), "migration message boundary: " + path);
        }
    }

    private static void UiMessageChecks()
    {
        foreach (var expression in new[] { "\"Title\"", "$\"Ceiling {1}\"" })
            Check.Require(HasInlineText(SyntaxFactory.ParseExpression(expression), null),
                "UI inline text fails: " + expression);
        Check.Require(!HasInlineText(SyntaxFactory.ParseExpression(
            "EngineText.Get(EngineTextKey.FolderBrowserPageCeiling, " +
            "(EngineTextArg.Count, n.ToString(EngineText.Get(EngineTextKey.ListingNumberFormat))))"),
            null), "UI key expression passes");
        Check.Require(HasInlineText(
            SyntaxFactory.ParseExpression("MoveText(\"data-move-noop\")"), null),
            "UI does not inherit migration literal-key permission");
        Check.Require(!HasInlineText(
            SyntaxFactory.ParseExpression("oldPages + newPages"), null),
            "UI numeric arithmetic is not literal text");

        foreach (var member in new[]
        {
            "const string T = \"Title\";",
            "static readonly string T = \"Title\";",
            "static string T => \"Title\";",
            "static string T { get { return \"Title\"; } }",
            "static string T { get; } = \"Title\";",
        })
            Check.Require(ReturnCarriesInlineText("static partial class C { " + member + " static string M() => T; }", null),
                "UI inline text through a field or property fails: " + member);

        const string catalogFixture = """
            enum EngineTextKey { FolderBrowserCursorRepeated }
            static class EngineText
            {
                internal static string Get(EngineTextKey key) => string.Empty;
            }
            """;
        foreach (var source in new[]
        {
            """
            static partial class C
            {
                static string M()
                {
                    var text = EngineText.Get(EngineTextKey.FolderBrowserCursorRepeated);
                    return text;
                }
            }
            """,
            """
            static partial class C
            {
                static string M() => Helper();
                static string Helper() =>
                    EngineText.Get(EngineTextKey.FolderBrowserCursorRepeated);
            }
            """
        })
            Check.Require(!ReturnCarriesInlineText(source + catalogFixture, null),
                "UI catalog expression through a local or helper passes");

        var root = FindRepoRoot();
        var sites = new (string Path, string Member,
            Func<MethodDeclarationSyntax, IEnumerable<SyntaxNode>> Select)[]
        {
            ("apps/SyncShark/DropboxFolderBrowserWindow.xaml.cs", "AppendPage",
                method => method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(local => local.Identifier.ValueText == "cycle")),
            ("apps/SyncShark/DropboxFolderBrowserWindow.xaml.cs", "TryReplace",
                method => new SyntaxNode[] { method }),
            ("apps/SyncShark/MainWindow.FolderStorage.cs", "ConfirmFolderStorageConsent",
                method => new SyntaxNode[] { method }),
            ("apps/SyncShark/MainWindow.FolderStorage.cs", "FolderStorageConsentDetail",
                method => new SyntaxNode[] { method }),
            ("apps/SyncShark/MainWindow.xaml.cs", "DisableFilesOnDemand_Click",
                method => method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression.ToString() == "AppDialogWindow.Confirm")),
        };
        foreach (var file in sites.GroupBy(site => site.Path, StringComparer.Ordinal))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file.Key)));
            var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            var model = MessageModel(tree);
            foreach (var site in file)
            {
                var matches = methods.Where(method => method.Identifier.ValueText == site.Member).ToArray();
                var selected = matches.SelectMany(site.Select).ToArray();
                Check.Require(matches.Length == 1 && selected.Length == 1 &&
                    !HasInlineText(selected[0], null, model),
                    "UI message from the strings owner: " + site.Path + " " + site.Member);
            }
        }
    }

    // Null templates require UI text to come from the strings owner; migration keeps its literal-key permission.
    // Both policies follow carried expressions in this file only.
    private static bool HasInlineText(SyntaxNode node, System.Text.Json.JsonElement? templates, SemanticModel? model = null,
        HashSet<ISymbol>? visited = null)
    {
        foreach (var part in node.DescendantNodesAndSelf())
        {
            if (part is InterpolatedStringExpressionSyntax) return true;
            if (templates.HasValue && part is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression)) return true;
            if (part is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                if (!templates.HasValue) return true;
                var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation?.Expression.ToString() != "MoveText") return true;
                var arguments = invocation.ArgumentList.Arguments;
                if (arguments[0].Expression.Span == literal.Span)
                {
                    if (!literal.Token.ValueText.StartsWith("data-move-", StringComparison.Ordinal) ||
                        !templates.Value.TryGetProperty(literal.Token.ValueText, out _)) return true;
                }
                else if (literal.Parent is not ArgumentSyntax { Parent: TupleExpressionSyntax tuple } ||
                    tuple.Arguments[0].Expression.Span != literal.Span || tuple.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax list } ||
                    list.Parent?.Span != invocation.Span) return true;
                continue;
            }
            if (model is null || part is not IdentifierNameSyntax identifier) continue;
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            // Only a string value can carry user text into a return site; other locals and calls cannot.
            var carriesText = symbol switch
            {
                ILocalSymbol local => local.Type.SpecialType == SpecialType.System_String,
                IMethodSymbol method => method.ReturnType.SpecialType == SpecialType.System_String,
                IFieldSymbol field when !templates.HasValue => field.Type.SpecialType == SpecialType.System_String,
                IPropertySymbol property when !templates.HasValue => property.Type.SpecialType == SpecialType.System_String,
                _ => false,
            };
            if (!carriesText) continue;
            visited ??= new(SymbolEqualityComparer.Default);
            if (!visited.Add(symbol!)) continue;
            try
            {
                // The stack entry lasts only while this expression is being resolved, so a repeated call is not recursion.
                if (CarriedText(symbol!, model).Any(carried => HasInlineText(carried, templates, model, visited))) return true;
            }
            finally { visited.Remove(symbol!); }
        }
        return false;
    }

    // Follow same-file initializers and returned expressions, including later assignments to locals.
    private static IEnumerable<SyntaxNode> CarriedText(ISymbol symbol, SemanticModel model)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            // Only this model's tree can be resolved; a fixture stub in another tree carries no user text.
            if (reference.SyntaxTree != model.SyntaxTree) continue;
            var declaration = reference.GetSyntax();
            if (declaration is VariableDeclaratorSyntax { Initializer.Value: { } initial }) yield return initial;
            if (declaration is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax)) continue;
            foreach (var expression in declaration.DescendantNodes().OfType<ReturnStatementSyntax>().Select(statement => (SyntaxNode)statement.Expression!)
                         .Concat(declaration.DescendantNodes().OfType<ArrowExpressionClauseSyntax>()))
                yield return expression;
        }
        if (symbol is not ILocalSymbol local) yield break;
        var references = local.DeclaringSyntaxReferences;
        if (references.Length != 1 || references[0].GetSyntax() is not VariableDeclaratorSyntax declarator) yield break;
        foreach (var assignment in declarator.Ancestors().OfType<MethodDeclarationSyntax>().SelectMany(method =>
                     method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                 .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                     assignment.Left is IdentifierNameSyntax left &&
                     SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(left).Symbol, symbol) &&
                     assignment.SpanStart > declarator.SpanStart))
            yield return assignment.Right;
    }

    private static bool ReturnCarriesInlineText(string source, System.Text.Json.JsonElement? templates)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == "M");
        var model = FixtureModel(tree);
        return method.DescendantNodes().OfType<ReturnStatementSyntax>().Select(statement => (SyntaxNode)statement.Expression!)
            .Concat(method.DescendantNodes().OfType<ArrowExpressionClauseSyntax>())
            .Any(node => HasInlineText(node, templates, model));
    }

    // The fixtures call the real catalog call by name, so they compile against this resolvable stub declaring it.
    private const string MoveTextStub = "static partial class C { internal static string MoveText(string key) => key; }";

    private static SemanticModel FixtureModel(SyntaxTree tree) =>
        CSharpCompilation.Create("MigrationFixtures",
            [tree, CSharpSyntaxTree.ParseText(MoveTextStub, new CSharpParseOptions(LanguageVersion.Latest))],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);

    private static SemanticModel MessageModel(SyntaxTree tree) =>
        CSharpCompilation.Create("MigrationMessages", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);


    private static void OwnerFixtures()
    {
        const string volume = """
            static class VolumeSpace {
                private const string KernelLibrary = "kernel32.dll";
                [System.Runtime.InteropServices.DllImport(KernelLibrary, CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
                private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong availableToCaller, out ulong totalBytes, out ulong totalFreeBytes);
            }
            """;
        const string download = """
            class DownloadSpace {
                private const int ErrorFileNotFound = 2;
                private const int ErrorPathNotFound = 3;
                private const int SingleHolder = 1;
                class VolumeState { public System.Threading.SemaphoreSlim Gate = new(SingleHolder, SingleHolder); public long ReservedBytes; }
                class Lease(VolumeState state, long bytes) {
                    private bool _retired;
                    async System.Threading.Tasks.Task DisposeAsync() {
                        await state.Gate.WaitAsync();
                        try { if (_retired) return; _retired = true; state.ReservedBytes -= bytes; }
                        finally { state.Gate.Release(); }
                    }
                }
            }
            """;
        foreach (var (owner, source) in new[] { ("VolumeSpace", volume), ("DownloadSpace", download) })
        {
            var path = OwnerFiles[owner];
            Passes(source, path);
            foreach (var wrongPath in new[] { null, "nested/" + path, path.Replace(owner + ".cs", "Other.cs") })
                Fails(source, wrongPath);
            Fails("class Other { " + source + " }", path);
        }
        Fails(volume.Replace("DllImport(KernelLibrary,", "DllImport(\"kernel32.dll\","), OwnerFiles["VolumeSpace"]);
        Fails(download.Replace("private const int SingleHolder = 1;", "")
            .Replace("class VolumeState {", "class VolumeState { private const int SingleHolder = 1;"), OwnerFiles["DownloadSpace"]);
        Passes("""class DropboxEntries { const string Token = "true"; }""", DropboxEntriesFile);
        Fails("""class DropboxEntries { class Projection { const string Token = "true"; } }""", DropboxEntriesFile);
        Fails("""class Other { class DropboxEntries { const string Token = "true"; } }""", DropboxEntriesFile);
        Fails("""class DropboxEntries { const string Token = "true"; }""", "nested/" + DropboxEntriesFile);
        foreach (var (owner, members) in new[]
        {
            ("EngineHost", "private const int SingleHolder = 1; private const int LatestStatusCapacity = 1;"),
            ("Monitor", "private const int QuietSeconds = 30;"),
            ("EngineSettings", "private const int AtomicReplaceDelayMultiplier = 2;"),
            ("SettingsParser", "private const int MaximumAtomicReplaceAttempts = 10; private const int MaximumAtomicReplaceInitialDelayMilliseconds = 60_000;"),
            ("DropboxListing", "private const int SpellingQueued = 1; private const int SpellingReleased = -1;")
        })
        {
            var source = "class " + owner + " { " + members + " }";
            Passes(source, OwnerFiles[owner]);
            foreach (var extra in new[]
            {
                "private const int RetryBudget = 5;",
                "private const string Label = \"label\";",
            })
                Fails(source.Replace(members, members + extra), OwnerFiles[owner]);
            Fails(source, "nested/" + OwnerFiles[owner]);
        }
        Fails("class EngineHost { private const int QuietSeconds = 30; }", OwnerFiles["EngineHost"]);
    }

    private static void DefaultArithmeticFixtures()
    {
        foreach (var operation in new[] { "+", "-" })
        {
            var expression = "default(int) " + operation + " 1";
            foreach (var wrapped in new[] { expression, "i + (" + expression + ")", "(" + expression + ")",
                "(long)(" + expression + ")", "checked(" + expression + ")", "unchecked(" + expression + ")",
                "(default(int)) " + operation + " (1)" })
            {
                var source = "class C { static long M(int i) =>\n" + wrapped + "; }";
                Check.Require(Violations(source).Any(offence => offence.Line == 2 && offence.What == "numeric default arithmetic"),
                    "default arithmetic target: " + source);
            }
            foreach (var (prefix, suffix, path) in new[]
            {
                ("class LocalErrors { const int Marker =", "; }", LocalErrorsFile),
                ("enum E { A =", " }", (string?)null),
                ("namespace Massdex.SyncShark.Engine; class PathScope { void HoldsNestedRoot() { for (var i =",
                    "; i < roots.Length; i++) Use(roots[i]); } }", "src/SyncShark.Engine/PathScope.cs"),
            })
            {
                var source = prefix + "\n" + expression + suffix;
                Check.Require(Violations(source, path).Any(offence => offence.Line == 2 && offence.What == "numeric default arithmetic"),
                    "default arithmetic precedence: " + source);
            }
        }
        foreach (var expression in new[] { "i+1", "i-1", "i+=1", "i-=1", "i+default(int)",
            "(default(int)+i)+1", "default(int)", "default(long)", "(int)default", "default(string)",
            "default(S)", "default(T)", "default(int?)" })
            Passes("struct S {} class C { static void M<T>(int i) { var value = " + expression + "; } }");
        Passes("struct Int32 { public static Int32 operator +(Int32 a, Int32 b) => a; } class C { void M() { var value = default(Int32) + default(Int32); } }");
        foreach (var expression in new[] { "5", "-1", "5+5" }) Passes("enum E { A = " + expression + " }");
        Fails("class C { static int M() { return 5; } }");
        Passes("class C { void M() { var retries = 0; } }");
        Passes("class C { void M() { Timeout(0); } }");
        Passes("class C { static readonly System.Threading.SemaphoreSlim Gate = new(0, 0); }");
        Fails("record EngineSettings(int Value = default(int));");
    }

    private static void ZeroFixtures()
    {
        foreach (var zero in new[] { "0", "0U", "0L", "0UL", "0F", "0D", "0M", "+0", "-0", "0.0" })
        {
            Passes("class C { void M() { var value = " + zero + "; } }");
            Passes("class C { static double M(bool flag, double value) => flag ? " + zero + " : value; }");
            Passes("class C { static double M(double? value) => value ?? " + zero + "; }");
        }
        Fails("class EngineSettings { int Value { get; } = default(int); }");
    }

    private static void GrammarFixtures()
    {
        foreach (var (name, text) in new[] { ("TrueToken", "true"), ("FalseToken", "false"), ("NullToken", "null"),
            ("EmptyStringToken", "\"\""), ("PropertyToken", "\"p\":"), ("NumberToken", "0 ") })
        {
            var literal = SymbolDisplay.FormatLiteral(text, quote: true) + "u8";
            var declaration = "private static ReadOnlySpan<byte> " + name + " => " + literal + ";";
            string Source(string member) => "namespace " + EngineNamespace + ".Dropbox; class DropboxEntries { class ReplyTokens { " + member + " } }";
            var source = Source(declaration);
            Passes(source, DropboxEntriesFile);
            Passes(Source(declaration.Replace("ReadOnlySpan<byte>", "global::System.ReadOnlySpan<byte>")), DropboxEntriesFile);
            Passes("using Bytes = System.ReadOnlySpan<byte>; " + Source(declaration.Replace("ReadOnlySpan<byte>", "Bytes")), DropboxEntriesFile);
            foreach (var path in new[] { null, "nested/" + DropboxEntriesFile, DropboxEntriesFile.Replace("DropboxEntries.cs", "Other.cs") })
                Fails(source, path);
            foreach (var variant in new[]
            {
                source.Replace(EngineNamespace + ".Dropbox", "Other"),
                source.Replace("class ReplyTokens", "class Projection"),
                "namespace " + EngineNamespace + ".Dropbox; class ReplyTokens { " + declaration + " }",
                "namespace " + EngineNamespace + ".Dropbox; class DropboxEntries { " + declaration + " }",
                Source("class Deeper { " + declaration + " }"),
                Source(declaration.Replace("static ", "")), Source(declaration.Replace("private ", "public ")),
                Source(declaration.Replace("ReadOnlySpan<byte>", "ref readonly ReadOnlySpan<byte>")),
                Source(declaration.Replace("=> " + literal + ";", "{ get { return " + literal + "; } }")),
                Source(declaration.Replace("=>", "=")), Source(declaration.Replace(name + " =>", name + "() =>")),
                Source(declaration.Replace(literal, "(" + literal + ")")),
                Source(declaration.Replace(literal, "(ReadOnlySpan<byte>)" + literal)),
                Source(declaration.Replace(literal, "Call(" + literal + ")")),
                Source(declaration.Replace(literal, "new[] { " + literal + " }")),
                Source("private static System.Func<ReadOnlySpan<byte>> " + name + " => () => " + literal + ";"),
                Source(declaration.Replace(name, "Other")),
                Source(declaration.Replace(literal, "\"wrong\"u8")), Source(declaration.Replace(literal, "\"user sentence\"u8")),
                Source(declaration.Replace(literal, "\"user sentence\"")), Source(declaration.Replace(literal, "$\"user sentence\"")),
                Source(declaration.Replace(literal, SymbolDisplay.FormatLiteral(text, quote: true))),
                "namespace " + EngineNamespace + ".Dropbox { class ReadOnlySpan<T> {} class DropboxEntries { class ReplyTokens { " + declaration + " } } }",
                "namespace System { class ReadOnlySpan<T> {} } namespace " + EngineNamespace + ".Dropbox { class DropboxEntries { class ReplyTokens { " + declaration.Replace("ReadOnlySpan<byte>", "System.ReadOnlySpan<byte>") + " } } }",
                Source("const string Other = " + SymbolDisplay.FormatLiteral(text, quote: true) + ";"),
                Source("class Deeper { const int Other = 5; }"),
            }) Fails(variant, DropboxEntriesFile);
            foreach (var type in new[] { "string", "Span<byte>", "ReadOnlySpan<char>", "byte[]" })
                Fails(Source(declaration.Replace("ReadOnlySpan<byte>", type)), DropboxEntriesFile);
            foreach (var wrong in name switch
            {
                "NumberToken" => new[] { "0", "0  ", "0\t", "0\n" },
                "PropertyToken" => new[] { "\"p\"" },
                "EmptyStringToken" => new[] { "\"" },
                _ => Array.Empty<string>(),
            }) Fails(Source(declaration.Replace(literal, SymbolDisplay.FormatLiteral(wrong, quote: true) + "u8")), DropboxEntriesFile);
        }
        Passes("class C { string Empty => \"\"; }");
    }

    private static void Fails(string source, string? relativePath = null) =>
        Check.Require(Violations(source, relativePath).Count > 0, "literals fixture should fail: " + source);

    private static void Passes(string source, string? relativePath = null) =>
        Check.Require(Violations(source, relativePath).Count == 0, "literals fixture should pass: " + source);

    private static IReadOnlyList<(int Line, string What)> Violations(string source, string? relativePath = null)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12)).GetRoot();
        var offences = new List<(int Line, string What)>();
        SemanticModel? grammarModel = null;

        // Text: "" anywhere; otherwise only a const field of an owner class in the owner's own file. Outside StateSchema
        // (SQL) the literal holds no space, so no sentence can hide there (a tab or newline separator token is fine).
        // Interpolated text has no const home, so any of it fails (a sentence must be a TextKey template).
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            {
                if (token.Value is string interpolated && interpolated.Length > 0)
                    offences.Add((Line(token), "interpolated text " + token.Text));
                continue;
            }
            if (!token.Kind().ToString().Contains("StringLiteral", StringComparison.Ordinal)) continue;
            if (token.Value is not string text || text.Length == 0) continue;
            var owner = OwnerOf(token.Parent, relativePath);
            if (owner is not null && (owner == "StateSchema" || !text.Contains(' '))) continue;
            if (relativePath == DropboxEntriesFile &&
                token.Parent is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.Utf8StringLiteralExpression) &&
                literal.Parent is ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax })
            {
                grammarModel ??= GrammarModel(root.SyntaxTree);
                if (IsNamedUtf8Grammar(token, grammarModel)) continue;
            }
            offences.Add((Line(token), "text literal " + token.Text));
        }

        // Numbers: zero anywhere; integral 1 only as a comparison or +/- operand or an index/range bound;
        // anything else only as an owner const or an enum member's explicit value (a frozen declaration).
        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
            if (IsDefaultArithmetic(binary))
                offences.Add((Line(binary), "numeric default arithmetic"));
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression)) continue;
            if (literal.Ancestors().OfType<EqualsValueClauseSyntax>()
                .Any(value => value.Parent is EnumMemberDeclarationSyntax && value.Value.Span.Contains(literal.Span))) continue;
            if (OwnerOf(literal, relativePath) is not null) continue;
            if (literal.Token.Value is 0 or 0U or 0L or 0UL or 0F or 0D or 0M) continue;
            if (literal.Token.Value is (1 or 1U or 1L or 1UL) && IsAllowedPosition(literal)) continue;
            offences.Add((Line(literal.Token), "number literal " + literal.Token.Text));
        }

        // Settings: no default-value expression in the record's or the parser's parameter lists or property initialisers.
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (type.Identifier.ValueText is not ("EngineSettings" or "SettingsParser")) continue;
            foreach (var parameter in type.DescendantNodes().OfType<ParameterSyntax>())
                if (parameter.Default is not null)
                    offences.Add((Line(parameter.Default), "settings default value"));
            foreach (var property in type.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                if (property.Initializer is not null)
                    offences.Add((Line(property.Initializer), "settings default value"));
        }

        return offences;
    }

    /// <summary>The classes allowed to hold named literals, each for its own kind, and the file each must be declared
    /// in (design section 10).</summary>
    private static readonly Dictionary<string, string> OwnerFiles = new(StringComparer.Ordinal)
    {
        ["EngineSettings"] = "src/SyncShark.Engine/EngineSettings.cs",
        ["SettingsParser"] = "src/SyncShark.Engine/SettingsParser.cs",
        ["DropboxListing"] = "src/SyncShark.Engine/Dropbox/DropboxListing.cs",
        ["StateSchema"] = "src/SyncShark.Engine/Storage/StateSchema.cs",
        ["DropboxProtocol"] = "src/SyncShark.Engine/Dropbox/DropboxProtocol.cs",
        ["DropboxEntries"] = DropboxEntriesFile,
        ["VolumeSpace"] = "src/SyncShark.Engine/Local/VolumeSpace.cs",
        ["DownloadSpace"] = "src/SyncShark.Engine/Local/DownloadSpace.cs",
        ["LocalBin"] = "src/SyncShark.Engine/Local/LocalBin.cs",
        ["LocalSide"] = "src/SyncShark.Engine/Local/LocalSide.cs",
        ["LocalListing"] = "src/SyncShark.Engine/Local/LocalListing.cs",
        ["LocalErrors"] = "src/SyncShark.Engine/Local/LocalErrors.cs",
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
        ["EngineCliCatalog"] = "src/SyncShark.Core/Engine/EngineCliCatalog.cs",
    };

    // Narrow exemptions follow each member's owner; other constants there remain offences.
    private static readonly Dictionary<string, IReadOnlySet<string>> MemberOwners = new(StringComparer.Ordinal)
    {
        ["EngineSettings"] = new HashSet<string>(StringComparer.Ordinal) { "AtomicReplaceDelayMultiplier" },
        ["SettingsParser"] = new HashSet<string>(StringComparer.Ordinal)
            { "MaximumAtomicReplaceAttempts", "MaximumAtomicReplaceInitialDelayMilliseconds" },
        ["DropboxListing"] = new HashSet<string>(StringComparer.Ordinal) { "SpellingQueued", "SpellingReleased" },
        ["EngineHost"] = new HashSet<string>(StringComparer.Ordinal)
            { "SingleHolder", "LatestStatusCapacity" },
        ["Monitor"] = new HashSet<string>(StringComparer.Ordinal) { "QuietSeconds" },
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

    private static bool IsAllowedPosition(LiteralExpressionSyntax literal)
    {
        var parent = literal.Parent;
        if (parent is BinaryExpressionSyntax binary)
            return binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression) ||
                binary.IsKind(SyntaxKind.LessThanExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
                binary.IsKind(SyntaxKind.GreaterThanExpression) ||
                binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ||
                binary.IsKind(SyntaxKind.AddExpression) || binary.IsKind(SyntaxKind.SubtractExpression);
        if (parent is AssignmentExpressionSyntax assignment)
            return assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                assignment.IsKind(SyntaxKind.SubtractAssignmentExpression);
        if (parent is RangeExpressionSyntax) return true;
        if (parent is ArgumentSyntax argument &&
            argument.Parent is BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax })
            return true;
        // A from-end index (`^1`) is a range bound whether it sits in a range (`[..^1]`) or alone (`[^1]`).
        return parent is PrefixUnaryExpressionSyntax unary &&
            unary.Parent is RangeExpressionSyntax or
                ArgumentSyntax { Parent: BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax } };
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parentheses)
            expression = parentheses.Expression;
        return expression;
    }

    // shortcut: only the observed built-in int default +/- literal forms; extend when the source inventory requires it.
    private static bool IsDefaultArithmetic(BinaryExpressionSyntax binary) =>
        (binary.IsKind(SyntaxKind.AddExpression) || binary.IsKind(SyntaxKind.SubtractExpression)) &&
        Unwrap(binary.Left) is DefaultExpressionSyntax { Type: PredefinedTypeSyntax predefined } &&
        predefined.Keyword.IsKind(SyntaxKind.IntKeyword) &&
        Unwrap(binary.Right) is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.NumericLiteralExpression) && literal.Token.Value is int or uint or long or ulong;

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

    private static int Line(SyntaxToken token) =>
        token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    internal static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "SyncShark.Engine")))
                return directory.FullName;
        throw new InvalidOperationException("literals: repository root not found from " + AppContext.BaseDirectory);
    }

    /// <summary>The engine's own source files, for the ownership fences that scan only the engine module.</summary>
    internal static IEnumerable<string> EngineFiles(string root)
    {
        var engine = Path.Combine(root, "src", "SyncShark.Engine");
        foreach (var file in Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories))
            if (!IsGenerated(file)) yield return file;
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string?>> TypeFiles = new(BuildTypeFiles);

    /// <summary>The repo-relative file declaring the exact engine type, or null when no single engine file declares
    /// it (a counterfeit or partial declaration elsewhere receives no ownership exemption).</summary>
    internal static string? DeclaredIn(string fullName) =>
        TypeFiles.Value.TryGetValue(fullName, out var file) ? file : null;

    private static IReadOnlyDictionary<string, string?> BuildTypeFiles() =>
        MapFiles(EngineFiles(FindRepoRoot()).Select(path => (
            Path.GetRelativePath(FindRepoRoot(), path).Replace('\\', '/'), File.ReadAllText(path))));

    /// <summary>Maps each top-level type to the one file declaring it; a second (partial) declaration makes the
    /// answer ambiguous (null), so it can never satisfy an ownership exemption.</summary>
    internal static IReadOnlyDictionary<string, string?> MapFiles(IEnumerable<(string Relative, string Source)> files)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (relative, source) in files)
        {
            var unit = (CompilationUnitSyntax)CSharpSyntaxTree
                .ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
            var ns = unit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            foreach (var type in unit.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (type.Parent is not (BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)) continue;
                var name = string.IsNullOrEmpty(ns) ? type.Identifier.ValueText : ns + "." + type.Identifier.ValueText;
                if (!map.TryGetValue(name, out var declared)) map[name] = relative;
                else if (declared != relative) map[name] = null;
            }
        }
        return map;
    }

    private static IEnumerable<string> ScannedFiles(string root)
    {
        var engine = Path.Combine(root, "src", "SyncShark.Engine");
        foreach (var file in Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories))
            if (!IsGenerated(file)) yield return file;
        var core = Path.Combine(root, "src", "SyncShark.Core", "Engine");
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
            yield return file;
        yield return Path.Combine(root, "src", "SyncShark.Core", "Cli", "CliCatalogResource.cs");
    }

    private static bool IsGenerated(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "obj" or "bin");
}
