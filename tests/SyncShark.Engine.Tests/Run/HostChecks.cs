namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for Host.</summary>
public static class HostChecks
{
    public static Task FailedMemberReadAsync() => throw new NotImplementedException();
    public static Task RepairReplaysJournalAsync() => throw new NotImplementedException();

    public static Task PublicationRetryAsync() => throw new NotImplementedException();
    public static Task ExclusionCauseAsync() => throw new NotImplementedException();
    public static Task PutBackAtHostAsync() => throw new NotImplementedException();

    /// <summary>Check host retry at integration.</summary>
    public static Task RetryAtHostAsync() => throw new NotImplementedException();
    /// <summary>Check host trim at integration.</summary>
    public static Task TrimAtHostAsync() => throw new NotImplementedException();
    /// <summary>Verifies the Host contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
