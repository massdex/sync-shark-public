/// <summary>One parsed invocation: its catalog key and the option values and flags it named.</summary>
public sealed class CliInvocation
{
    internal CliInvocation(string commandKey, IReadOnlyDictionary<string, string> values, IReadOnlySet<string> flags)
    {
        CommandKey = commandKey;
        Values = values;
        Flags = flags;
    }

    /// <summary>The catalog key of the matched command.</summary>
    public string CommandKey { get; }

    internal IReadOnlyDictionary<string, string> Values { get; }

    internal IReadOnlySet<string> Flags { get; }

    /// <summary>The value of the named option, or null when absent.</summary>
    public string? Value(string optionKey) => Values.TryGetValue(optionKey, out var value) ? value : null;

    /// <summary>Whether the named flag option was supplied.</summary>
    public bool Flag(string optionKey) => Flags.Contains(optionKey);
}
