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

    /// <summary>Resolve an endpoint without classifying an OAuth bootstrap URL as an API route.</summary>
    public static Uri Endpoint(string name) => name switch
    {
        DropboxProtocol.AuthorizationPath => new UriBuilder(Uri.UriSchemeHttps, DropboxProtocol.AuthorizationHost) { Path = name }.Uri,
        DropboxProtocol.TokenPath => new UriBuilder(Uri.UriSchemeHttps, DropboxProtocol.TokenHost) { Path = name }.Uri,
        _ => throw new NotImplementedException()
    };

    /// <summary>StyleOf for the selected Routes operation.</summary>
    public static Style StyleOf(string name) => throw new NotImplementedException();
    /// <summary>PolicyOf for the selected Routes operation.</summary>
    public static Policy PolicyOf(string name) => throw new NotImplementedException();
    /// <summary>SpecialOf for the selected Routes operation.</summary>
    public static Special? SpecialOf(string name) => throw new NotImplementedException();
    /// <summary>IsNoAuth for the selected Routes operation.</summary>
    public static bool IsNoAuth(string name) => throw new NotImplementedException();
    /// <summary>VoidArg for the selected Routes operation.</summary>
    public static bool VoidArg(string name) => throw new NotImplementedException();
    /// <summary>ArgHeader for the selected Routes operation.</summary>
    public static string ArgHeader(string json) => throw new NotImplementedException();
    /// <summary>Classifies style for the selected operation.</summary>
    public enum Style { Rpc, Upload, Download }
    /// <summary>Classifies policy for the selected operation.</summary>
    public enum Policy { Read, Write, Denied }
    /// <summary>Classifies special for the selected operation.</summary>
    public enum Special { Continue, AsyncCheck, UploadSession, Longpoll }
}
