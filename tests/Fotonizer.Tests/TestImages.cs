using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fotonizer.Tests;

internal static class TestImages
{
    public static string CreateJpeg(string directory, string fileName, int width, int height,
        Func<int, int, (byte R, byte G, byte B)> pixel, DateTime capturedAt, int quality = 92)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = pixel(x, y);
            var offset = y * stride + x * 4;
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 255;
        }

        WriteJpeg(path, pixels, width, height, quality);
        File.SetLastWriteTime(path, capturedAt);
        File.SetCreationTime(path, capturedAt);
        return path;
    }

    public static void WriteJpeg(string path, byte[] bgra, int width, int height, int quality = 92)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static byte[] CreateBgra(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = pixel(x, y);
            var offset = y * stride + x * 4;
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    public static byte[] BoxBlur(byte[] bgra, int width, int height, int radius)
    {
        var result = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            int r = 0, g = 0, b = 0, count = 0;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var sy = Math.Clamp(y + dy, 0, height - 1);
                var offset = (sy * width + sx) * 4;
                b += bgra[offset];
                g += bgra[offset + 1];
                r += bgra[offset + 2];
                count++;
            }

            var target = (y * width + x) * 4;
            result[target] = (byte)(b / count);
            result[target + 1] = (byte)(g / count);
            result[target + 2] = (byte)(r / count);
            result[target + 3] = 255;
        }

        return result;
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fotonizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
