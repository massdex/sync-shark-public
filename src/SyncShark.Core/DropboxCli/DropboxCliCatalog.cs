using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Loads the embedded Dropbox command data and exposes the registry rows and lookups.</summary>
public static class DropboxCliCatalog
{
    private const string Requires =
        "the running application or the exclusive product lock and the Dropbox provider";

    private static readonly Model Data = Model.Load();

    /// <summary>The 17 Dropbox registry rows, with RequiresYes computed from their routes.</summary>
    public static readonly IReadOnlyList<SyncCliCommandRow> Rows = Data.Verbs.Select(verb => new SyncCliCommandRow(
        Command: verb.Command,
        Usage: verb.Usage,
        Kind: SyncCliRequestKind.State,
        Approval: verb.RequiresYes ? "yes" : "none",
        Effect: verb.Effect,
        Requires: Requires,
        Mode: "direct|running-application",
        Options: string.Join(',', verb.Options.Select(option => Data.Flags[option])),
        LiveVerificationRequired: true,
        Mutating: verb.RequiresYes,
        RequiresYes: verb.RequiresYes)).ToArray();

    /// <summary>The value-taking option flags the shared parser does not already carry.</summary>
    public static readonly string[] ValueOptions =
    [
        "--root", "--cursor", "--out", "--in", "--mode", "--rev", "--client-modified", "--batch", "--to",
        "--size", "--format", "--route", "--body"
    ];

    /// <summary>The command group name.</summary>
    public static string Group => Data.Group;

    /// <summary>Related command shapes named by the group.</summary>
    public static IReadOnlyList<string> SeeAlso => Data.SeeAlso;

    /// <summary>The flag text for one option.</summary>
    public static string Flag(DropboxCliOption option) => Data.Flags[option];

    /// <summary>The verb definition for an exact command, or null when the command is not in the group.</summary>
    public static VerbDefinition? Verb(string command) =>
        Data.ByCommand.TryGetValue(command, out var verb) ? verb : null;

    /// <summary>True when this exact command belongs to the Dropbox group.</summary>
    public static bool Owns(string command) => Data.ByCommand.ContainsKey(command);

    /// <summary>The formatted message for a key, with each {name} placeholder replaced.</summary>
    public static string Message(DropboxCliMessage key, params (string Name, string Value)[] values) =>
        Fill(Data.Messages[key], values);

    /// <summary>The formatted human-output template for a key, with each {name} placeholder replaced.</summary>
    public static string View(DropboxCliView key, params (string Name, string Value)[] values) =>
        Fill(Data.Views[key], values);

    private static string Fill(string text, (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }

    /// <summary>How one option's value is read and templated.</summary>
    public enum ValueKind
    {
        Id,
        Text,
        Int,
        Flag,
        Json,
        DropboxPath,
        UtcTime,
        LocalFile,
        Namespace
    }

    /// <summary>One Dropbox verb as loaded from the embedded data.</summary>
    public sealed record VerbDefinition(DropboxCliVerb Key, string Command, string Usage, string Effect,
        IReadOnlyList<DropboxCliOption> Options, IReadOnlyList<DropboxCliOption> Required,
        IReadOnlyList<IReadOnlyList<DropboxCliOption>> OneOf, DropboxCliHandler Handler,
        IReadOnlyList<CallDefinition> Calls, OutputDefinition? Output, bool RequiresYes)
    {
        /// <summary>The first call whose <c>when</c> option is present, or that has no <c>when</c>.</summary>
        public CallDefinition? FirstCall(Func<DropboxCliOption, bool> present) =>
            Calls.FirstOrDefault(call => call.When is null || present(call.When.Value));

        /// <summary>The output shape: the call's own, else the verb's.</summary>
        public OutputDefinition? OutputFor(CallDefinition call) => call.Output ?? Output;

        /// <summary>The one call with the given purpose; put and changes compose several named routes.</summary>
        public CallDefinition Call(DropboxCliCall key) =>
            Calls.FirstOrDefault(call => call.Key == key)
            ?? throw new InvalidDataException("The verb '" + Command + "' has no '" + key + "' call.");
    }

    /// <summary>One route call inside a verb, with its optional argument template and batch poll.</summary>
    public sealed record CallDefinition(DropboxCliCall? Key, DropboxCliOption? When, DropboxRoute Route,
        JsonElement? Body, PollDefinition? Poll, OutputDefinition? Output);

    /// <summary>How a launched async batch job is polled until it leaves the in-progress state.</summary>
    public sealed record PollDefinition(DropboxRoute Route, string JobField);

    /// <summary>How a verb's or call's result is mapped to the output data.</summary>
    public sealed record OutputDefinition(string Kind, string? Items, string? EntryPath, string? At, bool IsDeleted);

    private sealed record Model(string Group, IReadOnlyList<string> SeeAlso,
        IReadOnlyDictionary<DropboxCliOption, string> Flags, IReadOnlyDictionary<DropboxCliOption, ValueKind> Kinds,
        IReadOnlyDictionary<string, VerbDefinition> ByCommand,
        IReadOnlyDictionary<DropboxCliMessage, string> Messages, IReadOnlyDictionary<DropboxCliView, string> Views,
        IReadOnlyList<VerbDefinition> Verbs)
    {
        internal static Model Load()
        {
            using var stream = typeof(DropboxCliCatalog).Assembly
                .GetManifestResourceStream("SyncShark.DropboxCli.json")
                ?? throw new InvalidOperationException("The embedded Dropbox command data is missing.");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var flags = new Dictionary<DropboxCliOption, string>();
            var kinds = new Dictionary<DropboxCliOption, ValueKind>();
            foreach (var option in root.GetProperty("options").EnumerateArray())
            {
                var key = Enum.Parse<DropboxCliOption>(option.GetProperty("key").GetString()!, ignoreCase: false);
                flags[key] = option.GetProperty("flag").GetString()!;
                kinds[key] = ParseKind(option.GetProperty("value").GetString()!);
            }

            var verbs = new List<VerbDefinition>();
            var byCommand = new Dictionary<string, VerbDefinition>(StringComparer.Ordinal);
            foreach (var verb in root.GetProperty("verbs").EnumerateArray())
            {
                var definition = ReadVerb(verb);
                verbs.Add(definition);
                byCommand[definition.Command] = definition;
            }

            var messages = new Dictionary<DropboxCliMessage, string>();
            foreach (var message in root.GetProperty("messages").EnumerateObject())
                messages[Enum.Parse<DropboxCliMessage>(message.Name)] = message.Value.GetString()!;

            var views = new Dictionary<DropboxCliView, string>();
            foreach (var view in root.GetProperty("views").EnumerateObject())
                views[Enum.Parse<DropboxCliView>(view.Name)] = view.Value.GetString()!;

            var seeAlso = root.GetProperty("seeAlso").EnumerateArray().Select(item => item.GetString()!).ToArray();
            return new Model(root.GetProperty("group").GetString()!, seeAlso, flags, kinds, byCommand, messages,
                views, verbs);
        }

        private static VerbDefinition ReadVerb(JsonElement verb)
        {
            var options = ReadOptions(verb.GetProperty("options"));
            var calls = new List<CallDefinition>();
            foreach (var call in verb.GetProperty("calls").EnumerateArray())
            {
                var routeName = call.GetProperty("route").GetString()!;
                var route = DropboxRouteTable.Embedded.Find(routeName)
                    ?? throw new InvalidDataException("The verb '" + verb.GetProperty("command").GetString() +
                        "' names the unknown route '" + routeName + "'.");
                PollDefinition? poll = null;
                if (call.TryGetProperty("poll", out var pollElement))
                    poll = new PollDefinition(
                        DropboxRouteTable.Embedded.Find(pollElement.GetProperty("route").GetString()!)
                            ?? throw new InvalidDataException("A batch check route is missing from the table."),
                        pollElement.GetProperty("jobField").GetString()!);
                calls.Add(new CallDefinition(
                    call.TryGetProperty("key", out var callKey)
                        ? Enum.Parse<DropboxCliCall>(callKey.GetString()!) : null,
                    call.TryGetProperty("when", out var when) ? Enum.Parse<DropboxCliOption>(when.GetString()!) : null,
                    route,
                    call.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null
                        ? body.Clone() : null,
                    poll,
                    call.TryGetProperty("output", out var output) ? ReadOutput(output) : null));
            }

            return new VerbDefinition(
                Enum.Parse<DropboxCliVerb>(verb.GetProperty("key").GetString()!),
                verb.GetProperty("command").GetString()!,
                verb.GetProperty("usage").GetString()!,
                verb.GetProperty("effect").GetString()!,
                options,
                verb.TryGetProperty("required", out var required) ? ReadOptions(required) : [],
                verb.TryGetProperty("oneOf", out var oneOf)
                    ? oneOf.EnumerateArray().Select(ReadOptions).ToArray() : [],
                verb.GetProperty("handler").GetString() is { } handler ? ParseHandler(handler) : throw new
                    InvalidDataException("A Dropbox verb has no handler."),
                calls,
                verb.TryGetProperty("output", out var verbOutput) ? ReadOutput(verbOutput) : null,
                calls.Count > 0 && calls.All(call => call.Route.Policy == DropboxRoutePolicy.Write));
        }

        private static OutputDefinition ReadOutput(JsonElement output) => new(
            output.GetProperty("kind").GetString()!,
            output.TryGetProperty("items", out var items) ? items.GetString() : null,
            output.TryGetProperty("entryPath", out var entryPath) ? entryPath.GetString() : null,
            output.TryGetProperty("at", out var at) ? at.GetString() : null,
            output.TryGetProperty("isDeleted", out var deleted) && deleted.GetBoolean());

        private static IReadOnlyList<DropboxCliOption> ReadOptions(JsonElement array) =>
            array.EnumerateArray().Select(item => Enum.Parse<DropboxCliOption>(item.GetString()!)).ToArray();

        private static ValueKind ParseKind(string value) => value switch
        {
            "id" => ValueKind.Id,
            "text" => ValueKind.Text,
            "int" => ValueKind.Int,
            "flag" => ValueKind.Flag,
            "json" => ValueKind.Json,
            "dropbox-path" => ValueKind.DropboxPath,
            "utc-time" => ValueKind.UtcTime,
            "local-file" => ValueKind.LocalFile,
            "namespace" => ValueKind.Namespace,
            _ => throw new InvalidDataException("Unknown Dropbox option value kind '" + value + "'.")
        };
    }

    /// <summary>True when the option takes a value (and so must be in the parser's value-option list).</summary>
    public static bool TakesValue(DropboxCliOption option) => Data.Kinds[option] != ValueKind.Flag;

    /// <summary>The value kind of one option.</summary>
    public static ValueKind Kind(DropboxCliOption option) => Data.Kinds[option];

    /// <summary>Parses a verb's handler key, requiring an exact declared name the dispatcher can run.</summary>
    internal static DropboxCliHandler ParseHandler(string value)
    {
        if (!Enum.TryParse<DropboxCliHandler>(value, ignoreCase: false, out var handler) ||
            !Enum.IsDefined(handler) || handler.ToString() != value)
            throw new InvalidDataException("The Dropbox command data names the unknown handler '" + value + "'.");
        return handler;
    }

    /// <summary>Every option's flag text, in declaration order.</summary>
    public static IReadOnlyList<string> AllFlags => Data.Flags.Values.ToArray();
}
