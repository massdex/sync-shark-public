namespace Massdex.SyncShark;

/// <summary>
/// The kind of one provider-issued storage-object key. A native object key is the provider's own file or
/// folder identity and it is the only key a provider API call is ever made with. A shared-namespace mount
/// key is a second, route-issued key for one backing shared object: the provider mounts that backing object
/// at a location inside an access route's namespace and issues a fresh location-scoped id, so the native ids
/// of two routes differ while the mount key names the one object both routes can reach. A provider with no
/// mount mapping issues no keys of that kind, and "ordinary file/folder IDs remain their native object keys".
/// </summary>
public enum SyncCloudObjectKeyKind
{
    /// <summary>The provider's own file or folder identity; the only key a provider API call uses.</summary>
    NativeObject = 0,

    /// <summary>A route-issued mount key naming a shared backing object reached from this route's namespace.</summary>
    SharedNamespaceMount = 1
}
