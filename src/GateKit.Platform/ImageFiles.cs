using StbImageWriteSharp;

namespace GateKit.Platform;

/// <summary>Reads a picture file into a <see cref="Screenshot"/>.</summary>
public static class ImageReader
{
    /// <summary>The pixels of a PNG (or any other format stb reads), as the stand's BGRA.</summary>
    public static Screenshot Read(byte[] file)
    {
        var image = StbImageSharp.ImageResult.FromMemory(file, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var bgra = image.Data;
        for (var i = 0; i + 3 < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return new Screenshot(image.Width, image.Height, bgra);
    }
}

/// <summary>Writes a <see cref="Screenshot"/> as JPEG (quality 82, as the old stand) or PNG, scaled down to a width.</summary>
public static class ImageFiles
{
    /// <summary>Saves the picture; the format follows the extension (.png, else JPEG).
    /// A picture wider than <paramref name="maxWidth"/> is scaled down; 0 keeps the size.</summary>
    public static (int Width, int Height) Save(Screenshot shot, string path, int maxWidth = 0, int jpegQuality = 82)
    {
        var (width, height, rgb) = maxWidth > 0 && shot.Width > maxWidth
            ? Shrink(shot, maxWidth)
            : (shot.Width, shot.Height, ToRgb(shot));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        var writer = new ImageWriter();
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            writer.WritePng(rgb, width, height, ColorComponents.RedGreenBlue, stream);
        else
            writer.WriteJpg(rgb, width, height, ColorComponents.RedGreenBlue, stream, jpegQuality);
        return (width, height);
    }

    static byte[] ToRgb(Screenshot shot)
    {
        var rgb = new byte[shot.Width * shot.Height * 3];
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = shot.Bgra[i + 2];
            rgb[o + 1] = shot.Bgra[i + 1];
            rgb[o + 2] = shot.Bgra[i];
        }
        return rgb;
    }

    /// <summary>Area-averaging downscale: every target pixel is the mean of the source pixels it covers.</summary>
    internal static (int, int, byte[]) Shrink(Screenshot shot, int width)
    {
        var height = Math.Max(1, (int)((long)shot.Height * width / shot.Width));
        var rgb = new byte[width * height * 3];
        var sx = (double)shot.Width / width;
        var sy = (double)shot.Height / height;
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(shot.Height, (int)((y + 1) * sy)));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(shot.Width, (int)((x + 1) * sx)));
                long r = 0, g = 0, b = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * shot.Width * 4;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = row + xx * 4;
                        b += shot.Bgra[i];
                        g += shot.Bgra[i + 1];
                        r += shot.Bgra[i + 2];
                    }
                }
                var n = (y1 - y0) * (x1 - x0);
                var o = (y * width + x) * 3;
                rgb[o] = (byte)(r / n);
                rgb[o + 1] = (byte)(g / n);
                rgb[o + 2] = (byte)(b / n);
            }
        }
        return (width, height, rgb);
    }
}
