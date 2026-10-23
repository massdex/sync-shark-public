using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.EngineChecks;

internal sealed class DependencyChecks
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private readonly Assembly core;
    private readonly Dictionary<Type, string> owners;
    private readonly SortedDictionary<string, SortedSet<string>> failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> framework;
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private DependencyChecks(Assembly core, Dictionary<Type, string> owners)
    {
        this.core = core;
        this.owners = owners;
        // Identity comes from the shared framework directory, never a System.* namespace guess
        // or the TPA list (which also contains third-party application packages).
        framework = Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static int Run(bool reportOnly = false)
    {
        var assembly = typeof(V2.ItemState).Assembly;
        var check = new DependencyChecks(assembly, new());
        try
        {
            check.Guard("Acceptance probes", Probes);
            var root = EngineLayoutSourceMap.FindRoot();
            var engine = Path.Combine(root, "src", "SyncShark.Core", "V2");
            var types = assembly.GetTypes();
            var map = new EngineLayoutSourceMap(Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
                .Select(p => new EngineLayoutSourceMap.Source(Path.GetRelativePath(engine, p), File.ReadAllText(p))), types, check.Error);
            foreach (var pair in map.Owners) check.owners.Add(pair.Key, pair.Value);
            foreach (var type in types)
            {
                var top = Top(type);
                if (check.owners.TryGetValue(top, out var module)) check.Inspect(type, module);
                else if (Neutral(top)) check.Inspect(type, "Neutral");
                else if (type.Namespace?.StartsWith("Massdex.SyncShark.V2", StringComparison.Ordinal) == true)
                    check.Error("Ownership", $"{type.FullName}: compiled engine type has no source owner");
            }
            check.Erased(map, types);
        }
        catch (Exception e) { check.Error("Inspection", $"{e.GetType().Name}: {e.Message}"); }
        var total = check.failures.Values.Sum(v => v.Count);
        if (reportOnly) Console.WriteLine($"ENGINE-LAYOUT-METER total={total}");
        foreach (var (edge, details) in check.failures)
        {
            if (reportOnly) Console.WriteLine($"[{edge}] {details.Count} violation(s)");
            else Console.Error.WriteLine($"[{edge}] {details.Count} violation(s)");
            foreach (var detail in details)
            {
                if (reportOnly) Console.WriteLine("  " + detail);
                else Console.Error.WriteLine("  " + detail);
            }
        }
        Console.WriteLine($"Engine layout: {total} violation(s), {check.failures.Count} group(s).");
        return reportOnly || check.failures.Count == 0 ? 0 : 1;
    }

    private void Error(string edge, string detail)
    {
        if (!failures.TryGetValue(edge, out var rows)) failures[edge] = rows = new(StringComparer.Ordinal);
        rows.Add(detail);
    }

    private static Type Top(Type type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }

    private static bool Neutral(Type type) => type.Name.StartsWith('<') &&
        type.IsDefined(typeof(CompilerGeneratedAttribute), false);

    private static bool Allowed(string from, string to) => from == to || to == "Contracts" ||
        from == "Edge" || from == "Run" && to != "Edge";

    private void Reference(Type type, string from, string site, MethodBase? caller = null, string? member = null)
    {
        if (type.HasElementType) { Reference(type.GetElementType()!, from, site, caller, member); return; }
        if (type.IsGenericParameter) return; // Constraints are traversed at declarations.
        if (type.IsFunctionPointer)
        {
            Reference(type.GetFunctionPointerReturnType(), from, site, caller);
            foreach (var p in type.GetFunctionPointerParameterTypes()) Reference(p, from, site, caller);
            foreach (var c in type.GetFunctionPointerCallingConventions()) Reference(c, from, site, caller);
            return;
        }
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) Reference(argument, from, site, caller);
        if (type.DeclaringType != null) Reference(type.DeclaringType, from, site, caller);
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var ns = definition.Namespace ?? "";
        // SQL belongs Storage (engineering standards sections 1.1/6.2).
        // This is independent of assembly identity and applies even to catch-only references.
        if (from != "Storage" && (ns == "Microsoft.Data" || ns.StartsWith("Microsoft.Data.", StringComparison.Ordinal)))
        { Error(from + " -> SQL", $"{site} -> {type.FullName}{Suffix(member)}"); return; }
        if (owners.TryGetValue(Top(definition), out var target))
        {
            if (from == "Neutral" || !Allowed(from, target)) Error(from + " -> " + target, $"{site} -> {type.FullName}{Suffix(member)}");
            return;
        }
        if (definition.Assembly == core)
        {
            if (Neutral(Top(definition))) return;
            var legacyAllowed = from is "Local" or "Dropbox" or "Run" or "Edge" ||
                from == "Execution" && Top(definition).FullName == "Massdex.SyncShark.SyncTransferResourceController";
            if (!legacyAllowed) Error(from + " -> Legacy", $"{site} -> {type.FullName}{Suffix(member)}");
            return;
        }
        if (!framework.Contains(definition.Assembly.Location))
        {
            if (from is "Contracts" or "Planning" or "Neutral")
                Error(from + " -> Package", $"{site} -> {type.FullName} ({definition.Assembly.GetName().Name})");
            return;
        }
        if (from is not ("Planning" or "Neutral")) return;
        var forbidden = ns == "System.IO" || ns.StartsWith("System.IO.", StringComparison.Ordinal) ||
            ns == "System.Net" || ns.StartsWith("System.Net.", StringComparison.Ordinal) ||
            ns == "System.Data" || ns.StartsWith("System.Data.", StringComparison.Ordinal) ||
            type == typeof(TimeProvider) || type == typeof(System.Diagnostics.Stopwatch) ||
            type == typeof(Environment) && !IteratorThreadId(caller, member) ||
            (type == typeof(DateTime) || type == typeof(DateTimeOffset)) && member is "get_Now" or "get_UtcNow" or "get_Today";
        if (forbidden) Error(from + " -> Effects", $"{site} -> {type.FullName}{Suffix(member)}");
    }

    private static string Suffix(string? member) => member == null ? "" : "." + member;

    private static bool IteratorThreadId(MethodBase? caller, string? member)
    {
        if (member != "get_CurrentManagedThreadId" || caller?.DeclaringType is not { IsNested: true } type ||
            !type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
        var enumerable = type.GetInterfaces().Any(i => i == typeof(System.Collections.IEnumerable) ||
            i == typeof(System.Collections.IEnumerator) || i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IEnumerable<>) || i.GetGenericTypeDefinition() == typeof(IEnumerator<>) ||
             i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>) || i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>)));
        var name = caller.Name.Split('.').Last();
        return enumerable && (caller.IsConstructor || name is "GetEnumerator" or "GetAsyncEnumerator");
    }

    private void Guard(string site, Action action)
    {
        try { action(); }
        catch (Exception e) { Error("Inspection", $"{site}: {e.GetType().Name}: {e.Message}"); }
    }

    private void Inspect(Type type, string module)
    {
        var site = type.FullName ?? type.Name;
        Guard(site, () =>
        {
            if (type.BaseType != null) Reference(type.BaseType, module, site);
            foreach (var i in type.GetInterfaces()) Reference(i, module, site);
            Constraints(type.GetGenericArguments(), module, site);
            Attributes(type.GetCustomAttributesData(), module, site);
        });
        foreach (var member in type.GetMembers(Declared))
            Guard(site + "." + member.Name, () => Member(member, module, site + "." + member.Name, null, true));
        // Type initializers are not consistently returned by GetMembers across runtimes.
        if (type.TypeInitializer is { } initializer)
            Guard(site + ".cctor", () => Member(initializer, module, site + ".cctor", null, true));
    }

    private void Constraints(IEnumerable<Type> arguments, string module, string site)
    {
        foreach (var argument in arguments.Where(t => t.IsGenericParameter))
        {
            foreach (var constraint in argument.GetGenericParameterConstraints()) Reference(constraint, module, site);
            Attributes(argument.GetCustomAttributesData(), module, site);
        }
    }

    private void Parameter(ParameterInfo parameter, string module, string site, MethodBase? caller)
    {
        Reference(parameter.ParameterType, module, site, caller);
        foreach (var modifier in parameter.GetRequiredCustomModifiers().Concat(parameter.GetOptionalCustomModifiers())) Reference(modifier, module, site, caller);
        Attributes(parameter.GetCustomAttributesData(), module, site);
    }

    private void Member(MemberInfo member, string module, string site, MethodBase? caller, bool body = false)
    {
        if (member is Type type) { Reference(type, module, site, caller); return; }
        if (member.DeclaringType != null) Reference(member.DeclaringType, module, site, caller, member.Name);
        // Inspect attributes on declarations, not recursively on framework callees.
        if (body) Attributes(member.GetCustomAttributesData(), module, site);
        switch (member)
        {
            case FieldInfo field:
                Reference(field.FieldType, module, site, caller);
                foreach (var modifier in field.GetRequiredCustomModifiers().Concat(field.GetOptionalCustomModifiers())) Reference(modifier, module, site, caller);
                break;
            case PropertyInfo property:
                Reference(property.PropertyType, module, site, caller);
                foreach (var parameter in property.GetIndexParameters()) Parameter(parameter, module, site, caller);
                break;
            case EventInfo evt:
                if (evt.EventHandlerType != null) Reference(evt.EventHandlerType, module, site, caller);
                break;
            case MethodBase method:
                if (method is MethodInfo info)
                {
                    Parameter(info.ReturnParameter, module, site, caller);
                    foreach (var argument in info.GetGenericArguments()) Reference(argument, module, site, caller);
                    if (body) Constraints(info.GetGenericArguments(), module, site);
                }
                foreach (var parameter in method.GetParameters()) Parameter(parameter, module, site, caller);
                if (body) Body(method, module, site);
                break;
        }
    }

    private void Attributes(IEnumerable<CustomAttributeData> attributes, string module, string site)
    {
        foreach (var attribute in attributes)
        {
            Reference(attribute.AttributeType, module, site);
            Member(attribute.Constructor, module, site, null);
            foreach (var value in attribute.ConstructorArguments) AttributeValue(value, module, site);
            foreach (var value in attribute.NamedArguments)
            { Member(value.MemberInfo, module, site, null); AttributeValue(value.TypedValue, module, site); }
        }
    }

    private void AttributeValue(CustomAttributeTypedArgument value, string module, string site)
    {
        Reference(value.ArgumentType, module, site);
        if (value.Value is Type type) Reference(type, module, site);
        if (value.Value is IEnumerable<CustomAttributeTypedArgument> values)
            foreach (var child in values) AttributeValue(child, module, site);
    }

    private void Body(MethodBase method, string module, string site)
    {
        var body = method.GetMethodBody();
        if (body == null)
        {
            if (!method.IsAbstract && (method.Attributes & MethodAttributes.PinvokeImpl) == 0 &&
                (method.GetMethodImplementationFlags() & (MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall)) == 0)
                Error("Inspection", site + ": missing method body");
            return;
        }
        foreach (var local in body.LocalVariables) Reference(local.LocalType, module, site, method);
        foreach (var clause in body.ExceptionHandlingClauses)
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType != null)
                Reference(clause.CatchType, module, site, method);
        var bytes = body.GetILAsByteArray() ?? throw new InvalidOperationException("Missing IL bytes");
        Decode(bytes, (offset, operand, token) => Guard($"{site} IL_{offset:x4}", () =>
        {
            var context = method.DeclaringType?.GetGenericArguments();
            var arguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            if (operand == OperandType.InlineSig)
                throw new NotSupportedException("Standalone signature operand is unsupported; cannot certify dependencies");
            if (operand == OperandType.InlineString) { _ = method.Module.ResolveString(token); return; }
            var resolved = method.Module.ResolveMember(token, context, arguments) ?? throw new InvalidOperationException($"Unresolved token {token:x8}");
            Member(resolved, module, site + $" IL_{offset:x4}", method);
        }));
    }

    private static void Decode(byte[] bytes, Action<int, OperandType, int> token)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var offset = i;
            short code = bytes[i++];
            if (code == 0xfe)
            { if (i == bytes.Length) throw new InvalidOperationException("Truncated two-byte opcode"); code = (short)(0xfe00 | bytes[i++]); }
            if (!Opcodes.TryGetValue(code, out var opcode)) throw new InvalidOperationException($"Unknown IL opcode {code:x4}");
            var operand = opcode.OperandType;
            var size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                _ => throw new NotSupportedException($"Unsupported operand {operand}")
            };
            if (size == -1)
            {
                if (i + 4 > bytes.Length) throw new InvalidOperationException("Truncated switch count");
                var count = BitConverter.ToInt32(bytes, i);
                if (count < 0 || count > (bytes.Length - i - 4) / 4) throw new InvalidOperationException("Invalid switch table");
                size = 4 + count * 4;
            }
            if (size > bytes.Length - i) throw new InvalidOperationException("Truncated IL operand");
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or
                OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType)
                token(offset, operand, BitConverter.ToInt32(bytes, i));
            i += size;
        }
    }

    // Bounded ledger, not a violation exemption list. All references to these families
    // are resolved against the very same compiled identities and source owners as IL.
    private static readonly HashSet<string> ErasedFamilies = new(StringComparer.Ordinal)
    {
        "InterruptedDelete", "SqliteStateStore", "ArtifactPaths", "LocalItems", "DropboxWriter",
        "DeleteSourceGuard", "DeleteSource", "EffectResolver", "EffectReasons", "ExecutionReasons",
        "RecoveryRestoreHold", "RunOutcomes", "NoticeChoices", "NoticeVocabulary", "JournalSizing",
        "JournalPolicy", "JournalSizingState", "VerificationBudget", "VerificationPolicy", "DefenderExclusions",
        "DefenderReview", "StorageNotices", "OwnedPointerProof", "QuarantineFailure",
        "SyncProviders", "ProviderIds", "ProviderTransientFailure", "SyncIgnoreKind", "PilotEntryKind",
        "DropboxOAuthError", "SyncCloudFileAvailability"
    };

    private void Erased(EngineLayoutSourceMap map, Type[] types)
    {
        var vocabulary = types.Where(t => !t.IsNested && ErasedFamilies.Contains(t.Name))
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var (source, module) in map.Sources)
        {
            var tokens = EngineLayoutSourceMap.Tokens(source.Text);
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 3 < tokens.Count; i++)
            {
                if (tokens[i] != "using") continue;
                var end = tokens.IndexOf(";", i);
                if (end < 0) continue;
                if (tokens[i + 2] == "=")
                {
                    var target = tokens.GetRange(i + 3, end - i - 3).LastOrDefault(vocabulary.ContainsKey);
                    if (target != null) aliases[tokens[i + 1]] = target;
                }
            }
            for (var i = 0; i < tokens.Count; i++)
            {
                var name = aliases.GetValueOrDefault(tokens[i], tokens[i]);
                if (!vocabulary.TryGetValue(name, out var targets)) continue;
                // Type declarations are ownership, not references. Ordinary nameof(parameter)
                // is outside the bounded type-name ledger and cannot create an edge.
                if (i > 0 && tokens[i - 1] is "class" or "struct" or "record" or "enum" or "interface") continue;
                if (targets.Length != 1) { Error("Inspection", source.Path + ": ambiguous erased owner " + name); continue; }
                Reference(targets[0], module, source.Path + " [erased " + name + "]");
            }
        }
    }

    private static void Probes()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Checker probe failed: " + message); }
        var core = typeof(V2.ItemState).Assembly;
        foreach (var module in EngineLayoutSourceMap.Modules)
        {
            var probe = new DependencyChecks(core, new());
            probe.owners[typeof(DependencyChecks)] = module;
            probe.Body(typeof(DependencyChecks).GetMethod(nameof(SqlCatchProbe), Declared)!, module, "SQL catch fixture");
            Require(probe.failures.ContainsKey(module + " -> SQL") == (module != "Storage"), module + " SQL ownership");
            if (module == "Storage") Require(probe.failures.Count == 0, "Storage SQL catch is permitted");
        }
        foreach (var from in new[] { "Storage", "Local", "Dropbox", "Planning", "Execution" })
            foreach (var to in new[] { "Storage", "Local", "Dropbox", "Planning", "Execution" })
                Require(Allowed(from, to) == (from == to), "peer matrix");
        var ownershipErrors = new List<string>();
        var source = new EngineLayoutSourceMap.Source("DependencyChecks.cs", "internal class DependencyChecks {} ");
        _ = new EngineLayoutSourceMap([source], [], (_, e) => ownershipErrors.Add(e));
        Require(ownershipErrors.Count != 0, "excluded/zero candidate");
        ownershipErrors.Clear();
        _ = new EngineLayoutSourceMap([source], [typeof(DependencyChecks), typeof(DependencyChecks)], (_, e) => ownershipErrors.Add(e));
        Require(ownershipErrors.Count != 0, "multiple candidates");
        ownershipErrors.Clear();
        _ = new EngineLayoutSourceMap([source, new("Local/DependencyChecks.Part.cs", "partial class DependencyChecks {}")],
            [typeof(DependencyChecks)], (_, e) => ownershipErrors.Add(e));
        Require(ownershipErrors.Any(e => e.Contains("crosses folders")), "cross-folder partial");
        Require(EngineLayoutSourceMap.Declarations("﻿namespace A; [Flags] public enum Flag { A } ").SequenceEqual(["Flag"]), "flags/BOM");
        Require(EngineLayoutSourceMap.Declarations("namespace A; public readonly record struct Row(int X);").SequenceEqual(["Row"]), "record struct");
        var mapped = new DependencyChecks(core, new() { [typeof(V2.ItemState)] = "Local" });
        mapped.Reference(typeof(List<V2.ItemState>), "Planning", "generic-only fixture");
        Require(mapped.failures.ContainsKey("Planning -> Local"), "generic argument ownership");
        var attribute = new DependencyChecks(core, new() { [typeof(V2.ItemState)] = "Local" });
        attribute.AttributeValue(new CustomAttributeTypedArgument(typeof(Type), typeof(V2.ItemState)), "Planning", "attribute-only fixture");
        Require(attribute.failures.ContainsKey("Planning -> Local"), "attribute typed argument");
        var legacyNamespace = new DependencyChecks(core, new() { [typeof(SyncProviders)] = "Contracts" });
        legacyNamespace.Reference(typeof(SyncProviders), "Planning", "legacy-namespaced engine fixture");
        Require(legacyNamespace.failures.Count == 0, "namespace must not override source owner");
        legacyNamespace.owners[typeof(SyncProviders)] = "Local";
        legacyNamespace.Reference(typeof(SyncProviders), "Planning", "legacy-namespaced peer fixture");
        Require(legacyNamespace.failures.ContainsKey("Planning -> Local"), "legacy namespace cannot hide a peer edge");
        var erased = new DependencyChecks(core, new() { [typeof(V2.ExecutionReasons)] = "Execution" });
        var erasedMap = new EngineLayoutSourceMap([new("Probe.cs", "class Probe { string X = ExecutionReasons.ConflictCopyBlocked; }")], [], (_, _) => { });
        erased.Erased(erasedMap, [typeof(V2.ExecutionReasons)]);
        Require(erased.failures.ContainsKey("Contracts -> Execution"), "erased const");
        foreach (var text in new[]
        {
            "using E = Massdex.SyncShark.V2.ExecutionReasons; class Probe { string X = E.ConflictCopyBlocked; }",
            "using static Massdex.SyncShark.V2.ExecutionReasons; class Probe { string X = ConflictCopyBlocked; }",
            "class Probe { string X = nameof(Massdex.SyncShark.V2.ExecutionReasons.ConflictCopyBlocked); }"
        })
        {
            var supplement = new DependencyChecks(core, new() { [typeof(V2.ExecutionReasons)] = "Execution" });
            var sources = new EngineLayoutSourceMap([new("Probe.cs", text)], [], (_, _) => { });
            supplement.Erased(sources, [typeof(V2.ExecutionReasons)]);
            Require(supplement.failures.ContainsKey("Contracts -> Execution"), "qualified/alias/static/nameof erased reference");
        }
        var literalTokens = EngineLayoutSourceMap.Tokens("// ExecutionReasons\n/* ExecutionReasons */ class Probe { string X = \"ExecutionReasons\"; string F(int parameter) => nameof(parameter); }");
        Require(!literalTokens.Contains("ExecutionReasons"), "comments and strings are not references");
        var broken = false;
        try { Decode([0x28, 1], (_, _, _) => { }); } catch (InvalidOperationException) { broken = true; }
        Require(broken, "truncated operand");
        var unresolved = new DependencyChecks(core, new());
        unresolved.Guard("unresolved operand fixture", () => Decode([0x28, 0xff, 0xff, 0xff, 0x7f],
            (_, _, token) => _ = core.ManifestModule.ResolveMember(token)));
        Require(unresolved.failures.ContainsKey("Inspection"), "unresolved operand");
        var anonymous = new { Value = 1 }.GetType();
        Require(Neutral(anonymous), "neutral compiler helper");
        var neutral = new DependencyChecks(anonymous.Assembly, new());
        neutral.Inspect(anonymous, "Neutral");
        Require(neutral.failures.Count == 0, "neutral helper traversal");
        var iterator = IteratorProbe().GetType();
        var generated = new DependencyChecks(core, new() { [typeof(DependencyChecks)] = "Planning" });
        generated.Inspect(iterator, "Planning");
        Require(generated.failures.Count == 0, "ordinary iterator traversal");
        var moveNext = iterator.GetMethod("MoveNext", Declared)!;
        generated.Reference(typeof(Environment), "Planning", "iterator user code fixture", moveNext, "get_CurrentManagedThreadId");
        Require(generated.failures.ContainsKey("Planning -> Effects"), "MoveNext is not exempt");
    }

    private static void SqlCatchProbe(Action action)
    {
        try { action(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { throw; }
    }

    private static IEnumerable<int> IteratorProbe() { yield return 1; }
}
