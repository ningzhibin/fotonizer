using System.Windows.Media.Imaging;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Security.Cryptography;

namespace Fotonizer.Core.Media;

public sealed class ImageScore
{
    public double Sharpness { get; init; }
    public double Exposure { get; init; }
    public double FaceScore { get; init; }
    public double FaceSharpness { get; init; }
    public int FaceCount { get; init; }
    public double Total { get; set; }
    public string? Error { get; init; }
    public bool HasFaces => FaceCount > 0;
}

/// <summary>
/// Computes objective quality signals used to pick the best frame of a burst:
/// sharpness (variance of Laplacian), exposure quality, face count/size, and face sharpness.
/// </summary>
public sealed class ImageScorer : IDisposable
{
    private readonly int _maxDimension;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FaceDetector? _faceDetector;
    private bool _faceDetectorUnavailable;

    public ImageScorer(int maxDimension = 1400)
    {
        _maxDimension = Math.Clamp(maxDimension, 400, 4000);
    }

    public async Task<ImageScore> ScoreAsync(string path, PhotoMetadata meta, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BitmapSource image;
            try
            {
                image = ImageTools.DecodeOriented(path, meta.Orientation, _maxDimension);
            }
            catch (Exception ex)
            {
                return new ImageScore { Error = $"无法解码：{ex.Message}" };
            }

            var gray = ImageTools.ToGray8(image);
            var width = image.PixelWidth;
            var height = image.PixelHeight;
            cancellationToken.ThrowIfCancellationRequested();

            var sharpness = LaplacianVariance(gray, width, height);
            var exposure = ExposureScore(gray);
            var faces = await DetectFacesAsync(gray, width, height, cancellationToken).ConfigureAwait(false);

            var faceScore = 0.0;
            var faceSharpness = 0.0;
            if (faces.Count > 0)
            {
                double area = faces.Sum(f => (double)f.Width * f.Height);
                var areaRatio = area / Math.Max(1, width * height);
                var sizeScore = Math.Min(1.0, areaRatio / 0.06);
                var countScore = Math.Min(1.0, 0.55 + 0.15 * (faces.Count - 1));
                faceScore = Math.Min(1.0, countScore * 0.7 + sizeScore * 0.3);

                var values = new List<double>(faces.Count);
                foreach (var face in faces)
                {
                    var value = LaplacianVarianceRegion(gray, width, height, face);
                    if (value > 0) values.Add(value);
                }

                if (values.Count > 0) faceSharpness = values.Average();
            }

            return new ImageScore
            {
                Sharpness = sharpness,
                Exposure = exposure,
                FaceScore = faceScore,
                FaceSharpness = faceSharpness,
                FaceCount = faces.Count
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<FaceRegion>> DetectFacesAsync(byte[] gray, int width, int height,
        CancellationToken cancellationToken)
    {
        var result = new List<FaceRegion>();
        if (_faceDetectorUnavailable) return result;

        try
        {
            _faceDetector ??= await FaceDetector.CreateAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (_faceDetector == null) return result;

            var buffer = CryptographicBuffer.CreateFromByteArray(gray);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Gray8, width, height);
            var detected = await _faceDetector.DetectFacesAsync(bitmap).AsTask(cancellationToken)
                .ConfigureAwait(false);

            foreach (var face in detected)
            {
                var box = face.FaceBox;
                result.Add(new FaceRegion((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _faceDetectorUnavailable = true;
            AppLog.Shared.Warn($"人脸检测不可用，连拍评分将只使用清晰度/曝光：{ex.Message}");
        }

        return result;
    }

    private readonly record struct FaceRegion(int X, int Y, int Width, int Height);

    internal static double LaplacianVariance(byte[] gray, int width, int height)
    {
        if (width < 3 || height < 3) return 0;
        double sum = 0, sumSq = 0;
        long count = 0;

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x;
                var value = 4 * gray[i] - gray[i - 1] - gray[i + 1] - gray[i - width] - gray[i + width];
                sum += value;
                sumSq += (double)value * value;
                count++;
            }
        }

        if (count == 0) return 0;
        var mean = sum / count;
        var variance = sumSq / count - mean * mean;
        return variance < 0 ? 0 : variance;
    }

    private static double LaplacianVarianceRegion(byte[] gray, int width, int height, FaceRegion region)
    {
        var x0 = Math.Clamp(region.X, 1, width - 2);
        var y0 = Math.Clamp(region.Y, 1, height - 2);
        var x1 = Math.Clamp(region.X + region.Width, 1, width - 2);
        var y1 = Math.Clamp(region.Y + region.Height, 1, height - 2);
        if (x1 - x0 < 5 || y1 - y0 < 5) return 0;

        double sum = 0, sumSq = 0;
        long count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                var i = row + x;
                var value = 4 * gray[i] - gray[i - 1] - gray[i + 1] - gray[i - width] - gray[i + width];
                sum += value;
                sumSq += (double)value * value;
                count++;
            }
        }

        if (count == 0) return 0;
        var mean = sum / count;
        var variance = sumSq / count - mean * mean;
        return variance < 0 ? 0 : variance;
    }

    internal static double ExposureScore(byte[] gray)
    {
        if (gray.Length == 0) return 0;
        var hist = new long[256];
        long total = gray.Length;
        double sum = 0;
        foreach (var value in gray)
        {
            hist[value]++;
            sum += value;
        }

        long clippedHigh = 0, clippedLow = 0;
        for (var i = 0; i < 8; i++) clippedLow += hist[i];
        for (var i = 248; i < 256; i++) clippedHigh += hist[i];

        var mean = sum / total / 255.0;
        var variance = 0.0;
        for (var i = 0; i < 256; i++)
        {
            var delta = i / 255.0 - mean;
            variance += delta * delta * hist[i];
        }

        var stdDev = Math.Sqrt(variance / total);
        var score = 1.0;
        score -= 2.2 * (clippedHigh / (double)total);
        score -= 1.4 * (clippedLow / (double)total);
        score -= 1.1 * Math.Abs(mean - 0.5);
        score *= 0.65 + 0.35 * Math.Min(1.0, stdDev / 0.18);
        return Math.Clamp(score, 0, 1);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
