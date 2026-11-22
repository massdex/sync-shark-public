using System.Text;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The one readable projection of a run event, built from the text catalog so a human sees the approved
/// labels, reasons and results instead of a raw record. Present facts are shown, empty facts omitted, data values are
/// escaped, and typed facts (a stage end, a failure, a notice, an action) reach the appropriate block. The stored
/// canonical TSV and its timestamp are RunLog's; this projection is for display (D-1105-6, M22, product-0689).</summary>
public static class LogText
{
    /// <summary>Projects one event to its readable block.</summary>
    public static string Format(RunEvent value)
    {
        var item = RunLog.Present(value);
        return value.Kind switch
        {
            RunEventKind.Stage => StageText(item),
            RunEventKind.Effect => EffectText(item),
            RunEventKind.Issue => AttentionText(item, NoticeLabel(value), NoticeResult(value)),
            RunEventKind.Failed => AttentionText(item, StoppedLabel(value), TextKey.HumanAttentionFailedResult),
            RunEventKind.Wait => WaitText(item),
            _ => AttentionText(item, TextKey.HumanAttention, TextKey.HumanAttentionAutomaticResult),
        };
    }

    private static string StageText(RunEvent item)
    {
        var name = RunLog.EscapeDisplay(item.What);
        if (item.Ended)
            return Text.Get(TextKey.HumanStageCompleteNoCount,
                (TextArg.Indent, Text.Get(TextKey.HumanIndent)), (TextArg.Name, name), (TextArg.Elapsed, string.Empty));
        return Text.Get(TextKey.HumanStageHeading, (TextArg.Time, string.Empty), (TextArg.Name, name));
    }

    private static string EffectText(RunEvent item)
    {
        var (operation, result) = CardText(item.Action);
        var builder = new StringBuilder();
        builder.Append(Text.Get(TextKey.HumanCardHeading,
            (TextArg.Time, string.Empty), (TextArg.Label, Text.Get(TextKey.HumanOkLabel)),
            (TextArg.Operation, Text.Get(operation))));
        AppendLine(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
        if (DirectionKey(item.Action) is { } direction)
            builder.AppendLine().Append(Text.Get(TextKey.HumanCardDirection, (TextArg.Message, Text.Get(direction))));
        if (item.Side is { } side)
            builder.AppendLine().Append(Text.Get(TextKey.HumanCardSide, (TextArg.Side, Text.Get(SideKey(side)))));
        builder.AppendLine().Append(Text.Get(TextKey.HumanCardResult, (TextArg.Message, Text.Get(result))));
        return builder.ToString();
    }

    private static string AttentionText(RunEvent item, TextKey label, TextKey result)
    {
        var builder = new StringBuilder();
        builder.Append(Text.Get(TextKey.HumanAttentionHeading,
            (TextArg.Time, string.Empty), (TextArg.Label, Text.Get(label))));
        AppendLine(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
        AppendLine(builder, TextKey.HumanAttentionReason, TextArg.Message, item.Detail);
        builder.AppendLine().Append(Text.Get(TextKey.HumanAttentionResult, (TextArg.Message, Text.Get(result))));
        return builder.ToString();
    }

    private static string WaitText(RunEvent item)
    {
        var builder = new StringBuilder();
        builder.Append(Text.Get(TextKey.HumanAttentionHeading,
            (TextArg.Time, string.Empty), (TextArg.Label, Text.Get(TextKey.HumanWaitLabel))));
        AppendLine(builder, TextKey.HumanWaitReason, TextArg.Message, item.Detail);
        builder.AppendLine().Append(Text.Get(TextKey.HumanWaitActivity));
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, TextKey template, TextArg argument, string value)
    {
        if (value.Length == 0) return;
        builder.AppendLine().Append(Text.Get(template, (argument, RunLog.EscapeDisplay(value))));
    }

    private static TextKey NoticeLabel(RunEvent value) =>
        value.Notice is NoticeKind.Info or NoticeKind.Skipped ? TextKey.HumanNotice : TextKey.HumanAttention;

    private static TextKey NoticeResult(RunEvent value) =>
        value.Notice is NoticeKind.Info or NoticeKind.Skipped
            ? TextKey.HumanAttentionAutomaticResult
            : TextKey.HumanAttentionUnsyncedResult;

    private static TextKey StoppedLabel(RunEvent value) =>
        value.Error is OperationCanceledException ? TextKey.HumanStopped : TextKey.HumanError;

    private static TextKey SideKey(Side side) => side == Side.Local ? TextKey.HumanSideLocal : TextKey.HumanSideDropbox;

    private static TextKey? DirectionKey(ActionKind? action) => action switch
    {
        ActionKind.Upload => TextKey.HumanDirectionUpload,
        ActionKind.Download => TextKey.HumanDirectionDownload,
        _ => null,
    };

    private static (TextKey Operation, TextKey Result) CardText(ActionKind? action) => action switch
    {
        ActionKind.Upload => (TextKey.HumanOperationUpload, TextKey.HumanResultSynced),
        ActionKind.Download => (TextKey.HumanOperationDownload, TextKey.HumanResultSynced),
        ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder =>
            (TextKey.HumanOperationFolderCreate, TextKey.HumanResultFolderCreated),
        ActionKind.DeleteDropbox or ActionKind.DeleteLocal =>
            (TextKey.HumanOperationRemove, TextKey.HumanResultRemovalCommitted),
        ActionKind.MoveDropbox or ActionKind.MoveLocal or ActionKind.MoveUnlinked =>
            (TextKey.HumanOperationMove, TextKey.HumanResultMoveCommitted),
        ActionKind.KeepBoth => (TextKey.HumanOperationKeepBoth, TextKey.HumanResultKeptBoth),
        ActionKind.Record => (TextKey.HumanOperationRecorded, TextKey.HumanResultRecorded),
        ActionKind.ForgetOnly => (TextKey.HumanOperationForgotten, TextKey.HumanResultForgotten),
        _ => (TextKey.HumanOperationReplace, TextKey.HumanResultReplaced),
    };
}
