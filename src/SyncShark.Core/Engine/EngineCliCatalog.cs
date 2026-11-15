using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>Loads the embedded engine command data and exposes the registry rows, lookups and validation
/// (engine design 1.3). All command words, flags, usage and messages come from engine-cli.json.</summary>
public static class EngineCliCatalog
{
    private const string ResourceName = "SyncShark.EngineCli.json";
    private const char OptionSeparator = ',';
    private const string MessagesProperty = "messages";
    private const string ApprovalsProperty = "approvals";
    private const string OptionsProperty = "options";
    private const string CommandsProperty = "commands";
    private const string KeyProperty = "key";
    private const string ConfirmProperty = "confirm";

    private static readonly Model Data = Model.Load();

    /// <summary>The command group name, the first command word.</summary>
    public static string Group => Data.File.Group;

    /// <summary>The engine registry rows, one per command.</summary>
    public static readonly IReadOnlyList<SyncCliCommandRow> Rows = Data.File.Commands.Select(command =>
        new SyncCliCommandRow(
            Command: command.Command,
            Usage: command.Usage,
            Kind: SyncCliRequestKind.State,
            Approval: Data.File.Approvals[command.Confirm],
            Effect: command.Effect,
            // A row may narrow the file defaults (the preference commands run only against an existing host).
            Requires: command.Requires ?? Data.File.Requires,
            Mode: command.Mode ?? Data.File.Mode,
            Options: string.Join(OptionSeparator, command.Options.Select(Flag)),
            Mutating: command.Confirm != EngineConfirm.Never,
            RequiresYes: command.Confirm == EngineConfirm.Always,
            RequiresPair: command.NeedsPair)).ToArray();

    /// <summary>True when this exact command belongs to the engine group.</summary>
    public static bool Owns(string command) => Data.ByCommand.ContainsKey(command);

    /// <summary>The stable key of an engine command.</summary>
    public static EngineCommandKey CommandKey(string command) => Data.ByCommand[command].Key;

    /// <summary>The flag text of one option.</summary>
    public static string Flag(EngineOptionKey option) => Data.ByOption[option].Flag;

    /// <summary>Returns the catalog-owned spelling of a stable enum name.</summary>
    public static string OptionValue(EngineOptionKey option, string enumName) =>
        Data.ByOption[option].Values.Single(value =>
            string.Equals(NormalizeValue(value), enumName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Matches catalog spellings to existing enum names without inventing new wire values.</summary>
    public static bool TryParseValue<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(NormalizeValue(value), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private static string NormalizeValue(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>True when an engine option flag takes a value; the shared parser asks this for engine commands, so
    /// an engine flag never inherits another command's value rule.</summary>
    public static bool TakesValue(string flag) => Data.ByFlag[flag].TakesValue;

    /// <summary>The message for a key, with each {placeholder} filled.</summary>
    public static string Message(EngineMessageKey key, params (EngineMessageArg Name, string Value)[] values) =>
        CliCatalogResource.Fill(Data.File.Messages[key],
            values.Select(value => (value.Name.ToString(), value.Value)).ToArray());

    /// <summary>Loads and validates a catalog document; the check passes poisoned fixtures here (R1).</summary>
    internal static void CheckCatalog(JsonDocument document) => Model.Load(document);

    /// <summary>Checks required options and listed values, before any runtime exists (R2-M18).</summary>
    public static void Validate(SyncCliInvocation invocation)
    {
        var command = Data.ByCommand[invocation.Command];
        foreach (var key in command.Required)
            if (invocation.Value(Flag(key)) is null)
                throw new SyncCliUsageException(Message(EngineMessageKey.MissingOption,
                    (EngineMessageArg.Command, invocation.Command), (EngineMessageArg.Flag, Flag(key))));
        foreach (var key in command.Options)
        {
            var option = Data.ByOption[key];
            if (invocation.Value(option.Flag) is { } value && option.Values.Count > 0 &&
                !option.Values.Contains(value, StringComparer.Ordinal))
                throw new SyncCliUsageException(Message(EngineMessageKey.BadValue,
                    (EngineMessageArg.Flag, option.Flag),
                    (EngineMessageArg.Values, string.Join(Data.File.ListSeparator, option.Values))));
        }
        // The changed-field patch grammar is owned by UiPreferencesUpdate, so it is refused here before any runtime.
        if (command.Key == EngineCommandKey.PreferencesSet &&
            invocation.Value(Flag(EngineOptionKey.Settings)) is { } patch)
            UiPreferencesUpdate.Parse(patch);
    }

    private sealed record OptionData(EngineOptionKey Key, string Flag, bool TakesValue, IReadOnlyList<string> Values);

    private sealed record CommandData(EngineCommandKey Key, string Command, string Usage, string Effect,
        IReadOnlyList<EngineOptionKey> Options, IReadOnlyList<EngineOptionKey> Required, EngineConfirm Confirm,
        bool NeedsPair, string? Requires = null, string? Mode = null);

    private sealed record FileData(string Group, string Requires, string Mode, string ListSeparator,
        IReadOnlyDictionary<EngineConfirm, string> Approvals, IReadOnlyList<OptionData> Options,
        IReadOnlyList<CommandData> Commands, IReadOnlyDictionary<EngineMessageKey, string> Messages);

    private sealed record Model(FileData File, IReadOnlyDictionary<string, CommandData> ByCommand,
        IReadOnlyDictionary<EngineOptionKey, OptionData> ByOption, IReadOnlyDictionary<string, OptionData> ByFlag)
    {
        internal static Model Load()
        {
            using var document = CliCatalogResource.Load(typeof(EngineCliCatalog).Assembly, ResourceName);
            return Load(document);
        }

        // Exact property names, the same case-sensitive rule the validations below use; a numeric enum value is not
        // a name (R1).
        internal static Model Load(JsonDocument document)
        {
            var root = document.RootElement;
            RequireUniqueNames(root);
            var file = root.Deserialize<FileData>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
            }) ?? throw new InvalidDataException(ResourceName);
            // The data and the enums must name the same keys exactly once each, by name: a numeric key is not a name
            // (R3-M20). Every command's confirmation rule must be one of those names too.
            RequireKeySet(file, root.GetProperty(MessagesProperty).EnumerateObject().Select(property => property.Name),
                Enum.GetNames<EngineMessageKey>(), nameof(EngineMessageKey));
            RequireKeySet(file, root.GetProperty(ApprovalsProperty).EnumerateObject().Select(property => property.Name),
                Enum.GetNames<EngineConfirm>(), nameof(EngineConfirm));
            RequireKeySet(file, root.GetProperty(OptionsProperty).EnumerateArray().Select(option => Name(option, KeyProperty)),
                Enum.GetNames<EngineOptionKey>(), nameof(EngineOptionKey));
            var commands = root.GetProperty(CommandsProperty).EnumerateArray().ToList();
            RequireKeySet(file, commands.Select(command => Name(command, KeyProperty)),
                Enum.GetNames<EngineCommandKey>(), nameof(EngineCommandKey));
            foreach (var command in commands)
                RequireName(file, Name(command, ConfirmProperty), Enum.GetNames<EngineConfirm>(),
                    nameof(EngineConfirm));
            return new Model(file,
                file.Commands.ToDictionary(command => command.Command, StringComparer.Ordinal),
                file.Options.ToDictionary(option => option.Key),
                file.Options.ToDictionary(option => option.Flag, StringComparer.Ordinal));
        }

        // The JSON name of an enum key; null when the property is missing or not a string (a numeric key is not a name).
        private static string? Name(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // A document with two property names that differ only in case is poisoned: an exact reader and an insensitive
        // one would disagree about its value, so it is refused before either runs (R1).
        private static void RequireUniqueNames(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = element.EnumerateObject().Select(property => property.Name).ToList();
                if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    throw new InvalidDataException(ResourceName);
                foreach (var property in element.EnumerateObject())
                    RequireUniqueNames(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    RequireUniqueNames(item);
            }
        }

        private static void RequireKeySet(FileData file, IEnumerable<string?> names, string[] declared, string name)
        {
            var actual = names.ToList();
            Require(file, actual.Count == declared.Length &&
                actual.Distinct(StringComparer.Ordinal).Count() == actual.Count &&
                declared.All(key => actual.Contains(key)), name);
        }

        // The confirmation words in the data are the declared names (the design writes them in lower case, and the
        // enum converter reads them case-insensitively); a numeric value is not a name.
        private static void RequireName(FileData file, string? name, string[] declared, string kind) =>
            Require(file, name is not null &&
                Array.Exists(declared, key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)), kind);

        private static void Require(FileData file, bool holds, string name)
        {
            if (holds) return;
            if (file.Messages.TryGetValue(EngineMessageKey.CatalogMismatch, out var template))
                throw new InvalidDataException(CliCatalogResource.Fill(template,
                    [(nameof(EngineMessageArg.Name), name)]));
            throw new InvalidDataException(ResourceName);
        }
    }
}
