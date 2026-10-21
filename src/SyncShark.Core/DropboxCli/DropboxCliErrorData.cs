using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The compact failure receipt for a Dropbox provider error.</summary>
public sealed record DropboxCliErrorData(string? Route, int? HttpStatus, string? ErrorSummary, JsonElement? Error,
    bool EffectUncertain) : IDropboxCliData;
