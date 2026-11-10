using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Issue continuation: the stored-row lookup and the rule that decides whether a raise continues an issue.
/// </summary>
public static partial class IssueTable
{
    /// <summary>The stored issue of one kind at an exact path: the one lookup a raise decides over.</summary>
    internal static Issue? AtPath(SqliteConnection connection, SqliteTransaction? transaction, string pairId,
        string path, IssueKind kind) => ReadOne(connection, transaction, StateSchema.SelectIssueByPathKind,
        (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path), (StateSchema.ParamKind, (int)kind));

    /// <summary>True when the stored issue continues a raise of this cause and items, instead of a new raise.</summary>
    internal static bool Continues([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Issue? existing,
        TextKey cause, IssueSubject subject) =>
        existing is not null && existing.Cause == cause && existing.Subject.HasSameItems(subject);

    /// <summary>True when a continuing issue must still refresh its stored local proof or first-run marker.</summary>
    internal static bool Refreshes(Issue existing, IssueSubject subject) =>
        existing.Subject.LocalHash != subject.LocalHash ||
        existing.Subject.FirstRunDeletion != subject.FirstRunDeletion;
}
