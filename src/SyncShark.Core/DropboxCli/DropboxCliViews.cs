using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Human text for the Dropbox group: one line per page or batch entry, otherwise indented JSON.</summary>
public static class DropboxCliViews
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>Renders one Dropbox data record for the console.</summary>
    public static string Render(IDropboxCliData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (data)
        {
            case DropboxCliPageData page:
                var text = new StringBuilder();
                foreach (var entry in page.Entries) text.AppendLine(Line(entry));
                text.AppendLine(DropboxCliCatalog.View(DropboxCliView.PageFooter,
                    ("cursor", page.Cursor ?? None), ("hasMore", page.HasMore.ToString())));
                return text.ToString();
            case DropboxCliBatchData batch:
                var builder = new StringBuilder();
                foreach (var entry in batch.Entries)
                    builder.AppendLine(entry.Ok ? Line(entry.Entry!) : DropboxCliCatalog.View(
                        DropboxCliView.BatchFailed, ("error", entry.Error?.ToString() ?? string.Empty)));
                builder.AppendLine(DropboxCliCatalog.View(DropboxCliView.BatchFooter,
                    ("complete", batch.Complete.ToString()), ("job", batch.JobId ?? None)));
                return builder.ToString();
            case DropboxCliRoutesData routes:
                // One line per route keeps the table readable and inside the reply limit, unlike indented JSON.
                var table = new StringBuilder();
                table.AppendLine(DropboxCliCatalog.View(DropboxCliView.RoutesHeader,
                    ("count", routes.Count.ToString(CultureInfo.InvariantCulture)), ("commit", routes.SpecCommit)));
                foreach (var route in routes.Routes) table.AppendLine(RouteLine(route));
                return table.ToString();
            default:
                return JsonSerializer.Serialize(data, data.GetType(), Options) + Environment.NewLine;
        }
    }

    private static string None => DropboxCliCatalog.View(DropboxCliView.None);

    private static string Absent => DropboxCliCatalog.View(DropboxCliView.Absent);

    private static string RouteLine(DropboxCliRouteRow route) => DropboxCliCatalog.View(DropboxCliView.RouteLine,
        ("name", route.Name), ("style", route.Style), ("host", route.Host), ("policy", route.Policy),
        ("scope", route.Scope),
        ("granted", route.Granted is null ? string.Empty
            : DropboxCliCatalog.View(DropboxCliView.RouteGranted, ("granted", route.Granted.Value.ToString()))),
        ("special", route.Special is null ? string.Empty
            : DropboxCliCatalog.View(DropboxCliView.RouteSpecial, ("special", route.Special))));

    private static string Line(DropboxCliEntry entry) => DropboxCliCatalog.View(DropboxCliView.EntryLine,
        ("tag", entry.Tag),
        ("size", entry.Size?.ToString(CultureInfo.InvariantCulture) ?? Absent),
        ("modified", entry.ServerModified?.UtcDateTime.ToString(DropboxCliCatalog.View(DropboxCliView.EntryTime),
            CultureInfo.InvariantCulture) ?? Absent),
        ("path", entry.PathDisplay));
}
