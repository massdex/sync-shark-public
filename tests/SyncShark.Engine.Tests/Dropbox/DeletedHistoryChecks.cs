using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DeletedHistory.</summary>
public static class DeletedHistoryChecks
{
    /// <summary>Verifies the DeletedHistory contract through the module surface.</summary>
    public static Task RunAsync()
    {
        // A coherent time is parsed; a missing or malformed one keeps the remainder usable with its own cause.
        var valid = DeletedHistory.DeletionTime("2024-01-02T03:04:05Z", wrongType: false);
        Check.Require(valid.Reason is null && valid.Value == new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "A valid server deletion time was not parsed as UTC.");

        var missingTime = DeletedHistory.DeletionTime(null, wrongType: false);
        Check.Require(missingTime.Value is null && missingTime.Reason == TextKey.DeletionTimeMissing,
            "A missing deletion time lost its own cause.");

        var malformedTime = DeletedHistory.DeletionTime("not-a-time", wrongType: false);
        Check.Require(malformedTime.Value is null && malformedTime.Reason == TextKey.DeletionTimeMalformed,
            "A malformed deletion time lost its own cause.");

        var wrongTimeType = DeletedHistory.DeletionTime("2024-01-02T03:04:05Z", wrongType: true);
        Check.Require(wrongTimeType.Value is null && wrongTimeType.Reason == TextKey.DeletionTimeMalformed,
            "A wrong-typed deletion time was accepted.");

        // The retained content hash must be a coherent Dropbox block digest, normalised to lower case.
        var digest = new string('A', 64);
        var hash = DeletedHistory.DeletionHash(digest, wrongType: false);
        Check.Require(hash.Reason is null && hash.Value == new string('a', 64),
            "A valid content hash was not accepted or normalised.");

        var missingHash = DeletedHistory.DeletionHash(null, wrongType: false);
        Check.Require(missingHash.Value is null && missingHash.Reason == TextKey.DeletionHashMissing,
            "A missing content hash lost its own cause.");

        var malformedHash = DeletedHistory.DeletionHash("abc", wrongType: false);
        Check.Require(malformedHash.Value is null && malformedHash.Reason == TextKey.DeletionHashMalformed,
            "A malformed content hash lost its own cause.");

        var wrongHashType = DeletedHistory.DeletionHash(digest, wrongType: true);
        Check.Require(wrongHashType.Value is null && wrongHashType.Reason == TextKey.DeletionHashMalformed,
            "A wrong-typed content hash was accepted.");
        return Task.CompletedTask;
    }
}
