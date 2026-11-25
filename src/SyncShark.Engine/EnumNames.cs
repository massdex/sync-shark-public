namespace Massdex.SyncShark.Engine;

/// <summary>Exact enum-name parsing for the engine's data files (settings and text templates, 2.2).
/// Only a declared member's canonical name is accepted: no case folding, numeric spelling or whitespace variant.
/// Deliberately not used for the CLI's lenient spelling or for persisted enums.</summary>
internal static class EnumNames
{
    /// <summary>Parses text as a declared member of T; false (and default) for null or any noncanonical name.</summary>
    internal static bool TryExact<T>(string? text, out T value) where T : struct, Enum
    {
        if (text is not null && Enum.TryParse<T>(text, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed) && parsed.ToString() == text)
        {
            value = parsed;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Reflective form of <see cref="TryExact{T}"/>; a non-enum type fails before parsing. Failure yields null.</summary>
    internal static bool TryExact(Type type, string? text, out object? value)
    {
        value = null;
        if (!type.IsEnum || text is null || !Enum.TryParse(type, text, ignoreCase: false, out var parsed)
            || parsed is null || !Enum.IsDefined(type, parsed) || parsed.ToString() != text)
            return false;
        value = parsed;
        return true;
    }
}
