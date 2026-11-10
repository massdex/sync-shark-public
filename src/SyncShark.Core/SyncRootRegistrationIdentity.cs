using System.Security.Principal;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Owns the product's shell registration identity, not native Cloud Files ownership.</summary>
public static class SyncRootRegistrationIdentity
{
    public const string ProviderId = "Massdex.SyncShark";
    // StorageProviderSyncRootInfo.Id's documented Windows limit.
    private const int MaximumLength = 174;

    public static bool IsProductRegistration(string registrationId) =>
        registrationId == ProviderId || registrationId.StartsWith(ProviderId + "!", StringComparison.Ordinal);

    /// <summary>Rejects indeterminate product metadata; a different valid SID needs no mapping inspection.</summary>
    public static bool IsForUser(string registrationId, string userSid)
    {
        if (!IsProductRegistration(registrationId)) return false;
        var parts = registrationId.Split('!');
        if (parts.Length != 3 || registrationId.Length > MaximumLength || !ValidSid(parts[1]) || !ValidAccount(parts[2]))
            throw new InvalidDataException(MessageCatalog.Default.Message("startup-registration-invalid"));
        return string.Equals(parts[1], userSid, StringComparison.Ordinal);
    }

    /// <summary>Creates the existing provider/SID/account shell ID without registering it.</summary>
    public static string Create(string userSid, string accountId)
    {
        if (!ValidSid(userSid) || !ValidAccount(accountId))
            throw new InvalidDataException(MessageCatalog.Default.Message("startup-registration-invalid"));
        var id = ProviderId + "!" + userSid + "!" + accountId;
        if (id.Length > MaximumLength)
            throw new InvalidDataException(MessageCatalog.Default.Message("startup-registration-invalid"));
        return id;
    }

    internal static bool ValidSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try { return new SecurityIdentifier(sid).Value == sid; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidAccount(string account) => !string.IsNullOrWhiteSpace(account) &&
        account.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
