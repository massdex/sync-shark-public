using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task ContractIdentityChecksAsync()
    {
        Require((int)PilotEntryKind.File == 0 && (int)PilotEntryKind.Directory == 1 &&
            (int)SyncCloudObjectKeyKind.NativeObject == 0 && (int)SyncCloudObjectKeyKind.SharedNamespaceMount == 1 &&
            (int)SyncProviderOperation.Read == 0 && (int)SyncProviderOperation.Write == 1 &&
            (int)SyncCloudFileAvailability.NotManaged == 0 && (int)SyncCloudFileAvailability.OnlineOnly == 1 &&
            (int)SyncCloudFileAvailability.PartiallyHydrated == 2 && (int)SyncCloudFileAvailability.AvailableOffline == 3,
            "moved enum ordinals remain persisted identities");
        Require(typeof(PilotRemoteEntry).FullName == "Massdex.SyncShark.PilotRemoteEntry" &&
            typeof(SyncCloudObjectKey).FullName == "Massdex.SyncShark.SyncCloudObjectKey" &&
            typeof(LocalGapProof).FullName == "Massdex.SyncShark.V2.LocalGapProof",
            "moved contracts retain CLR names");
        var key = SyncCloudObjectKey.Native(" DROPBOX ", "id:item");
        Require(key == new SyncCloudObjectKey("dropbox", SyncCloudObjectKeyKind.NativeObject, "id:item") &&
            key != key with { Value = "id:ITEM" }, "cloud key retains normalized provider and ordinal value equality");
        const string remoteJson = "{\"RelativePath\":\"Child\",\"Kind\":0,\"Id\":\"id:item\",\"Revision\":\"rev\",\"Size\":12,\"ContentHash\":\"hash\",\"ModifiedUtc\":null,\"TransferBlockedReason\":null,\"ObjectAliases\":[{\"Provider\":\"dropbox\",\"Kind\":0,\"Value\":\"id:item\"}],\"ClientModifiedUtc\":null,\"RequiresExport\":true,\"ExportAs\":\"pdf\",\"ExportOptions\":[\"pdf\"]}";
        var remote = JsonSerializer.Deserialize<PilotRemoteEntry>(remoteJson)!;
        Require(remote.ObjectAliases![0] == key && remote.RequiresExport && remote.ExportAs == "pdf" &&
            remote.ExportOptions!.SequenceEqual(["pdf"]) && remote == remote with { }, "remote JSON and record equality survive relocation");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(remote));
        Require(json.RootElement.GetProperty("RelativePath").GetString() == "Child" &&
            json.RootElement.GetProperty("ObjectAliases")[0].GetProperty("Value").GetString() == "id:item" &&
            json.RootElement.TryGetProperty("ClientModifiedUtc", out _) && !json.RootElement.TryGetProperty("relativePath", out _),
            "remote PascalCase SQL JSON paths and optional properties survive relocation");
        var proof = new LocalGapProof($"{0xabUL:x16}:20", new NtfsFileUsn(1, 2, 3, 4, 5) { RequiresHash = true });
        var proofJson = JsonSerializer.Serialize(proof);
        Require(JsonSerializer.Deserialize<LocalGapProof>(proofJson) == proof && proofJson.Contains("\"File\"") &&
            proofJson.Contains("\"RequiresHash\":true"), "proof JSON retains nested evidence and equality");
        var checkpoint = new NtfsUsnCheckpoint(0xab, 10, 30, 15);
        Require(checkpoint.Cursor == $"{0xabUL:x16}:30" &&
            NtfsUsnCheckpoint.TryParseCursor("AB:20", out var journal, out var next) && journal == 0xab && next == 20 &&
            !checkpoint.RequiresFullReconciliation("ab:15") && !checkpoint.RequiresFullReconciliation("ab:30") &&
            checkpoint.RequiresFullReconciliation("ab:14") && checkpoint.RequiresFullReconciliation("ab:31") &&
            checkpoint.RequiresFullReconciliation("ac:20") && checkpoint.RequiresFullReconciliation("invalid"),
            "checkpoint hexadecimal cursor and inclusive reconciliation bounds remain unchanged");
        var before = 0; var released = 0;
        var previousSource = SyncUploadSource.Current.Value;
        var previousStaging = SyncEffectStaging.Current.Value;
        var observer = SyncUploadSource.BeforeUploadObserved;
        using var stream = new MemoryStream();
        await using var source = new SyncUploadSource(stream, () => before++);
        var staging = new SyncEffectStaging(() => released++);
        try
        {
            SyncUploadSource.BeforeUploadObserved = () => before++;
            SyncUploadSource.Current.Value = source;
            SyncEffectStaging.Current.Value = staging;
            await Task.Yield();
            Require(ReferenceEquals(SyncUploadSource.Current.Value, source) &&
                ReferenceEquals(SyncEffectStaging.Current.Value, staging), "ambient state flows through await with same owner");
            source.BeforeUpload(); staging.Release(); staging.Release();
            using var other = new MemoryStream();
            await source.ReleaseAsync(other);
            Require(stream.CanRead && before == 2 && released == 1, "ambient callbacks and release identity remain unchanged");
            await source.ReleaseAsync(stream);
            Require(!stream.CanRead, "source releases only its owned stream");
        }
        finally
        {
            SyncUploadSource.Current.Value = previousSource;
            SyncEffectStaging.Current.Value = previousStaging;
            SyncUploadSource.BeforeUploadObserved = observer;
        }
        var touched = 0;
        using var inner = new MemoryStream([1]);
        using (var tracked = RunOperationStream.Touching(inner, () => touched++))
            Require(tracked.ReadByte() == 1 && touched == 1, "moved stream still invokes progress callback");
        Require(inner.CanRead, "touch wrapper retains leave-open semantics");
        ProgressGuard.Invoke(() => touched++);
        Require(touched == 2, "moved progress guard invokes ambient callback");
        NativeIoDiagnosticChecks.Run();
    }
}
