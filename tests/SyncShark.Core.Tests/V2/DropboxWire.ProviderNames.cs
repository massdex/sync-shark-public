using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal sealed partial class DropboxWire
{
    private static HttpResponseMessage NameError(string tag, string reason) => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, object>
            {
                [".tag"] = tag,
                [tag == "path" ? "reason" : tag] = new Dictionary<string, object> { [".tag"] = reason }
            }
        }), Encoding.UTF8, "application/json")
    };
}
