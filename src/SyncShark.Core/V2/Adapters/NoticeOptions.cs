namespace Massdex.SyncShark.V2;

/// <summary>Projects the shared catalogue for the legacy display surface; no admission policy.</summary>
public static class NoticeOptions
{
    public static IReadOnlyList<SyncNoticeOption> Options(Notice notice) =>
        NoticeChoices.Catalogue(notice)
            .Select(option => new SyncNoticeOption(option.Choice, option.Label, option.RequestsName)).ToArray();
}
