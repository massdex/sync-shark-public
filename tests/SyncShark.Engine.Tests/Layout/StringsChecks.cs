using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Loads the real text catalog; missing, repeated or unknown keys are errors.</summary>
public static class StringsChecks
{
    public static Task RunAsync()
    {
        Check.Require(Text.All.Count == Enum.GetValues<TextKey>().Length, "Incomplete text registry");
        Check.Require(Sql.All.Count == Enum.GetValues<SqlKey>().Length, "Incomplete SQL registry");
        Check.Require(Text.Get(TextKey.InfoLinks, (TextArg.Count, "2"), (TextArg.Folders, "Top")) ==
            "2 links or shortcuts are not followed or synced, in: Top.", "Links notice placeholders");
        return Task.CompletedTask;
    }
    /// <summary>Wire limits belong to the provider data, not the transport or its callers.</summary>
    public static Task ProviderLimitsAsync()
    {
        using var stream=typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Routes) ?? throw new InvalidDataException(ResourceNames.Routes);
        using var doc=System.Text.Json.JsonDocument.Parse(stream);var root=doc.RootElement;
        var actual=root.GetProperty("routes").EnumerateArray().Where(row=>row.TryGetProperty("maxEntries",out _)).ToDictionary(row=>row.GetProperty("name").GetString()!,row=>row.GetProperty("maxEntries").GetInt32());
        (string Name,int Max)[] expected=[("files/delete_batch",1000),("files/create_folder_batch",10000),("files/move_batch_v2",1000),("files/upload_session/finish_batch_v2",1000)];
        Check.Require(actual.Count==expected.Length && expected.All(x=>actual.GetValueOrDefault(x.Name)==x.Max),"Incorrect route limit (D05/D07)");
        var limits=root.GetProperty("limits");Check.Require(limits.GetProperty("contentRequestMiB").GetInt64()==150 && limits.GetProperty("relocationFiles").GetInt32()==10000,"Incorrect content or relocation limit (D06/D07)");
        return Task.CompletedTask;
    }
}
