using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The sole shared notice renderer (LOCAL-IN-USE D1): one issue's neutral cause, then the retry
/// guidance its current presentation state owns. CLI and UI call it; text selection stays here, not in them.</summary>
public static class IssuePresentation
{
    /// <summary>The issue's neutral cause sentence, followed for the in-use retry family by the guidance for its
    /// current state. The cause never promises background activity; only the guidance names the real opportunities.</summary>
    public static string Render(Issue issue, IssueRetryView retry)
    {
        if (issue.Cause == TextKey.InfoKeptBoth) return KeptBoth(issue);
        var cause = Text.Get(issue.Cause, (TextArg.Path, issue.Path), (TextArg.Message, ""));
        return IssuePolicy.IsLocalAccessRetryCause(issue.Cause)
            ? cause + Environment.NewLine + Text.Get(StateKey(retry.State))
            : cause;
    }

    private static string KeptBoth(Issue issue)
    {
        var items = issue.Subject.Items;
        var twoLegs = items is [_, _, { Side: Side.Dropbox }, { Side: Side.Local }];
        var oneLeg = items is [_, _, _];
        if ((!twoLegs && !oneLeg) ||
            items is not [{ Side: Side.Local } local, { Side: Side.Dropbox } dropbox, var copy, ..])
            return Text.Get(issue.Cause, (TextArg.Path, issue.Path));

        var keptLocal = oneLeg && copy.Side == Side.Local;
        var retained = keptLocal ? local : dropbox;
        var path = oneLeg ? retained.Path : issue.Path;
        var localVersion = twoLegs
            ? Text.Get(TextKey.InfoKeptBothLocalVersion, (TextArg.Path, local.Path), (TextArg.Name, copy.Path))
            : Text.Get(keptLocal ? TextKey.InfoKeptBothLocalOnly : TextKey.InfoKeptBothDropboxOnly,
                (TextArg.Path, retained.Path), (TextArg.Name, copy.Path));
        var dropboxVersion = twoLegs
            ? Environment.NewLine + Text.Get(TextKey.InfoKeptBothDropboxVersion,
                (TextArg.Path, dropbox.Path), (TextArg.Name, items[^1].Path)) : string.Empty;
        return Text.Get(oneLeg ? TextKey.InfoKeptBothRetained : issue.Cause, (TextArg.Path, path)) + Environment.NewLine +
            Text.Get(TextKey.InfoKeptBothLocations) + Environment.NewLine + localVersion + dropboxVersion;
    }

    /// <summary>The choices one notice offers, with their data-owned labels; Run still validates every exact
    /// choice. The ignored-folder-kept cause retains its special one-off and always labels.</summary>
    public static IReadOnlyList<SyncNoticeOption> Options(Issue issue)
    {
        return Run.EngineOperations.ResolutionChoices(issue).Select(choice =>
            new SyncNoticeOption(choice.ToString(), choice switch
            {
                ResolutionChoice.BindRoots => Text.Get(TextKey.ResolutionBindRootsLabel),
                ResolutionChoice.Unignore => Text.Get(TextKey.RestoreLabel),
                ResolutionChoice.Dismiss when issue.Cause == TextKey.InfoIgnoredFolderKept =>
                    EngineCliCatalog.Message(EngineMessageKey.KeepThisTimeLabel),
                ResolutionChoice.KeepFolderAlways => EngineCliCatalog.Message(EngineMessageKey.KeepFolderAlwaysLabel),
                _ => EngineCliCatalog.OptionValue(EngineOptionKey.Choice, choice.ToString())
            })).ToArray();
    }

    /// <summary>The one extra value a choice requires from the user, or null when it needs none.</summary>
    public static EngineOptionKey? InputKey(ResolutionChoice choice) => choice switch
    {
        ResolutionChoice.KeepFolderAlways => EngineOptionKey.Rules,
        ResolutionChoice.RenameIncoming or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox =>
            EngineOptionKey.Name,
        _ => null
    };

    public static string ResolutionConfirmation(Issue issue, ResolutionChoice choice)
    {
        if (!Run.EngineOperations.ResolutionChoices(issue).Contains(choice))
            throw new ArgumentException(null, nameof(choice));
        if (IssuePolicy.RetryRunsRepair(issue, choice))
            return Text.Get(TextKey.ResolutionRetryRepairConsequence);
        var confirmation = ResolutionConfirmation(choice);
        if (choice != ResolutionChoice.Ignore) return confirmation;
        var consequence = IssuePolicy.IgnoreConsequence(issue.Kind, issue.Cause);
        return consequence is null ? confirmation : confirmation + Environment.NewLine + Text.Get(consequence.Value);
    }

    public static string ResolutionConfirmation(ResolutionChoice choice)
    {
        var key = choice switch
        {
            ResolutionChoice.UseLocal or ResolutionChoice.KeepLocal => TextKey.ResolutionUseLocalConsequence,
            ResolutionChoice.UseDropbox or ResolutionChoice.KeepDropbox => TextKey.ResolutionUseDropboxConsequence,
            ResolutionChoice.KeepBoth => TextKey.ResolutionKeepBothConsequence,
            ResolutionChoice.Ignore => TextKey.ResolutionIgnoreConsequence,
            ResolutionChoice.Unignore => TextKey.ResolutionRestoreConsequence,
            ResolutionChoice.Dismiss => TextKey.ResolutionDismissConsequence,
            ResolutionChoice.RenameIncoming or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox => TextKey.ResolutionRenameConsequence,
            ResolutionChoice.ReplaceExisting => TextKey.ResolutionReplaceConsequence,
            ResolutionChoice.Retry => TextKey.ResolutionRetryConsequence,
            ResolutionChoice.FirstSync => TextKey.ResolutionFirstSyncConsequence,
            ResolutionChoice.SyncNormally => TextKey.ResolutionSyncNormallyConsequence,
            ResolutionChoice.KeepFolderAlways => TextKey.ResolutionKeepFolderConsequence,
            ResolutionChoice.BindRoots => TextKey.ResolutionBindRootsConsequence,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
        return Text.Get(key);
    }

    /// <summary>Projects exact member eligibility without treating a staged locator as owned bytes.</summary>
    public static IReadOnlyList<RecoveryChoice> RecoveryChoices(RecoveryView view, FolderRemovalItem? item = null)
    {
        if (item is null) return view.AllowedChoices;
        return view.FolderPhase is null || item.OperationId != view.OperationId
            ? [] : FolderRetirement.ItemChoices(item);
    }

    /// <summary>One scope-specific consequence for the exact offered recovery choice.</summary>
    public static string RecoveryConfirmation(string pairId, RecoveryView view, RecoveryChoice choice,
        FolderRemovalItem? item = null)
    {
        if (!RecoveryChoices(view, item).Contains(choice)) throw new ArgumentException(null, nameof(choice));
        var key = choice switch
        {
            RecoveryChoice.Check when view.IsLegacy => TextKey.RecoveryLegacyCheckConsequence,
            RecoveryChoice.Check when view.FolderPhase is not null => TextKey.RecoveryFolderCheckConsequence,
            RecoveryChoice.Check => TextKey.RecoveryCheckConsequence,
            RecoveryChoice.AcceptCurrent when item is not null => TextKey.RecoveryAcceptFolderItemConsequence,
            RecoveryChoice.AcceptCurrent when view.FolderPhase is not null => TextKey.RecoveryAcceptFolderOperationConsequence,
            RecoveryChoice.AcceptCurrent => TextKey.RecoveryAcceptCurrentConsequence,
            RecoveryChoice.AbandonIncoming => TextKey.RecoveryAbandonIncomingConsequence,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
        var heading = OperationHeading(pairId, view);
        if (item is not null)
            heading += Environment.NewLine + Text.Get(TextKey.RecoveryItemHeading,
                (TextArg.Id, item.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (TextArg.Value, item.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (TextArg.Path, item.Path));
        // The exact evidence paths the user is deciding about stay with the shared renderer, not the window.
        return heading + Environment.NewLine + Text.Get(key) + Environment.NewLine + view.Path +
            Environment.NewLine + view.StagePath + Environment.NewLine + view.BinLocator +
            Environment.NewLine + (item?.Stage?.Path ?? string.Empty) +
            Environment.NewLine + (item?.StageAllocation?.Path ?? string.Empty);
    }

    private static string OperationHeading(string pairId, RecoveryView view) =>
        Text.Get(TextKey.RecoveryOperationHeading, (TextArg.Pair, pairId), (TextArg.Id, view.OperationId),
            (TextArg.Value, view.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The operation's recovery evidence: the shared heading, exact paths, phase fact and recorded cause.</summary>
    public static string RecoveryDetail(string pairId, RecoveryView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return OperationHeading(pairId, view) + Environment.NewLine + view.Path + Environment.NewLine +
            view.StagePath + Environment.NewLine + view.BinLocator + Environment.NewLine + view.FolderStatus +
            (view.Cause is { } cause
                ? Environment.NewLine + Text.Get(cause, (TextArg.Path, view.Path), (TextArg.Message, string.Empty))
                : string.Empty);
    }

    /// <summary>Renders the actual verified or retained result, never inferring acknowledgement from a choice.</summary>
    public static string RecoveryResult(RemovalResult result, string? knownPath = null)
    {
        var path = result.Final?.Path ?? knownPath;
        var key = result.Outcome switch
        {
            RemovalOutcome.Removed => TextKey.RecoveryRemoved,
            RemovalOutcome.Published when path is not null => TextKey.RecoveryPublished,
            RemovalOutcome.NoEffect => TextKey.RecoveryNoEffect,
            RemovalOutcome.WrongRestored when path is not null => TextKey.RecoveryWrongRestored,
            RemovalOutcome.Retained when path is not null => TextKey.RecoveryRetained,
            _ when path is not null => TextKey.RecoveryUnknown,
            _ => TextKey.RecoveryUnknownOperation
        };
        if (key == TextKey.RecoveryUnknownOperation && result.OperationId is null)
            return Text.Get(TextKey.RecoveryVictimUnknown);
        var message = Text.Get(key, (TextArg.Path, path ?? string.Empty), (TextArg.Id, result.OperationId ?? string.Empty));
        return result.Outcome == RemovalOutcome.Published
            ? message + Environment.NewLine + Text.Get(TextKey.RecoveryPublishedBytesLimits)
            : message;
    }

    /// <summary>The issue's retry view, or the one missing-authority answer when Run supplied none.</summary>
    public static IssueRetryView View(IReadOnlyDictionary<long, IssueRetryView>? retryViews, Issue issue) =>
        retryViews is not null && retryViews.TryGetValue(issue.Id, out var view) ? view : Unavailable;

    private static readonly IssueRetryView Unavailable = new(RetryPresentationState.Unavailable);

    /// <summary>The stable key of the host's transient status, owned here so the record carries data only.</summary>
    public static TextKey HostStatusKey(AutoStatus? status) => status switch
    {
        AutoStatus.Running => TextKey.StatusStillWorking,
        AutoStatus.Waiting => TextKey.StatusStillWaiting,
        AutoStatus.Idle => TextKey.StatusWatching,
        AutoStatus.Paused => TextKey.StatusPaused,
        AutoStatus.Off => TextKey.StatusDisabled,
        AutoStatus.Held => TextKey.StatusNeedsAttention,
        _ => TextKey.StatusUnavailable
    };

    public static string WorkingStatus(string? name) => string.IsNullOrEmpty(name)
        ? Text.Get(TextKey.StatusStillQuiet)
        : Text.Get(TextKey.StatusStillWorking, (TextArg.Name, name));

    /// <summary>The one host-status sentence: an explicit message, else the key's template with its detail.</summary>
    public static string RenderHostStatus(AutoStatus? status, string? detail, string? message)
    {
        var key = HostStatusKey(status);
        return message ?? (key switch
        {
            TextKey.StatusStillWorking => WorkingStatus(detail),
            TextKey.StatusStillWaiting => Text.Get(key, (TextArg.Reason, detail ?? string.Empty)),
            _ => Text.Get(key)
        });
    }

    private static TextKey StateKey(RetryPresentationState state) => state switch
    {
        RetryPresentationState.Running => TextKey.RetryRunning,
        RetryPresentationState.Waiting => TextKey.RetryWaiting,
        RetryPresentationState.Offline => TextKey.RetryOffline,
        RetryPresentationState.Paused => TextKey.RetryPaused,
        RetryPresentationState.Disabled or RetryPresentationState.RepairOnly => TextKey.RetryRepairOnly,
        RetryPresentationState.RecoveryRequired => TextKey.RetryRecoveryRequired,
        RetryPresentationState.Unavailable => TextKey.RetryUnavailable,
        // Any value outside the declared states is missing authority, never an active retry.
        _ => TextKey.RetryUnavailable
    };
}
