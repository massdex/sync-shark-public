using System.ComponentModel;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Structured proof diagnostics retained in existing failure-reason fields, without a schema change.</summary>
public sealed record ContentProofFailure(ContentProofFailure.StageKind Stage, string Cause, int? NativeError = null)
{
    public enum StageKind { Eligibility, Classification, ParentIdentity, Journal, VolumeOpen, ContentOpen, BeforeRead, Read, AfterRead, PathIdentity, RootOpen }
    internal const string Prefix = "content-proof:";
    internal static readonly StageKind[] ItemStages = [StageKind.Classification, StageKind.ContentOpen,
        StageKind.BeforeRead, StageKind.Read, StageKind.AfterRead, StageKind.PathIdentity];
    internal bool ItemScoped => ItemStages.Contains(Stage);
    public string Encode() => Prefix + JsonSerializer.Serialize(this);
    public static ContentProofFailure? Decode(string? text)
    {
        if (text?.StartsWith(Prefix, StringComparison.Ordinal) != true) return null;
        try { return JsonSerializer.Deserialize<ContentProofFailure>(text[Prefix.Length..]); }
        catch (JsonException) { return null; }
    }
    public static ContentProofFailure FromException(StageKind stage, Exception error) => new(stage, error.Message,
        error is Win32Exception native ? native.NativeErrorCode :
        (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : null);
    public static ContentProofFailure FromDiagnostic(StageKind stage, string cause)
    {
        var opening = cause.LastIndexOf('(');
        var code = opening >= 0 && cause.EndsWith(')') &&
            int.TryParse(cause.AsSpan(opening + 1, cause.Length - opening - 2), out var parsed) ? (int?)parsed : null;
        return new(stage, cause, code);
    }
    public string Display => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        MessageCatalog.Default.Message("proof-failure-display"), Stage, Cause,
        NativeError is { } code ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Message("proof-native-display"), code) : "");
}
