using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>Typed, validated reads of one invocation's Dropbox options.</summary>
public sealed class DropboxCliArguments(SyncCliInvocation invocation, DropboxCliCatalog.VerbDefinition verb,
    DropboxCliSettings settings)
{
    private string? _namespaceId;
    private bool _namespaceParsed;
    private readonly Dictionary<DropboxCliOption, string> _jsonFiles = new();

    /// <summary>The verb being run.</summary>
    public DropboxCliCatalog.VerbDefinition Verb => verb;

    /// <summary>The exact command text.</summary>
    public string Command => verb.Command;

    /// <summary>Whether --yes was given.</summary>
    public bool Yes => invocation.Flag(DropboxCliCatalog.Flag(DropboxCliOption.Yes));

    /// <summary>Whether an option is present in the invocation.</summary>
    public bool Present(DropboxCliOption option) => invocation.Flag(DropboxCliCatalog.Flag(option));

    /// <summary>The raw text of an option, or null when it is absent.</summary>
    public string? Value(DropboxCliOption option) => invocation.Value(DropboxCliCatalog.Flag(option));

    /// <summary>The namespace id from --root, validated, or null when --root is absent.</summary>
    public string? NamespaceId
    {
        get
        {
            if (!_namespaceParsed)
            {
                _namespaceParsed = true;
                if (Present(DropboxCliOption.Root))
                {
                    var raw = Value(DropboxCliOption.Root) ?? "";
                    if (!raw.StartsWith("ns:", StringComparison.Ordinal) || raw.Length <= 3 ||
                        !raw[3..].All(char.IsAsciiDigit))
                        throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.BadRoot));
                    _namespaceId = raw[3..];
                }
            }
            return _namespaceId;
        }
    }

    /// <summary>Validates required options, the mutually exclusive groups, and each option's value kind.</summary>
    public void Validate()
    {
        var missing = verb.Required.Where(option => !Present(option)).Select(DropboxCliCatalog.Flag).ToArray();
        if (missing.Length > 0)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.MissingRequired,
                ("command", Command), ("options", string.Join(", ", missing))));
        if (verb.OneOf.Count > 0 && verb.OneOf.Count(group => group.All(Present)) != 1)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.OneOf,
                ("command", Command),
                ("groups", string.Join(" or ", verb.OneOf.Select(group =>
                    string.Join(" ", group.Select(DropboxCliCatalog.Flag)))))));
        foreach (var option in verb.Options.Where(option =>
                     DropboxCliCatalog.Kind(option) == DropboxCliCatalog.ValueKind.LocalFile && Present(option)))
            RequireAbsolute(option, Value(option) ?? "");
        if (Present(DropboxCliOption.Root)) _ = NamespaceId;
        if (verb.Key == DropboxCliVerb.Put && (Present(DropboxCliOption.Mode) || Present(DropboxCliOption.Rev)))
            ValidateMode();
        if (Present(DropboxCliOption.Batch))
        {
            if (Json(DropboxCliOption.Batch) is not JsonArray entries)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.BadJson,
                    ("option", DropboxCliCatalog.Flag(DropboxCliOption.Batch))));
            if (entries.Count > DropboxApiLimits.BatchMaxEntries)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.BatchTooLarge,
                    ("max", DropboxApiLimits.BatchMaxEntries.ToString(CultureInfo.InvariantCulture))));
        }
        if (Present(DropboxCliOption.Seconds))
        {
            var seconds = Int(DropboxCliOption.Seconds)!.Value;
            if (seconds < DropboxApiLimits.LongpollMinSeconds || seconds > DropboxApiLimits.LongpollMaxSeconds)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.LongpollSeconds,
                    ("min", DropboxApiLimits.LongpollMinSeconds.ToString(CultureInfo.InvariantCulture)),
                    ("max", DropboxApiLimits.LongpollMaxSeconds.ToString(CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>The absolute local path named by --in or --out.</summary>
    public string LocalPath(DropboxCliOption option)
    {
        var value = Value(option) ?? throw new SyncCliUsageException(
            DropboxCliCatalog.Message(DropboxCliMessage.MissingRequired,
                ("command", Command), ("options", DropboxCliCatalog.Flag(option))));
        RequireAbsolute(option, value);
        return Path.GetFullPath(value);
    }

    /// <summary>Parses --body or --batch, inline or from an @absolute-file, within its bounds.</summary>
    public JsonNode? Json(DropboxCliOption option)
    {
        if (!Present(option)) return null;
        var raw = Value(option) ?? "";
        string text;
        if (raw.StartsWith('@'))
        {
            text = _jsonFiles.TryGetValue(option, out var resolved)
                ? resolved
                : throw new InvalidOperationException("A file-backed JSON option was read before it was resolved.");
        }
        else
        {
            if (raw.Length > settings.InlineJsonMaxChars)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.JsonTooLarge,
                    ("option", DropboxCliCatalog.Flag(option))));
            text = raw;
        }
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        }
        catch (JsonException)
        {
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.BadJson,
                ("option", DropboxCliCatalog.Flag(option))));
        }
    }

    /// <summary>Reads each @file JSON option once, asynchronously, so no read happens on the synchronous path.</summary>
    public async Task ResolveJsonFilesAsync(CancellationToken cancellationToken)
    {
        foreach (var option in new[] { DropboxCliOption.Body, DropboxCliOption.Batch })
        {
            if (!Present(option)) continue;
            var raw = Value(option) ?? "";
            if (!raw.StartsWith('@')) continue;
            var path = raw[1..];
            RequireAbsolute(option, path);
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.JsonFileMissing,
                    ("option", DropboxCliCatalog.Flag(option))));
            if (file.Length > settings.JsonFileMaxBytes)
                throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.JsonTooLarge,
                    ("option", DropboxCliCatalog.Flag(option))));
            _jsonFiles[option] = await File.ReadAllTextAsync(path, cancellationToken);
        }
    }

    /// <summary>Reads an int option, or null when absent.</summary>
    public int? Int(DropboxCliOption option)
    {
        if (!Present(option)) return null;
        return int.TryParse(Value(option), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.MissingRequired,
                ("command", Command), ("options", DropboxCliCatalog.Flag(option))));
    }

    /// <summary>Reads a Dropbox path option, turning the root "/" into the empty string Dropbox expects.</summary>
    public string? DropboxPath(DropboxCliOption option)
    {
        var value = Value(option);
        return value == "/" ? "" : value;
    }

    /// <summary>The typed JSON node one templated option contributes to a request body.</summary>
    public JsonNode? TemplateValue(DropboxCliOption option)
    {
        switch (DropboxCliCatalog.Kind(option))
        {
            case DropboxCliCatalog.ValueKind.Flag:
                return JsonValue.Create(true);
            case DropboxCliCatalog.ValueKind.Int:
                return JsonValue.Create(Int(option)!.Value);
            case DropboxCliCatalog.ValueKind.Json:
                return Json(option);
            case DropboxCliCatalog.ValueKind.UtcTime:
                return JsonValue.Create(UtcTime(option));
            case DropboxCliCatalog.ValueKind.DropboxPath:
                return JsonValue.Create(DropboxPath(option));
            default:
                return JsonValue.Create(Value(option));
        }
    }

    private string? UtcTime(DropboxCliOption option)
    {
        if (!Present(option)) return null;
        if (!DateTimeOffset.TryParse(Value(option), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.MissingRequired,
                ("command", Command), ("options", DropboxCliCatalog.Flag(option))));
        return when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    private void ValidateMode()
    {
        var mode = Value(DropboxCliOption.Mode) ?? "add";
        if (mode is not ("add" or "overwrite" or "update"))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ModeValue));
        if (mode == "update" && !Present(DropboxCliOption.Rev))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ModeRevRequired));
        if (Present(DropboxCliOption.Rev) && mode != "update")
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ModeRev));
    }

    private void RequireAbsolute(DropboxCliOption option, string value)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.AbsolutePath,
                ("option", DropboxCliCatalog.Flag(option))));
    }
}
