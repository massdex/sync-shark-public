namespace Massdex.SyncShark.V2;

internal sealed record ActionKindProgress(ActionKind Kind, long Done, long Total);
