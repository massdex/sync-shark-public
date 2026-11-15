using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>The shared finite retry episode and one completion claim (design G04, A163-A167).</summary>
public static class FailureEpisodeChecks
{
    private static readonly FailureRetryOwner.BackoffSettings Backoff = new(1, 1.6, 0.2, 120);

    public static void Run()
    {
        GenericFailuresAdvanceAndSaturate();
        ProviderWaitPrecedesAndCountsByContext();
        SuccessResetsOnlyItsEpisode();
        ClaimResultIsOneAtomicAnswer();
    }

    private static void GenericFailuresAdvanceAndSaturate()
    {
        var episode = new FailureRetryOwner.Episode(providerWaitCounts: false);
        Check.Require(episode.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) ==
                TimeSpan.FromSeconds(1) &&
            episode.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) == TimeSpan.FromSeconds(1.6) &&
            episode.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) == TimeSpan.FromSeconds(2.56),
            "consecutive generic failures keep today's adopted delay in this context (A166)");
        for (var index = 0; index < 1_000; index++)
            episode.Failed(null, null, TimeProvider.System, Backoff, () => 0.5);
        Check.Require(episode.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) ==
                TimeSpan.FromSeconds(Backoff.MaximumBackoffSeconds),
            "the episode counter saturates at the backoff ceiling");
    }

    private static void ProviderWaitPrecedesAndCountsByContext()
    {
        var nonCounting = new FailureRetryOwner.Episode(providerWaitCounts: false);
        var counting = new FailureRetryOwner.Episode(providerWaitCounts: true);
        var floor = TimeSpan.FromSeconds(45);
        Check.Require(nonCounting.Failed(floor, null, TimeProvider.System, Backoff, () => 0.5) == floor &&
            counting.Failed(floor, null, TimeProvider.System, Backoff, () => 0.5) == floor,
            "an applicable provider wait precedes the generic schedule in both contexts (A167)");
        Check.Require(nonCounting.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) ==
                TimeSpan.FromSeconds(1),
            "a context that does not count provider waits keeps its prior failure count (A167)");
        Check.Require(counting.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) ==
                TimeSpan.FromSeconds(1.6),
            "a context that counts provider waits advances its failure count (A167)");
    }

    private static void SuccessResetsOnlyItsEpisode()
    {
        var reset = new FailureRetryOwner.Episode(providerWaitCounts: false);
        var untouched = new FailureRetryOwner.Episode(providerWaitCounts: false);
        reset.Failed(null, null, TimeProvider.System, Backoff, () => 0.5);
        reset.Failed(null, null, TimeProvider.System, Backoff, () => 0.5);
        untouched.Failed(null, null, TimeProvider.System, Backoff, () => 0.5);
        reset.Succeeded();
        Check.Require(reset.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) ==
                TimeSpan.FromSeconds(1) &&
            untouched.Failed(null, null, TimeProvider.System, Backoff, () => 0.5) == TimeSpan.FromSeconds(1.6),
            "success resets only its own episode (A165)");
    }

    private static void ClaimResultIsOneAtomicAnswer()
    {
        var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
        Check.Require(attempt.ClaimResult() is null, "an uncompleted attempt has no claimable result");
        Check.Require(attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.FromSeconds(5), true),
            "the attempt publishes one completion");
        var claimed = attempt.ClaimResult();
        Check.Require(claimed is { Result: RequestResult.TransientFailure, ProviderWait: { } wait } &&
                wait == TimeSpan.FromSeconds(5) && attempt.ClaimResult() is null,
            "the first claim returns the published result and a second claim sees none (A163)");
    }
}
