using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed record AutoSyncHostReply(int Version, string RequestId, AutoSyncHostIdentity Host,
    string State, bool Ok, JsonElement? Data, SyncCliError? Error);
