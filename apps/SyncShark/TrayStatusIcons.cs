using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Massdex.SyncShark;

internal static class TrayStatusIcons
{
    internal static Icon Create(Icon original, string state)
    {
        if (state == "idle") return (Icon)original.Clone();
        var sizes = new[] { 16, 20, 24, 32, 40, 48 };
        var frames = sizes.Select(size =>
        {
            using var bitmap = Render(original, state, size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        stream.Position = 0;
        using var icon = new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }

    internal static Bitmap Render(Icon original, string state, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawIcon(original, new Rectangle(0, 0, size, size));
        }
        if (state == "idle") return bitmap;
        var color = state switch
        {
            "paused" => Color.FromArgb(244, 188, 65),
            "attention" => Color.FromArgb(231, 73, 73),
            "starting" => Color.Black,
            "working-light" => Color.FromArgb(228, 239, 255),
            _ => Color.FromArgb(0, 102, 255)
        };
        // Tint only the original mark. Never add an overlay or an opaque canvas which hides
        // the fin; retain its exact alpha at every size and a visible keel during startup.
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A == 0) continue;
            var isKeel = pixel.R > 100 && Math.Abs(pixel.R - pixel.G) < 55;
            bitmap.SetPixel(x, y, Color.FromArgb(pixel.A,
                state == "starting" && isKeel ? Color.FromArgb(185, 204, 224) : color));
        }
        return bitmap;
    }
}
