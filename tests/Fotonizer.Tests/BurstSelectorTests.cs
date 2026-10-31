using Fotonizer.Core.Media;
using Fotonizer.Core.Models;

namespace Fotonizer.Tests;

public class BurstSelectorTests
{
    [Fact]
    public async Task SelectsSharpestFrameOfBurst()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var baseTime = new DateTime(2024, 5, 1, 10, 15, 0);
            var sharpPixels = TestImages.CreateBgra(640, 480, (x, y) =>
            {
                var v = (x * 37 + y * 91) % 255;
                return ((byte)v, (byte)v, (byte)v);
            });

            var files = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var pixels = i == 0 ? sharpPixels : TestImages.BoxBlur(sharpPixels, 640, 480, i * 2);
                var path = Path.Combine(dir, $"burst{i}.jpg");
                TestImages.WriteJpeg(path, pixels, 640, 480);
                File.SetLastWriteTime(path, baseTime.AddMilliseconds(i * 200));
                File.SetCreationTime(path, baseTime.AddMilliseconds(i * 200));
                files.Add(path);
            }

            var metas = files.Select(f => MetadataReader.Read(f, false)).ToList();
            using var scorer = new ImageScorer(800);
            var selector = new BurstSelector();
            var settings = new AppSettings
            {
                BurstSelection = true,
                BurstMinCount = 3,
                BurstWindowSeconds = 2
            };

            var groups = await selector.FindAndScoreAsync(metas, settings, scorer, CancellationToken.None);

            Assert.Single(groups);
            Assert.Equal(3, groups[0].Items.Count);
            Assert.Equal(files[0], groups[0].Best!.Metadata.SourcePath);
            Assert.All(groups[0].Items, item => Assert.NotNull(item.Score));
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DoesNotGroupPhotosFarApartInTime()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var files = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var path = Path.Combine(dir, $"single{i}.jpg");
                TestImages.WriteJpeg(path, TestImages.CreateBgra(100, 100, (x, y) => (50, 60, 70)), 100, 100);
                File.SetLastWriteTime(path, new DateTime(2024, 5, 1, 8 + i, 0, 0));
                files.Add(path);
            }

            var metas = files.Select(f => MetadataReader.Read(f, false)).ToList();
            using var scorer = new ImageScorer(400);
            var selector = new BurstSelector();
            var settings = new AppSettings { BurstSelection = true, BurstMinCount = 2, BurstWindowSeconds = 1 };

            var groups = await selector.FindAndScoreAsync(metas, settings, scorer, CancellationToken.None);
            Assert.Empty(groups);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }
}
