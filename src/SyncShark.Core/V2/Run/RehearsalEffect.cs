namespace Massdex.SyncShark.V2;

internal sealed record RehearsalEffect(string Kind, string Side, string Provider, string Path, long Size, string? Hash);
