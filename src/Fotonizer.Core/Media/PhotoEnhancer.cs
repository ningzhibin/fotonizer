using System.Windows.Media.Imaging;
using Fotonizer.Core.Models;

namespace Fotonizer.Core.Media;

/// <summary>
/// Local "auto enhance" implementation (Windows Photos exposes no automation API).
///
/// The pipeline is intentionally conservative and close to what phone galleries do:
///   1. percentile black/white point stretch (0.5% / 99.5%)
///   2. grey-world white balance with clamped gains
///   3. mid-tone gamma correction toward 0.48
///   4. vibrance-style saturation boost
///   5. unsharp mask on luminance
///
/// Pixels are never rotated, so the original EXIF orientation tag stays valid and is
/// copied to the output together with the rest of the metadata.
/// </summary>
public static class PhotoEnhancer
{
    public static bool ShouldEnhance(string path, AppSettings settings) =>
        settings.EnhanceMode != EnhanceMode.Off && MediaTypes.IsEnhanceable(path);

    /// <summary>Decodes <paramref name="sourcePath"/>, enhances it and writes a JPEG to <paramref name="outputPath"/>.</summary>
    public static void EnhanceToJpeg(string sourcePath, string outputPath, EnhanceMode mode, int quality)
    {
        var frame = ImageTools.LoadStoredFrame(sourcePath);
        var (pixels, width, height) = ImageTools.ToBgra32(frame);
        ProcessInPlace(pixels, width, height, mode);
        var metadata = frame.Metadata as BitmapMetadata;
        ImageTools.SaveJpeg(outputPath, pixels, width, height, quality, metadata);
    }

    public static void ProcessInPlace(byte[] pixels, int width, int height, EnhanceMode mode)
    {
        if (mode == EnhanceMode.Off || width < 8 || height < 8) return;

        var strength = mode == EnhanceMode.Subtle ? 0.70 : 1.0;
        var total = width * height;

        // ---- sample the image (never iterate all pixels twice unnecessarily) ----
        var step = Math.Max(1, total / 250_000);
        var histogram = new long[256];
        double sumR = 0, sumG = 0, sumB = 0;
        long samples = 0;

        for (var i = 0; i < total; i += step)
        {
            var p = i * 4;
            int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
            var y = (r * 77 + g * 150 + b * 29) >> 8;
            histogram[y]++;
            sumR += r;
            sumG += g;
            sumB += b;
            samples++;
        }

        if (samples == 0) return;

        var lowBin = Percentile(histogram, samples, 0.005);
        var highBin = Percentile(histogram, samples, 0.995);
        if (highBin - lowBin < 40)
        {
            lowBin = 0;
            highBin = 255;
        }

        // ---- mid-tone gamma based on the stretched histogram ----
        double stretchedSum = 0;
        long stretchedCount = 0;
        for (var i = lowBin; i <= highBin; i++)
        {
            var x = (i - lowBin) / (double)Math.Max(1, highBin - lowBin);
            stretchedSum += x * histogram[i];
            stretchedCount += histogram[i];
        }

        var mean = stretchedCount > 0 ? stretchedSum / stretchedCount : 0.5;
        var gamma = Math.Log(0.48) / Math.Log(Math.Clamp(mean, 0.02, 0.98));
        gamma = Math.Clamp(gamma, 0.72, 1.45);

        // ---- grey-world white balance, clamped so colours do not go wild ----
        var avgR = sumR / samples;
        var avgG = sumG / samples;
        var avgB = sumB / samples;
        var grey = (avgR + avgG + avgB) / 3.0;
        var gainR = Math.Clamp(grey / Math.Max(1, avgR), 0.82, 1.22);
        var gainG = Math.Clamp(grey / Math.Max(1, avgG), 0.82, 1.22);
        var gainB = Math.Clamp(grey / Math.Max(1, avgB), 0.82, 1.22);

        // Preserve overall luminance after applying channel gains.
        var lumaGain = 0.299 * gainR + 0.587 * gainG + 0.114 * gainB;
        if (lumaGain > 0.001)
        {
            gainR /= lumaGain;
            gainG /= lumaGain;
            gainB /= lumaGain;
        }

        var lutR = BuildLut(lowBin, highBin, gamma, gainR, strength);
        var lutG = BuildLut(lowBin, highBin, gamma, gainG, strength);
        var lutB = BuildLut(lowBin, highBin, gamma, gainB, strength);

        // ---- apply LUT + saturation ----
        var saturation = 1.0 + 0.16 * strength;
        var satMul = (int)Math.Round(saturation * 256);

        Parallel.For(0, height, y =>
        {
            var p = y * width * 4;
            for (var x = 0; x < width; x++, p += 4)
            {
                int b = lutB[pixels[p]];
                int g = lutG[pixels[p + 1]];
                int r = lutR[pixels[p + 2]];

                var luma = (r * 77 + g * 150 + b * 29) >> 8;
                var nr = luma + (((r - luma) * satMul) >> 8);
                var ng = luma + (((g - luma) * satMul) >> 8);
                var nb = luma + (((b - luma) * satMul) >> 8);

                pixels[p] = (byte)Math.Clamp(nb, 0, 255);
                pixels[p + 1] = (byte)Math.Clamp(ng, 0, 255);
                pixels[p + 2] = (byte)Math.Clamp(nr, 0, 255);
            }
        });

        // ---- unsharp mask on luminance ----
        var original = new byte[total];
        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            original[i] = (byte)((pixels[p] * 29 + pixels[p + 1] * 150 + pixels[p + 2] * 77) >> 8);
        }

        var blurred = new byte[total];
        var temp = new byte[total];
        var radius = mode == EnhanceMode.Subtle ? 2 : 3;

        BoxBlurHorizontal(original, temp, width, height, radius);
        BoxBlurVertical(temp, blurred, width, height, radius);
        BoxBlurHorizontal(blurred, temp, width, height, radius);
        BoxBlurVertical(temp, blurred, width, height, radius);

        var amount = mode == EnhanceMode.Subtle ? 0.35 : 0.55;
        var amountMul = (int)Math.Round(amount * 256);

        Parallel.For(0, height, y =>
        {
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = offset + x;
                var detail = original[i] - blurred[i];
                if (detail is >= -2 and <= 2) continue;

                var add = (detail * amountMul) >> 8;
                var p = i * 4;
                pixels[p] = (byte)Math.Clamp(pixels[p] + add, 0, 255);
                pixels[p + 1] = (byte)Math.Clamp(pixels[p + 1] + add, 0, 255);
                pixels[p + 2] = (byte)Math.Clamp(pixels[p + 2] + add, 0, 255);
            }
        });
    }

    private static byte[] BuildLut(int low, int high, double gamma, double gain, double strength)
    {
        var lut = new byte[256];
        var range = Math.Max(1, high - low);
        for (var v = 0; v < 256; v++)
        {
            var x = Math.Clamp((v - low) / (double)range, 0, 1);
            x = Math.Pow(x, gamma);
            x = Math.Clamp(x * gain, 0, 1);
            var target = x * 255.0;
            var blended = v + (target - v) * strength;
            lut[v] = (byte)Math.Clamp((int)Math.Round(blended), 0, 255);
        }

        return lut;
    }

    private static int Percentile(long[] histogram, long total, double percentile)
    {
        var target = Math.Max(0, (long)(total * percentile));
        long accumulated = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= target) return i;
        }

        return 255;
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            var sum = 0;
            for (var x = -radius; x <= radius; x++)
                sum += source[row + Math.Clamp(x, 0, width - 1)];

            for (var x = 0; x < width; x++)
            {
                destination[row + x] = (byte)(sum / window);
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
            }
        });
    }

    private static void BoxBlurVertical(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        Parallel.For(0, width, x =>
        {
            var sum = 0;
            for (var y = -radius; y <= radius; y++)
                sum += source[Math.Clamp(y, 0, height - 1) * width + x];

            for (var y = 0; y < height; y++)
            {
                destination[y * width + x] = (byte)(sum / window);
                sum += source[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
                sum -= source[Math.Clamp(y - radius, 0, height - 1) * width + x];
            }
        });
    }
}
