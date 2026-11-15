namespace Massdex.SyncShark.Engine;

/// <summary>Submission receipt certainty, not authority to settle mappings or complete a folder removal.</summary>
public abstract record FolderSubmitResult
{
    /// <summary>A positively established refusal before any member effect.</summary>
    public sealed record KnownNoEffect(TextKey? Cause) : FolderSubmitResult;

    /// <summary>A provider job receipt whose members still require reconciliation.</summary>
    public sealed record Accepted(string JobId) : FolderSubmitResult;

    /// <summary>Completed member receipts attributed by their original wire ordinals, not by path.</summary>
    public sealed record Terminal(IReadOnlyList<FolderMemberResult> Results) : FolderSubmitResult;

    /// <summary>A possibly submitted effect without sufficient receipt evidence; never permission to replay.</summary>
    public sealed record Unknown(TextKey? Cause) : FolderSubmitResult;
}
