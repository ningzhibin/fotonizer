using Fotonizer.Core.Media;
using Fotonizer.Core.Models;
using System.Windows.Media;

namespace Fotonizer.Tests;

public class PhotoEnhancerTests
{
    [Fact]
    public void ProcessInPlace_BrightensDarkLowContrastImage()
    {
        const int width = 320, height = 240;
        var pixels = TestImages.CreateBgra(width, height, (x, y) =>
        {
            var v = (byte)(35 + (x + y) % 25);
            return (v, (byte)(v + 4), (byte)(v + 10));
        });

        var before = AverageLuma(pixels);
        PhotoEnhancer.ProcessInPlace(pixels, width, height, EnhanceMode.Strong);
        var after = AverageLuma(pixels);

        Assert.True(after > before + 25, $"expected brighter image, before={before}, after={after}");
    }

    [Fact]
    public void ProcessInPlace_Off_LeavesPixelsUntouched()
    {
        var pixels = TestImages.CreateBgra(64, 64, (x, y) => (10, 20, 30));
        var copy = pixels.ToArray();
        PhotoEnhancer.ProcessInPlace(pixels, 64, 64, EnhanceMode.Off);
        Assert.Equal(copy, pixels);
    }

    [Fact]
    public void EnhanceToJpeg_WritesDecodableImage()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var source = Path.Combine(dir, "source.jpg");
            var bgra = TestImages.CreateBgra(200, 100, (x, y) => ((byte)(x % 255), (byte)(y % 255), 80));
            TestImages.WriteJpeg(source, bgra, 200, 100);

            var output = Path.Combine(dir, "out.jpg");
            PhotoEnhancer.EnhanceToJpeg(source, output, EnhanceMode.Subtle, 90);

            Assert.True(File.Exists(output));
            var frame = ImageTools.LoadStoredFrame(output);
            Assert.Equal(200, frame.PixelWidth);
            Assert.Equal(100, frame.PixelHeight);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void ApplyOrientation_RotatesPortrait()
    {
        var source = TestImages.CreateBgra(100, 50, (x, y) => (1, 2, 3));
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(100, 50, 96, 96,
            PixelFormats.Bgra32, null, source, 100 * 4);

        var rotated = ImageTools.ApplyOrientation(bitmap, 6);
        Assert.Equal(50, rotated.PixelWidth);
        Assert.Equal(100, rotated.PixelHeight);
    }

    private static double AverageLuma(byte[] bgra)
    {
        double sum = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            sum += bgra[i] * 0.114 + bgra[i + 1] * 0.587 + bgra[i + 2] * 0.299;
        return sum / (bgra.Length / 4.0);
    }
}
