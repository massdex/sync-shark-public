namespace Massdex.SyncShark.V2;

/// <summary>
/// The one home for normalizing and resolving first-sync one-sided choices (rule 32 one-sided), so the Planner
/// and the CLI edge agree on the longest-prefix rule and its single tie-break (owner decisions 34/50).
/// </summary>
internal static class OneSidedChoiceResolver
{
    /// <summary>Choices with a trailing '/' trimmed, longest prefix first; the tie-break is the ordinal prefix.</summary>
    public static IReadOnlyList<OneSidedChoice> Normalize(IReadOnlyList<OneSidedChoice>? choices) =>
        choices is null || choices.Count == 0
            ? Array.Empty<OneSidedChoice>()
            : choices.Select(choice => choice with { Prefix = choice.Prefix.TrimEnd('/') })
                .OrderByDescending(choice => choice.Prefix.Length)
                .ThenBy(choice => choice.Prefix, StringComparer.Ordinal).ToArray();

    /// <summary>The first (longest) choice on the side that holds the item whose prefix is the path or one of its folders.</summary>
    public static OneSidedChoice? Resolve(IReadOnlyList<OneSidedChoice> normalized, Side present, string path)
    {
        foreach (var choice in normalized)
            if (choice.Present == present && (choice.Prefix.Length == 0 || PairPath.IsSelfOrUnder(path, choice.Prefix)))
                return choice;
        return null;
    }
}
