namespace Massdex.SyncShark.V2;

/// <summary>One bounded absence observation. A null observation never authorizes deletion; Reason is diagnostic only.</summary>
public readonly record struct AbsenceObservation(bool? Absent, string? Reason)
{
    /// <summary>A short, non-sensitive explanation for an inconclusive observation.</summary>
    internal static string Cause(Exception error) => error switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate limited",
        HttpRequestException => "network error",
        TimeoutException => "timeout",
        OperationCanceledException => "timeout",
        InvalidDataException or ArgumentException => "the source identity or path is malformed",
        _ => error.Message,
    };
}
