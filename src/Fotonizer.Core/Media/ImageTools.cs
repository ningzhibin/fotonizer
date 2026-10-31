using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core.Media;

/// <summary>WIC/WPF based decoding helpers. All methods are thread-agnostic but not thread-safe on shared state.</summary>
public static class ImageTools
{
    public static BitmapFrame LoadStoredFrame(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("图像没有可用的帧");
        var frame = decoder.Frames[0];
        if (frame.CanFreeze) frame.Freeze();
        return frame;
    }

    /// <summary>Decodes an image, optionally downscaling it, and applies the EXIF orientation.</summary>
    public static BitmapSource DecodeOriented(string path, int orientation, int maxDimension = 0)
    {
        var (storedWidth, storedHeight) = ProbeSize(path);
        var scale = 1.0;
        if (maxDimension > 0)
        {
            var longest = Math.Max(storedWidth, storedHeight);
            if (longest > maxDimension) scale = (double)maxDimension / longest;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path, UriKind.Absolute);
        if (scale < 1.0)
            image.DecodePixelWidth = Math.Max(1, (int)Math.Round(storedWidth * scale));
        image.EndInit();

        var oriented = ApplyOrientation(image, orientation);
        if (oriented.CanFreeze) oriented.Freeze();
        return oriented;
    }

    public static (int Width, int Height) ProbeSize(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("图像没有可用的帧");
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    public static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        if (orientation is <= 1 or > 8) return source;
        BitmapSource current = source;

        if (orientation is 2 or 5 or 7)
            current = new TransformedBitmap(current, new ScaleTransform(-1, 1));
        else if (orientation is 4)
            current = new TransformedBitmap(current, new ScaleTransform(1, -1));

        var angle = orientation switch
        {
            3 or 4 => 180,
            5 or 8 => 270,
            6 or 7 => 90,
            _ => 0
        };

        if (angle != 0)
            current = new TransformedBitmap(current, new RotateTransform(angle));

        return current;
    }

    public static (byte[] Pixels, int Width, int Height) ToBgra32(BitmapSource source)
    {
        BitmapSource converted = source;
        if (source.Format != PixelFormats.Bgra32)
            converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, width, height);
    }

    public static byte[] ToGray8(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height];
        converted.CopyPixels(pixels, width, 0);
        return pixels;
    }

    public static void SaveJpeg(string path, byte[] bgra, int width, int height, int quality,
        BitmapMetadata? metadata)
    {
        var stride = width * 4;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            bgra, stride);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        BitmapMetadata? copy = null;
        if (metadata != null)
        {
            try
            {
                copy = metadata.Clone() as BitmapMetadata;
            }
            catch (Exception ex)
            {
                AppLog.Shared.Debug($"元数据克隆失败，将不写入 EXIF：{ex.Message}");
            }
        }

        try
        {
            Encode(source, copy);
        }
        catch (Exception ex) when (copy != null)
        {
            AppLog.Shared.Debug($"写入 EXIF 失败，改为无元数据输出：{ex.Message}");
            Encode(source, null);
        }

        void Encode(BitmapSource bitmap, BitmapMetadata? meta)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, meta, null));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
        }
    }

    public static bool TryGetMetadata(string path, out BitmapMetadata? metadata)
    {
        metadata = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            metadata = decoder.Frames[0].Metadata as BitmapMetadata;
            return metadata != null;
        }
        catch
        {
            return false;
        }
    }
}
