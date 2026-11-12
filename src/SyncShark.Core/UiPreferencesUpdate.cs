using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>A changed-field preference patch: an omitted member leaves its saved value untouched. The closed
/// wire schema is owned here so the CLI, the host and the App share one grammar.</summary>
public sealed record UiPreferencesUpdate
{
    private const string PatchName = "settings";
    internal static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string FolderMember = Wire(nameof(UpdateDeliveryFolder));
    private static readonly string PagesMember = Wire(nameof(PageViewModes));
    private readonly string? _updateDeliveryFolder;

    public string? Theme { get; init; }
    public bool? KeepRunningWhenClosed { get; init; }
    public bool? CompactRows { get; init; }
    public bool? NotifyProblems { get; init; }
    public bool? NotifyCompleted { get; init; }
    public bool? NotifyTransfers { get; init; }
    public bool? NotificationSound { get; init; }
    public string? StatusIconStyle { get; init; }
    public bool? AutomaticUpdateChecks { get; init; }

    /// <summary>Only the changed exact page keys; they merge into the saved pages.</summary>
    public IReadOnlyDictionary<string, string>? PageViewModes { get; init; }

    /// <summary>Assigning this member, even to null, is the change; null clears the saved folder.</summary>
    public string? UpdateDeliveryFolder
    {
        get => _updateDeliveryFolder;
        init
        {
            _updateDeliveryFolder = value;
            HasUpdateDeliveryFolder = true;
        }
    }

    // Deliberately not public: the serializer must never see the presence bit as a wire member.
    internal bool HasUpdateDeliveryFolder { get; private init; }

    /// <summary>Parses a patch and refuses anything but the closed schema, before any state is touched.</summary>
    public static UiPreferencesUpdate Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasCollidingMembers(root) ||
                root.EnumerateObject().Any(member => member.Value.ValueKind == JsonValueKind.Null && member.Name != FolderMember) ||
                (root.TryGetProperty(PagesMember, out var pages) && HasCollidingMembers(pages)))
                throw RefusedPatch();
            var update = root.Deserialize<UiPreferencesUpdate>(WireOptions)!;
            update.Validate();
            return update;
        }
        catch (JsonException) { throw RefusedPatch(); }
    }

    /// <summary>The patch in its wire form, for the --settings value.</summary>
    public string ToJson() => ToWire().ToJsonString(WireOptions);

    internal void Validate()
    {
        if (ToWire().Count == 0 || PageViewModes?.Count == 0) throw RefusedPatch();
        if (Theme is not null && !UiPreferencesStore.IsSupportedTheme(Theme))
            throw Refused(Wire(nameof(Theme)), UiPreferencesStore.Themes);
        if (StatusIconStyle is not null && !UiPreferencesStore.IsStatusIconStyle(StatusIconStyle))
            throw Refused(Wire(nameof(StatusIconStyle)), UiPreferencesStore.IconSets);
        if (PageViewModes?.Any(page => !UiPreferencesStore.IsPageKey(page.Key) || !UiPreferencesStore.IsViewMode(page.Value)) == true)
            throw Refused(PagesMember, [UiPreferencesStore.SimpleView, UiPreferencesStore.DetailedView]);
        if (UpdateDeliveryFolder is { } folder && !IsFullLocalPath(folder))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.AbsolutePath, ("option", FolderMember)));
    }

    internal UiPreferences ApplyTo(UiPreferences current) => current with
    {
        Theme = Theme ?? current.Theme,
        KeepRunningWhenClosed = KeepRunningWhenClosed ?? current.KeepRunningWhenClosed,
        CompactRows = CompactRows ?? current.CompactRows,
        NotifyProblems = NotifyProblems ?? current.NotifyProblems,
        NotifyCompleted = NotifyCompleted ?? current.NotifyCompleted,
        NotifyTransfers = NotifyTransfers ?? current.NotifyTransfers,
        NotificationSound = NotificationSound ?? current.NotificationSound,
        StatusIconStyle = StatusIconStyle ?? current.StatusIconStyle,
        AutomaticUpdateChecks = AutomaticUpdateChecks ?? current.AutomaticUpdateChecks,
        UpdateDeliveryFolder = HasUpdateDeliveryFolder ? UpdateDeliveryFolder : current.UpdateDeliveryFolder,
        PageViewModes = MergePages(current.PageViewModes)
    };

    private IReadOnlyDictionary<string, string>? MergePages(IReadOnlyDictionary<string, string>? saved)
    {
        if (PageViewModes is null) return saved;
        var merged = new Dictionary<string, string>(saved ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var page in PageViewModes) merged[page.Key] = page.Value;
        return merged;
    }

    private JsonObject ToWire()
    {
        var patch = (JsonObject)JsonSerializer.SerializeToNode(this, WireOptions)!;
        if (HasUpdateDeliveryFolder) patch[FolderMember] = UpdateDeliveryFolder;
        return patch;
    }

    private static string Wire(string member) => JsonNamingPolicy.CamelCase.ConvertName(member);

    // The picker stores Path.GetFullPath's form; a request in any other form is refused, not rewritten.
    private static bool IsFullLocalPath(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && !folder.Contains('\0') && Path.IsPathFullyQualified(folder) &&
        Path.GetFullPath(folder) == folder;

    private static bool HasCollidingMembers(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject()
            .GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    private static SyncCliUsageException RefusedPatch() =>
        Refused(PatchName, typeof(UiPreferencesUpdate).GetProperties().Select(property => Wire(property.Name)));

    private static SyncCliUsageException Refused(string member, IEnumerable<string> allowed) =>
        new(EngineCliCatalog.Message(EngineMessageKey.BadValue, (EngineMessageArg.Flag, member),
            (EngineMessageArg.Values, string.Join(", ", allowed))));
}
