namespace Massdex.SyncShark.V2;

public static class ProviderIds
{
    public const string Local = "local";
    public const string Dropbox = "dropbox";
    public const string GoogleDrive = "google-drive";

    public static string Normalize(string? provider) => string.IsNullOrWhiteSpace(provider)
        ? Dropbox
        : provider.Trim().ToLowerInvariant();
}
