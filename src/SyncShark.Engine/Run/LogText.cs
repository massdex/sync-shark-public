using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Renders observed facts without substituting a successful operation for an unknown one.</summary>
public static class LogText
{
    /// <summary>Formats a live event at observation time.</summary>
    public static string Format(RunEvent value) => Format(value, DateTimeOffset.UtcNow);

    /// <summary>Formats a historical event at its recorded time, not the time the reader opened the log.</summary>
    public static string Format(RunEvent value, DateTimeOffset recordedAt)
    {
        if (value.Kind == RunEventKind.Effect) return EffectText(value, recordedAt);
        if (value.Kind == RunEventKind.ProviderRequest && value.Outcome.Length != 0) return string.Empty;
        var clock = DisplayClock(recordedAt);
        switch (value.Kind)
        {
            case RunEventKind.Stage: return StageText(value, clock);
            case RunEventKind.ProviderRequest:
                var active = Text.Get(TextKey.HumanStageActive, (TextArg.Time, clock));
                return value.Detail.Length == 0 ? active : active + Environment.NewLine + Text.Get(TextKey.HumanIndent) + RunLog.EscapeDisplay(value.Detail);
            case RunEventKind.Wait:
                return AttentionText(value, clock, TextKey.HumanWaitLabel, null) + Environment.NewLine + Text.Get(TextKey.HumanWaitActivity);
            case RunEventKind.Failed:
                var stopped = value.Error is OperationCanceledException || value.What == nameof(OperationCanceledException);
                return AttentionText(value, clock, stopped ? TextKey.HumanStopped : TextKey.HumanError,
                    stopped ? TextKey.HumanAttentionStoppedResult : null);
            default:
                var notice = value.Notice ?? (EnumNames.TryExact<NoticeKind>(value.What, out var kind) ? kind : (NoticeKind?)null);
                var informational = notice is NoticeKind.Info or NoticeKind.Skipped;
                return AttentionText(value, clock, informational ? TextKey.HumanNotice : notice == NoticeKind.Failure
                    ? TextKey.HumanAttentionFileNotSynced : TextKey.HumanAttention,
                    informational ? TextKey.HumanAttentionAutomaticResult : TextKey.HumanAttentionUnsyncedResult);
        }
    }

    private static string StageText(RunEvent item, string clock)
    {
        var typed = EnumNames.TryExact<TextKey>(item.What, out var key);
        var name = typed ? Text.Get(key) : RunLog.EscapeDisplay(item.What);
        var unit = key switch
        {
            TextKey.StageLocalWalk or TextKey.StageDropboxListing or TextKey.StageListing => TextKey.HumanUnitObservations,
            TextKey.StageRows => TextKey.HumanUnitRows, TextKey.StageDropboxPages => TextKey.HumanUnitPages,
            TextKey.StageRenames => TextKey.HumanUnitGroups, TextKey.StageFolderPlans => TextKey.HumanUnitMembers,
            _ => (TextKey?)null,
        };
        var ended = item.Ended || item.Outcome == RunLog.EndedOutcome;
        // The run's completion line: the finished banner, then its applied count and elapsed time.
        if (typed && ended && key == TextKey.ResultRunCompleted)
            return Text.Get(TextKey.HumanStageHeading, (TextArg.Time, clock), (TextArg.Name, Text.Get(TextKey.HumanFinished,
                (TextArg.Name, Text.Get(TextKey.HumanTitleRepair)), (TextArg.Status, Text.Get(TextKey.HumanFinishedComplete)), (TextArg.Suffix, string.Empty))))
                + Environment.NewLine + RunLog.EscapeDisplay(item.Detail);
        var heading = Text.Get(TextKey.HumanStageHeading, (TextArg.Time, clock), (TextArg.Name, name));
        var builder = new StringBuilder(heading);
        if (item.Outcome == nameof(RunEventKind.Failed))
            builder.AppendLine().Append(Text.Get(TextKey.HumanStageInterrupted, (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
                (TextArg.Name, name), (TextArg.Elapsed, Text.Get(TextKey.HumanNotMeasured))));
        else if (ended)
        {
            var count = item.Count?.ToString(CultureInfo.InvariantCulture);
            if (count is not null && item.Total is { } total) count += "/" + total.ToString(CultureInfo.InvariantCulture);
            builder.AppendLine().Append(Text.Get(count is null ? TextKey.HumanStageCompleteNoCount : TextKey.HumanStageComplete,
                (TextArg.Indent, Text.Get(TextKey.HumanIndent)), (TextArg.Name, name), (TextArg.Count, count ?? string.Empty),
                (TextArg.Unit, typed && unit is { } u ? Text.Get(u) : string.Empty), (TextArg.Elapsed, Text.Get(TextKey.HumanNotMeasured))));
        }
        var detail = ended && item.Count is not null ? item.Detail : RunLog.Present(item).Detail;
        if (detail.Length != 0) builder.AppendLine().Append(RunLog.EscapeDisplay(detail));
        return builder.ToString();
    }

    /// <summary>A live line under the current time, as every block heading shows it.</summary>
    internal static string Heading(string text) =>
        Text.Get(TextKey.HumanStageHeading, (TextArg.Time, DisplayClock(DateTimeOffset.UtcNow)), (TextArg.Name, text));

    private static string DisplayClock(DateTimeOffset at) =>
        at.UtcDateTime.ToString(Text.Get(TextKey.HumanClockFormat), CultureInfo.InvariantCulture);

    private static string EffectText(RunEvent item, DateTimeOffset recordedAt)
    {
        var action = item.Action ?? (EnumNames.TryExact<ActionKind>(item.What, out var kind) ? kind : item.What switch
        {
            "Uploaded" => ActionKind.Upload, "Downloaded" => ActionKind.Download,
            "FolderCreated" => ActionKind.EnsureLocalFolder, "DeletedLocal" or "DeletedDropbox" => ActionKind.DeleteLocal,
            "MovedLocal" or "MovedDropbox" or nameof(ISide.MoveAsync) => ActionKind.MoveLocal,
            "KeptBoth" => ActionKind.KeepBoth, "Recorded" => ActionKind.Record, "Forgotten" => ActionKind.ForgetOnly,
            "Replaced" => ActionKind.ReplaceKind, _ => ActionKind.None,
        });
        var move = action is ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked;
        var committed = RunLog.IsCommittedEffect(item.Outcome);
        if (move && committed && (item.Detail.Length == 0 || item.Path.Length == 0)) return string.Empty;
        var clock = DisplayClock(recordedAt);
        var side = item.Side ?? (EnumNames.TryExact<Side>(item.Outcome, out var endSide) ? endSide : (Side?)null);
        var card = committed ? CardText(action) : null;
        var heading = card is { } known ? Text.Get(TextKey.HumanCardHeading, (TextArg.Time, clock),
            (TextArg.Label, Text.Get(TextKey.HumanOkLabel)), (TextArg.Operation, Text.Get(known.Operation))) :
            Text.Get(TextKey.HumanStageHeading, (TextArg.Time, clock), (TextArg.Name, RunLog.EscapeDisplay(item.What)));
        var builder = new StringBuilder(heading);
        if (card.HasValue && (move || action == ActionKind.KeepBoth))
        {
            AppendLine(builder, move ? TextKey.HumanCardFrom : TextKey.HumanCardOriginal, TextArg.Path, item.Detail);
            AppendLine(builder, move ? TextKey.HumanCardTo : TextKey.HumanCardCopy, TextArg.Path, item.Path);
        }
        else
        {
            AppendLine(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
            AppendLine(builder, TextKey.HumanAttentionReason, TextArg.Message, item.Detail);
        }
        if (action is ActionKind.Upload or ActionKind.Download)
            AppendLine(builder, TextKey.HumanCardDirection, TextArg.Message, Text.Get(action == ActionKind.Upload ? TextKey.HumanDirectionUpload : TextKey.HumanDirectionDownload));
        if (side is { } endpoint) AppendLine(builder, TextKey.HumanCardSide, TextArg.Side, Text.Get(endpoint == Side.Local ? TextKey.HumanSideLocal : TextKey.HumanSideDropbox));
        if (card is { } result) AppendLine(builder, TextKey.HumanCardResult, TextArg.Message, Text.Get(result.Result));
        else AppendLine(builder, TextKey.HumanCardResult, TextArg.Message, item.Outcome);
        return builder.ToString();
    }

    private static string AttentionText(RunEvent value, string clock, TextKey label, TextKey? result)
    {
        var item = RunLog.Present(value);
        var builder = new StringBuilder(Text.Get(TextKey.HumanAttentionHeading, (TextArg.Time, clock), (TextArg.Label, Text.Get(label))));
        AppendLine(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
        AppendLine(builder, TextKey.HumanAttentionReason, TextArg.Message, item.Detail.Length == 0 ? item.What : item.Detail);
        if (result is { } key) AppendLine(builder, TextKey.HumanAttentionResult, TextArg.Message, Text.Get(key));
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, TextKey template, TextArg argument, string value)
    {
        if (value.Length != 0) builder.AppendLine().Append(Text.Get(template, (argument, RunLog.EscapeDisplay(value))));
    }

    private static (TextKey Operation, TextKey Result)? CardText(ActionKind action) => action switch
    {
        ActionKind.Upload => (TextKey.HumanOperationUpload, TextKey.HumanResultSynced),
        ActionKind.Download => (TextKey.HumanOperationDownload, TextKey.HumanResultSynced),
        ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder => (TextKey.HumanOperationFolderCreate, TextKey.HumanResultFolderCreated),
        ActionKind.DeleteDropbox or ActionKind.DeleteLocal => (TextKey.HumanOperationRemove, TextKey.HumanResultRemovalCommitted),
        ActionKind.MoveDropbox or ActionKind.MoveLocal or ActionKind.MoveUnlinked => (TextKey.HumanOperationMove, TextKey.HumanResultMoveCommitted),
        ActionKind.KeepBoth => (TextKey.HumanOperationKeepBoth, TextKey.HumanResultKeptBoth),
        ActionKind.Record => (TextKey.HumanOperationRecorded, TextKey.HumanResultRecorded),
        ActionKind.ForgetOnly => (TextKey.HumanOperationForgotten, TextKey.HumanResultForgotten),
        ActionKind.ReplaceKind => (TextKey.HumanOperationReplace, TextKey.HumanResultReplaced),
        _ => null,
    };
}
