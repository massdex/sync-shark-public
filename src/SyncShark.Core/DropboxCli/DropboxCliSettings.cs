namespace Massdex.SyncShark;

/// <summary>Every Dropbox CLI tunable in one immutable record; the defaults live in <see cref="Default"/>.</summary>
public sealed record DropboxCliSettings(
    DropboxRetryLimits Retry,
    long SingleUploadMaxBytes,
    int UploadChunkBytes,
    TimeSpan BatchPollInterval,
    TimeSpan BatchPollMaxWait,
    int InlineJsonMaxChars,
    long JsonFileMaxBytes,
    string ResultFolderName,
    TimeSpan ResultRetention,
    TimeSpan HttpTimeout)
{
    /// <summary>The single place every Dropbox CLI default is defined, validated together.</summary>
    public static DropboxCliSettings Default { get; } = Validate(new DropboxCliSettings(
        Retry: new DropboxRetryLimits(RateLimitRetries: 5, ReadTransportRetries: 2, ReadServerRetries: 2,
            MaxBackoff: TimeSpan.FromMinutes(5)) { ReadTransientBudget = TimeSpan.FromMinutes(5) },
        SingleUploadMaxBytes: 128L * 1024 * 1024,
        UploadChunkBytes: 8 * 1024 * 1024,
        BatchPollInterval: TimeSpan.FromSeconds(1),
        BatchPollMaxWait: TimeSpan.FromSeconds(60),
        InlineJsonMaxChars: 32768,
        JsonFileMaxBytes: 1L * 1024 * 1024,
        ResultFolderName: "cli-results",
        ResultRetention: TimeSpan.FromDays(7),
        HttpTimeout: TimeSpan.FromMinutes(10)));

    private static DropboxCliSettings Validate(DropboxCliSettings settings)
    {
        if (settings.SingleUploadMaxBytes is < 1 or > DropboxApiLimits.SingleUploadMaxBytes)
            throw new InvalidDataException("SingleUploadMaxBytes must be positive and no larger than Dropbox's single-request limit.");
        if (settings.UploadChunkBytes < DropboxApiLimits.UploadChunkMultiple ||
            settings.UploadChunkBytes % DropboxApiLimits.UploadChunkMultiple != 0)
            throw new InvalidDataException("UploadChunkBytes must be a positive multiple of 4 MiB.");
        if (settings.InlineJsonMaxChars < 1 || settings.JsonFileMaxBytes < 1)
            throw new InvalidDataException("The JSON bounds must be positive.");
        if (string.IsNullOrWhiteSpace(settings.ResultFolderName))
            throw new InvalidDataException("ResultFolderName is required.");
        if (settings.HttpTimeout <= TimeSpan.Zero)
            throw new InvalidDataException("HttpTimeout must be positive.");
        return settings;
    }
}
