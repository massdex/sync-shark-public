using System.Text.Json;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Cli operations for the edge module. Parsing and help are driven from the keyed catalog so spellings
/// such as --keep, --yes and the first-sync values never live in code.</summary>
public static class Cli
{
    /// <summary>Exit code a definite pre-send host absence reports; a read renders HostNotRunning, a mutation may start the host.</summary>
    public const int HostAbsentExitCode = 3;

    /// <summary>A usage error (unknown command, missing or invalid option).</summary>
    public const int UsageExitCode = 2;

    /// <summary>A successful command.</summary>
    public const int SuccessExitCode = 0;

    /// <summary>A transient failure, such as a refused resolve.</summary>
    public const int TransientExitCode = 1;

    private static readonly Lazy<Catalog> CatalogData = new(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static Catalog Definitions => CatalogData.Value;

    /// <summary>The CLI word of a resolution choice, from the catalog (the reverse of ParseChoice).</summary>
    internal static string ChoiceWord(ResolutionChoice choice) =>
        Definitions.Choices.TryGetValue(choice.ToString(), out var word) ? word : choice.ToString();

    /// <summary>Use ResourceNames.LogDirectory for detached log reads. Confirmed host absence renders HostNotRunning for a non-log read; never start the host on a read.</summary>
    public static async Task<int> RunAsync(string[] args, ILogFiles files, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var invocation = Parse(args);
        if (invocation is null)
        {
            foreach (var line in Help()) error.WriteLine(line);
            return UsageExitCode;
        }

        // A detached log read never reaches the host; Edge.Commands owns it and forwards to RunLog.Read (R19).
        if (invocation.CommandKey == CommandKeys.Logs)
            return Commands.ReadLogs(invocation, files, output, ct);

        var result = await new HostClient().SendAsync(args, output, error, ct, waitForHostExit: invocation.CommandKey == CommandKeys.HostStop).ConfigureAwait(false);
        return await ResolveSend(invocation, result, output,
            c => new HostClient().SendAsync(args, output, error, c, waitForHostExit: invocation.CommandKey == CommandKeys.HostStop),
            c => HostClient.StartAsync(c), ct).ConfigureAwait(false);
    }

    /// <summary>Decides a send outcome (F052). A non-null exit is terminal (even a failure, or a lost reply) and is
    /// never replayed; a null exit is definite pre-send absence, where a read renders HostNotRunning and an eligible
    /// command may start the host and send exactly once.</summary>
    public static async Task<int> ResolveSend(CliInvocation invocation, (int? ExitCode, bool ReceivedExit) result,
        TextWriter output,
        Func<CancellationToken, Task<(int? ExitCode, bool ReceivedExit)>> sendAgain,
        Func<CancellationToken, Task<int>> startHost, CancellationToken ct)
    {
        if (result.ExitCode is { } code) return code;
        if (invocation.CommandKey == CommandKeys.HostStop) return SuccessExitCode;
        if (IsRead(invocation.CommandKey) || (invocation.CommandKey == CommandKeys.PairsExclude &&
            !invocation.Flag(OptionKeys.Keep) && !invocation.Flag(OptionKeys.Yes))) return RenderHostAbsent(output);
        var started = await startHost(ct).ConfigureAwait(false);
        if (started != SuccessExitCode) { RenderHostAbsent(output); return started; }
        var second = await sendAgain(ct).ConfigureAwait(false);
        return second.ExitCode ?? TransientExitCode;
    }

    /// <summary>Help operation on the supplied facts. Every catalog usage appears verbatim in one line.</summary>
    public static IReadOnlyList<string> Help()
    {
        var pad = new string(' ', 2);
        var lines = new List<string>();
        foreach (var row in Definitions.Commands)
        {
            lines.Add(row.Usage);
            if (row.Effect.Length != 0) lines.Add(string.Concat(pad, row.Effect));
        }
        return lines;
    }

    /// <summary>Parses one invocation against the catalog; null when unknown, incomplete or invalid.</summary>
    public static CliInvocation? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        var catalog = Definitions;

        var (match, matchedWords) = MatchCommand(catalog, args);
        if (match is null) return null;

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in match.OptionKeys)
            if (IsRequired(match.Usage, catalog.Options[key].Flag)) required.Add(key);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = matchedWords; index < args.Length; index++)
        {
            var token = args[index];
            OptionRow? option = null;
            foreach (var candidate in catalog.Options.Values)
                if (string.Equals(candidate.Flag, token, StringComparison.Ordinal)) { option = candidate; break; }
            if (option is null || !match.OptionKeys.Contains(option.Key, StringComparer.Ordinal)) return null;

            if (option.TakesValue)
            {
                if (index + 1 >= args.Length) return null;
                var value = args[++index];
                if (option.Values.Count > 0 && !option.Values.Contains(value, StringComparer.Ordinal)) return null;
                values[option.Key] = value;
            }
            else
            {
                flags.Add(option.Key);
            }
        }

        foreach (var key in required)
            if (!values.ContainsKey(key) && !flags.Contains(key)) return null;
        return new CliInvocation(match.Key, values, flags);
    }

    private static (CommandRow? Match, int Words) MatchCommand(Catalog catalog, string[] args)
    {
        CommandRow? match = null;
        var matchedWords = 0;
        foreach (var row in catalog.Commands)
        {
            var words = row.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > args.Length || words.Length < matchedWords) continue;
            var matches = true;
            for (var index = 0; index < words.Length; index++)
                if (!string.Equals(words[index], args[index], StringComparison.Ordinal)) { matches = false; break; }
            if (matches) { match = row; matchedWords = words.Length; }
        }
        return (match, matchedWords);
    }

    /// <summary>Renders a definitely absent host for a non-log read and reports a non-zero exit.</summary>
    public static int RenderHostAbsent(TextWriter output)
    {
        output.WriteLine(Text.Get(TextKey.HostNotRunning));
        return HostAbsentExitCode;
    }

    private static bool IsRead(string key) =>
        key == CommandKeys.Status || key == CommandKeys.Notices || key == CommandKeys.PairsList ||
        key == CommandKeys.PairsExclusions || key == CommandKeys.IgnoresList;

    // Required options are the declared flags that appear in the usage outside any [optional] group.
    private static bool IsRequired(string usage, string flag)
    {
        var index = usage.IndexOf(flag, StringComparison.Ordinal);
        if (index < 0) return false;
        var before = index - 1;
        while (before >= 0 && usage[before] == ' ') before--;
        return before < 0 || usage[before] != '[';
    }

    private static Catalog LoadCatalog()
    {
        using var document = CliCatalogResource.Load(typeof(Cli).Assembly, ResourceNames.Cli);
        var root = document.RootElement;

        var options = new Dictionary<string, OptionRow>(StringComparer.Ordinal);
        foreach (var element in root.GetProperty("options").EnumerateArray())
        {
            var key = element.GetProperty("key").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var flag = element.GetProperty("flag").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var takesValue = element.GetProperty("takesValue").GetBoolean();
            var values = element.GetProperty("values").EnumerateArray()
                .Select(v => v.GetString() ?? throw new InvalidDataException(ResourceNames.Cli)).ToArray();
            options.Add(key, new OptionRow(key, flag, takesValue, values));
        }

        var commands = new List<CommandRow>();
        foreach (var element in root.GetProperty("commands").EnumerateArray())
        {
            var command = element.GetProperty("command").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var usage = element.GetProperty("usage").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var effect = element.GetProperty("effect").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var key = element.GetProperty("key").GetString() ?? throw new InvalidDataException(ResourceNames.Cli);
            var optionKeys = element.GetProperty("options").EnumerateArray()
                .Select(v => v.GetString() ?? throw new InvalidDataException(ResourceNames.Cli)).ToArray();
            commands.Add(new CommandRow(command, usage, effect, key, optionKeys));
        }

        var firstRun = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("firstRunPolicies").EnumerateObject())
            firstRun[property.Name] = property.Value.GetString() ?? throw new InvalidDataException(ResourceNames.Cli);

        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("choices").EnumerateObject())
            choices[property.Name] = property.Value.GetString() ?? throw new InvalidDataException(ResourceNames.Cli);

        return new Catalog(commands, options, firstRun, choices);
    }

    internal sealed record CommandRow(string Command, string Usage, string Effect, string Key, IReadOnlyList<string> OptionKeys);

    internal sealed record OptionRow(string Key, string Flag, bool TakesValue, IReadOnlyList<string> Values);

    internal sealed record Catalog(IReadOnlyList<CommandRow> Commands, IReadOnlyDictionary<string, OptionRow> Options,
        IReadOnlyDictionary<string, string> FirstRunPolicies, IReadOnlyDictionary<string, string> Choices);
}
