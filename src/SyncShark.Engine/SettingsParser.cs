using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine;

/// <summary>Parses the embedded defaults (complete) and the user's overrides (partial) into EngineSettings (2.2).
/// An unknown name, a bad enum or a bad number throws InvalidDataException(Text SettingsInvalid).</summary>
public static class SettingsParser
{
    /// <summary>Validates and overlays the user's settings on the complete defaults.</summary>
    public static EngineSettings Parse(string defaultsJson, string? userJson)
    {
        using var defaults = JsonDocument.Parse(defaultsJson);
        using var user = UserDocument(userJson);
        return Parse(defaults.RootElement, user?.RootElement);
    }

    private static EngineSettings Parse(JsonElement root, JsonElement? overlay)
    {
        // The defaults are the complete set of names, so any user name absent from them is unknown.
        if (overlay is { } supplied)
            foreach (var property in supplied.EnumerateObject())
                if (!root.TryGetProperty(property.Name, out _))
                    throw Bad(property.Name, Raw(property.Value), fromUser: true);
        var settings = new EngineSettings(
            ReadString(root, overlay, nameof(EngineSettings.ConflictLabelFormat)),
            ReadString(root, overlay, nameof(EngineSettings.ConflictLabelTimeFormat)),
            ReadString(root, overlay, nameof(EngineSettings.ConflictLabelLocalName)),
            ReadString(root, overlay, nameof(EngineSettings.ConflictLabelDropboxName)),
            ReadString(root, overlay, nameof(EngineSettings.CaseRenameSuffix)),
            ReadEnum<RecycleFallback>(root, overlay, nameof(EngineSettings.WhenRecycleBinUnavailable)),
            ReadInt(root, overlay, nameof(EngineSettings.LogRetentionDays)),
            ReadEnum<FirstRunPolicy>(root, overlay, nameof(EngineSettings.DefaultFirstRunPolicy)),
            ReadInt(root, overlay, nameof(EngineSettings.InitialLimiterLevel)),
            ReadIgnoreRules(root, overlay, nameof(EngineSettings.DefaultIgnoreRules)),
            ReadInt(root, overlay, nameof(EngineSettings.LogBufferCharacters)),
            ReadInt(root, overlay, nameof(EngineSettings.StoreBatchRows)));
        var backoff = new FailureRetryOwner.BackoffSettings(
            ReadDouble(root, overlay, nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds)),
            ReadDouble(root, overlay, nameof(FailureRetryOwner.BackoffSettings.BackoffMultiplier)),
            ReadDouble(root, overlay, nameof(FailureRetryOwner.BackoffSettings.BackoffJitter)),
            ReadDouble(root, overlay, nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds)));
        var largest = Math.Max(backoff.InitialBackoffSeconds, backoff.MaximumBackoffSeconds);
        if (largest * (1 + backoff.BackoffJitter) * TimeSpan.TicksPerSecond > TimeSpan.MaxValue.Ticks)
        {
            var name = backoff.InitialBackoffSeconds >= backoff.MaximumBackoffSeconds
                ? nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds)
                : nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds);
            var (element, fromUser) = Source(root, overlay, name);
            if (!fromUser && overlay is { } suppliedJitter
                && suppliedJitter.TryGetProperty(nameof(FailureRetryOwner.BackoffSettings.BackoffJitter), out var jitter))
                throw Bad(nameof(FailureRetryOwner.BackoffSettings.BackoffJitter), Raw(jitter), fromUser: true);
            throw Bad(name, Raw(element), fromUser);
        }
        return settings with
        {
            Backoff = backoff,
            StoragePageSize = ReadInt(root, overlay, nameof(EngineSettings.StoragePageSize)),
            BatchPollIntervalSeconds = ReadDouble(root, overlay, nameof(EngineSettings.BatchPollIntervalSeconds)),
            FolderDeleteMinimumPollSeconds =
                ReadDouble(root, overlay, nameof(EngineSettings.FolderDeleteMinimumPollSeconds)),
            AtomicReplaceAttempts = ReadInt(root, overlay, nameof(EngineSettings.AtomicReplaceAttempts)),
            AtomicReplaceInitialDelayMilliseconds =
                ReadInt(root, overlay, nameof(EngineSettings.AtomicReplaceInitialDelayMilliseconds)),
            LogReadBufferCharacters = ReadInt(root, overlay, nameof(EngineSettings.LogReadBufferCharacters)),
            JournalPageBufferBytes = ReadInt(root, overlay, nameof(EngineSettings.JournalPageBufferBytes))
        };
    }

    /// <summary>Validates name and value and returns the new user JSON.</summary>
    public static string WithOverride(string? userJson, string name, string value)
    {
        var property = typeof(EngineSettings).GetProperty(name);
        var type = property is { SetMethod: not null } && property.PropertyType != typeof(FailureRetryOwner.BackoffSettings)
            ? property.PropertyType
            : BackoffType(name);
        if (type is null) throw Bad(name, value, fromUser: true);
        JsonNode? node;
        try
        {
            node = userJson is null ? new JsonObject() : JsonNode.Parse(userJson);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(Text.Get(TextKey.SettingsFileInvalid, (TextArg.Message, error.Message)));
        }
        if (node is not JsonObject root)
            throw new InvalidDataException(Text.Get(TextKey.SettingsFileInvalid,
                (TextArg.Message, (node?.GetValueKind() ?? JsonValueKind.Null).ToString())));
        root[name] = OverrideValue(type, name, value);
        return root.ToJsonString();
    }

    /// <summary>Lists validated effective settings in defaults order, marking user overrides.</summary>
    public static IReadOnlyList<(string Name, string Value, bool Overridden)> List(string defaultsJson, string? userJson)
    {
        using var defaults = JsonDocument.Parse(defaultsJson);
        using var user = UserDocument(userJson);
        var overlay = user?.RootElement;
        // List only what would actually run, so a saved value the engine refuses is never shown as usable.
        Parse(defaults.RootElement, overlay);
        var list = new List<(string, string, bool)>();
        foreach (var property in defaults.RootElement.EnumerateObject())
        {
            var value = property.Value;
            var overridden = overlay is { } supplied && supplied.TryGetProperty(property.Name, out value);
            list.Add((property.Name, Raw(overridden ? value : property.Value), overridden));
        }
        return list;
    }

    private static (JsonElement Element, bool FromUser) Source(JsonElement defaults, JsonElement? overlay, string name)
    {
        if (overlay is { } supplied && supplied.TryGetProperty(name, out var value)) return (value, true);
        if (defaults.TryGetProperty(name, out var fallback)) return (fallback, false);
        throw Bad(name, "", fromUser: false);
    }

    private static string ReadString(JsonElement defaults, JsonElement? overlay, string name)
    {
        var (element, fromUser) = Source(defaults, overlay, name);
        if (element.ValueKind != JsonValueKind.String) throw Bad(name, Raw(element), fromUser);
        return element.GetString()!;
    }

    /// <summary>The default ignore rules, validated by the same parser the pair rules use.</summary>
    private static string ReadIgnoreRules(JsonElement defaults, JsonElement? overlay, string name)
    {
        var (element, fromUser) = Source(defaults, overlay, name);
        if (element.ValueKind != JsonValueKind.String) throw Bad(name, Raw(element), fromUser);
        var value = element.GetString()!;
        RequireIgnoreRules(name, value, fromUser);
        return value;
    }

    private static void RequireIgnoreRules(string name, string value, bool fromUser)
    {
        try
        {
            SyncIgnorePolicy.Normalize(SyncIgnorePolicy.Parse(value));
        }
        catch (InvalidDataException)
        {
            throw Bad(name, value, fromUser);
        }
    }

    private static int ReadInt(JsonElement defaults, JsonElement? overlay, string name)
    {
        var (element, fromUser) = Source(defaults, overlay, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number)
            || !IsValidInt(name, number))
            throw Bad(name, Raw(element), fromUser);
        return number;
    }

    // Existing atomic-publish validation bounds; these are not defaults.
    private const int MaximumAtomicReplaceAttempts = 10;
    private const int MaximumAtomicReplaceInitialDelayMilliseconds = 60_000;

    /// <summary>The shared integer rule: positive storage sizes and bounded atomic-replace settings.</summary>
    private static bool IsValidInt(string name, int number) => name switch
    {
        nameof(EngineSettings.AtomicReplaceAttempts) => number > 0 && number <= MaximumAtomicReplaceAttempts,
        nameof(EngineSettings.AtomicReplaceInitialDelayMilliseconds) => number >= 0 && number <= MaximumAtomicReplaceInitialDelayMilliseconds,
        nameof(EngineSettings.InitialLimiterLevel) or nameof(EngineSettings.LogBufferCharacters) or
            nameof(EngineSettings.LogReadBufferCharacters) or nameof(EngineSettings.JournalPageBufferBytes) or
            nameof(EngineSettings.StoragePageSize) or nameof(EngineSettings.StoreBatchRows) => number > 0,
        _ => true
    };

    private static double ReadDouble(JsonElement defaults, JsonElement? overlay, string name)
    {
        var (element, fromUser) = Source(defaults, overlay, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number)
            || !IsValidDouble(name, number))
            throw Bad(name, Raw(element), fromUser);
        return number;
    }

    /// <summary>The rule for each double setting: waits are finite and non-negative (jitter is a fraction of the
    /// nominal delay, at most the whole); the polling inputs are finite, positive and representable as a TimeSpan.</summary>
    private static bool IsValidDouble(string name, double number) => name switch
    {
        nameof(EngineSettings.BatchPollIntervalSeconds) or nameof(EngineSettings.FolderDeleteMinimumPollSeconds) =>
            double.IsFinite(number) && number > 0 && number <= TimeSpan.MaxValue.TotalSeconds,
        nameof(FailureRetryOwner.BackoffSettings.BackoffJitter) =>
            double.IsFinite(number) && number >= 0 && number <= 1,
        _ => double.IsFinite(number) && number >= 0
    };

    private static T ReadEnum<T>(JsonElement defaults, JsonElement? overlay, string name) where T : struct, Enum
    {
        var (element, fromUser) = Source(defaults, overlay, name);
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        // Exact declared names only: no case folding and no numeric spelling.
        if (!EnumNames.TryExact<T>(text, out var parsed))
            throw Bad(name, Raw(element), fromUser);
        return parsed;
    }

    /// <summary>The override type of each backoff setting; null for every other name.</summary>
    private static Type? BackoffType(string name) =>
        name is nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds)
            or nameof(FailureRetryOwner.BackoffSettings.BackoffMultiplier)
            or nameof(FailureRetryOwner.BackoffSettings.BackoffJitter)
            or nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds)
            ? typeof(double)
            : null;

    private static JsonNode OverrideValue(Type type, string name, string value)
    {
        if (type == typeof(string))
        {
            if (name == nameof(EngineSettings.DefaultIgnoreRules)) RequireIgnoreRules(name, value, fromUser: true);
            return JsonValue.Create(value)!;
        }
        if (type == typeof(int))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || !IsValidInt(name, number))
                throw Bad(name, value, fromUser: true);
            return JsonValue.Create(number)!;
        }
        if (type == typeof(double))
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !IsValidDouble(name, number))
                throw Bad(name, value, fromUser: true);
            return JsonValue.Create(number)!;
        }
        if (EnumNames.TryExact(type, value, out _)) return JsonValue.Create(value)!;
        throw Bad(name, value, fromUser: true);
    }

    private static string Raw(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();

    private static InvalidDataException Bad(string name, string value, bool fromUser) =>
        new(fromUser
            ? Text.Get(TextKey.SettingsInvalid, (TextArg.Name, name), (TextArg.Value, value))
            : Text.Get(TextKey.SettingsDefaultsInvalid, (TextArg.Name, name)));

    /// <summary>The user's settings file, or null when there is none; malformed JSON or a non-object root throws
    /// InvalidDataException(SettingsFileInvalid): the file can be edited by hand, so it is a trust boundary.</summary>
    private static JsonDocument? UserDocument(string? userJson)
    {
        if (userJson is null) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(userJson);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(Text.Get(TextKey.SettingsFileInvalid, (TextArg.Message, error.Message)));
        }
        var kind = document.RootElement.ValueKind;
        if (kind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new InvalidDataException(Text.Get(TextKey.SettingsFileInvalid, (TextArg.Message, kind.ToString())));
    }
}
