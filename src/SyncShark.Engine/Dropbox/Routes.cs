namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Routes operations for the dropbox module.</summary>
public static class Routes
{
    /// <summary>The maximum ordered members of this batch route, from its provider definition.</summary>
    public static int MaxEntries(string route) => throw new NotImplementedException();

    /// <summary>The content request limit in bytes, converted from the catalog's MiB value.</summary>
    public static long ContentRequestBytes => throw new NotImplementedException();
    /// <summary>The number of files the provider permits in one relocation.</summary>
    public static int RelocationFiles => throw new NotImplementedException();

    /// <summary>Endpoint operation on the supplied facts.</summary>
    public static Uri Endpoint(string name) => throw new NotImplementedException();
}
