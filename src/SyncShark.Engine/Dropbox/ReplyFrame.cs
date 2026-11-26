using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

// Selection null is a skipped container; Slots marks an object or item, otherwise it is an array.
internal sealed class ReplyFrame(DropboxReplyShapes.Selection? selection)
{
    internal readonly DropboxReplyShapes.Selection? Selection = selection;
    internal byte[]?[]? Slots;
    internal List<byte[]>? Items;
    internal DropboxEntries.Projection? Projection;
    internal int? Field;
    internal int Count;
    internal int Depth;
    internal bool Excess;

    internal static void Deliver(ReplyFrame frame, byte[] value)
    {
        if (frame.Slots is not null)
        {
            frame.Slots[frame.Field!.Value] = value;
            frame.Field = null;
        }
        else
        {
            (frame.Items ??= []).Add(value);
        }
    }

    internal static byte[] Object(string[] names, byte[]?[] slots) => Write(writer =>
    {
        writer.WriteStartObject();
        for (int index = 0; index < names.Length; index++)
        {
            if (slots[index] is not { } value) continue;
            writer.WritePropertyName(names[index]);
            writer.WriteRawValue(value, skipInputValidation: true);
        }
        writer.WriteEndObject();
    });

    internal static byte[] Array(ReplyFrame frame) => Write(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in frame.Items ?? []) writer.WriteRawValue(value, skipInputValidation: true);
        // Excess elements are never captured; one null keeps the array length from matching a bounded count.
        if (frame.Excess) writer.WriteNullValue();
        writer.WriteEndArray();
    });

    internal static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) body(writer);
        return output.ToArray();
    }
}
