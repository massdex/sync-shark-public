namespace Massdex.SyncShark;

/// <summary>
/// One provider-qualified storage-object key: the registered provider literal, the kind of key and the exact
/// provider-issued value. Two keys name the same backing object only when the provider, the kind and the
/// value all match, with the value compared ordinal and case-sensitively, so one identical literal id under
/// two different providers never aliases and two case variants are never merged. The provider literal is
/// normalised to its registered lowercase form so value equality and SyncCloudObjectKeys.SameObject agree.
/// Keys are readback evidence only: they are never user-supplied CLI authority and they are never inferred
/// from a display name, account label, e-mail or path.
/// </summary>
public readonly record struct SyncCloudObjectKey(string Provider, SyncCloudObjectKeyKind Kind, string Value)
{
    /// <summary>One native provider object key.</summary>
    public static SyncCloudObjectKey Native(string provider, string value) =>
        Create(provider, SyncCloudObjectKeyKind.NativeObject, value);

    /// <summary>One route-issued shared-namespace mount key.</summary>
    public static SyncCloudObjectKey SharedNamespaceMount(string provider, string value) =>
        Create(provider, SyncCloudObjectKeyKind.SharedNamespaceMount, value);

    public static SyncCloudObjectKey Create(string provider, SyncCloudObjectKeyKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new InvalidDataException("A cloud object key requires a registered provider literal.");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A cloud object key requires a provider-issued value.");
        return new SyncCloudObjectKey(provider.Trim().ToLowerInvariant(), kind, value);
    }

    public bool IsWellFormed => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Provider + ":" + Kind + ":" + Value;
}
