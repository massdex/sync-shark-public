using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.Storage;
using Emit = System.Reflection.Emit;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Checks engine dependency boundaries (`--engine-checks dependencies`).</summary>
public static class DependencyChecks
{
    private const string RootNamespace = "Massdex.SyncShark.Engine";
    private const string RootModule = "root";
    private const string Storage = "Storage";
    private const string Local = "Local";
    private const string Dropbox = "Dropbox";
    private const string Planner = "Planner";
    private const string Executor = "Executor";
    private const string RunModule = "Run";
    private const string Edge = "Edge";

    private static readonly string[] Modules = [Storage, Local, Dropbox, Planner, Executor, RunModule, Edge];

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [RootModule] = [],
        [Storage] = [RootModule],
        [Local] = [RootModule],
        [Dropbox] = [RootModule],
        [Planner] = [RootModule],
        [Executor] = [RootModule, Planner],
        [RunModule] = [RootModule, Storage, Local, Dropbox, Planner, Executor],
        [Edge] = [RootModule, RunModule],
    };

    private static readonly TypeId FileType = new("System.IO", "File");
    private static readonly TypeId DirectoryType = new("System.IO", "Directory");
    private static readonly TypeId FileStreamType = new("System.IO", "FileStream");
    private static readonly TypeId StreamReaderType = new("System.IO", "StreamReader");
    private static readonly TypeId StreamWriterType = new("System.IO", "StreamWriter");
    private static readonly TypeId RandomAccessType = new("System.IO", "RandomAccess");
    private static readonly TypeId FileSystemInfoType = new("System.IO", "FileSystemInfo");
    private static readonly TypeId SafeFileHandleType = new("Microsoft.Win32.SafeHandles", "SafeFileHandle");
    private static readonly TypeId WindowsListingType = new("Massdex.SyncShark", "WindowsDirectoryListing");
    private static readonly TypeId UsnJournalType = new("Massdex.SyncShark", "NtfsUsnJournal");
    private static readonly TypeId HttpClientType = new("System.Net.Http", "HttpClient");
    private static readonly TypeId HttpInvokerType = new("System.Net.Http", "HttpMessageInvoker");
    private static readonly TypeId FileInfoType = new("System.IO", "FileInfo");
    private static readonly TypeId DirectoryInfoType = new("System.IO", "DirectoryInfo");
    private static readonly TypeId ResponseBodyType = new("System.Net.Http", "HttpResponseMessage");
    private static readonly TypeId HttpContentType = new("System.Net.Http", "HttpContent");
    private static readonly TypeId JsonDocumentType = new("System.Text.Json", "JsonDocument");
    private static readonly TypeId PathType = new("System.IO", "Path");
    private static readonly TypeId StringType = new("System", "String");
    private const string SqliteNamespace = "Microsoft.Data.Sqlite";

    // A StateSchema statement is the SQL-text constant, not the binding name or path constants.
    private static readonly HashSet<string> StateSchemaStatements = typeof(StateSchema)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Where(value => value.Contains(' '))
        .ToHashSet(StringComparer.Ordinal);

    // Connection setup attaches fixed schema names and sets pragmas on scan_state only, so it writes no durable row.
    private static readonly HashSet<string> PlanStatements = new(
        QueryPlanChecks.PlanStatementInventory.Select(statement => statement.Sql), StringComparer.Ordinal)
    {
        StateSchema.AttachState,
        StateSchema.AttachScan,
        StateSchema.PlanPragmas,
    };

    // Ownership fences: the exact owner type and the one file declaring it, plus the records whose
    // construction/clone they restrict. Missing or ambiguous attribution never grants an exemption.
    private const string Engine = "src/SyncShark.Engine/";
    private const string DropboxFolder = Engine + "Dropbox/";
    private const string DropboxApiFile = DropboxFolder + "DropboxApi.cs";
    private sealed record Owner(TypeId Type, string File, string[]? Methods = null);
    private static readonly TypeId SyncActionType = new(RootNamespace + "." + Planner, "SyncAction");
    private static readonly TypeId RenameDecisionType = new(RootNamespace + "." + Planner, "RenameDecision");
    private static readonly TypeId IssueCountType = new(RootNamespace + ".Edge.Results", "IssueCount");
    private static readonly TypeId EntryType = new(RootNamespace, "Entry");
    private static readonly TypeId IssueItemType = new(RootNamespace, "IssueItem");
    private static readonly TypeId IStateStoreType = new(RootNamespace, "IStateStore");
    private static readonly TypeId StateStoreType = new(RootNamespace + "." + Storage, "StateStore");
    private static readonly TypeId HashSetType = new("System.Collections.Generic", "HashSet`1");
    private static readonly TypeId EnumerableType = new("System.Linq", "Enumerable");

    private static Owner At(string type, string file, params string[] methods) =>
        new(new TypeId(RootNamespace, type), Engine + file, methods.Length == 0 ? null : methods);
    private static Owner In(string module, string type, string file, params string[] methods) =>
        new(new TypeId(RootNamespace + "." + module, type), Engine + file, methods.Length == 0 ? null : methods);

    private static readonly Owner[] SyncActionCtorOwners =
    [
        In(Planner, "Decide", "Planner/Decide.cs"), In(Planner, "Exclusions", "Planner/Exclusions.cs"),
        In(Planner, "FolderDeletes", "Planner/FolderDeletes.cs"),
        In(Planner, "Resolutions", "Planner/Resolutions.cs"),
    ];

    private static readonly Owner[] SyncActionCloneOwners =
        [In(Planner, "Decide", "Planner/Decide.cs"), In(Planner, "Exclusions", "Planner/Exclusions.cs"),
            In(Planner, "Resolutions", "Planner/Resolutions.cs")];

    private static readonly Owner[] RenameDecisionCtorOwners =
        [In(Planner, "Renames", "Planner/Renames.cs"),
            In(Executor, "RenameRunner", "Executor/RenameRunner.cs", "RunAsync", "RunCurrentAsync", "Failure")];

    private static readonly Owner[] RenameDecisionCloneOwners = [In(Planner, "Renames", "Planner/Renames.cs")];
    private static readonly Owner[] IssueCountOwners = [In("Edge.Results", "IssuesResult", "Edge/Results/IssuesResult.cs")];
    private static readonly Owner[] EntryCtorOwners =
    [
        In(Dropbox, "DropboxEntries", "Dropbox/DropboxEntries.cs"),
        // The sealed page spool reconstructs the same record on replay; it never invents metadata.
        In(Dropbox, "DropboxPage", "Dropbox/DropboxPage.cs"),
    ];
    // Only the metadata decoder may update an Entry's fields; the spool and listing only construct or respell.
    private static readonly Owner[] EntrySetterOwners =
        [In(Dropbox, "DropboxEntries", "Dropbox/DropboxEntries.cs")];
    private static readonly Owner[] EntryCloneOwners =
        [In(Dropbox, "DropboxEntries", "Dropbox/DropboxEntries.cs"),
            In(Dropbox, "DropboxListing", "Dropbox/DropboxListing.cs")];
    private static readonly Owner[] SubjectOwners = [At("IssueSubject", "IssueSubject.cs")];
    private static readonly Owner[] InScanOwners = [In(Executor, "FolderPlanBuilder", "Executor/FolderPlanBuilder.cs")];
    private static readonly Owner DropboxApiOwner = In(Dropbox, "DropboxApi", "Dropbox/DropboxApi.cs");
    private static readonly Owner StateStoreFileOwner = In(Storage, "StateStore", "Storage/StateStore.cs", "OpenStore");
    // Scratch files belong to these lifetime boundaries, not to the Storage module generally.
    private static readonly Owner[] ScanExistsOwners =
        [In(Storage, "StateStore", "Storage/StateStore.cs", "AttachScan", "OpenScanRead")];
    private static readonly Owner[] ScratchDeleteOwners =
    [
        In(Storage, "StateStore", "Storage/StateStore.cs", "OpenFolderPlan", "ResetScan"),
        In(Storage, "StoredFolderPlan", "Storage/StoredFolderPlan.cs", "Dispose"),
    ];
    private static readonly Owner NativeShellOwner =
        In(Local, "LocalBin", "Local/LocalBin.cs", "SHCreateItemFromParsingName");
    // P2 section 7: parent native probes belong to VolumeSpace alone, never to Local generally.
    private static readonly Owner NativeSpaceOwner =
        In(Local, "VolumeSpace", "Local/VolumeSpace.cs", "GetDiskFreeSpaceExW");
    // Each native import is legal only for its exact owner operation, library and entry point.
    private sealed record NativeOwner(Owner Owner, string Library, string EntryPoint);
    private static readonly NativeOwner[] NativeOwners =
    [
        new(NativeShellOwner, "shell32.dll", "SHCreateItemFromParsingName"),
        new(NativeSpaceOwner, "kernel32.dll", "GetDiskFreeSpaceExW"),
    ];
    // P2 section 7: the exclusive plan-scratch create belongs to StateStore.OpenFolderPlan, not to Storage generally.
    private static readonly Owner[] ScratchCreateOwners =
        [In(Storage, "StateStore", "Storage/StateStore.cs", "OpenFolderPlan")];
    // READING 8.3 decoder owners: the adapter's own selected-field and typed response parse sites.
    private static readonly Owner[] ReplyDecoderOwners =
    [
        DropboxApiOwner,
        In(Dropbox, "DropboxEntries", "Dropbox/DropboxEntries.cs"),
        In(Dropbox, "DropboxReply", "Dropbox/DropboxReply.cs"),
    ];
    private static readonly Owner DropboxListingOwner = In(Dropbox, "DropboxListing", "Dropbox/DropboxListing.cs");
    private static readonly string[] BodyReaders =
        ["ReadAsStringAsync", "ReadAsStreamAsync", "ReadAsByteArrayAsync", "ReadAsStream"];
    private static readonly string[] JsonParsers = ["Parse", "ParseAsync"];

    /// <summary>The parsed-source declaration file of an engine type; fixtures substitute their own map.</summary>
    private static Func<string, string?> FileOf { get; set; } = LiteralsChecks.DeclaredIn;

    private static readonly Dictionary<int, Emit.OpCode> OpCodesByValue =
        typeof(Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Emit.OpCode))
            .Select(field => (Emit.OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => opcode.Size == 1 ? (int)(byte)opcode.Value : (int)(ushort)opcode.Value);

    public static void Run()
    {
        SyncerSurface();
        var assembly = typeof(Massdex.SyncShark.Engine.Text).Assembly;
        using var stream = System.IO.File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeDefinitions)
        {
            var source = TypeOf(reader, handle);
            var module = ModuleOf(source.Namespace);
            if (module is null) continue;

            var uses = Collect(reader, pe, reader.GetTypeDefinition(handle));
            foreach (var message in Messages(uses, source, module))
                Fail(message);
            if (module == Storage && TopLevelName(source) is "StoredFolderPlan" or "StateStore")
                foreach (var message in PlanSqlCallSites(uses, Show(source),
                             TopLevelName(source) == "StateStore" ? "OpenFolderPlan" : null))
                    Fail(message);
        }

        Fixtures();
    }

    private static void SyncerSurface()
    {
        var methods = typeof(Syncer).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(method => !method.IsPrivate).ToArray();
        string[] expected = ["SyncScannedAsync", "SyncWorkAsync", "FailItem", "ReconcileReceiptsAsync",
            "SettleOutstandingAsync", "RecoverAsync", "ResolveAsync", "PreviewExclusionAsync"];
        Check.Require(methods.Select(method => method.Name).Order().SequenceEqual(expected.Order()),
            "A1: Syncer exposes exactly its eight workflow entries.");
        Check.Require(methods.Single(method => method.Name == "FailItem").ReturnType == typeof(void),
            "C1: native item failure reporting is synchronous and exposes no task to block on.");
        Check.Require(methods.All(method => !Forbidden(method.ReturnType) &&
            method.GetParameters().All(parameter => !Forbidden(parameter.ParameterType))),
            "A1: Syncer exposes no row facts, action, prefix set, admitted execution or delegate.");

        static bool Forbidden(Type type) => type == typeof(Row) || type == typeof(RowContext) ||
            type == typeof(Massdex.SyncShark.Engine.Planner.SyncAction) || type == typeof(PrefixSet) ||
            type == typeof(IAdmittedExecution) || typeof(Delegate).IsAssignableFrom(type) ||
            type.HasElementType && Forbidden(type.GetElementType()!) ||
            type.IsGenericType && type.GetGenericArguments().Any(Forbidden);
    }

    private static IEnumerable<string> Messages(Uses uses, TypeId source, string module) =>
        CheckEdges(uses, source, module)
            .Concat(CheckSqlite(uses, source, module))
            .Concat(CheckTransport(uses, source))
            .Concat(CheckFileSystem(uses, source, module))
            .Concat(CheckConstruction(uses, source))
            .Concat(CheckRowFacts(uses, source))
            .Concat(CheckLocalEvidence(uses, source))
            .Concat(CheckNative(uses, source))
            .Concat(CheckEntrySetters(uses, source))
            .Concat(CheckSubjectSets(uses, source))
            .Concat(CheckInScan(uses, source))
            .Concat(CheckStartupAcquisition(uses, source));

    private static IEnumerable<string> CheckStartupAcquisition(Uses uses, TypeId source)
    {
        var compose = In("Run", "EngineHost", "Run/EngineHost.cs", "Compose");
        var reads = In("Run", "EngineOperations", "Run/EngineOperations.cs", "Read", "ExistingPair");
        var direct = In("Run", "EngineOperations", "Run/EngineOperations.cs", "WithHostAsync");
        var hostedStart = In("Run", "EngineHost", "Run/EngineHost.cs", "StartCoreAsync");
        // IPC-wait progress owns no product root, authority or store; it is not an Engine acquisition.
        var clientProgress = In("Edge", "EngineHostClient", "Edge/EngineHostClient.cs", "TryExecuteResultAsync");
        var monitor = new TypeId(RootNamespace + ".Run", "Monitor");
        var monitorConstructor = In("Run", "Monitor", "Run/Monitor.cs", ".ctor");
        foreach (var (method, parent, member) in uses.Calls)
        {
            if (parent == StateStoreType && member is "Open" or "OpenReadOnly" &&
                !OwnerFor(source, method, compose) && !(member == "OpenReadOnly" && OwnerFor(source, method, reads)))
                yield return $"startup: {Show(source)}.{method} opens current state outside admitted acquisition owners.";
            // Constructor chaining initializes the same Monitor; it does not acquire another lifetime.
            if (parent == monitor && member is ".ctor" or "Start" &&
                !OwnerFor(source, method, compose, direct, clientProgress) &&
                !(member == "Start" && OwnerFor(source, method, hostedStart)) &&
                !(member == ".ctor" && source == monitor && OwnerFor(source, method, monitorConstructor)))
                yield return $"startup: {Show(source)}.{method} starts Monitor outside its exact lifetime owner.";
        }
    }

    /// <summary>F20/F21: only DropboxApi sends requests or reads response bodies; only the adapter's decoder owners parse one.</summary>
    private static IEnumerable<string> CheckTransport(Uses uses, TypeId source)
    {
        if (!OwnedBy(source, DropboxApiOwner))
            foreach (var (parent, member) in uses.Members)
                if (parent == HttpClientType && (member.StartsWith("Send", StringComparison.Ordinal) ||
                        member.EndsWith("Async", StringComparison.Ordinal)) ||
                    parent == HttpInvokerType && member.StartsWith("Send", StringComparison.Ordinal))
                    yield return $"{parent.Name}.{member} outside DropboxApi: {Show(source)}.";
        if (!InDropbox(source) || OwnedBy(source, DropboxApiOwner)) yield break;
        foreach (var (parent, member) in uses.Members)
        {
            if (parent == ResponseBodyType && member == "get_Content")
                yield return $"response content outside DropboxApi: {Show(source)}.";
            else if (parent == HttpContentType && Array.IndexOf(BodyReaders, member) >= 0)
                yield return $"response body read outside DropboxApi: {Show(source)}.";
            else if (parent == JsonDocumentType && Array.IndexOf(JsonParsers, member) >= 0 &&
                !OwnedBy(source, ReplyDecoderOwners))
                yield return $"response parse outside its Dropbox decoder owner: {Show(source)}.";
        }
    }

    /// <summary>F15/F16/F33/F22: the policy and metadata records are constructed and copied only by their owners.</summary>
    private static IEnumerable<string> CheckConstruction(Uses uses, TypeId source) =>
        Construction(uses, source, SyncActionType, SyncActionCtorOwners, SyncActionCloneOwners)
            .Concat(Construction(uses, source, RenameDecisionType, RenameDecisionCtorOwners, RenameDecisionCloneOwners))
            .Concat(Construction(uses, source, IssueCountType, IssueCountOwners, IssueCountOwners))
            .Concat(Construction(uses, source, EntryType, EntryCtorOwners, EntryCloneOwners, dropboxOnly: true));

    private static IEnumerable<string> CheckRowFacts(Uses uses, TypeId source)
    {
        var rowContext = new TypeId(RootNamespace, nameof(RowContext));
        if (source == rowContext || OwnedBy(source, In(Executor, "RowContexts", "Executor/RowContexts.cs"))) yield break;
        if (uses.Calls.Any(call => call.Parent == rowContext && call.Member == ".ctor"))
            yield return "A2: row context construction outside its owner.";
    }

    private static IEnumerable<string> CheckLocalEvidence(Uses uses, TypeId source)
    {
        var evidence = new TypeId(RootNamespace + "." + Executor, "LocalEvidence");
        foreach (var (method, parent, member) in uses.Calls)
        {
            if (parent == EntryType && member == "set_ContentHash" &&
                ModuleOf(source.Namespace) is RootModule or Local or Executor &&
                !OwnerFor(source, method, At("RemovalResult", "RemovalResult.cs", "CompleteObservation")))
                yield return "B9: complete Local observations are constructed only by RemovalResult.CompleteObservation.";
            if (parent != evidence) continue;
            if (member == "Rebind" && !OwnerFor(source, method,
                    In(Executor, "LocalEvidence", "Executor/LocalEvidence.cs", "Adopt")) ||
                member == "Adopt" && !OwnerFor(source, method,
                    In(Executor, "ItemPreparation", "Executor/ItemPreparation.cs", "RefreshAsync")) ||
                member == "Transfer" && !OwnerFor(source, method,
                    In(Executor, "LocalEvidence", "Executor/LocalEvidence.cs", "Adopt"),
                    In(Executor, "SelectionAdmission", "Executor/SelectionAdmission.cs", "RunAsync")))
                yield return "B9: observation adoption and proof transfer must use their exact lifetime owners.";
        }
    }

    private static IEnumerable<string> Construction(Uses uses, TypeId source, TypeId created, Owner[] ctorOwners,
        Owner[] cloneOwners, bool dropboxOnly = false)
    {
        // The record's own generated <Clone>$ calls its copy constructor; that is not a foreign construction.
        if (source == created || dropboxOnly && !InDropbox(source)) yield break;
        foreach (var (method, parent, member) in uses.Calls)
        {
            if (parent != created) continue;
            if (member == ".ctor" && !OwnerFor(source, method, ctorOwners))
                yield return $"construction: {Show(source)} constructs {Show(created)} outside its owners.";
            else if (member == "<Clone>$" && !OwnedBy(source, cloneOwners))
                yield return $"construction: {Show(source)} copies {Show(created)} outside its owners.";
        }
    }

    /// <summary>F07: the database-existence check is the sole FileInfo exception outside Local.</summary>
    private static IEnumerable<string> CheckFileInfo(Uses uses, TypeId source)
    {
        if (ModuleOf(source.Namespace) == Local) yield break;
        foreach (var type in new[] { FileInfoType, FileSystemInfoType })
        {
            if (uses.SignatureTypes.Contains(type))
                yield return $"{Show(type)} signature outside Local: {Show(source)}.";
            foreach (var (method, parent, member) in uses.Calls)
                if (parent == type && !(OwnerFor(source, method, StateStoreFileOwner) &&
                        (member == "get_Exists" || type == FileInfoType && member == ".ctor")))
                    yield return $"{Show(type)}.{member} outside StateStore.OpenStore: {Show(source)}.";
            if (uses.IlTypes.Contains(type) && !uses.Calls.Any(call => call.Parent == type))
                yield return $"{Show(type)} outside Local: {Show(source)}.";
        }
    }

    private static IEnumerable<string> CheckNative(Uses uses, TypeId source)
    {
        if (References(uses, new TypeId("System.Runtime.InteropServices", "NativeLibrary")))
            yield return $"dynamic native loading has no permitted engine owner: {Show(source)}.";
        // Storage uses the managed SQLite adapter; it has no permission to introduce native file operations.
        foreach (var (method, library, entryPoint) in uses.NativeImports)
            if (!NativeOwners.Any(owner => owner.Library == library && owner.EntryPoint == entryPoint &&
                    OwnerFor(source, method, owner.Owner)))
                yield return $"native import outside its exact owner operation: {Show(source)}.{method}.";
    }

    /// <summary>F22: an Entry copy may set Path only, and only for DropboxListing's path respelling.</summary>
    private static IEnumerable<string> CheckEntrySetters(Uses uses, TypeId source)
    {
        if (!InDropbox(source) || OwnedBy(source, EntrySetterOwners)) yield break;
        foreach (var (method, parent, member) in uses.Calls)
            if (parent == EntryType && member.StartsWith("set_", StringComparison.Ordinal) &&
                !(member == "set_Path" && OwnedBy(source, DropboxListingOwner)))
                yield return $"Entry setter {member} outside the path-only respelling: {Show(source)}.";
    }

    /// <summary>F12: subject-set and subject-distinct policy belongs to IssueSubject.</summary>
    private static IEnumerable<string> CheckSubjectSets(Uses uses, TypeId source)
    {
        if (OwnedBy(source, SubjectOwners)) yield break;
        if (uses.Instantiations.Any(instantiation =>
                instantiation.Generic == HashSetType && instantiation.Arguments.Contains(IssueItemType)))
            yield return $"subject set: {Show(source)} builds HashSet<IssueItem> outside IssueSubject.";
        if (uses.GenericCalls.Any(call => call.Parent == EnumerableType && call.Member == "Distinct" &&
                call.Arguments.Contains(IssueItemType)))
            yield return $"subject distinct: {Show(source)} runs Enumerable.Distinct<IssueItem> outside IssueSubject.";
    }

    /// <summary>F31: only FolderPlanBuilder takes scan evidence, through the guarded acquisition.</summary>
    private static IEnumerable<string> CheckInScan(Uses uses, TypeId source)
    {
        if (OwnedBy(source, InScanOwners)) yield break;
        if (uses.Members.Any(member => member.Member == "InScan" &&
                (member.Parent == IStateStoreType || member.Parent == StateStoreType)))
            yield return $"InScan outside FolderPlanBuilder: {Show(source)}.";
    }

    private static IEnumerable<string> PlanSqlCallSites(Uses uses, string source, string? method = null) =>
        uses.Strings
            .Where(entry => method is null || entry.Method == method)
            .Where(entry => StateSchemaStatements.Contains(entry.Value) && !PlanStatements.Contains(entry.Value))
            .Select(entry => $"folder plan: SQL call site {source}.{entry.Method} inlines an unverified statement.");

    private static string? SourceFile(TypeId source) => FileOf(Show(TopLevel(source)));

    // The exemption needs the exact top-level type and the one file declaring it; unresolved attribution fails closed.
    private static bool OwnedBy(TypeId source, params Owner[] owners) =>
        SourceFile(source) is { } file && owners.Any(owner => source.Namespace == owner.Type.Namespace &&
            TopLevelName(source) == owner.Type.Name && file == owner.File);

    private static bool OwnerFor(TypeId source, string? method, params Owner[] owners) =>
        owners.Any(owner => (owner.Methods is null ||
                method is not null && Array.IndexOf(owner.Methods, method) >= 0) &&
            OwnedBy(source, owner));

    private static bool InDropbox(TypeId source) => ModuleOf(source.Namespace) == Dropbox;

    private static TypeId TopLevel(TypeId source) => source with { Name = TopLevelName(source) };

    private static IEnumerable<string> CheckEdges(Uses uses, TypeId source, string module)
    {
        var allowed = Allowed[module];
        foreach (var target in All(uses))
        {
            var targetModule = ModuleOf(target.Namespace);
            if (targetModule is null || targetModule == module) continue;
            if (Array.IndexOf(allowed, targetModule) >= 0) continue;
            yield return $"1.2 dependency: {Show(source)} in {module} references {Show(target)} in {targetModule}.";
        }
    }

    private static IEnumerable<string> CheckSqlite(Uses uses, TypeId source, string module)
    {
        if (module == Storage) yield break;
        foreach (var target in All(uses))
        {
            var sqlite = target.Namespace == SqliteNamespace
                         || target.Namespace.StartsWith(SqliteNamespace + ".", StringComparison.Ordinal);
            if (sqlite) yield return $"Microsoft.Data.Sqlite outside Storage: {Show(source)} references {Show(target)}.";
        }
    }

    private static string TopLevelName(TypeId type)
    {
        var nested = type.Name.IndexOf('+');
        var name = nested < 0 ? type.Name : type.Name[..nested];
        // A generic type's metadata name carries its arity; the source declaration and owner map do not.
        var arity = name.IndexOf('`');
        return arity < 0 ? name : name[..arity];
    }

    private static IEnumerable<string> CheckFileSystem(Uses uses, TypeId source, string module)
    {
        if (module == Local) yield break;

        foreach (var (parent, member) in uses.PathConstructors)
            yield return $"{Show(parent)}.{member} opens a path outside Local: {Show(source)}.";
        if (uses.SignatureTypes.Contains(FileType) ||
            uses.IlTypes.Contains(FileType) && !uses.Calls.Any(call => call.Parent == FileType))
            yield return $"System.IO.File outside Local: {Show(source)}.";
        foreach (var (method, parent, member) in uses.Calls)
            if (parent == FileType && !(member == "Exists" && OwnerFor(source, method, ScanExistsOwners) ||
                    member == "Delete" && OwnerFor(source, method, ScratchDeleteOwners)))
                yield return $"System.IO.File.{member} outside its scratch-file owner: {Show(source)}.{method}.";
        if (uses.SignatureTypes.Contains(FileStreamType))
            yield return $"System.IO.FileStream signature outside Local: {Show(source)}.";
        foreach (var (method, parent, member) in uses.Calls)
            if (parent == FileStreamType &&
                !(member == ".ctor" && OwnerFor(source, method, ScratchCreateOwners)))
                yield return $"System.IO.FileStream.{member} outside its scratch-create owner: {Show(source)}.{method}.";
        if (uses.IlTypes.Contains(FileStreamType) && !uses.Calls.Any(call => call.Parent == FileStreamType))
            yield return $"System.IO.FileStream outside Local: {Show(source)}.";
        if (References(uses, DirectoryInfoType))
            yield return $"System.IO.DirectoryInfo outside Local: {Show(source)}.";
        if (uses.Members.Any(member => member.Parent == PathType && member.Member == nameof(Path.Exists)))
            yield return $"System.IO.Path.Exists outside Local: {Show(source)}.";
        if (uses.Members.Any(member => member.Parent == PathType && member.Member == nameof(Path.GetTempFileName)))
            yield return $"System.IO.Path.GetTempFileName outside Local: {Show(source)}.";
        // The remaining file-system types have one owner, Local; the Core listing and journal are Local's to port.
        foreach (var banned in new[] { RandomAccessType, SafeFileHandleType, WindowsListingType, UsnJournalType,
                     new TypeId("Massdex.SyncShark", "PilotDirectoryLease") })
            if (References(uses, banned))
                yield return $"{Show(banned)} outside Local: {Show(source)}.";
        foreach (var message in CheckFileInfo(uses, source)) yield return message;

        if (uses.SignatureTypes.Contains(DirectoryType) ||
            uses.IlTypes.Contains(DirectoryType) && !uses.Calls.Any(call => call.Parent == DirectoryType))
            yield return $"System.IO.Directory outside Local: {Show(source)}.";
        foreach (var (method, parent, member) in uses.Calls)
            if (parent == DirectoryType && !(member == "CreateDirectory" &&
                    OwnerFor(source, method, StateStoreFileOwner)))
                yield return $"System.IO.Directory.{member} outside StateStore.OpenStore: {Show(source)}.{method}.";
    }

    private static void Fail(string message) => Check.Require(false, message);

    // The reviewer counterexamples: each fixture is IL the rule must catch, and the real engine code must still pass.
    private static void Fixtures()
    {
        FixtureFails(typeof(GenericSpecFixture), Local,
            "in Local references Massdex.SyncShark.Engine.Storage.ScanWriter in Storage");
        FixtureFails(typeof(CatchTypeFixture), Local, "Microsoft.Data.Sqlite outside Storage");
        FixtureFails(typeof(PathConstructorFixture), Planner,
            "System.IO.StreamReader..ctor opens a path outside Local",
            "System.IO.StreamWriter..ctor opens a path outside Local");
        FixtureFails(typeof(IllegalPathFixture), Planner,
            "System.IO.Path.GetTempFileName outside Local",
            "System.IO.RandomAccess outside Local",
            "System.IO.FileSystemInfo outside Local",
            "Microsoft.Win32.SafeHandles.SafeFileHandle outside Local",
            "Massdex.SyncShark.WindowsDirectoryListing outside Local",
            "Massdex.SyncShark.NtfsUsnJournal outside Local",
            "Massdex.SyncShark.PilotDirectoryLease outside Local");
        FixturePasses(typeof(IllegalPathFixture), Local);
        // A Stream-based constructor opens no path, so it is not a local-file effect.
        FixturePasses(typeof(StreamConstructorFixture), Planner);

        WithFiles(_ => Engine + "Executor/RowContexts.cs", () =>
            Check.Require(!CheckRowFacts(UsesOf(typeof(RowFactsFixture)),
                new TypeId(RootNamespace + "." + Executor, "RowContexts")).Any(),
                "A2: the real row projection owner may construct row facts."));
        WithFiles(_ => Engine + "Executor/SelectionPass.cs", () =>
            Check.Require(CheckRowFacts(UsesOf(typeof(RowFactsFixture)),
                new TypeId(RootNamespace + "." + Executor, "SelectionPass")).Any(),
                "A2: constructing row facts outside their owner fails, including target-typed new."));
        OwnershipFixtures();

        ScratchFileFixtures();
        NativeFixtures();
        PlanSqlCallSiteFixtures();
        ReplyDecoderFixtures();
        DropboxSpoolFixtures();
        ContractOwnershipFixtures();
        ScanOwnershipFixtures();
        AdapterFenceFixtures();
        StartupAcquisitionFixtures();
        LocalEvidenceFixtures();
    }

    private static void LocalEvidenceFixtures()
    {
        var cases = new (Type Fixture, Owner Owner)[]
        {
            (typeof(CompleteObservationFixture), At("RemovalResult", "RemovalResult.cs")),
            (typeof(RebindFixture), In(Executor, "LocalEvidence", "Executor/LocalEvidence.cs")),
            (typeof(AdoptFixture), In(Executor, "ItemPreparation", "Executor/ItemPreparation.cs")),
            (typeof(TransferFixture), In(Executor, "SelectionAdmission", "Executor/SelectionAdmission.cs")),
        };
        foreach (var (fixture, owner) in cases)
        {
            var uses = UsesOf(fixture);
            WithFiles(_ => owner.File, () =>
                Check.Require(!CheckLocalEvidence(uses, owner.Type).Any(),
                    "B9: the exact observation/evidence owner may use its building block."));
            WithFiles(_ => Engine + "Executor/ItemSync.cs", () =>
                Check.Require(CheckLocalEvidence(uses, new TypeId(RootNamespace + "." + Executor, "ItemSync")).Any(),
                    "B9: another owner cannot reconstruct observations or inline proof adoption/transfer."));
            WithFiles(_ => Engine + "Executor/ItemSync.cs", () =>
                Check.Require(CheckLocalEvidence(uses, owner.Type).Any(),
                    "B9: the correct type in another file is not an ownership exemption."));
        }
        WithFiles(_ => Engine + "Executor/ItemPreparation.cs", () =>
            Check.Require(CheckLocalEvidence(UsesOf(typeof(WrongAdoptionMethodFixture)),
                new TypeId(RootNamespace + "." + Executor, "ItemPreparation")).Any(),
                "B9: an evidence owner cannot adopt from another lifetime method."));
    }

    private static class WrongAdoptionMethodFixture
    {
        public static void Other(LocalEvidence evidence, Row row) => evidence.Adopt(row, null, row, null);
    }

    private static class CompleteObservationFixture
    {
        public static Entry CompleteObservation(Entry entry) => entry with { ContentHash = "complete-digest" };
    }

    private static class RebindFixture
    {
        public static void Adopt(LocalEvidence evidence, Row row, Entry observed) => evidence.Rebind(row, row, observed);
    }

    private static class AdoptFixture
    {
        public static void RefreshAsync(LocalEvidence evidence, Row row) => evidence.Adopt(row, null, row, null);
    }

    private static class TransferFixture
    {
        public static void RunAsync(LocalEvidence evidence, Row row) => evidence.Transfer(row, null, row);
    }

    private static void StartupAcquisitionFixtures()
    {
        var host = new TypeId(RootNamespace + ".Run", "EngineHost");
        var operations = new TypeId(RootNamespace + ".Run", "EngineOperations");
        var client = new TypeId(RootNamespace + ".Edge", "EngineHostClient");
        var uses = UsesOf(typeof(StartupAcquisitionFixture));
        WithFiles(_ => Engine + "Run/EngineHost.cs", () =>
        {
            Check.Require(!CheckStartupAcquisition(uses, host).Any(), "startup: composition owns its acquisitions");
            Check.Require(CheckStartupAcquisition(uses, operations).Any(), "startup: wrong type cannot borrow composition");
        });
        WithFiles(_ => Engine + "Run/Other.cs", () =>
            Check.Require(CheckStartupAcquisition(uses, host).Any(), "startup: wrong file cannot borrow composition"));
        var reads = UsesOf(typeof(StartupReadFixture));
        WithFiles(_ => Engine + "Run/EngineOperations.cs", () =>
        {
            Check.Require(!CheckStartupAcquisition(reads, operations).Any(), "startup: exact read owner opens read-only state");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupWrongMethodFixture)), operations).Any(),
                "startup: another method on the same owner cannot open state or start Monitor");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupWritableReadFixture)), operations).Any(),
                "startup: a read owner cannot open writable state");
            Check.Require(!CheckStartupAcquisition(UsesOf(typeof(StartupDirectFixture)), operations).Any(),
                "startup: direct acquisition owns Monitor");
        });
        WithFiles(_ => Engine + "Edge/EngineHostClient.cs", () =>
            Check.Require(!CheckStartupAcquisition(UsesOf(typeof(StartupClientFixture)), client).Any(),
                "startup: exact client progress exception remains legal"));
        var monitor = new TypeId(RootNamespace + ".Run", "Monitor");
        var chain = UsesOf(typeof(Massdex.SyncShark.Engine.Run.Monitor));
        Check.Require(chain.Calls.Any(call => call.Method == ".ctor" && call.Parent == monitor && call.Member == ".ctor"),
            "startup: own-chain fixture contains the real constructor delegation");
        WithFiles(_ => Engine + "Run/Monitor.cs", () =>
        {
            Check.Require(!CheckStartupAcquisition(chain, monitor).Any(), "startup: Monitor owns its constructor chain");
            Check.Require(CheckStartupAcquisition(chain, operations).Any(), "startup: wrong type cannot borrow Monitor chaining");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupMonitorStartFixture)), monitor).Any(),
                "startup: Monitor constructor cannot start its lifetime");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupMonitorConstructionFixture)), monitor).Any(),
                "startup: another Monitor method cannot borrow constructor chaining");
        });
        WithFiles(_ => Engine + "Run/Other.cs", () =>
            Check.Require(CheckStartupAcquisition(chain, monitor).Any(), "startup: wrong file cannot borrow Monitor chaining"));
        var hostedStart = WithNested(typeof(StartupHostedStartFixture)).Select(UsesOf)
            .Single(candidate => candidate.Calls.Any(call => call.Parent == monitor && call.Member == "Start"));
        WithFiles(_ => Engine + "Run/EngineHost.cs", () =>
        {
            Check.Require(!CheckStartupAcquisition(hostedStart, host).Any(), "startup: hosted startup promotes its owned Monitor");
            Check.Require(CheckStartupAcquisition(hostedStart, operations).Any(), "startup: wrong type cannot borrow hosted start");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupMonitorStartFixture)), host).Any(),
                "startup: another host method cannot borrow hosted start");
            Check.Require(CheckStartupAcquisition(UsesOf(typeof(StartupHostedConstructionFixture)), host).Any(),
                "startup: hosted startup cannot construct a Monitor");
        });
        WithFiles(_ => Engine + "Run/Other.cs", () =>
            Check.Require(CheckStartupAcquisition(hostedStart, host).Any(), "startup: wrong file cannot borrow hosted start"));
    }

    private static class StartupHostedStartFixture
    {
        public static async Task StartCoreAsync(Massdex.SyncShark.Engine.Run.Monitor monitor)
        {
            await Task.CompletedTask;
            monitor.Start();
        }
    }

    private static class StartupHostedConstructionFixture
    {
        public static Massdex.SyncShark.Engine.Run.Monitor StartCoreAsync() => new(TimeProvider.System);
    }

    private static class StartupMonitorConstructionFixture
    {
        public static Massdex.SyncShark.Engine.Run.Monitor Other() => new(TimeProvider.System);
    }

    private sealed class StartupMonitorStartFixture
    {
        public StartupMonitorStartFixture(Massdex.SyncShark.Engine.Run.Monitor monitor) => monitor.Start(quietOnly: true);
    }

    private static class StartupAcquisitionFixture
    {
        public static void Compose(string path)
        {
            using var store = Massdex.SyncShark.Engine.Storage.StateStore.Open(path);
            using var read = Massdex.SyncShark.Engine.Storage.StateStore.OpenReadOnly(path);
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
            monitor.Start(quietOnly: true);
        }
    }
    private static class StartupReadFixture
    {
        public static void Read(string path) { using var store = Massdex.SyncShark.Engine.Storage.StateStore.OpenReadOnly(path); }
    }
    private static class StartupWritableReadFixture
    {
        public static void Read(string path) { using var store = Massdex.SyncShark.Engine.Storage.StateStore.Open(path); }
    }
    private static class StartupWrongMethodFixture
    {
        public static void Other(string path)
        {
            using var store = Massdex.SyncShark.Engine.Storage.StateStore.OpenReadOnly(path);
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
            monitor.Start(quietOnly: true);
        }
    }
    private static class StartupDirectFixture
    {
        public static void WithHostAsync()
        {
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
            monitor.Start(quietOnly: true);
        }
    }
    private static class StartupClientFixture
    {
        public static void TryExecuteResultAsync()
        {
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
            monitor.Start(quietOnly: true);
        }
    }

    private static class RowFactsFixture
    {
        public static RowContext Create() => new(false, false, false, null, false, false, false, false);
    }

    private static void PlanSqlCallSiteFixtures() =>
        Check.Require(PlanSqlCallSites(UsesOf(typeof(PlanSqlFixture)), "PlanSqlFixture").Any(),
            "folder plan: a plan call site inlining a non-inventory statement fails");

    // READING 8.3: the selected-field projection (DropboxEntries) and typed decoder (DropboxReply) own their parse;
    // no other Dropbox type parses a response, and a counterfeit file receives no exemption.
    private static void ReplyDecoderFixtures()
    {
        var entries = new TypeId(RootNamespace + "." + Dropbox, "DropboxEntries");
        var reply = new TypeId(RootNamespace + "." + Dropbox, "DropboxReply");
        var feed = new TypeId(RootNamespace + "." + Dropbox, "DropboxFeed");
        WithFiles(_ => DropboxFolder + "DropboxEntries.cs", () =>
            Check.Require(!CheckTransport(UsesOf(typeof(JsonParseFixture)), entries).Any(),
                "READING 8.3: DropboxEntries owns its selected-field parse"));
        WithFiles(_ => DropboxFolder + "DropboxReply.cs", () =>
            Check.Require(!CheckTransport(UsesOf(typeof(JsonParseFixture)), reply).Any(),
                "READING 8.3: DropboxReply owns its typed response parse"));
        WithFiles(_ => DropboxFolder + "DropboxFeed.cs", () =>
            Check.Require(CheckTransport(UsesOf(typeof(JsonParseFixture)), feed).Any(),
                "READING 8.3: another Dropbox type must not parse a response"));
        WithFiles(_ => DropboxFolder + "Nested/DropboxEntries.cs", () =>
            Check.Require(CheckTransport(UsesOf(typeof(JsonParseFixture)), entries).Any(),
                "READING 8.3: a counterfeit decoder file receives no parse exemption"));
    }

    // CV-ACCESS: the page spool is the Stream the Local adapter owns; Dropbox serializes into it and opens no path.
    private static void DropboxSpoolFixtures()
    {
        var source = new TypeId(RootNamespace + "." + Dropbox, "DropboxPage");
        Check.Require(!CheckFileSystem(UsesOf(typeof(DropboxSpoolFixture)), source, Dropbox).Any(),
            "CV-ACCESS: the page spool serializes through the adapter-owned Stream");
        var failures = new (Type Fixture, string[] Operations)[]
        {
            (typeof(DropboxFileStreamFixture), ["System.IO.FileStream"]),
            (typeof(DropboxFileDeleteFixture), ["System.IO.File.Delete"]),
            (typeof(DropboxFileExistsFixture), ["System.IO.File.Exists"]),
            (typeof(DropboxPathConstructorFixture), ["System.IO.StreamReader..ctor"]),
        };
        foreach (var (fixture, operations) in failures)
        {
            var messages = CheckFileSystem(UsesOf(fixture), source, Dropbox).ToList();
            foreach (var operation in operations)
                Check.Require(messages.Any(message => message.Contains(operation, StringComparison.Ordinal)),
                    $"CV-ACCESS: a Dropbox file effect fails ({fixture.Name})");
        }
    }

    // CV-RETRYVIEW / LOCAL-IN-USE C2 correction 2: the retry-presentation values are Engine Contracts at the root
    // namespace, each in its one file; Run may reference them and never Edge.
    private static void ContractOwnershipFixtures()
    {
        Check.Require(typeof(Massdex.SyncShark.Engine.IssueRetryView).Namespace == RootNamespace &&
            LiteralsChecks.DeclaredIn(RootNamespace + ".IssueRetryView") == Engine + "IssueRetryView.cs",
            "retry view: IssueRetryView is an Engine Contracts value in its one file");
        Check.Require(typeof(Massdex.SyncShark.Engine.RetryPresentationState).Namespace == RootNamespace &&
            LiteralsChecks.DeclaredIn(RootNamespace + ".RetryPresentationState") == Engine + "RetryPresentationState.cs",
            "retry view: RetryPresentationState is an Engine Contracts value in its one file");
        var run = new TypeId(RootNamespace + "." + RunModule, "FixtureRunType");
        Check.Require(!CheckEdges(UsesOf(typeof(RunRetryProjectionFixture)), run, RunModule).Any(),
            "CV-RETRYVIEW: Run may reference the Contracts retry projection");
        Check.Require(CheckEdges(UsesOf(typeof(RunEdgeReferenceFixture)), run, RunModule)
                .Any(message => message.Contains(Edge, StringComparison.Ordinal)),
            "CV-INUSE: Run must not reference Edge; the guidance stays in Edge");
    }

    // CV-ACCESS (AutoSync 5.1): ScanOwnership and the injectable PairSession constructor stay internal; the public
    // intended Open composition remains, and the Executor keeps no Run dependency.
    private static void ScanOwnershipFixtures()
    {
        Check.Require(!typeof(Massdex.SyncShark.Engine.Run.ScanOwnership).IsPublic,
            "CV-ACCESS: ScanOwnership stays internal with no public bypass");
        Check.Require(typeof(PairSession).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
            "CV-ACCESS: the injectable PairSession constructor stays internal");
        Check.Require(!typeof(PairSession).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(method => method.Name == "Open") &&
            typeof(PairSession).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Any(method => method.Name == "Open" && method.IsAssembly &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Massdex.SyncShark.Engine.Run.EngineHost)) &&
                    method.GetParameters().Any(parameter => parameter.Name == "registration")),
            "CV-ACCESS: PairSession composition is internal and requires the existing host registration");
        Check.Require(!typeof(PairSession).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(Massdex.SyncShark.Engine.Run.ScanOwnership)),
            "CV-ACCESS: no public PairSession member exposes the scan owner");
        var executor = new TypeId(RootNamespace + "." + Executor, "FixtureExecutorType");
        Check.Require(CheckEdges(UsesOf(typeof(ExecutorRunReferenceFixture)), executor, Executor)
                .Any(message => message.Contains(RunModule, StringComparison.Ordinal)),
            "CV-ACCESS: the Executor must not reference Run");
    }

    // P2-11: the concrete/native Local free-space owners stay out of Contracts and Executor; Transfers is the
    // acquisition/lifetime owner; DownloadSpace binds the parent probes; adapters carry no reservation parameter
    // or output callback.
    private static void AdapterFenceFixtures()
    {
        var root = new TypeId(RootNamespace, "FixtureRootType");
        var executor = new TypeId(RootNamespace + "." + Executor, "FixtureExecutorType");
        Check.Require(CheckEdges(UsesOf(typeof(LocalSpaceReferenceFixture)), root, RootModule).Any(),
            "P2-11: Contracts must not reference the Local free-space owners");
        Check.Require(CheckEdges(UsesOf(typeof(LocalSpaceReferenceFixture)), executor, Executor).Any(),
            "P2-11: the Executor must not reference the concrete Local free-space owners");
        var transfers = UsesOf(typeof(Transfers));
        var context = new TypeId(RootNamespace + "." + Executor, "ExecutorContext");
        Check.Require(transfers.Members.Any(member => member.Parent == context &&
                member.Member == "get_ReserveDownloadAsync"),
            "P2-11: Transfers is the acquisition/lifetime owner of the download reservation");
        Check.Require(!References(transfers, new TypeId(RootNamespace + "." + Local, "DownloadSpace")),
            "P2-11: the Executor never touches the concrete accounting owner");
        var space = UsesOf(typeof(DownloadSpace));
        var volume = NativeSpaceOwner.Type;
        Check.Require(space.Members.Any(member => member.Parent == volume && member.Member == "Serial") &&
            space.Members.Any(member => member.Parent == volume && member.Member == "Available"),
            "P2-11: DownloadSpace binds the parent volume probes through VolumeSpace");
        foreach (var method in typeof(ISide).GetMethods())
            foreach (var parameter in method.GetParameters())
                Check.Require(!typeof(Delegate).IsAssignableFrom(parameter.ParameterType) &&
                        parameter.ParameterType != typeof(DownloadSpace),
                    $"P2-11: the adapter signature {method.Name} carries no reservation parameter or output callback");
    }

    private static void ScratchFileFixtures()
    {
        var store = StateStoreFileOwner.Type;
        var plan = ScratchDeleteOwners[1].Type;
        var cases = new (Type Fixture, TypeId Source, string File)[]
        {
            (typeof(ScanExistsFixture), store, "Storage/StateStore.cs"),
            (typeof(StoreScratchDeleteFixture), store, "Storage/StateStore.cs"),
            (typeof(PlanDisposeFixture), plan, "Storage/StoredFolderPlan.cs"),
            (typeof(DatabaseDirectoryFixture), store, "Storage/StateStore.cs"),
            (typeof(StateStoreOpenStoreFixture), store, "Storage/StateStore.cs"),
        };
        foreach (var (fixture, source, file) in cases)
        {
            var uses = UsesOf(fixture);
            WithFiles(_ => Engine + file, () =>
            {
                Check.Require(!CheckFileSystem(uses, source, Storage).Any(),
                    $"scratch file: exact scratch owner passes ({fixture.Name})");
                Check.Require(CheckFileSystem(uses, source with { Name = "OtherStore" }, Storage).Any(),
                    $"scratch file: a foreign type in the owner file fails ({fixture.Name})");
                Check.Require(CheckFileSystem(uses, source with { Namespace = RootNamespace + "." + Planner }, Planner).Any(),
                    $"scratch file: a foreign namespace fails ({fixture.Name})");
            });
            foreach (var counterfeit in new string?[] { Engine + "Storage/Nested/" + Path.GetFileName(file), null })
                WithFiles(_ => counterfeit, () =>
                    Check.Require(CheckFileSystem(uses, source, Storage).Any(),
                        $"scratch file: counterfeit or unresolved file fails ({fixture.Name})"));
        }
        WithFiles(_ => Engine + "Storage/StateStore.cs", () =>
        {
            Check.Require(!CheckFileSystem(UsesOf(typeof(StorageStreamFixture)), store, Storage).Any(),
                "scratch file: the exclusive plan-scratch create belongs to OpenFolderPlan");
            var failures = new (Type Fixture, string[] Operations)[]
            {
                (typeof(ForeignScratchMethodFixture), ["File.Delete", "File.Exists", "Directory.CreateDirectory"]),
                (typeof(WrongScratchOperationFixture), ["File.ReadAllText", "Directory.Delete"]),
                (typeof(ForeignScratchStreamFixture), ["System.IO.FileStream"]),
                (typeof(StorageDirectoryInfoFixture), ["DirectoryInfo"]),
                (typeof(PlanDisposeFixture), ["File.Delete"]),
                (typeof(ForeignFileInfoFixture), ["get_Exists"]),
                (typeof(FileInfoArgumentFixture), [".ctor"]),
            };
            foreach (var (fixture, operations) in failures)
            {
                var messages = CheckFileSystem(UsesOf(fixture), store, Storage).ToList();
                foreach (var operation in operations)
                    Check.Require(messages.Any(message => message.Contains(operation, StringComparison.Ordinal)),
                        $"scratch file: unapproved Storage operation fails ({fixture.Name}, {operation})");
            }
        });
        WithFiles(_ => Engine + "Storage/StoredFolderPlan.cs", () =>
        {
            Check.Require(CheckFileSystem(UsesOf(typeof(PlanExistsFixture)), plan, Storage).Any(),
                "scratch file: plan disposal has Delete permission, not Exists permission");
            Check.Require(CheckFileSystem(UsesOf(typeof(StoreScratchDeleteFixture)), plan, Storage).Any(),
                "scratch file: the plan does not inherit StateStore's methods");
        });
    }

    private static void NativeFixtures()
    {
        var shell = NativeShellOwner.Type;
        var approved = UsesOf(typeof(NativeShellFixture));
        Check.Require(approved.NativeImports.Count == 1, "native import: declarations are collected without IL bodies");
        WithFiles(_ => Engine + "Local/LocalBin.cs", () =>
        {
            Check.Require(!CheckNative(approved, shell).Any(), "native import: LocalBin's existing shell import passes");
            foreach (var fixture in new[] { typeof(NativeOtherMethodFixture), typeof(NativeOtherLibraryFixture),
                         typeof(NativeOtherEntryFixture) })
                Check.Require(CheckNative(UsesOf(fixture), shell).Any(),
                    $"native import: a different native operation fails ({fixture.Name})");
            Check.Require(CheckNative(approved, shell with { Name = "OtherLocal" }).Any(),
                "native import: another type in LocalBin's file has no native exemption");
            Check.Require(CheckNative(approved, StateStoreFileOwner.Type).Any(),
                "native import: Storage cannot bypass the managed file boundary with native imports");
            Check.Require(CheckNative(UsesOf(typeof(DynamicNativeFixture)), shell).Any(),
                "native import: the shell exemption does not permit dynamic native loading");
        });
        foreach (var file in new string?[] { Engine + "Local/Nested/LocalBin.cs", null })
            WithFiles(_ => file, () => Check.Require(CheckNative(approved, shell).Any(),
                "native import: a counterfeit or unresolved native owner fails"));

        // P2-11: VolumeSpace alone owns the parent volume probes; the shell and other kernel imports stay denied.
        var volume = NativeSpaceOwner.Type;
        var space = UsesOf(typeof(NativeVolumeFixture));
        Check.Require(space.NativeImports.Count == 1, "native import: the volume probe is declared once");
        WithFiles(_ => Engine + "Local/VolumeSpace.cs", () =>
        {
            Check.Require(!CheckNative(space, volume).Any(), "native import: VolumeSpace's parent space probe passes");
            foreach (var fixture in new[] { typeof(NativeVolumeOtherMethodFixture),
                         typeof(NativeVolumeOtherLibraryFixture), typeof(NativeVolumeOtherEntryFixture) })
                Check.Require(CheckNative(UsesOf(fixture), volume).Any(),
                    $"native import: a different volume operation fails ({fixture.Name})");
            Check.Require(CheckNative(space, volume with { Name = "OtherLocal" }).Any(),
                "native import: another type in VolumeSpace's file has no native exemption");
            Check.Require(CheckNative(approved, volume).Any(),
                "native import: the shell operation is not permitted in VolumeSpace");
            Check.Require(CheckNative(space, shell).Any(),
                "native import: the volume probe is not permitted in LocalBin");
            Check.Require(CheckNative(UsesOf(typeof(DynamicNativeFixture)), volume).Any(),
                "native import: the volume exemption does not permit dynamic native loading");
            Check.Require(CheckNative(space, StateStoreFileOwner.Type).Any(),
                "native import: Storage cannot bypass the managed file boundary with the volume probe");
        });
        foreach (var file in new string?[] { Engine + "Local/Nested/VolumeSpace.cs", null })
            WithFiles(_ => file, () => Check.Require(CheckNative(space, volume).Any(),
                "native import: a counterfeit or unresolved volume owner fails"));
    }

    private static void FixtureFails(Type type, string module, params string[] expected)
    {
        var messages = Violations(type, module);
        foreach (var fragment in expected)
            Check.Require(messages.Any(message => message.Contains(fragment, StringComparison.Ordinal)),
                $"dependencies fixture should fail ({type.Name}): {fragment}; got: {string.Join(" | ", messages)}");
    }

    /// <summary>F01/F07/F08/F12/F15/F16/F20/F21/F22/F31/F33 fixtures: every rule catches its counterexample and
    /// keeps its exact owner file; a counterfeit file receives no exemption.</summary>
    private static void OwnershipFixtures()
    {
        // File identity is parsed from the engine sources: the owner is found, a counterfeit/partial is not.
        Check.Require(LiteralsChecks.DeclaredIn(RootNamespace + "." + Planner + ".Decide") ==
            Engine + "Planner/Decide.cs", "ownership identity: the real owner file is found");
        Check.Require(LiteralsChecks.DeclaredIn(RootNamespace + ".NotDeclared") is null,
            "ownership identity: a counterfeit type has no declaration file");

        var planner = new TypeId(RootNamespace + "." + Planner, "FixturePlannerType");
        var root = new TypeId(RootNamespace, "FixtureRootType");
        Check.Require(!CheckEdges(UsesOf(typeof(RootReferenceFixture)), planner, Planner).Any(),
            "F01: Planner may use root labels");
        Check.Require(CheckEdges(UsesOf(typeof(ExecutorReferenceFixture)), planner, Planner)
                .Any(message => message.Contains(Executor, StringComparison.Ordinal)),
            "F01: Planner must not reference the Executor");
        Check.Require(CheckEdges(UsesOf(typeof(DropboxReferenceFixture)), root, RootModule)
                .Any(message => message.Contains(Dropbox, StringComparison.Ordinal)),
            "F01: root must not reference Dropbox");

        var arbitraryFile = new TypeId(RootNamespace, "FixtureFileUser");
        Check.Require(CheckFileSystem(UsesOf(typeof(ArbitraryFileFixture)), arbitraryFile, RootModule).Any(),
            "F07: arbitrary root filesystem use must fail");
        Check.Require(!CheckFileSystem(UsesOf(typeof(RootReferenceFixture)), arbitraryFile, RootModule).Any(),
            "F07: a non-filesystem root type passes");

        var storage = new TypeId(RootNamespace + "." + Storage, "FixtureStore");
        foreach (var fixture in new[] { typeof(SqliteSignatureFixture), typeof(SqliteGenericFixture) })
        {
            Check.Require(!CheckSqlite(UsesOf(fixture), storage, Storage).Any(),
                $"F08: Storage may use Sqlite ({fixture.Name})");
            Check.Require(CheckSqlite(UsesOf(fixture), arbitraryFile, RootModule).Any(),
                $"F08: a root Sqlite reference must fail ({fixture.Name})");
        }
        Check.Require(!CheckSqlite(UsesOf(typeof(RootReferenceFixture)), storage, Storage).Any(),
            "F08: Storage without Sqlite passes");

        var api = new TypeId(RootNamespace + "." + Dropbox, "DropboxApi");
        var writer = new TypeId(RootNamespace + "." + Dropbox, "DropboxWriter");
        WithFiles(_ => DropboxApiFile, () =>
            Check.Require(!CheckTransport(UsesOf(typeof(HttpSendFixture)), api).Any(), "F20: DropboxApi may send"));
        WithFiles(_ => DropboxFolder + "DropboxWriter.cs", () =>
            Check.Require(CheckTransport(UsesOf(typeof(HttpSendFixture)), writer).Any(),
                "F20: another owner must not send"));
        WithFiles(_ => DropboxFolder + "Nested/DropboxApi.cs", () =>
            Check.Require(CheckTransport(UsesOf(typeof(HttpSendFixture)), api).Any(),
                "F20: a counterfeit DropboxApi file receives no exemption"));
        WithFiles(_ => DropboxApiFile, () =>
            Check.Require(CheckTransport(UsesOf(typeof(HttpSendFixture)), writer).Any(),
                "F20: a foreign type in DropboxApi.cs is not the owner"));

        WithFiles(_ => DropboxApiFile, () =>
        {
            Check.Require(!CheckTransport(UsesOf(typeof(HttpBodyFixture)), api).Any(), "F21: DropboxApi may read content");
            Check.Require(!CheckTransport(UsesOf(typeof(HttpReadFixture)), api).Any(), "F21: DropboxApi may read the body");
            Check.Require(!CheckTransport(UsesOf(typeof(JsonParseFixture)), api).Any(), "F21: DropboxApi may parse");
        });
        WithFiles(_ => DropboxFolder + "AdaptiveLimiter.cs", () =>
        {
            Check.Require(CheckTransport(UsesOf(typeof(HttpBodyFixture)), new TypeId(RootNamespace + "." + Dropbox, "AdaptiveLimiter")).Any(),
                "F21: another owner must not read content");
            Check.Require(CheckTransport(UsesOf(typeof(HttpReadFixture)), new TypeId(RootNamespace + "." + Dropbox, "AdaptiveLimiter")).Any(),
                "F21: another owner must not read the body");
            Check.Require(CheckTransport(UsesOf(typeof(JsonParseFixture)), new TypeId(RootNamespace + "." + Dropbox, "AdaptiveLimiter")).Any(),
                "F21: another owner must not parse");
        });
        // Missing attribution must fail closed inside the Dropbox module, never switch the fence off.
        WithFiles(_ => null, () =>
            Check.Require(CheckTransport(UsesOf(typeof(HttpReadFixture)),
                    new TypeId(RootNamespace + "." + Dropbox, "AdaptiveLimiter")).Any(),
                "F21: unresolved attribution is ineligible, not out of scope"));
        var partial = LiteralsChecks.MapFiles([
            (Engine + "Planner/Decide.cs", "namespace Massdex.SyncShark.Engine.Planner; class Decide {}"),
            (Engine + "Planner/Other.cs", "namespace Massdex.SyncShark.Engine.Planner; partial class Decide {}")]);
        Check.Require(partial["Massdex.SyncShark.Engine.Planner.Decide"] is null,
            "ownership identity: an ambiguous partial declaration is ineligible");

        SubjectFixtures();
        InScanFixtures();
        ConstructionFixtures();
        RunnerFixtures();
        EntrySetterFixtures();
    }

    private static void SubjectFixtures()
    {
        var owner = new TypeId(RootNamespace, "IssueSubject");
        var other = new TypeId(RootNamespace + "." + Storage, "IssueTable");
        var fixtures = new[] { typeof(SubjectSetExplicitFixture), typeof(SubjectSetTargetTypedFixture),
            typeof(SubjectDistinctFixture) };
        foreach (var fixture in fixtures)
        {
            WithFiles(_ => Engine + "IssueSubject.cs", () =>
                Check.Require(!CheckSubjectSets(UsesOf(fixture), owner).Any(),
                    $"F12: IssueSubject owns the subject forms ({fixture.Name})"));
            WithFiles(_ => Engine + "Storage/IssueTable.cs", () =>
                Check.Require(CheckSubjectSets(UsesOf(fixture), other).Any(),
                    $"F12: a subject form outside IssueSubject fails ({fixture.Name})"));
        }
    }

    private static void InScanFixtures()
    {
        var owner = new TypeId(RootNamespace + "." + Executor, "FolderPlanBuilder");
        var other = new TypeId(RootNamespace + "." + Storage, "StateStore");
        foreach (var fixture in new[] { typeof(InScanFixture), typeof(ConditionalInScanFixture),
                     typeof(ConcreteInScanFixture) })
        {
            WithFiles(_ => Engine + "Executor/FolderPlanBuilder.cs", () =>
                Check.Require(!CheckInScan(UsesOf(fixture), owner).Any(),
                    $"F31: FolderPlanBuilder takes scan evidence ({fixture.Name})"));
            WithFiles(_ => Engine + "Storage/StateStore.cs", () =>
                Check.Require(CheckInScan(UsesOf(fixture), other).Any(),
                    $"F31: another owner must not take scan evidence ({fixture.Name})"));
        }
    }

    private static void ConstructionFixtures()
    {
        var decide = new TypeId(RootNamespace + "." + Planner, "Decide");
        var renames = new TypeId(RootNamespace + "." + Planner, "Renames");
        var folderDeletes = new TypeId(RootNamespace + "." + Planner, "FolderDeletes");
        var issuesResult = new TypeId(RootNamespace + ".Edge.Results", "IssuesResult");
        var engineCommands = new TypeId(RootNamespace + ".Edge", "EngineCommands");
        var entries = new TypeId(RootNamespace + "." + Dropbox, "DropboxEntries");
        var listing = new TypeId(RootNamespace + "." + Dropbox, "DropboxListing");
        var writer = new TypeId(RootNamespace + "." + Dropbox, "DropboxWriter");
        // The same type identity with different declaration files; a counterfeit/foreign file receives no exemption.
        var cases = new (Type Type, TypeId Source, string File, string? Fails)[]
        {
            (typeof(SyncActionConstructorFixture), decide, "Planner/Decide.cs", null),
            (typeof(SyncActionTargetTypedFixture), decide, "Planner/Decide.cs", null),
            (typeof(SyncActionWithFixture), decide, "Planner/Decide.cs", null),
            (typeof(SyncActionConstructorFixture), decide, "Planner/Counterfeit.cs", "construction"),
            (typeof(SyncActionTargetTypedFixture), decide, "Planner/Counterfeit.cs", "construction"),
            (typeof(SyncActionWithFixture), decide, "Planner/Counterfeit.cs", "construction"),
            (typeof(SyncActionWithFixture), decide, "Executor/FolderDeleteRunner.cs", "copies"),
            (typeof(SyncActionTargetTypedFixture), folderDeletes, "Planner/FolderDeletes.cs", null),
            (typeof(RenameDecisionConstructorFixture), renames, "Planner/Renames.cs", null),
            (typeof(RenameDecisionWithFixture), renames, "Planner/Renames.cs", null),
            (typeof(RenameDecisionWithFixture), renames, "Executor/RenameRunner.cs", "copies"),
            (typeof(RenameDecisionConstructorFixture), renames, "Run/Repair.cs", "construction"),
            (typeof(IssueCountConstructorFixture), issuesResult, "Edge/Results/IssuesResult.cs", null),
            (typeof(IssueCountConstructorFixture), engineCommands, "Edge/EngineCommands.cs", "construction"),
            (typeof(EntryConstructorFixture), entries, "Dropbox/DropboxEntries.cs", null),
            (typeof(EntryConstructorFixture), new TypeId(RootNamespace + "." + Dropbox, "DropboxPage"),
                "Dropbox/DropboxPage.cs", null),
            (typeof(EntryConstructorFixture), new TypeId(RootNamespace + "." + Dropbox, "DropboxReply"),
                "Dropbox/DropboxReply.cs", "construction"),
            (typeof(EntryConstructorFixture), listing, "Dropbox/DropboxListing.cs", "construction"),
            (typeof(EntryCopyFixture), listing, "Dropbox/DropboxListing.cs", null),
            (typeof(EntryCopyFixture), writer, "Dropbox/DropboxWriter.cs", "copies"),
        };
        foreach (var (type, source, file, fails) in cases)
            WithFiles(_ => Engine + file, () =>
            {
                var messages = CheckConstruction(UsesOf(type), source).ToList();
                Check.Require(fails is null
                        ? messages.Count == 0
                        : messages.Any(message => message.Contains(fails, StringComparison.Ordinal)),
                    $"construction fixture ({type.Name}, {file}, {(fails is null ? "pass" : "fail " + fails)})");
            });
    }

    private static void RunnerFixtures()
    {
        var folderDelete = new TypeId(RootNamespace + "." + Executor, "FolderDeleteRunner");
        var renameRunner = new TypeId(RootNamespace + "." + Executor, "RenameRunner");
        WithFiles(_ => Engine + "Executor/FolderDeleteRunner.cs", () =>
        {
            Check.Require(CheckConstruction(UsesOf(typeof(FolderDeleteFailureFixture)), folderDelete).Any(),
                "F15: FolderDeleteRunner builds no action outside a registered owner member");
            Check.Require(CheckConstruction(UsesOf(typeof(FolderDeleteOtherFixture)), folderDelete).Any(),
                "F15: another method may not build an action");
            Check.Require(!CheckConstruction(UsesOf(typeof(RunnerListFixture)), folderDelete).Any(),
                "F15: an unrelated construction passes");
            Check.Require(CheckConstruction(UsesOf(typeof(SyncActionWithFixture)), folderDelete).Any(),
                "F15: a runner may not reclassify an action with a copy");
        });
        WithFiles(_ => Engine + "Executor/RenameRunner.cs", () =>
        {
            Check.Require(!CheckConstruction(UsesOf(typeof(RenameResultFixture)), renameRunner).Any(),
                "F16: the result/failure methods may build a decision");
            Check.Require(CheckConstruction(UsesOf(typeof(RenameOtherFixture)), renameRunner).Any(),
                "F16: another method may not build a decision");
            Check.Require(!CheckConstruction(UsesOf(typeof(RunnerListFixture)), renameRunner).Any(),
                "F16: an unrelated construction passes");
            Check.Require(CheckConstruction(UsesOf(typeof(RenameDecisionWithFixture)), renameRunner).Any(),
                "F16: a runner may not reclassify a decision with a copy");
        });
        foreach (var fixture in new[] { typeof(AsyncLambdaApprovedFixture),
                     typeof(AsyncLocalFunctionApprovedFixture), typeof(AsyncLambdaOtherFixture),
                     typeof(AsyncLocalFunctionOtherFixture) })
            Check.Require(NestedViolations(fixture, folderDelete),
                $"F15: a generated method still has no action exemption ({fixture.Name})");
        Check.Require(!NestedViolations(typeof(RenameAsyncApprovedFixture), renameRunner),
            "F16: an async generated method in RunAsync is attributed to its source method");
        Check.Require(NestedViolations(typeof(RenameAsyncOtherFixture), renameRunner),
            "F16: an async generated method in another method fails");
    }

    private static bool NestedViolations(Type fixture, TypeId source) =>
        WithNested(fixture).Any(type => CheckConstruction(UsesOf(type), source).Any());

    private static IEnumerable<Type> WithNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(WithNested));

    private static void EntrySetterFixtures()
    {
        var listing = new TypeId(RootNamespace + "." + Dropbox, "DropboxListing");
        WithFiles(_ => DropboxFolder + "DropboxListing.cs", () =>
        {
            Check.Require(!CheckEntrySetters(UsesOf(typeof(EntryCopyFixture)), listing).Any(),
                "F22: a path-only copy passes");
            Check.Require(CheckEntrySetters(UsesOf(typeof(EntrySizeCopyFixture)), listing).Any(),
                "F22: a non-path copy fails");
            Check.Require(CheckEntrySetters(UsesOf(typeof(EntryInferredCopyFixture)), listing).Any(),
                "F22: an inferred-local non-path copy fails");
            Check.Require(!CheckEntrySetters(UsesOf(typeof(UnrelatedEntryCopyFixture)), listing).Any(),
                "F22: an unrelated record member named Entry passes");
        });
        WithFiles(_ => DropboxFolder + "DropboxEntries.cs", () =>
            Check.Require(!CheckEntrySetters(UsesOf(typeof(EntrySizeCopyFixture)),
                    new TypeId(RootNamespace + "." + Dropbox, "DropboxEntries")).Any(),
                "F22: the DropboxEntries metadata copy passes"));
        WithFiles(_ => Engine + "Local/LocalSide.cs", () =>
            Check.Require(!CheckEntrySetters(UsesOf(typeof(EntrySizeCopyFixture)),
                    new TypeId(RootNamespace + ".Local", "LocalSide")).Any(),
                "F22: an Entry copy outside Dropbox passes"));
    }

    private static void WithFiles(Func<string, string?> fileOf, Action body)
    {
        var previous = FileOf;
        FileOf = fileOf;
        try
        {
            body();
        }
        finally
        {
            FileOf = previous;
        }
    }

    private static Uses UsesOf(Type type)
    {
        using var stream = System.IO.File.OpenRead(type.Assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var source = TypeIdOf(type);
        foreach (var handle in reader.TypeDefinitions)
            if (TypeOf(reader, handle).Equals(source))
                return Collect(reader, pe, reader.GetTypeDefinition(handle));
        throw new InvalidOperationException("dependencies fixture type not found: " + type.FullName);
    }

    private static void FixturePasses(Type type, string module)
    {
        var messages = Violations(type, module);
        Check.Require(messages.Count == 0,
            $"dependencies fixture should pass ({type.Name}): {string.Join(" | ", messages)}");
    }

    private static IReadOnlyList<string> Violations(Type type, string module) =>
        Messages(UsesOf(type), TypeIdOf(type), module).ToList();

    private static TypeId TypeIdOf(Type type)
    {
        var ns = type.Namespace ?? "";
        return new TypeId(ns, ns.Length == 0 ? type.Name : type.FullName![(ns.Length + 1)..]);
    }

    private static class GenericSpecFixture
    {
        public static void Use() => Take<Massdex.SyncShark.Engine.Storage.ScanWriter>();

        private static void Take<T>() { }
    }

    private static class CatchTypeFixture
    {
        public static void Use()
        {
            // A call keeps the try region in a Release build, so the catch type stays in the exception table.
            try { System.GC.KeepAlive(null); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
    }

    private static class PathConstructorFixture
    {
        public static void Use()
        {
            _ = new System.IO.StreamReader("a");
            _ = new System.IO.StreamWriter("b");
        }
    }

    private static class PlanSqlFixture
    {
        public static string Use() => StateSchema.Create;
    }

    private static class StreamConstructorFixture
    {
        public static void Use()
        {
            _ = new System.IO.StreamReader(System.IO.Stream.Null);
            _ = new System.IO.StreamWriter(System.IO.Stream.Null);
        }
    }

    private static class IllegalPathFixture
    {
        public static void Use()
        {
            _ = System.IO.Path.GetTempFileName();
            Touch(typeof(System.IO.RandomAccess));
            Touch(typeof(System.IO.FileSystemInfo));
            Touch(typeof(Microsoft.Win32.SafeHandles.SafeFileHandle));
            Touch(typeof(global::Massdex.SyncShark.WindowsDirectoryListing));
            Touch(typeof(global::Massdex.SyncShark.NtfsUsnJournal));
            Touch(typeof(global::Massdex.SyncShark.PilotDirectoryLease));
        }

        private static void Touch(object value) => System.GC.KeepAlive(value);
    }

    private static class RootReferenceFixture
    {
        public static object Use() => typeof(Massdex.SyncShark.Engine.TextKey);
    }

    private static class ExecutorReferenceFixture
    {
        public static object Use() => typeof(Massdex.SyncShark.Engine.Executor.LockKeys);
    }

    private static class DropboxReferenceFixture
    {
        public static object Use() => typeof(Massdex.SyncShark.Engine.Dropbox.AdaptiveLimiter);
    }

    private static class ArbitraryFileFixture
    {
        public static string Use() => System.IO.File.ReadAllText("path");
    }

    private static class SqliteSignatureFixture
    {
        public static void Use(Microsoft.Data.Sqlite.SqliteConnection connection) => System.GC.KeepAlive(connection);
    }

    private static class SqliteGenericFixture
    {
        public static void Use() => Take<Microsoft.Data.Sqlite.SqliteCommand>();

        private static void Take<T>() { }
    }

    private static class HttpSendFixture
    {
        public static System.Threading.Tasks.Task Use(System.Net.Http.HttpClient client) =>
            client.SendAsync(new System.Net.Http.HttpRequestMessage());
    }

    private static class HttpBodyFixture
    {
        public static object Use(System.Net.Http.HttpResponseMessage response) => response.Content;
    }

    private static class HttpReadFixture
    {
        public static System.Threading.Tasks.Task<string> Use(System.Net.Http.HttpContent content) =>
            content.ReadAsStringAsync();
    }

    private static class JsonParseFixture
    {
        public static System.Text.Json.JsonDocument Use(string json) => System.Text.Json.JsonDocument.Parse(json);
    }

    private static class SubjectSetExplicitFixture
    {
        public static object Use(System.Collections.Generic.IReadOnlyList<Massdex.SyncShark.Engine.IssueItem> items) =>
            new System.Collections.Generic.HashSet<Massdex.SyncShark.Engine.IssueItem>(items);
    }

    private static class SubjectSetTargetTypedFixture
    {
        public static void Use(System.Collections.Generic.IReadOnlyList<Massdex.SyncShark.Engine.IssueItem> items)
        {
            System.Collections.Generic.HashSet<Massdex.SyncShark.Engine.IssueItem> set = new(items);
            System.GC.KeepAlive(set);
        }
    }

    private static class SubjectDistinctFixture
    {
        public static object Use(Massdex.SyncShark.Engine.IssueSubject subject) =>
            subject.Items.Distinct().ToArray();
    }

    private static class InScanFixture
    {
        public static bool Use(Massdex.SyncShark.Engine.IStateStore store) =>
            store.InScan("pair", Guid.Empty, Massdex.SyncShark.Engine.Side.Local, "key");
    }

    private static class ConditionalInScanFixture
    {
        public static bool? Use(Massdex.SyncShark.Engine.IStateStore? store) =>
            store?.InScan("pair", Guid.Empty, Massdex.SyncShark.Engine.Side.Local, "key");
    }

    private static class ConcreteInScanFixture
    {
        public static bool Use(Massdex.SyncShark.Engine.Storage.StateStore store) =>
            store.InScan("pair", Guid.Empty, Massdex.SyncShark.Engine.Side.Local, "key");
    }

    private static class FolderDeleteFailureFixture
    {
        public static void DeleteAsync(Massdex.SyncShark.Engine.Row row, Massdex.SyncShark.Engine.Side side) =>
            System.GC.KeepAlive(new Massdex.SyncShark.Engine.Planner.SyncAction(
                Massdex.SyncShark.Engine.Planner.SyncAction.DeleteOn(side), row));
    }

    private static class FolderDeleteOtherFixture
    {
        public static void Other(Massdex.SyncShark.Engine.Row row)
        {
            Massdex.SyncShark.Engine.Planner.SyncAction action = new(Massdex.SyncShark.Engine.ActionKind.Upload, row);
            System.GC.KeepAlive(action);
        }
    }

    private static class RenameResultFixture
    {
        public static Massdex.SyncShark.Engine.Planner.RenameDecision RunAsync() =>
            new(Massdex.SyncShark.Engine.Planner.RenameKind.None);

        public static Massdex.SyncShark.Engine.Planner.RenameDecision Failure() =>
            new(Massdex.SyncShark.Engine.Planner.RenameKind.Hold);
    }

    private static class RenameOtherFixture
    {
        public static Massdex.SyncShark.Engine.Planner.RenameDecision Other() =>
            new(Massdex.SyncShark.Engine.Planner.RenameKind.Follow, Massdex.SyncShark.Engine.Side.Local, "target");
    }

    private static class AsyncLambdaApprovedFixture
    {
        public static Action<Massdex.SyncShark.Engine.Row> DeleteAsync() => async row =>
        {
            await System.Threading.Tasks.Task.Yield();
            Massdex.SyncShark.Engine.Planner.SyncAction action = new(Massdex.SyncShark.Engine.ActionKind.Upload, row);
            System.GC.KeepAlive(action);
        };
    }

    private static class AsyncLambdaOtherFixture
    {
        public static Action<Massdex.SyncShark.Engine.Row> Other() => async row =>
        {
            await System.Threading.Tasks.Task.Yield();
            Massdex.SyncShark.Engine.Planner.SyncAction action = new(Massdex.SyncShark.Engine.ActionKind.Upload, row);
            System.GC.KeepAlive(action);
        };
    }

    private static class AsyncLocalFunctionApprovedFixture
    {
        public static void DeleteAsync(Massdex.SyncShark.Engine.Row row)
        {
            _ = Local();
            async System.Threading.Tasks.Task Local()
            {
                await System.Threading.Tasks.Task.Yield();
                Massdex.SyncShark.Engine.Planner.SyncAction action =
                    new(Massdex.SyncShark.Engine.ActionKind.Upload, row);
                System.GC.KeepAlive(action);
            }
        }
    }

    private static class AsyncLocalFunctionOtherFixture
    {
        public static void Other(Massdex.SyncShark.Engine.Row row)
        {
            _ = Local();
            async System.Threading.Tasks.Task Local()
            {
                await System.Threading.Tasks.Task.Yield();
                Massdex.SyncShark.Engine.Planner.SyncAction action =
                    new(Massdex.SyncShark.Engine.ActionKind.Upload, row);
                System.GC.KeepAlive(action);
            }
        }
    }

    private static class RunnerListFixture
    {
        public static System.Collections.Generic.List<string> Use() => new();
    }

    // These bodies are inspected as IL, never executed against a filesystem or native library.
    private static class ScanExistsFixture
    {
        public static bool AttachScan(string path) => File.Exists(path);
        public static bool OpenScanRead(string path) => File.Exists(path);
    }

    private static class StoreScratchDeleteFixture
    {
        public static void OpenFolderPlan(string path) => File.Delete(path);
        public static void ResetScan(string path) => File.Delete(path);
    }

    private static class PlanDisposeFixture
    {
        public static void Dispose(string path) => File.Delete(path);
    }

    private static class PlanExistsFixture
    {
        public static bool Dispose(string path) => File.Exists(path);
    }

    private static class ForeignScratchMethodFixture
    {
        public static bool Other(string path)
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            return File.Exists(path);
        }
    }

    private static class WrongScratchOperationFixture
    {
        public static string OpenFolderPlan(string path) => File.ReadAllText(path);
        public static void OpenStore(string path) => Directory.Delete(path);
    }

    private static class StorageStreamFixture
    {
        public static Stream OpenFolderPlan(string path) => new FileStream(path, FileMode.Open);
    }

    private static class StorageDirectoryInfoFixture
    {
        public static object OpenFolderPlan(string path) => new DirectoryInfo(path);
    }

    private static class ForeignFileInfoFixture
    {
        public static bool Other(FileInfo info) => info.Exists;
    }

    private static class DatabaseDirectoryFixture
    {
        public static void OpenStore(string path) => Directory.CreateDirectory(path);
    }

    private static class NativeShellFixture
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName")]
        public static extern int SHCreateItemFromParsingName();
    }

    private static class DynamicNativeFixture
    {
        public static IntPtr Use(string library) => System.Runtime.InteropServices.NativeLibrary.Load(library);
    }

    private static class NativeOtherMethodFixture
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName")]
        public static extern int Other();
    }

    private static class NativeOtherLibraryFixture
    {
        [System.Runtime.InteropServices.DllImport("other.dll", EntryPoint = "SHCreateItemFromParsingName")]
        public static extern int SHCreateItemFromParsingName();
    }

    private static class NativeOtherEntryFixture
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "Other")]
        public static extern int SHCreateItemFromParsingName();
    }

    private static class NativeVolumeFixture
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW")]
        public static extern bool GetDiskFreeSpaceExW();
    }

    private static class NativeVolumeOtherMethodFixture
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW")]
        public static extern bool Other();
    }

    private static class NativeVolumeOtherLibraryFixture
    {
        [System.Runtime.InteropServices.DllImport("other.dll", EntryPoint = "GetDiskFreeSpaceExW")]
        public static extern bool GetDiskFreeSpaceExW();
    }

    private static class NativeVolumeOtherEntryFixture
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "Other")]
        public static extern bool GetDiskFreeSpaceExW();
    }

    private static class ForeignScratchStreamFixture
    {
        public static Stream Other(string path) => new FileStream(path, FileMode.Open);
    }

    // The page spool is the Stream Local opens; Dropbox only serializes into it and replays from it.
    private static class DropboxSpoolFixture
    {
        public static long Use(Stream spool)
        {
            spool.Position = 0;
            var buffer = new byte[8];
            var read = spool.Read(buffer, 0, buffer.Length);
            spool.SetLength(0);
            return read;
        }
    }

    private static class DropboxFileStreamFixture
    {
        public static Stream Use(string path) => new FileStream(path, FileMode.Open);
    }

    private static class DropboxFileDeleteFixture
    {
        public static void Use(string path) => File.Delete(path);
    }

    private static class DropboxFileExistsFixture
    {
        public static bool Use(string path) => File.Exists(path);
    }

    private static class DropboxPathConstructorFixture
    {
        public static object Use(string path) => new StreamReader(path);
    }

    private static class RunRetryProjectionFixture
    {
        public static object Use() =>
            new Massdex.SyncShark.Engine.IssueRetryView(Massdex.SyncShark.Engine.RetryPresentationState.Unavailable);
    }

    private static class RunEdgeReferenceFixture
    {
        public static object Use() => typeof(Massdex.SyncShark.Engine.Edge.IssuePresentation);
    }

    private static class ExecutorRunReferenceFixture
    {
        public static object Use() => typeof(Massdex.SyncShark.Engine.Run.RunLog);
    }

    private static class LocalSpaceReferenceFixture
    {
        public static object Use() => new[] { typeof(DownloadSpace), typeof(VolumeSpace) };
    }

    private static class RenameAsyncApprovedFixture
    {
        public static Action<Massdex.SyncShark.Engine.Planner.RenameDecision> RunAsync() => async _ =>
        {
            await System.Threading.Tasks.Task.Yield();
            System.GC.KeepAlive(new Massdex.SyncShark.Engine.Planner.RenameDecision(
                Massdex.SyncShark.Engine.Planner.RenameKind.Hold));
        };
    }

    private static class RenameAsyncOtherFixture
    {
        public static Action<Massdex.SyncShark.Engine.Planner.RenameDecision> Other() => async _ =>
        {
            await System.Threading.Tasks.Task.Yield();
            System.GC.KeepAlive(new Massdex.SyncShark.Engine.Planner.RenameDecision(
                Massdex.SyncShark.Engine.Planner.RenameKind.Hold));
        };
    }

    private static class FileInfoArgumentFixture
    {
        public static bool Use(string path) => Exists(new(path));

        private static bool Exists(System.IO.FileInfo info) => info.Exists;
    }

    private static class StateStoreOpenStoreFixture
    {
        public static bool OpenStore(string path) => new System.IO.FileInfo(path).Exists;
    }

    private static class EntrySizeCopyFixture
    {
        public static Massdex.SyncShark.Engine.Entry Use(Massdex.SyncShark.Engine.Entry entry) =>
            entry with { Size = 1 };
    }

    private static class EntryInferredCopyFixture
    {
        public static Massdex.SyncShark.Engine.Entry Use(Massdex.SyncShark.Engine.Entry entry)
        {
            var copy = entry;
            return copy with { Size = 1 };
        }
    }

    private sealed record Box(EntryHolder Entry);
    private sealed record EntryHolder(int Value);

    private static class UnrelatedEntryCopyFixture
    {
        public static EntryHolder Use(Box box) => box.Entry with { Value = 1 };
    }

    private static class EntryConstructorFixture
    {
        public static Massdex.SyncShark.Engine.Entry Use() =>
            new Massdex.SyncShark.Engine.Entry("path", false, 0, default, "id", "", "", null);
    }

    private static class EntryCopyFixture
    {
        public static Massdex.SyncShark.Engine.Entry Use(Massdex.SyncShark.Engine.Entry entry) =>
            entry with { Path = "path" };
    }

    // Construction fences: each form (explicit, target-typed, `with`) must be visible in IL.
    private static class SyncActionConstructorFixture
    {
        public static Massdex.SyncShark.Engine.Planner.SyncAction Use(Massdex.SyncShark.Engine.Row row) =>
            new Massdex.SyncShark.Engine.Planner.SyncAction(Massdex.SyncShark.Engine.ActionKind.None, row);
    }

    private static class SyncActionTargetTypedFixture
    {
        public static void Use(Massdex.SyncShark.Engine.Row row)
        {
            Massdex.SyncShark.Engine.Planner.SyncAction action = new(Massdex.SyncShark.Engine.ActionKind.None, row);
            System.GC.KeepAlive(action);
        }
    }

    private static class SyncActionWithFixture
    {
        public static Massdex.SyncShark.Engine.Planner.SyncAction Use(
            Massdex.SyncShark.Engine.Planner.SyncAction existing) =>
            existing with { Kind = Massdex.SyncShark.Engine.ActionKind.None };
    }

    private static class RenameDecisionConstructorFixture
    {
        public static Massdex.SyncShark.Engine.Planner.RenameDecision Use() =>
            new(Massdex.SyncShark.Engine.Planner.RenameKind.Hold);
    }

    private static class RenameDecisionWithFixture
    {
        public static Massdex.SyncShark.Engine.Planner.RenameDecision Use(
            Massdex.SyncShark.Engine.Planner.RenameDecision existing) => existing with { NoticeKind = null };
    }

    private static class IssueCountConstructorFixture
    {
        public static Massdex.SyncShark.Engine.Edge.Results.IssueCount Use() =>
            new(Massdex.SyncShark.Engine.TextKey.SkipLink, Massdex.SyncShark.Engine.IssueKind.Info, 1);
    }

    private static bool References(Uses uses, TypeId type) =>
        uses.SignatureTypes.Contains(type) || uses.IlTypes.Contains(type);

    private static IEnumerable<TypeId> All(Uses uses) => uses.SignatureTypes.Concat(uses.IlTypes);

    private static string? ModuleOf(string ns)
    {
        if (ns == RootNamespace) return RootModule;
        foreach (var module in Modules)
        {
            var prefix = RootNamespace + "." + module;
            if (ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal)) return module;
        }
        return null;
    }

    private static Uses Collect(MetadataReader reader, PEReader pe, TypeDefinition type)
    {
        // IL, signatures, base types, interfaces, fields, events and locals; not custom-attribute arguments or
        // generic constraints.
        // shortcut: attribute and constraint payloads are not scanned, extend when one carries a cross-module type.
        var uses = new Uses();
        var provider = new NameProvider(uses);

        AddType(reader, provider, type.BaseType, uses.SignatureTypes);
        foreach (var handle in type.GetInterfaceImplementations())
            AddType(reader, provider, reader.GetInterfaceImplementation(handle).Interface, uses.SignatureTypes);
        foreach (var handle in type.GetFields())
            reader.GetFieldDefinition(handle).DecodeSignature(provider, null);
        // A state machine or closure is attributed to the source method its generated name encodes.
        var typeName = reader.GetString(type.Name);
        var stateMethod = SourceMethod(typeName);
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var methodName = SourceMethod(reader.GetString(method.Name));
            if (methodName is "MoveNext" or "MoveNextAsync" && stateMethod != typeName) methodName = stateMethod;
            method.DecodeSignature(provider, null);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                var import = method.GetImport();
                uses.NativeImports.Add((methodName,
                    reader.GetString(reader.GetModuleReference(import.Module).Name), reader.GetString(import.Name)));
            }
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
                reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(provider, null);
            // A catch type appears only in the exception-region table, never as an IL token of its own.
            foreach (var region in body.ExceptionRegions)
                AddType(reader, provider, region.CatchType, uses.IlTypes);
            ReadIl(reader, uses, provider, body.GetILContent(), methodName);
        }
        foreach (var handle in type.GetEvents())
            AddType(reader, provider, reader.GetEventDefinition(handle).Type, uses.SignatureTypes);

        return uses;
    }

    private static string SourceMethod(string name)
    {
        var start = 0;
        while (start < name.Length && name[start] == '<') start++;
        var end = name.IndexOf('>', start);
        return start > 0 && end > start ? name[start..end] : name;
    }

    private static void ReadIl(MetadataReader reader, Uses uses, NameProvider provider, ImmutableArray<byte> il,
        string methodName)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            var key = (int)first;
            if (first == 0xFE) key = 0xFE00 | il[offset++];
            if (!OpCodesByValue.TryGetValue(key, out var opcode))
                throw new InvalidOperationException($"Unknown IL opcode 0x{key:X2}.");
            switch (opcode.OperandType)
            {
                case Emit.OperandType.InlineNone:
                    break;
                case Emit.OperandType.ShortInlineI:
                case Emit.OperandType.ShortInlineVar:
                case Emit.OperandType.ShortInlineBrTarget:
                    offset += 1;
                    break;
                case Emit.OperandType.InlineVar:
                    offset += 2;
                    break;
                case Emit.OperandType.InlineI8:
                case Emit.OperandType.InlineR:
                    offset += 8;
                    break;
                case Emit.OperandType.InlineSwitch:
                {
                    var count = ReadInt32(il, offset);
                    offset += 4 + (count * 4);
                    break;
                }
                case Emit.OperandType.InlineString:
                    uses.Strings.Add((methodName,
                        reader.GetUserString(MetadataTokens.UserStringHandle(ReadInt32(il, offset)))));
                    offset += 4;
                    break;
                case Emit.OperandType.InlineField:
                case Emit.OperandType.InlineMethod:
                case Emit.OperandType.InlineType:
                case Emit.OperandType.InlineTok:
                    AddToken(reader, uses, provider, ReadInt32(il, offset), methodName);
                    offset += 4;
                    break;
                case Emit.OperandType.InlineSig:
                    AddStandaloneSignature(reader, provider, ReadInt32(il, offset));
                    offset += 4;
                    break;
                default:
                    offset += 4;
                    break;
            }
        }
    }

    private static int ReadInt32(ImmutableArray<byte> il, int offset) =>
        il[offset] | (il[offset + 1] << 8) | (il[offset + 2] << 16) | (il[offset + 3] << 24);

    private static void AddToken(MetadataReader reader, Uses uses, NameProvider provider, int token, string methodName)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.IsNil) return;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                AddType(reader, provider, handle, uses.IlTypes);
                break;
            case HandleKind.MemberReference:
                AddMember(reader, uses, provider, (MemberReferenceHandle)handle, methodName);
                break;
            case HandleKind.MethodDefinition:
            {
                // Engine-internal calls use MethodDef tokens, so record the member name too (construction fences).
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var declaring = TypeOf(reader, method.GetDeclaringType());
                Add(uses.IlTypes, declaring);
                if (!string.IsNullOrEmpty(declaring.Namespace))
                {
                    var name = reader.GetString(method.Name);
                    uses.Members.Add((declaring, name));
                    uses.Calls.Add((methodName, declaring, name));
                }
                break;
            }
            case HandleKind.FieldDefinition:
                Add(uses.IlTypes, TypeOf(reader, reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType()));
                break;
            case HandleKind.MethodSpecification:
            {
                // The generic arguments live only in the specification's instantiation blob, not in its IL token.
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                AddToken(reader, uses, provider, MetadataTokens.GetToken(specification.Method), methodName);
                var arguments = specification.DecodeSignature(provider, null).ToArray();
                foreach (var argument in arguments)
                    Add(uses.IlTypes, argument);
                if (MethodOf(reader, specification.Method) is { } call)
                    uses.GenericCalls.Add((call.Parent, call.Name, arguments));
                break;
            }
        }
    }

    private static (TypeId Parent, string Name)? MethodOf(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            return (TypeOf(reader, method.GetDeclaringType()), reader.GetString(method.Name));
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
                return (ReferenceName(reader, (TypeReferenceHandle)member.Parent), reader.GetString(member.Name));
            if (member.Parent.Kind == HandleKind.TypeDefinition)
                return (TypeOf(reader, (TypeDefinitionHandle)member.Parent), reader.GetString(member.Name));
        }
        return null;
    }

    private static void AddMember(MetadataReader reader, Uses uses, NameProvider provider, MemberReferenceHandle handle,
        string methodName)
    {
        var member = reader.GetMemberReference(handle);
        TypeId parent;
        switch (member.Parent.Kind)
        {
            case HandleKind.TypeDefinition:
                parent = TypeOf(reader, (TypeDefinitionHandle)member.Parent);
                break;
            case HandleKind.TypeReference:
                parent = ReferenceName(reader, (TypeReferenceHandle)member.Parent);
                break;
            case HandleKind.TypeSpecification:
                parent = reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(provider, null);
                break;
            case HandleKind.MethodDefinition:
                parent = TypeOf(reader, reader.GetMethodDefinition((MethodDefinitionHandle)member.Parent).GetDeclaringType());
                break;
            default:
                return;
        }
        Add(uses.IlTypes, parent);
        if (string.IsNullOrEmpty(parent.Namespace)) return;
        var name = reader.GetString(member.Name);
        uses.Members.Add((parent, name));
        uses.Calls.Add((methodName, parent, name));
        if (name == ".ctor" && (parent == StreamReaderType || parent == StreamWriterType) &&
            IsPathConstructor(provider, member))
            uses.PathConstructors.Add((parent, name));
    }

    // The path constructors of StreamReader and StreamWriter take a string first; the stream constructors take a Stream.
    private static bool IsPathConstructor(NameProvider provider, MemberReference member)
    {
        var signature = member.DecodeMethodSignature(provider, null);
        return signature.ParameterTypes.Length > 0 && signature.ParameterTypes[0] == StringType;
    }

    private static void AddStandaloneSignature(MetadataReader reader, NameProvider provider, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.StandaloneSignature) return;
        reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(provider, null);
    }

    private static void AddType(MetadataReader reader, NameProvider provider, EntityHandle handle, HashSet<TypeId> sink)
    {
        if (handle.IsNil) return;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                Add(sink, TypeOf(reader, (TypeDefinitionHandle)handle));
                break;
            case HandleKind.TypeReference:
                Add(sink, ReferenceName(reader, (TypeReferenceHandle)handle));
                break;
            case HandleKind.TypeSpecification:
                Add(sink, reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, null));
                break;
        }
    }

    private static void Add(HashSet<TypeId> sink, TypeId type)
    {
        if (!string.IsNullOrEmpty(type.Namespace)) sink.Add(type);
    }

    private static TypeId TypeOf(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            var outer = TypeOf(reader, declaring);
            return outer with { Name = outer.Name + "+" + name };
        }
        return new TypeId(reader.GetString(type.Namespace), name);
    }

    private static TypeId ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = ReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope);
            return outer with { Name = outer.Name + "+" + name };
        }
        return new TypeId(reader.GetString(type.Namespace), name);
    }

    private static string Show(TypeId type) => type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name;

    private readonly record struct TypeId(string Namespace, string Name);

    private sealed class Uses
    {
        public HashSet<TypeId> SignatureTypes { get; } = [];
        public HashSet<TypeId> IlTypes { get; } = [];
        public List<(string Method, string Value)> Strings { get; } = [];
        public List<(TypeId Parent, string Member)> Members { get; } = [];
        public List<(TypeId Parent, string Member)> PathConstructors { get; } = [];
        public List<(string Method, TypeId Parent, string Member)> Calls { get; } = [];
        public List<(string Method, string Library, string EntryPoint)> NativeImports { get; } = [];
        public List<(TypeId Generic, TypeId[] Arguments)> Instantiations { get; } = [];
        public List<(TypeId Parent, string Member, TypeId[] Arguments)> GenericCalls { get; } = [];
    }

    private sealed class NameProvider(Uses uses) : ISignatureTypeProvider<TypeId, object?>
    {
        private static readonly TypeId None = new("", "");

        public TypeId GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            typeCode == PrimitiveTypeCode.String ? StringType : None;

        public TypeId GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = TypeOf(reader, handle);
            Add(uses.SignatureTypes, type);
            return type;
        }

        public TypeId GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = ReferenceName(reader, handle);
            Add(uses.SignatureTypes, type);
            return type;
        }

        public TypeId GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public TypeId GetArrayType(TypeId elementType, ArrayShape shape) => elementType;
        public TypeId GetByReferenceType(TypeId elementType) => elementType;
        public TypeId GetFunctionPointerType(MethodSignature<TypeId> signature) => None;
        public TypeId GetGenericInstantiation(TypeId genericType, ImmutableArray<TypeId> typeArguments)
        {
            uses.Instantiations.Add((genericType, typeArguments.ToArray()));
            return genericType;
        }
        public TypeId GetGenericMethodParameter(object? genericContext, int index) => None;
        public TypeId GetGenericTypeParameter(object? genericContext, int index) => None;
        public TypeId GetModifiedType(TypeId modifier, TypeId unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeId GetPinnedType(TypeId elementType) => elementType;
        public TypeId GetPointerType(TypeId elementType) => elementType;
        public TypeId GetSZArrayType(TypeId elementType) => elementType;
    }
}
