using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>The bounded ownership fences of design section 7 (F02-F19, F22-F32, F34): a named building block may
/// appear only in its owner type, declared in its one file. Each rule runs the passing and failing fixture of its
/// table row, then the engine source; IL fences (F01/F07/F08/F12/F15/F16/F20/F21/F22/F31/F33) live in
/// <see cref="DependencyChecks"/>. Repair fences resolve path identity, unreadable reasons and scan ingestion
/// symbols. Retry-presentation fences keep the two Contracts values in their root files and keep rendering and text
/// selection in Edge only.</summary>
public static class OwnershipChecks
{
    private const string Engine = "src/SyncShark.Engine/";
    private const string PathNamesFile = Engine + "PathNames.cs";
    private const string StateSchemaFile = Engine + "Storage/StateSchema.cs";
    private const string StateEncodingFile = Engine + "Storage/StateEncoding.cs";
    private const string TimeRulesFile = Engine + "Planner/TimeRules.cs";
    private const string RunEventFile = Engine + "RunEvent.cs";
    private const string ResourceNamesFile = Engine + "ResourceNames.cs";
    private const string IssuePresentationFile = Engine + "Edge/IssuePresentation.cs";
    private const string ContractsNamespace = "Massdex.SyncShark.Engine";
    private const string EdgeNamespace = "Massdex.SyncShark.Engine.Edge";

    private static readonly Dictionary<string, string> RetryContractFiles = new(StringComparer.Ordinal)
    {
        ["IssueRetryView"] = Engine + "IssueRetryView.cs",
        ["RetryPresentationState"] = Engine + "RetryPresentationState.cs",
    };

    private sealed record Rule(string Id, Func<CompilationUnitSyntax, string, IEnumerable<(int Line, string What)>> Find);

    private static readonly Rule[] Rules =
    [
        new("imports", ImportAliases),
        new("neutral", RowContextConstruction),
        new("retryview", RetryContracts),
        new("presentation", PresentationOwnership),
        new("F02", IgnoreCaseTokens),
        new("F03", CorePathPrimitives),
        new("F04", Transferability),
        new("F05", Containment),
        new("F06", Separators),
        new("F09", SqlText),
        new("F10", CopiedDecode),
        new("F11", CopiedPacking),
        new("F13", TimePrimitives),
        new("F14", PlannerEffects),
        new("F17", CauseConstants),
        new("F18", ProtectedExclusion),
        new("F19", SegmentValidation),
        new("F22", MetadataFields),
        new("F23", ListingFields),
        new("F24", WriteEnvelope),
        new("F25", ErrorFields),
        new("F26", ShapeFailures),
        new("F27", LocalErrorSets),
        new("F28", EffectEvents),
        new("F29", IdOwnership),
        new("F30", EnumParsing),
        new("F32", EdgePairUnknown),
        new("F34", MarkerConcatenation),
        new("repair", RepairOwnership),
        new("record-bindings", RecordProducers),
    ];

    public static void Run()
    {
        Fixtures();
        var root = LiteralsChecks.FindRepoRoot();
        var offences = new List<string>();
        foreach (var file in LiteralsChecks.EngineFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var unit = Unit(File.ReadAllText(file));
            foreach (var rule in Rules)
                offences.AddRange(rule.Find(unit, relative).Select(offence =>
                    $"{relative}:{offence.Line} {rule.Id} {offence.What}"));
        }
        Check.Require(offences.Count == 0, "ownership: " + string.Join("; ", offences));
    }

    // One shared neutral context lives with RowContext; only the run projection may construct row facts.
    private static IEnumerable<(int Line, string What)> RowContextConstruction(CompilationUnitSyntax unit,
        string relative)
    {
        if (relative is Engine + "RowContext.cs" or Engine + "Executor/RowContexts.cs") yield break;
        foreach (var creation in unit.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var type = creation.Type.ToString();
            if (type is "RowContext" or "Massdex.SyncShark.Engine.RowContext")
                yield return (Line(creation), "row context construction");
        }
    }

    // The engine has no `using X = ...` or `using static`, so every fence names building blocks directly.
    private static IEnumerable<(int Line, string What)> ImportAliases(CompilationUnitSyntax unit, string relative)
    {
        foreach (var directive in unit.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is not null) yield return (Line(directive), "using alias");
            if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) yield return (Line(directive), "using static");
        }
    }

    // LOCAL-IN-USE D1 / C2-review correction 2: the two retry-presentation values are Engine Contracts at the root
    // namespace, each declared in its one file; a duplicate DTO in Run or Edge receives no exemption.
    private static IEnumerable<(int Line, string What)> RetryContracts(CompilationUnitSyntax unit, string relative)
    {
        foreach (var type in unit.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!RetryContractFiles.TryGetValue(type.Identifier.ValueText, out var file)) continue;
            if (relative == file && NamespaceOf(type) == ContractsNamespace) continue;
            yield return (Line(type), "retry presentation contract " + type.Identifier.ValueText);
        }
    }

    // LOCAL-IN-USE D1: IssuePresentation alone selects the guidance text. It is declared only in Edge/IssuePresentation.cs,
    // and no other module (notably Run) may reference it, so Run keeps no Edge dependency.
    private static IEnumerable<(int Line, string What)> PresentationOwnership(CompilationUnitSyntax unit, string relative)
    {
        if (Under(relative, "Edge"))
        {
            foreach (var type in unit.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                if (type.Identifier.ValueText == "IssuePresentation" &&
                    !(relative == IssuePresentationFile && NamespaceOf(type) == EdgeNamespace))
                    yield return (Line(type), "IssuePresentation outside its Edge owner file");
            yield break;
        }
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "IssuePresentation")
                yield return (Line(token), "Edge rendering outside Edge");
    }

    private static IEnumerable<(int Line, string What)> IgnoreCaseTokens(CompilationUnitSyntax unit, string relative) =>
        Names(unit, relative, PathNamesFile, "PathNames", "OrdinalIgnoreCase", "InvariantCultureIgnoreCase",
            "CurrentCultureIgnoreCase");

    private static IEnumerable<(int Line, string What)> CorePathPrimitives(CompilationUnitSyntax unit, string relative) =>
        Names(unit, relative, PathNamesFile, "PathNames", "PairPath", "PathKey", "DropboxNameMapping");

    private static IEnumerable<(int Line, string What)> Transferability(CompilationUnitSyntax unit, string relative) =>
        Names(unit, relative, PathNamesFile, "PathNames", "IsWindowsTransferPath");

    private static IEnumerable<(int Line, string What)> Containment(CompilationUnitSyntax unit, string relative) =>
        Names(unit, relative, Engine + "Local/LocalPaths.cs", "LocalPaths", "ResolveContainedPath");

    // F06: the owner type, exactly the DropboxKey root sentinel, and the one approved protocol constant.
    private static IEnumerable<(int Line, string What)> Separators(CompilationUnitSyntax unit, string relative)
    {
        foreach (var token in unit.DescendantTokens())
        {
            if (Owner(token, relative, PathNamesFile, "PathNames")) continue;
            if (token.IsKind(SyntaxKind.CharacterLiteralToken) && token.ValueText == "/" ||
                token.IsKind(SyntaxKind.StringLiteralToken) && token.ValueText == "/")
            {
                if (Owner(token, relative, Engine + "Dropbox/DropboxProtocol.cs", "DropboxProtocol") &&
                    IsConstField(token, "ErrorSummarySeparator"))
                    continue;
                yield return (Line(token), "separator literal " + token.Text);
            }
            else if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "PathSeparator" &&
                     !Owner(token, relative, PathNamesFile, "PathNames") &&
                     !(Owner(token, relative, Engine + "PathLocks.cs", "PathLocks") && MethodName(token) == "DropboxKey"))
            {
                yield return (Line(token), "separator constant");
            }
        }
    }

    // F09: SQL text belongs to the StateSchema type. Assembling SQL inside StateSchema is the SQL owner's own
    // business; the counted SQL budget covers its per-call cost, so this fence is a location rule only.
    private static readonly System.Text.RegularExpressions.Regex Sql = new(
        @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE)\s",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<(int Line, string What)> SqlText(CompilationUnitSyntax unit, string relative)
    {
        foreach (var token in unit.DescendantTokens())
        {
            if (!IsStringText(token) || !Sql.IsMatch(token.ValueText)) continue;
            if (Owner(token, relative, StateSchemaFile, "StateSchema")) continue;
            yield return (Line(token), "SQL text");
        }
    }

    // F10: persistence decoding rejects null only in StateEncoding; other JSON domains are out of scope.
    private static IEnumerable<(int Line, string What)> CopiedDecode(CompilationUnitSyntax unit, string relative)
    {
        if (!relative.StartsWith(Engine + "Storage/", StringComparison.Ordinal)) yield break;
        foreach (var call in unit.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (Owner(call, relative, StateEncodingFile, "StateEncoding") ||
                call.Expression is not MemberAccessExpressionSyntax access ||
                LastName(access.Expression) != "JsonSerializer" || access.Name.Identifier.ValueText != "Deserialize")
                continue;
            SyntaxNode node = call;
            while (node.Parent is ParenthesizedExpressionSyntax) node = node.Parent;
            if (node.Parent is BinaryExpressionSyntax coalesce && coalesce.IsKind(SyntaxKind.CoalesceExpression) &&
                coalesce.Left == node && coalesce.Right is ThrowExpressionSyntax ||
                node.Parent is ConditionalExpressionSyntax ternary &&
                (ternary.WhenTrue is ThrowExpressionSyntax || ternary.WhenFalse is ThrowExpressionSyntax))
                yield return (Line(call), "null-rejecting decode");
        }
    }

    // F11: low-level Local ID and hex packing stays in StateEncoding; code tokens, never comment text.
    private static IEnumerable<(int Line, string What)> CopiedPacking(CompilationUnitSyntax unit, string relative)
    {
        if (!relative.StartsWith(Engine + "Storage/", StringComparison.Ordinal)) yield break;
        foreach (var access in unit.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (Owner(access, relative, StateEncodingFile, "StateEncoding")) continue;
            if (LastName(access.Expression) == "Convert" && access.Name.Identifier.ValueText == "FromHexString")
                yield return (Line(access), "hex packing");
            if (access.Name.Identifier.ValueText == "TryParse" && access.Parent is InvocationExpressionSyntax call &&
                call.ArgumentList.Arguments.Any(argument => argument.Expression.DescendantTokens().Any(token =>
                    token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "NumberStyles")))
                yield return (Line(access), "numeric packing");
        }
    }

    private static IEnumerable<(int Line, string What)> TimePrimitives(CompilationUnitSyntax unit, string relative)
    {
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) &&
                token.ValueText is "ToUnixTimeSeconds" or "FileTimeEpochTicks" &&
                !Owner(token, relative, TimeRulesFile, "TimeRules"))
                yield return (Line(token), "low-level time conversion");
        foreach (var access in unit.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            if (access.Name.Identifier.ValueText is "Seconds" or "Unreliable" &&
                LastName(access.Expression) == "TimeRules" &&
                !Owner(access, relative, TimeRulesFile, "TimeRules"))
                yield return (Line(access), "low-level time primitive");
    }

    private static readonly string[] PlannerBanned = ["UtcNow", "TimeProvider", "ISide", "IStateStore", "IAdmission"];

    private static IEnumerable<(int Line, string What)> PlannerEffects(CompilationUnitSyntax unit, string relative)
    {
        if (!Under(relative, "Planner")) yield break;
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && Array.IndexOf(PlannerBanned, token.ValueText) >= 0)
                yield return (Line(token), "Planner " + token.ValueText);
    }

    private static readonly Dictionary<string, (string File, string Type)[]> CauseOwners = new(StringComparer.Ordinal)
    {
        ["TieEqualTimes"] =
        [
            (Engine + "Planner/Decide.cs", "Decide"), (Engine + "Planner/Resolutions.cs", "Resolutions"),
            (Engine + "TextKey.cs", "TextKey"),
        ],
        ["TieUnreliableTime"] =
        [
            (Engine + "Planner/Decide.cs", "Decide"), (Engine + "Planner/Resolutions.cs", "Resolutions"),
            (Engine + "TextKey.cs", "TextKey"),
        ],
        ["TieCaseBoth"] =
        [
            (Engine + "Planner/Renames.cs", "Renames"), (Engine + "Planner/Resolutions.cs", "Resolutions"),
            (Engine + "TextKey.cs", "TextKey"),
        ],
        ["TieKindBoth"] =
        [
            (Engine + "Planner/Decide.cs", "Decide"), (Engine + "Planner/Resolutions.cs", "Resolutions"),
            (Engine + "TextKey.cs", "TextKey"),
        ],
    };

    private static IEnumerable<(int Line, string What)> CauseConstants(CompilationUnitSyntax unit, string relative)
    {
        foreach (var token in unit.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) ||
                !CauseOwners.TryGetValue(token.ValueText, out var owners))
                continue;
            if (owners.Any(owner => Owner(token, relative, owner.File, owner.Type))) continue;
            yield return (Line(token), "cause " + token.ValueText);
        }
    }

    // F18: the protected pair is defined once; consumers use it, so only its definition and enum declaration are legal.
    private static IEnumerable<(int Line, string What)> ProtectedExclusion(CompilationUnitSyntax unit, string relative)
    {
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "FailExcludeHoldsProtected" &&
                !Owner(token, relative, Engine + "TextKey.cs", "TextKey") &&
                !Owner(token, relative, Engine + "IssuePolicy.cs", "IssuePolicy"))
                yield return (Line(token), "protected-exclusion cause");
    }

    private static IEnumerable<(int Line, string What)> SegmentValidation(CompilationUnitSyntax unit, string relative)
    {
        if (!Under(relative, "Planner")) yield break;
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) &&
                token.ValueText is "DirectorySeparatorChar" or "AltDirectorySeparatorChar")
                yield return (Line(token), "OS separator validation");
    }

    // F22: metadata fields stay in the Dropbox owner; the path-only Entry copy is checked in IL (property setters).
    private static IEnumerable<(int Line, string What)> MetadataFields(CompilationUnitSyntax unit, string relative)
    {
        if (!Under(relative, "Dropbox")) return [];
        return ProtocolFields(unit, relative, Engine + "Dropbox/DropboxEntries.cs", "DropboxEntries",
            "Size", "ClientModified", "Id", "Rev", "ContentHash", "IsDownloadable", "PathDisplay");
    }

    private static IEnumerable<(int Line, string What)> ListingFields(CompilationUnitSyntax unit, string relative) =>
        ProtocolFields(unit, relative, Engine + "Dropbox/DropboxListing.cs", "DropboxListing",
            "Entries", "Cursor", "HasMore");

    private static IEnumerable<(int Line, string What)> WriteEnvelope(CompilationUnitSyntax unit, string relative) =>
        ProtocolFields(unit, relative, Engine + "Dropbox/DropboxWriter.cs", "DropboxWriter", "Metadata");

    private static IEnumerable<(int Line, string What)> ErrorFields(CompilationUnitSyntax unit, string relative) =>
        ProtocolFields(unit, relative, Engine + "Dropbox/DropboxErrors.cs", "DropboxErrors",
            "ErrorSummary", "Error", "Reason");

    private static IEnumerable<(int Line, string What)> ProtocolFields(CompilationUnitSyntax unit, string relative,
        string ownerFile, string ownerType, params string[] fields)
    {
        foreach (var access in unit.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            if (LastName(access.Expression) == "DropboxProtocol" &&
                Array.IndexOf(fields, access.Name.Identifier.ValueText) >= 0 &&
                !Owner(access, relative, ownerFile, ownerType))
                yield return (Line(access), "protocol field " + access.Name.Identifier.ValueText);
    }

    private static IEnumerable<(int Line, string What)> ShapeFailures(CompilationUnitSyntax unit, string relative)
    {
        foreach (var pattern in unit.DescendantNodes().OfType<IsPatternExpressionSyntax>())
        {
            if (Owner(pattern, relative, Engine + "Dropbox/DropboxEntries.cs", "DropboxEntries")) continue;
            var types = PatternTypes(pattern.Pattern);
            if (types.Contains("KeyNotFoundException") && types.Contains("InvalidOperationException") &&
                types.Contains("FormatException"))
                yield return (Line(pattern), "copied shape-failure set");
        }
    }

    private static IEnumerable<(int Line, string What)> LocalErrorSets(CompilationUnitSyntax unit, string relative)
    {
        if (!Under(relative, "Local") && !Under(relative, "Run")) yield break;
        foreach (var pattern in unit.DescendantNodes().OfType<IsPatternExpressionSyntax>())
        {
            if (Owner(pattern, relative, Engine + "Local/LocalErrors.cs", "LocalErrors")) continue;
            var types = PatternTypes(pattern.Pattern);
            if (types.Contains("IOException") && types.Contains("UnauthorizedAccessException"))
                yield return (Line(pattern), "copied local exception set");
        }
    }

    // F28: the Effect factory and DropboxApi own their event kinds; other construction stays legal. The creation's type is
    // resolved first, and any argument may carry the kind (named and target-typed forms included).
    private static IEnumerable<(int Line, string What)> EffectEvents(CompilationUnitSyntax unit, string relative)
    {
        foreach (var creation in unit.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var effect = creation.ArgumentList?.Arguments.Any(argument => IsEventKind(argument.Expression, "Effect")) == true;
            var request = creation.ArgumentList?.Arguments.Any(argument => IsEventKind(argument.Expression, "ProviderRequest")) == true;
            var runEvent = creation switch
            {
                ObjectCreationExpressionSyntax { Type: not null } explicitCreation => LastName(explicitCreation.Type) == "RunEvent",
                // Only a governed event argument needs the semantic model.
                ImplicitObjectCreationExpressionSyntax => (effect || request) && InferredType(unit.SyntaxTree, creation) == "RunEvent",
                _ => false,
            };
            if (!runEvent) continue;
            if (effect && !Owner(creation, relative, RunEventFile, "RunEvent"))
                yield return (Line(creation), "direct Effect event construction");
            if (request && !Owner(creation, relative, Engine + "Dropbox/DropboxApi.cs", "DropboxApi"))
                yield return (Line(creation), "request observation outside DropboxApi");
        }
    }

    private static IEnumerable<(int Line, string What)> IdOwnership(CompilationUnitSyntax unit, string relative)
    {
        foreach (var access in unit.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            if (LastName(access.Expression) == "Guid" && access.Name.Identifier.ValueText == "NewGuid" &&
                !Owner(access, relative, ResourceNamesFile, "ResourceNames"))
                yield return (Line(access), "direct GUID id");
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.StringLiteralToken) && token.ValueText == "N" &&
                !Owner(token, relative, ResourceNamesFile, "ResourceNames"))
                yield return (Line(token), "id format literal");
        foreach (var field in unit.DescendantNodes().OfType<FieldDeclarationSyntax>())
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "IdLength") &&
                !Owner(field, relative, ResourceNamesFile, "ResourceNames"))
                yield return (Line(field), "redeclared id length");
    }

    private static IEnumerable<(int Line, string What)> EnumParsing(CompilationUnitSyntax unit, string relative)
    {
        if (relative != Engine + "SettingsParser.cs" && relative != Engine + "Text.cs") yield break;
        foreach (var access in unit.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            if (LastName(access.Expression) == "Enum" &&
                access.Name.Identifier.ValueText is "TryParse" or "IsDefined" or "Parse")
                yield return (Line(access), "rebuilt exact-enum validation");
    }

    private static IEnumerable<(int Line, string What)> EdgePairUnknown(CompilationUnitSyntax unit, string relative)
    {
        if (!Under(relative, "Edge")) yield break;
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "PairUnknown")
                yield return (Line(token), "Edge references TextKey.PairUnknown");
    }

    // F34: the case-marker concatenation belongs to PathNames; both the Planner parameter and the settings member.
    private static IEnumerable<(int Line, string What)> MarkerConcatenation(CompilationUnitSyntax unit, string relative)
    {
        foreach (var add in unit.DescendantNodes().OfType<BinaryExpressionSyntax>())
            if (add.IsKind(SyntaxKind.AddExpression) && !Owner(add, relative, PathNamesFile, "PathNames") &&
                add.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) &&
                    token.ValueText is "caseRenameSuffix" or "CaseRenameSuffix"))
                yield return (Line(add), "marker concatenation");
    }

    private static readonly HashSet<(string File, string Method)> PreparedRecordProducers =
    [
        (Engine + "Executor/ActionExecutor.cs", "ApplyAsync"),
        (Engine + "Executor/ActionExecutor.cs", "CopyAsync"),
        (Engine + "Executor/KeepBothRunner.cs", "RunAsync"),
        (Engine + "Executor/Parents.cs", "EnsureAsync"),
        (Engine + "Executor/RenameRunner.cs", "ApplyAsync")
    ];

    private static readonly Dictionary<(string File, string Method), string> ExemptRecordProducers = new()
    {
        [(Engine + "Executor/Recovery.cs", "ResolvePendingAsync")] =
            "Recovery publishes the newly allocated Local stage identity through protected pending settlement.",
        [(Engine + "Executor/ActionExecutor.cs", "CopyAsync.DownAsync")] =
            "Download publication supplies a newly allocated Local stage identity; its protected callback stays synchronous."
    };

    private static IEnumerable<(int Line, string What)> RecordProducers(CompilationUnitSyntax unit, string relative)
    {
        var assignments = unit.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(item => item.Left.ToString() == "Record").ToArray();
        var constructions = unit.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().ToArray();
        if (assignments.Length == 0 && constructions.Length == 0) yield break;
        var model = CSharpCompilation.Create("record-fence", [unit.SyntaxTree, .. GlobalUsings.Value], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                metadataImportOptions: MetadataImportOptions.All)).GetSemanticModel(unit.SyntaxTree, true);
        bool Prepares(SyntaxNode node) => node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call =>
        {
            var info = model.GetSymbolInfo(call);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            return symbol?.ContainingType?.ToDisplayString() == "Massdex.SyncShark.Engine.Executor.RecordBindings" &&
                symbol.Name == "PrepareAsync";
        });
        foreach (var construction in constructions)
        {
            var info = model.GetSymbolInfo(construction);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (relative != Engine + "StateChange.cs" &&
                symbol?.ContainingType?.ToDisplayString() == ContractsNamespace + ".StateChange")
                yield return (Line(construction), "StateChange producers must use Empty with named, inventoried Record assignment");
        }
        foreach (var assignment in assignments)
        {
            var info = model.GetSymbolInfo(assignment.Left);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol?.ContainingType?.ToDisplayString() != ContractsNamespace + ".StateChange") continue;
            var method = assignment.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var key = (relative, method?.Identifier.ValueText ?? "");
            if (ExemptRecordProducers.ContainsKey(key)) continue;
            if (method is null || !PreparedRecordProducers.Contains(key) || !Prepares(method))
            {
                yield return (Line(assignment), "Record producer missing indexed preparation or explicit exemption");
                continue;
            }
            if (key == (Engine + "Executor/ActionExecutor.cs", "CopyAsync"))
            {
                foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = (call.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
                    if (name == "DownAsync" && ExemptRecordProducers.ContainsKey((relative, "CopyAsync.DownAsync"))) continue;
                    if (name is not ("UpAsync" or "EnsureFolderAsync")) continue;
                    var block = call.Ancestors().OfType<BlockSyntax>().First();
                    if (!Prepares(block)) yield return (Line(call), "identity-preserving copy branch missing RecordBindings.PrepareAsync");
                }
            }
        }
    }

    // Resolve the building block, not its spelling: a shared-owner call is reuse, a same-named local is unrelated.
    private static IEnumerable<(int Line, string What)> RepairOwnership(CompilationUnitSyntax unit, string relative)
    {
        var names = unit.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name =>
            name.Identifier.ValueText is "SkipUnreadable" or "ScanUpsert" or "ScanDeleteSelf" or
                "ScanDeleteDescendants" or "ScanHasDescendants" or "Hash" or "PathCase" or "Of" or "Same" or "IsSelfOrUnder" or
                "IsUnder" or "SameLeafLocation" or "CoveredBy" or "Observed" or "Rebase" or
                "ToUpperInvariant" or "EqualsIgnoreCase").ToArray();
        if (names.Length == 0) yield break;
        // Internal owners must resolve too, or the fence silently fails open.
        var model = CSharpCompilation.Create("fence", [unit.SyntaxTree, .. GlobalUsings.Value], References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    metadataImportOptions: MetadataImportOptions.All))
            .GetSemanticModel(unit.SyntaxTree, ignoreAccessibility: true);
        foreach (var name in names)
        {
            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var type = symbol?.ContainingType?.ToDisplayString();
            var member = symbol?.Name;
            if (type == "Massdex.SyncShark.Engine.TextKey" && member == "SkipUnreadable")
            {
                // Only the full-walk producer and the predicate owner interpret this reason.
                if (Owner(name, relative, Engine + "Local/LocalListing.cs", "LocalListing") &&
                    MethodNameFor(name) == "Walk" ||
                    Owner(name, relative, Engine + "IssuePolicy.cs", "IssuePolicy") &&
                    MethodNameFor(name) is "ProtectsSubtree" or "ProtectedSkipCause")
                    continue;
                yield return (Line(name), "unreadable policy outside producer or IssuePolicy.ProtectsSubtree");
            }
            else if (type == "Massdex.SyncShark.Engine.Storage.StateSchema" &&
                     member is "ScanUpsert" or "ScanDeleteSelf" or "ScanDeleteDescendants" or "ScanHasDescendants")
            {
                // The delete statements belong to Apply; the #129 existence probe has its own owner member.
                var allowed = member == "ScanHasDescendants" ? "HasDescendants" : "Apply";
                if (Owner(name, relative, Engine + "Storage/ScanEntries.cs", "ScanEntries") &&
                    MethodNameFor(name) == allowed)
                    continue;
                yield return (Line(name), "scan ingestion outside ScanEntries." + allowed);
            }
            else if (type == "Massdex.SyncShark.Engine.PathNames" && member == "Hash")
            {
                if (Owner(name, relative, Engine + "FrozenPathComparer.cs", "FrozenPathComparer") &&
                    MethodNameFor(name) == "GetHashCode")
                    continue;
                yield return (Line(name), "path hash outside FrozenPathComparer.GetHashCode");
            }
            else if (type == "Massdex.SyncShark.Engine.ResourceNames" && member == "PathCase" ||
                     type == "System.Text.Ascii" && member == "EqualsIgnoreCase")
            {
                if (!Owner(name, relative, PathNamesFile, "PathNames"))
                    yield return (Line(name), "path folding outside PathNames");
            }
            else if (member == "ToUpperInvariant" &&
                     (symbol?.ContainingType?.SpecialType is SpecialType.System_String or SpecialType.System_Char ||
                      type == "System.Text.Rune") ||
                     type == "Massdex.SyncShark.V2.PathKey" && member == "Of" ||
                     type == "Massdex.SyncShark.V2.PairPath" && member is "Same" or "IsSelfOrUnder" or "IsUnder" or
                         "SameLeafLocation" or "CoveredBy" or "Observed" or "Rebase")
            {
                yield return (Line(name), "runtime path identity instead of frozen PathNames");
            }
        }
    }

    private static IEnumerable<(int Line, string What)> Names(CompilationUnitSyntax unit, string relative,
        string ownerFile, string ownerType, params string[] names)
    {
        foreach (var token in unit.DescendantTokens())
            if (token.IsKind(SyntaxKind.IdentifierToken) && Array.IndexOf(names, token.ValueText) >= 0 &&
                !Owner(token, relative, ownerFile, ownerType))
                yield return (Line(token), "uses " + token.ValueText);
    }

    // Owner = the exact top-level type AND the one file declaring it.
    private static bool Owner(SyntaxNode node, string relative, string ownerFile, string ownerType) =>
        relative == ownerFile && DeclaredTypeName(node) == ownerType;

    private static bool Owner(SyntaxToken token, string relative, string ownerFile, string ownerType) =>
        token.Parent is { } node && Owner(node, relative, ownerFile, ownerType);

    private static string? NamespaceOf(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private static string? DeclaredTypeName(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
            if (ancestor is BaseTypeDeclarationSyntax type &&
                type.Parent is (BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
                return type.Identifier.ValueText;
        return null;
    }

    private static bool IsEventKind(ExpressionSyntax expression, string kind) =>
        expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == kind &&
        LastName(access.Expression) == "RunEventKind";

    private static string? InferredType(SyntaxTree tree, ExpressionSyntax expression) =>
        CSharpCompilation.Create("fence", [tree, .. GlobalUsings.Value], References.Value)
            .GetSemanticModel(tree).GetTypeInfo(expression).Type?.Name;

    private static readonly Lazy<ImmutableArray<SyntaxTree>> GlobalUsings = new(LoadGlobalUsings);

    private static ImmutableArray<SyntaxTree> LoadGlobalUsings()
    {
        var root = LiteralsChecks.FindRepoRoot();
        var path = Directory.EnumerateFiles(Path.Combine(root, "src", "SyncShark.Engine", "obj"),
                "*GlobalUsings.g.cs", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal).FirstOrDefault();
        if (path is null)
            throw new InvalidOperationException("fence semantics: the Engine global-usings tree was not found");
        return [CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest))];
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "fence semantics: cannot load reference " + assembly.Location, error);
            }
        }
        return [.. references];
    }

    private static HashSet<string> PatternTypes(PatternSyntax pattern)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        Walk(pattern);
        return types;

        void Walk(PatternSyntax current)
        {
            switch (current)
            {
                case TypePatternSyntax type:
                    types.Add(LastName(type.Type));
                    break;
                case ConstantPatternSyntax constant:
                    types.Add(LastName(constant.Expression));
                    break;
                case RecursivePatternSyntax { Type: { } recursiveType }:
                    types.Add(LastName(recursiveType));
                    break;
                case BinaryPatternSyntax binary:
                    Walk(binary.Left);
                    Walk(binary.Right);
                    break;
                case ParenthesizedPatternSyntax parenthesized:
                    Walk(parenthesized.Pattern);
                    break;
                case UnaryPatternSyntax unary:
                    Walk(unary.Pattern);
                    break;
            }
        }
    }

    private static bool Under(string relative, string folder) =>
        relative.StartsWith(Engine + folder + "/", StringComparison.Ordinal);

    private static bool IsConstField(SyntaxToken token, string name) =>
        token.Parent?.AncestorsAndSelf().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)) is { } field &&
        field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name);

    private static bool IsStringText(SyntaxToken token) =>
        token.Kind().ToString().Contains("StringLiteral", StringComparison.Ordinal) ||
        token.IsKind(SyntaxKind.InterpolatedStringTextToken);

    private static string? MethodName(SyntaxToken token) => token.Parent is { } node ? MethodNameFor(node) : null;

    private static string? MethodNameFor(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case TypeDeclarationSyntax:
                    return null;
            }
        return null;
    }

    private static string LastName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        _ => "",
    };

    private static CompilationUnitSyntax Unit(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            .GetRoot();

    private static int Line(SyntaxToken token) =>
        token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static Rule RuleOf(string id) => Rules.First(rule => rule.Id == id);

    private sealed record Fixture(string Id, string File, string Source, string? Fails);

    private static readonly Fixture[] Cases =
    [
        new("imports", Engine + "Text.cs", "using System.Text.Json;", null),
        new("neutral", Engine + "RowContext.cs", "namespace Massdex.SyncShark.Engine; public sealed record RowContext { static readonly RowContext N = new RowContext(false, false, false, null, false, false, false, false); }", null),
        new("neutral", Engine + "Executor/RowContexts.cs", "namespace Massdex.SyncShark.Engine.Executor; class RowContexts { static RowContext M() => new RowContext(false, false, false, null, false, false, false, false); }", null),
        new("neutral", Engine + "Executor/SelectionPass.cs", "namespace Massdex.SyncShark.Engine.Executor; class C { static RowContext M() => new RowContext(false, false, false, null, false, false, false, false); }", "row context construction"),
        new("imports", Engine + "Text.cs", "using P = Massdex.SyncShark.Engine.Dropbox.DropboxProtocol;", "using alias"),
        new("imports", Engine + "Text.cs", "using static Massdex.SyncShark.Engine.Dropbox.DropboxProtocol;", "using static"),
        new("retryview", Engine + "IssueRetryView.cs", "namespace Massdex.SyncShark.Engine; public sealed record IssueRetryView(RetryPresentationState State);", null),
        new("retryview", Engine + "RetryPresentationState.cs", "namespace Massdex.SyncShark.Engine; public enum RetryPresentationState { Running }", null),
        new("retryview", Engine + "Run/EngineOperations.cs", "namespace Massdex.SyncShark.Engine.Run; static class EngineOperations { static IssueRetryView M() => new(RetryPresentationState.Unavailable); }", null),
        new("retryview", Engine + "Edge/Results/IssuesResult.cs", "namespace Massdex.SyncShark.Engine.Edge.Results; public sealed record IssueRetryView(RetryPresentationState State);", "retry presentation contract"),
        new("retryview", Engine + "IssueRetryView.cs", "namespace Massdex.SyncShark.Engine.Run; public sealed record IssueRetryView(RetryPresentationState State);", "retry presentation contract"),
        new("presentation", IssuePresentationFile, "namespace Massdex.SyncShark.Engine.Edge; static class IssuePresentation { static string Render() => \"\"; }", null),
        new("presentation", Engine + "Edge/Results/IssuesResult.cs", "namespace Massdex.SyncShark.Engine.Edge.Results; static class IssuesResult { static string CauseText() => IssuePresentation.Render(); }", null),
        new("presentation", Engine + "Edge/Other.cs", "namespace Massdex.SyncShark.Engine.Edge; static class IssuePresentation { }", "IssuePresentation outside its Edge owner file"),
        new("presentation", Engine + "Run/Repair.cs", "namespace Massdex.SyncShark.Engine.Run; static class Repair { static string M() => IssuePresentation.Render(); }", "Edge rendering outside Edge"),
        new("F02", Engine + "PrefixSet.cs", "static class PrefixSet { static readonly System.Collections.Generic.HashSet<string> Keys = new(PathNames.Comparer); }", null),
        new("F02", Engine + "Executor/Observer.cs", "static class Observer { static bool M(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase); }", "OrdinalIgnoreCase"),
        new("F03", PathNamesFile, "static class PathNames { static int Depth(string path) => PairPath.Depth(path); }", null),
        new("F03", Engine + "Storage/ScanReader.cs", "static class ScanReader { static int M(string path) => PairPath.Depth(path); }", "PairPath"),
        new("F03", PathNamesFile, "static class Other { static int M(string path) => PairPath.Depth(path); }", "PairPath"),
        new("F04", PathNamesFile, "static class PathNames { static bool M(string path) => PathRules.IsWindowsTransferPath(path); }", null),
        new("F04", Engine + "Dropbox/DropboxEntries.cs", "static class DropboxEntries { static bool M(string path) => PathRules.IsWindowsTransferPath(path); }", "IsWindowsTransferPath"),
        new("F05", Engine + "Local/LocalPaths.cs", "static class LocalPaths { static string M(string root, string rel) => PathRules.ResolveContainedPath(root, rel); }", null),
        new("F05", Engine + "PathLocks.cs", "static class PathLocks { static string M(string root, string rel) => PathRules.ResolveContainedPath(root, rel); }", "ResolveContainedPath"),
        new("F06", Engine + "PathLocks.cs", "static class PathLocks { static string DropboxKey(string root) { var fullPath = root; if (fullPath.Length == 0) fullPath = PathNames.PathSeparator; return fullPath; } }", null),
        new("F06", Engine + "Dropbox/DropboxProtocol.cs", "static class DropboxProtocol { public const char ErrorSummarySeparator = '/'; }", null),
        new("F06", Engine + "PathLocks.cs", "static class PathLocks { static int M(string key) => key.LastIndexOf(PathNames.PathSeparator); }", "separator constant"),
        new("F06", Engine + "PathLocks.cs", "static class Other { static int DropboxKey(string key) => key.LastIndexOf(PathNames.PathSeparator); }", "separator constant"),
        new("F06", Engine + "Executor/RowContexts.cs", "static class Caller { static string M(string a, string b) => a + PathNames.PathSeparator + b; }", "separator constant"),
        new("F06", Engine + "Dropbox/DropboxProtocol.cs", "static class DropboxProtocol { public const char OtherSeparator = '/'; }", "separator literal"),
        new("F09", StateSchemaFile, "static class StateSchema { public const string Select = \"SELECT 1 FROM pair;\"; }", null),
        new("F09", StateSchemaFile, "static class StateSchema { static string M(string value) => \"SELECT \" + value; }", null),
        new("F09", Engine + "Storage/IssueTable.cs", "static class IssueTable { private const string Select = \"select 1 from pair; \"; }", "SQL text"),
        new("F09", Engine + "Storage/IssueTable.cs", "static class IssueTable { private const string Select = \"\"\"\nselect 1\n\"\"\"; }", "SQL text"),
        new("F09", Engine + "Local/StateSchema.cs", "static class StateSchema { private const string Select = \"SELECT 1 FROM pair;\"; }", "SQL text"),
        new("F10", StateEncodingFile, "static class StateEncoding { static T M<T>(string json) where T : class => System.Text.Json.JsonSerializer.Deserialize<T>(json) ?? throw new System.IO.InvalidDataException(); }", null),
        new("F10", Engine + "SettingsParser.cs", "static class SettingsParser { static T M<T>(string json) where T : class => System.Text.Json.JsonSerializer.Deserialize<T>(json)!; }", null),
        new("F10", Engine + "Storage/IssueTable.cs", "static class IssueTable { static T M<T>(string json) where T : class => System.Text.Json.JsonSerializer.Deserialize<T>(json) ?? throw new System.IO.InvalidDataException(); }", "null-rejecting decode"),
        new("F11", StateEncodingFile, "static class StateEncoding { static object M(string value) => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : value; }", null),
        new("F11", Engine + "Storage/ScanWriter.cs", "static class ScanWriter { static object M(string value) => long.TryParse((/* NumberStyles */ value), out var id) ? id : value; }", null),
        new("F11", Engine + "Storage/ScanWriter.cs", "static class ScanWriter { static object M(string value) => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : value; }", "numeric packing"),
        new("F11", Engine + "Storage/ScanWriter.cs", "static class ScanWriter { static byte[] M(string value) => System.Convert.FromHexString(value); }", "hex packing"),
        new("F13", TimeRulesFile, "static class TimeRules { static long M(System.DateTimeOffset time) => time.ToUnixTimeSeconds(); }", null),
        new("F13", Engine + "Planner/Renames.cs", "static class Renames { static long M(System.TimeSpan span) => span.Seconds; }", null),
        new("F13", Engine + "Planner/Decide.cs", "static class Decide { static long M(System.DateTimeOffset time) => time.ToUnixTimeSeconds(); }", "low-level time conversion"),
        new("F13", Engine + "Planner/Renames.cs", "static class Renames { static bool M(System.DateTimeOffset a, System.DateTimeOffset b) => TimeRules.Unreliable(a, b); }", "low-level time primitive"),
        new("F14", Engine + "Planner/Decide.cs", "static class Decide { static object M(Entry entry, SyncedEntry? saved) => entry; }", null),
        new("F14", Engine + "Planner/Decide.cs", "static class Decide { static object M(System.TimeProvider time) => System.DateTimeOffset.UtcNow; }", "UtcNow"),
        new("F17", Engine + "Planner/Decide.cs", "static class Decide { static object M() => TextKey.TieEqualTimes; }", null),
        new("F17", Engine + "Planner/Resolutions.cs", "static class Resolutions { static object M() => TextKey.TieCaseBoth; }", null),
        new("F17", Engine + "Executor/ActionExecutor.cs", "static class ActionExecutor { static object M() => TextKey.TieEqualTimes; }", "cause TieEqualTimes"),
        new("F18", Engine + "IssuePolicy.cs", "static class IssuePolicy { static object M() => (IssueKind.Failure, TextKey.FailExcludeHoldsProtected); }", null),
        new("F18", Engine + "Planner/Exclusions.cs", "static class Exclusions { static object M() => TextKey.FailExcludeHoldsProtected; }", "protected-exclusion cause"),
        new("F18", Engine + "Storage/IssueTable.cs", "static class IssueTable { static object M() => TextKey.FailExcludeHoldsProtected; }", "protected-exclusion cause"),
        new("F19", Engine + "Planner/Resolutions.cs", "static class Resolutions { static bool M(string name) => PathNames.IsSingleSegment(name); }", null),
        new("F19", Engine + "Planner/Resolutions.cs", "static class Resolutions { static bool M(string name) => name.IndexOf(System.IO.Path.DirectorySeparatorChar) < 0; }", "OS separator validation"),
        new("F22", Engine + "Dropbox/DropboxEntries.cs", "static class DropboxEntries { static object M(System.Text.Json.JsonElement metadata) => metadata.GetProperty(DropboxProtocol.Size); }", null),
        new("F22", Engine + "Dropbox/DropboxListing.cs", "static class DropboxListing { static object M(System.Text.Json.JsonElement metadata) => metadata.GetProperty(DropboxProtocol.Size); }", "protocol field Size"),
        new("F22", Engine + "Planner/Decide.cs", "static class Decide { static object M(System.Text.Json.JsonElement metadata) => metadata.GetProperty(DropboxProtocol.Size); }", null),
        new("F23", Engine + "Dropbox/DropboxListing.cs", "static class DropboxListing { static object M(System.Text.Json.JsonElement root) => root.GetProperty(DropboxProtocol.Entries); }", null),
        new("F23", Engine + "Dropbox/DropboxWriter.cs", "static class DropboxWriter { static object M(System.Text.Json.JsonElement root) => root.GetProperty(DropboxProtocol.Entries); }", "protocol field Entries"),
        new("F24", Engine + "Dropbox/DropboxWriter.cs", "static class DropboxWriter { static object M(System.Text.Json.JsonElement root) => root.GetProperty(DropboxProtocol.Metadata); }", null),
        new("F24", Engine + "Dropbox/DropboxEntries.cs", "static class DropboxEntries { static bool M(System.Text.Json.JsonElement root) => root.TryGetProperty(DropboxProtocol.Metadata, out _); }", "protocol field Metadata"),
        new("F25", Engine + "Dropbox/DropboxErrors.cs", "static class DropboxErrors { static bool M(System.Text.Json.JsonElement root) => root.TryGetProperty(DropboxProtocol.ErrorSummary, out _); }", null),
        new("F25", Engine + "Dropbox/AdaptiveLimiter.cs", "static class AdaptiveLimiter { static bool M(System.Text.Json.JsonElement root) => root.TryGetProperty(DropboxProtocol.Error, out _); }", "protocol field Error"),
        new("F26", Engine + "Dropbox/DropboxListing.cs", "static class DropboxListing { static void M() { try { } catch (System.Exception e) when (DropboxEntries.IsShapeFailure(e)) { } } }", null),
        new("F26", Engine + "Dropbox/DropboxListing.cs", "static class DropboxListing { static void M() { try { } catch (System.FormatException) { } } }", null),
        new("F26", Engine + "Dropbox/DropboxWriter.cs", "static class DropboxWriter { static bool M(System.Exception error) => error is System.Collections.Generic.KeyNotFoundException or System.InvalidOperationException or System.FormatException; }", "copied shape-failure set"),
        new("F27", Engine + "Local/LocalSide.cs", "static class LocalSide { static void M() { try { } catch (System.Exception e) when (LocalErrors.IsFileFailure(e)) { } } }", null),
        new("F27", Engine + "Local/LocalListing.cs", "static class LocalListing { static void M() { try { } catch (System.ComponentModel.Win32Exception) { } } }", null),
        new("F27", Engine + "Local/LocalListing.cs", "static class LocalListing { static bool M(System.Exception error) => error is System.IO.IOException or System.UnauthorizedAccessException or System.ComponentModel.Win32Exception; }", "copied local exception set"),
        new("F28", Engine + "Executor/ActionExecutor.cs", "static class ActionExecutor { static void M(System.Action<RunEvent> events) => events(RunEvent.Effect(EffectKind.Downloaded, \"p\", \"\", Side.Local)); }", null),
        new("F28", RunEventFile, "static class RunEvent { static object M() => new RunEvent(RunEventKind.Stage, \"s\", \"\", \"d\", \"\"); }", null),
        new("F28", Engine + "Dropbox/DropboxApi.cs", "static class DropboxApi { static object M() => new RunEvent(RunEventKind.ProviderRequest, \"s\", \"\", \"\", \"\"); }", null),
        new("F28", Engine + "Executor/ScanPass.cs", "static class ScanPass { static object M() => new RunEvent(RunEventKind.ProviderRequest, \"s\", \"\", \"\", \"\"); }", "request observation outside DropboxApi"),
        new("F28", Engine + "Executor/Syncer.cs", "static class Syncer { static RunEvent M() => new RunEvent(RunEventKind.Effect, \"x\", \"p\", \"d\", \"o\"); }", "direct Effect event construction"),
        new("F28", Engine + "Executor/Syncer.cs", "using Massdex.SyncShark.Engine; static class Syncer { static RunEvent M() => new(RunEventKind.Effect, \"x\", \"p\", \"d\", \"o\"); }", "direct Effect event construction"),
        new("F28", Engine + "Executor/Syncer.cs", "static class Syncer { static RunEvent M() => new RunEvent(What: \"x\", Kind: RunEventKind.Effect, Path: \"p\", Detail: \"d\", Outcome: \"o\"); }", "direct Effect event construction"),
        new("F28", Engine + "Executor/Syncer.cs", "using Massdex.SyncShark.Engine; static class Syncer { static System.Tuple<RunEventKind> M() => new(RunEventKind.Effect); }", null),
        new("F28", Engine + "Executor/Syncer.cs", "using Massdex.SyncShark.Engine; static class Syncer { static void M(System.Action<RunEvent> events) => events(new(RunEventKind.Effect, \"x\", \"p\", \"d\", \"o\")); }", "direct Effect event construction"),
        new("F28", Engine + "Executor/Syncer.cs", "using Massdex.SyncShark.Engine; static class Syncer { static RunEvent Event { get; } = new(RunEventKind.Effect, \"x\", \"p\", \"d\", \"o\"); }", "direct Effect event construction"),
        new("F28", Engine + "Executor/Syncer.cs", "using Massdex.SyncShark.Engine; static class Syncer { static void M(Action<RunEvent> events) => events(new(RunEventKind.Effect, \"\", \"\", \"\", \"\")); }", "direct Effect event construction"),
        new("F29", Engine + "Local/StageNames.cs", "static class StageNames { static string M(string path) => path + ResourceNames.NewId(); }", null),
        new("F29", Engine + "Local/StageNames.cs", "static class StageNames { static string M() => System.Guid.NewGuid().ToString(\"N\"); }", "direct GUID id"),
        new("F29", Engine + "Local/StageNames.cs", "static class StageNames { private const int IdLength = 32; }", "redeclared id length"),
        new("F30", Engine + "SettingsParser.cs", "static class SettingsParser { static bool M<T>(string text, out T parsed) where T : struct, System.Enum => EnumNames.TryExact(text, out parsed); }", null),
        new("F30", Engine + "SettingsParser.cs", "static class SettingsParser { static bool M<T>(string text, out T parsed) where T : struct, System.Enum => System.Enum.TryParse(text, false, out parsed); }", "rebuilt exact-enum validation"),
        new("F30", Engine + "Text.cs", "static class Text { static bool M<T>(string text, out T parsed) where T : struct, System.Enum => System.Enum.IsDefined(text); }", "rebuilt exact-enum validation"),
        new("F32", Engine + "Edge/EngineCommands.cs", "static class EngineCommands { static object M(string pairId) => EngineOperations.Pair(pairId); }", null),
        new("F32", Engine + "Edge/EngineCommands.cs", "static class EngineCommands { static object M() => TextKey.PairUnknown; }", "PairUnknown"),
        new("F34", Engine + "Planner/Renames.cs", "static class Renames { static string M(string path, string caseRenameSuffix) => PathNames.CaseRenamePath(path, caseRenameSuffix); }", null),
        new("F34", Engine + "Planner/Renames.cs", "static class Renames { static string M(string path, string caseRenameSuffix) => path + caseRenameSuffix; }", "marker concatenation"),
        new("F34", Engine + "Dropbox/DropboxWriter.cs", "static class DropboxWriter { static string M(string path, EngineSettings settings) => path + settings.CaseRenameSuffix; }", "marker concatenation"),
        new("repair", PathNamesFile, "static class PathNames { static string M(string path) => path.ToUpperInvariant(); }", "runtime path identity"),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { static string M(string path) => path.ToUpperInvariant(); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class Other { static char M(char value) => char.ToUpperInvariant(value); }", "runtime path identity"),
        new("repair", Engine + "FrozenPathComparer.cs", "static class FrozenPathComparer { static System.Text.Rune M(System.Text.Rune value) => System.Text.Rune.ToUpperInvariant(value); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string a, string b) => System.Text.Ascii.EqualsIgnoreCase(a, b); }", null),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { static bool M(string a, string b) => System.Text.Ascii.EqualsIgnoreCase(a, b); }", "path folding"),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { static string ToUpperInvariant(string value) => value; static string M(string value) => ToUpperInvariant(value); }", null),
        new("repair", Engine + "Planner/Decide.cs", "using Massdex.SyncShark.Engine; static class Decide { static bool M(string a, string b) => PathNames.Same(a, b); }", null),
        new("repair", PathNamesFile, "static class PathNames { static string M(string path) => Massdex.SyncShark.V2.PathKey.Of(path); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string a, string b) => Massdex.SyncShark.V2.PairPath.Same(a, b); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string a, string b) => Massdex.SyncShark.V2.PairPath.IsSelfOrUnder(a, b); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string a, string b) => Massdex.SyncShark.V2.PairPath.IsUnder(a, b); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string a, string b) => Massdex.SyncShark.V2.PairPath.SameLeafLocation(a, b); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string path, string[] roots) => Massdex.SyncShark.V2.PairPath.CoveredBy(path, roots); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static bool M(string path, string[] roots) => Massdex.SyncShark.V2.PairPath.Observed(path, roots); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static string M(string path, string from, string to) => Massdex.SyncShark.V2.PairPath.Rebase(path, from, to); }", "runtime path identity"),
        new("repair", PathNamesFile, "static class PathNames { static string M() => Massdex.SyncShark.Engine.ResourceNames.PathCase; }", null),
        new("repair", Engine + "FrozenPathComparer.cs", "static class FrozenPathComparer { static string M() => Massdex.SyncShark.Engine.ResourceNames.PathCase; }", "path folding"),
        new("repair", Engine + "FrozenPathComparer.cs", "static class FrozenPathComparer { static int GetHashCode(string path) => Massdex.SyncShark.Engine.PathNames.Hash(path); }", null),
        new("repair", Engine + "FrozenPathComparer.cs", "static class FrozenPathComparer { static int Other(string path) => Massdex.SyncShark.Engine.PathNames.Hash(path); }", "path hash"),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { static int M(string path) => Massdex.SyncShark.Engine.PathNames.Hash(path); }", "path hash"),
        new("repair", PathNamesFile, "static class PathNames { static int M(string path) => Massdex.SyncShark.Engine.PathNames.Hash(path); }", "path hash"),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { static string M() => nameof(Massdex.SyncShark.Engine.PathNames.Hash); }", "path hash"),
        new("repair", Engine + "Local/LocalListing.cs", "using Massdex.SyncShark.Engine; static class LocalListing { static string Walk() => TextKey.SkipUnreadable.ToString(); }", null),
        new("repair", Engine + "IssuePolicy.cs", "using Massdex.SyncShark.Engine; static class IssuePolicy { static bool ProtectsSubtree(string skip) => skip == TextKey.SkipUnreadable.ToString(); }", null),
        new("repair", Engine + "Run/Repair.cs", "using Massdex.SyncShark.Engine; static class Repair { static bool M(string skip) => skip == TextKey.SkipUnreadable.ToString(); }", "unreadable policy"),
        new("repair", Engine + "Local/LocalListing.cs", "using Massdex.SyncShark.Engine; static class LocalListing { static string One() => TextKey.SkipUnreadable.ToString(); }", "unreadable policy"),
        new("repair", Engine + "IssuePolicy.cs", "using Massdex.SyncShark.Engine; static class Other { static bool ProtectsSubtree(string skip) => skip == TextKey.SkipUnreadable.ToString(); }", "unreadable policy"),
        new("repair", Engine + "Planner/Decide.cs", "static class Decide { enum Other { SkipUnreadable } static object M() => Other.SkipUnreadable; }", null),
        new("repair", Engine + "Storage/ScanEntries.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanEntries { static string Apply() => StateSchema.ScanUpsert + StateSchema.ScanDeleteSelf + StateSchema.ScanDeleteDescendants; }", null),
        new("repair", Engine + "Storage/ScanEntries.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanEntries { static string HasDescendants() => StateSchema.ScanHasDescendants; }", null),
        new("repair", Engine + "Storage/ScanEntries.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanEntries { static string Apply() => StateSchema.ScanHasDescendants; }", "scan ingestion"),
        new("repair", Engine + "Storage/ScanWriter.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanWriter { static string HasDescendants() => StateSchema.ScanHasDescendants; }", "scan ingestion"),
        new("repair", Engine + "Storage/ScanWriter.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanWriter { static string Apply() => StateSchema.ScanUpsert; }", "scan ingestion"),
        new("repair", Engine + "Storage/StoredFolderPlan.cs", "using Massdex.SyncShark.Engine.Storage; static class StoredFolderPlan { static string CaptureAsync() => StateSchema.ScanDeleteSelf; }", "scan ingestion"),
        new("repair", Engine + "Storage/StoredFolderPlan.cs", "using Massdex.SyncShark.Engine.Storage; static class StoredFolderPlan { static string CaptureAsync() => StateSchema.ScanDeleteDescendants; }", "scan ingestion"),
        new("repair", Engine + "Storage/ScanEntries.cs", "using Massdex.SyncShark.Engine.Storage; static class ScanEntries { static string Other() => StateSchema.ScanUpsert; }", "scan ingestion"),
        new("repair", Engine + "Storage/ScanEntries.cs", "using Massdex.SyncShark.Engine.Storage; static class Other { static string Apply() => StateSchema.ScanUpsert; }", "scan ingestion"),
        new("repair", StateSchemaFile, "static class StateSchema { static string M() => Massdex.SyncShark.Engine.Storage.StateSchema.ScanUpsert; }", "scan ingestion"),
        new("repair", Engine + "Storage/ScanWriter.cs", "static class ScanWriter { static class Other { public const string ScanUpsert = \"\"; } static string M() => Other.ScanUpsert; }", null),
    ];

    private static void Fixtures()
    {
        const string prepared = "using Massdex.SyncShark.Engine; using Massdex.SyncShark.Engine.Executor; " +
            "class Parents { async Task<StateChange> EnsureAsync(ExecutorContext c, SyncedEntry e) { " +
            "var change = StateChange.Empty with { Record = [e] }; return await RecordBindings.PrepareAsync(c, change, default); } }";
        Check.Require(!RecordProducers(Unit(prepared), Engine + "Executor/Parents.cs").Any(),
            "Record producer fixture accepts shared preparation.");
        Check.Require(RecordProducers(Unit(prepared.Replace("return await RecordBindings.PrepareAsync(c, change, default)",
            "return change")), Engine + "Executor/Parents.cs").Any(),
            "Record producer fixture rejects missing preparation.");
        Check.Require(RecordProducers(Unit(prepared), Engine + "Executor/Unknown.cs").Any(),
            "Record producer fixture rejects an unlisted producer even with preparation.");
        Check.Require(!RecordProducers(Unit("using Massdex.SyncShark.Engine; class Recovery { " +
            "StateChange ResolvePendingAsync(SyncedEntry e) => StateChange.Empty with { Record = [e] }; }"),
            Engine + "Executor/Recovery.cs").Any(), "Record producer fixture accepts documented stage publication exemption.");
        foreach (var expression in new[] { "new StateChange([e], [], [], [], [], [], [])", "new([e], [], [], [], [], [], [])" })
            Check.Require(RecordProducers(Unit("using Massdex.SyncShark.Engine; class Producer { " +
                "StateChange Create(SyncedEntry e) => " + expression + "; }"), Engine + "Executor/Unknown.cs").Any(),
                "Positional and target-typed constructors cannot bypass the Record producer inventory.");
        foreach (var fixture in Cases)
        {
            var found = RuleOf(fixture.Id).Find(Unit(fixture.Source), fixture.File).ToList();
            var matches = fixture.Fails is { } fragment
                ? found.Any(offence => offence.What.Contains(fragment, StringComparison.Ordinal))
                : found.Count == 0;
            Check.Require(matches,
                $"{fixture.Id} fixture ({fixture.File}, {(fixture.Fails is null ? "pass" : "fail " + fixture.Fails)}): "
                + $"{fixture.Source}; got: {string.Join(" | ", found.Select(offence => offence.What))}");
        }
    }
}
