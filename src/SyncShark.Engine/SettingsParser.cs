using System.Text.Json;

namespace Massdex.SyncShark.Engine;

/// <summary>Loads the embedded settings once at a composition root; there is no writable overlay.</summary>
public static class SettingsParser
{
    // USN_RECORD_V2 has a 60-byte header and at most 255 UTF-16 units, aligned to 8 bytes after the next-USN field.
    internal const int MinimumJournalPageBytes = sizeof(ulong) + (60 + 255 * sizeof(char) + sizeof(ulong) - 1) / sizeof(ulong) * sizeof(ulong);

    public static EngineSettings Load()
    {
        using var doc = CliCatalogResource.Load(typeof(EngineSettings).Assembly, ResourceNames.Settings);
        var root = doc.RootElement;
        return ReadInitProperties(root, ReadRecord(root));
    }

    private static InvalidDataException Bad(string name) => new(Text.Get(TextKey.SettingsDefaultsInvalid,(TextArg.Name,name)));
    private static JsonElement Get(JsonElement root, string name) =>
        root.ValueKind==JsonValueKind.Object && root.TryGetProperty(name,out var value) ? value : throw Bad(name);
    private static string ReadString(JsonElement root, string name) =>
        Get(root,name) is { ValueKind:JsonValueKind.String } v ? v.GetString()! : throw Bad(name);
    private static T ReadEnum<T>(JsonElement root, string name) where T:struct,Enum =>
        EnumNames.TryExact<T>(ReadString(root,name),out var value) ? value : throw Bad(name);
    private static int ReadInt(JsonElement root, string name) => Get(root,name) is { ValueKind:JsonValueKind.Number } v && v.TryGetInt32(out var n) && n>0 &&
        (name!=nameof(EngineSettings.JournalPageBufferBytes) || n>=MinimumJournalPageBytes) ? n : throw Bad(name);
    private static double ReadDouble(JsonElement root, string name) => Get(root,name) is { ValueKind:JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) && n>=0 &&
        (name!=nameof(EngineSettings.BackoffSettings.BackoffJitter) || n<=1) ? n : throw Bad(name);
    private static string ReadIgnoreRules(JsonElement root, string name) { var rules=ReadString(root,name); IgnoreRule.Normalize(IgnoreRule.Parse(rules)); return rules; }

    private static EngineSettings ReadRecord(JsonElement root) => new(
        ReadString(root,nameof(EngineSettings.ConflictLabelFormat)),
        ReadString(root,nameof(EngineSettings.ConflictLabelTimeFormat)),
        ReadString(root,nameof(EngineSettings.ConflictLabelLocalName)),
        ReadString(root,nameof(EngineSettings.ConflictLabelDropboxName)),
        ReadString(root,nameof(EngineSettings.CaseRenameSuffix)),
        ReadInt(root,nameof(EngineSettings.LogRetentionDays)),
        ReadEnum<FirstRunPolicy>(root,nameof(EngineSettings.DefaultFirstRunPolicy)),
        ReadInt(root,nameof(EngineSettings.InitialLimiterLevel)),
        ReadIgnoreRules(root,nameof(EngineSettings.DefaultIgnoreRules)),
        ReadInt(root,nameof(EngineSettings.LogBufferCharacters)),
        ReadInt(root,nameof(EngineSettings.StoreBatchRows)));

    private static EngineSettings ReadInitProperties(JsonElement root, EngineSettings settings)
    {
        var backoff = new EngineSettings.BackoffSettings(
            ReadDouble(root,nameof(EngineSettings.BackoffSettings.InitialBackoffSeconds)),
            ReadDouble(root,nameof(EngineSettings.BackoffSettings.BackoffMultiplier)),
            ReadDouble(root,nameof(EngineSettings.BackoffSettings.BackoffJitter)),
            ReadDouble(root,nameof(EngineSettings.BackoffSettings.MaximumBackoffSeconds)));
        if(Math.Max(backoff.InitialBackoffSeconds,backoff.MaximumBackoffSeconds)*(1+backoff.BackoffJitter)>TimeSpan.MaxValue.TotalSeconds)
            throw Bad(nameof(EngineSettings.BackoffSettings.MaximumBackoffSeconds));
        return settings with
        {
            ComputerName = Environment.MachineName,
            Backoff = backoff,
            StoragePageSize = ReadInt(root,nameof(EngineSettings.StoragePageSize)),
            LogReadBufferBytes = ReadInt(root,nameof(EngineSettings.LogReadBufferBytes)),
            JournalPageBufferBytes = ReadInt(root,nameof(EngineSettings.JournalPageBufferBytes)),
            ConnectionListMaxBytes = ReadInt(root,nameof(EngineSettings.ConnectionListMaxBytes)),
            ConnectionListMaxAppendRecords = ReadInt(root,nameof(EngineSettings.ConnectionListMaxAppendRecords)),
            DropboxErrorBodyMaxBytes = ReadInt(root,nameof(EngineSettings.DropboxErrorBodyMaxBytes)),
            DropboxConnectAllowanceSeconds = ReadInt(root,nameof(EngineSettings.DropboxConnectAllowanceSeconds)),
            SignInMaxJsonBytes = ReadInt(root,nameof(EngineSettings.SignInMaxJsonBytes)),
            SignInMaxCallbackBytes = ReadInt(root,nameof(EngineSettings.SignInMaxCallbackBytes)),
            SignInMaxCallbackLineBytes = ReadInt(root,nameof(EngineSettings.SignInMaxCallbackLineBytes)),
            SignInReadChunkBytes = ReadInt(root,nameof(EngineSettings.SignInReadChunkBytes)),
            TokenRefreshMarginMinutes = ReadInt(root,nameof(EngineSettings.TokenRefreshMarginMinutes)),
            PipeMaxPayloadBytes = ReadInt(root,nameof(EngineSettings.PipeMaxPayloadBytes)),
            DirectoryListingBufferBytes = ReadInt(root,nameof(EngineSettings.DirectoryListingBufferBytes)),
            RecycleBinTrimDays = ReadInt(root,nameof(EngineSettings.RecycleBinTrimDays))
        };
    }
}
