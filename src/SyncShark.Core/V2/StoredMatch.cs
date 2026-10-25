using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

/// <summary>
/// One path the prepare saw on both sides, with a compact per-side identity and content stamp .
/// Short JSON names keep the record small: a first sync can match hundreds of thousands of paths.
/// </summary>
internal sealed record StoredMatch([property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("a")] string A, [property: JsonPropertyName("b")] string B);
