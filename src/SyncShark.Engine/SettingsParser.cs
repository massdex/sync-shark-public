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
        var root=doc.RootElement;
        static InvalidDataException Bad(string name) => new(Text.Get(TextKey.SettingsDefaultsInvalid,(TextArg.Name,name)));
        JsonElement Get(string name) => root.ValueKind==JsonValueKind.Object && root.TryGetProperty(name,out var value) ? value : throw Bad(name);
        string ReadString(string name) => Get(name) is { ValueKind:JsonValueKind.String } v ? v.GetString()! : throw Bad(name);
        T ReadEnum<T>(string name) where T:struct,Enum => EnumNames.TryExact<T>(ReadString(name),out var value) ? value : throw Bad(name);
        int ReadInt(string name) => Get(name) is { ValueKind:JsonValueKind.Number } v && v.TryGetInt32(out var n) && n>0 &&
            (name!=nameof(EngineSettings.JournalPageBufferBytes) || n>=MinimumJournalPageBytes) ? n : throw Bad(name);
        double ReadDouble(string name) => Get(name) is { ValueKind:JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) && n>=0 &&
            (name!=nameof(EngineSettings.BackoffSettings.BackoffJitter) || n<=1) ? n : throw Bad(name);
        string ReadIgnoreRules(string name) { var rules=ReadString(name); IgnoreRule.Normalize(IgnoreRule.Parse(rules)); return rules; }
        var settings = new EngineSettings(
            ReadString(nameof(EngineSettings.ConflictLabelFormat)),
            ReadString(nameof(EngineSettings.ConflictLabelTimeFormat)),
            ReadString(nameof(EngineSettings.ConflictLabelLocalName)),
            ReadString(nameof(EngineSettings.ConflictLabelDropboxName)),
            ReadString(nameof(EngineSettings.CaseRenameSuffix)),
            ReadInt(nameof(EngineSettings.LogRetentionDays)),
            ReadEnum<FirstRunPolicy>(nameof(EngineSettings.DefaultFirstRunPolicy)),
            ReadInt(nameof(EngineSettings.InitialLimiterLevel)),
            ReadIgnoreRules(nameof(EngineSettings.DefaultIgnoreRules)),
            ReadInt(nameof(EngineSettings.LogBufferCharacters)),
            ReadInt(nameof(EngineSettings.StoreBatchRows)));
        var backoff = new EngineSettings.BackoffSettings(
            ReadDouble(nameof(EngineSettings.BackoffSettings.InitialBackoffSeconds)),
            ReadDouble(nameof(EngineSettings.BackoffSettings.BackoffMultiplier)),
            ReadDouble(nameof(EngineSettings.BackoffSettings.BackoffJitter)),
            ReadDouble(nameof(EngineSettings.BackoffSettings.MaximumBackoffSeconds)));
        if(Math.Max(backoff.InitialBackoffSeconds,backoff.MaximumBackoffSeconds)*(1+backoff.BackoffJitter)>TimeSpan.MaxValue.TotalSeconds)
            throw Bad(nameof(EngineSettings.BackoffSettings.MaximumBackoffSeconds));
        return settings with
        {
            ComputerName = Environment.MachineName,
            Backoff = backoff,
            StoragePageSize = ReadInt(nameof(EngineSettings.StoragePageSize)),
            LogReadBufferBytes = ReadInt(nameof(EngineSettings.LogReadBufferBytes)),
            JournalPageBufferBytes = ReadInt(nameof(EngineSettings.JournalPageBufferBytes))
        };
    }

}
