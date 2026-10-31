using Fotonizer.Core.Import;
using Fotonizer.Core.Models;

namespace Fotonizer.Tests;

public class NamingTemplateTests
{
    private static PhotoMetadata Meta(DateTime captured, string? model = "EOS R6",
        string sourcePath = @"C:\DCIM\IMG_1234.JPG", string? make = "Canon") => new()
    {
        SourcePath = sourcePath,
        CapturedAt = captured,
        Model = model,
        Make = make
    };

    [Fact]
    public void DefaultTemplate_MatchesUserExample()
    {
        var name = NamingTemplate.Render("{yyyy}-{MM}-{dd}-{HH}{mm}{ss}",
            Meta(new DateTime(2024, 5, 1, 10, 15, 0)));
        Assert.Equal("2024-05-01-101500", name);
    }

    [Fact]
    public void MonthAndMinuteAreCaseSensitive()
    {
        var meta = Meta(new DateTime(2024, 3, 9, 7, 5, 2));
        Assert.Equal("03-05", NamingTemplate.Render("{MM}-{mm}", meta));
    }

    [Fact]
    public void CustomDateFormat_AndCounter()
    {
        var meta = Meta(new DateTime(2024, 12, 31, 23, 59, 59));
        var name = NamingTemplate.Render("{date:yyyy_MM_dd}-{counter:0000}-{camera}", meta, 42);
        Assert.Equal("2024_12_31-0042-EOS R6", name);
    }

    [Fact]
    public void ExtensionToken_IsNotDuplicated()
    {
        var name = NamingTemplate.BuildFileName("shot-{yyyy}.{ext}", Meta(new DateTime(2024, 1, 1)), 0, ".jpg");
        Assert.Equal("shot-2024.jpg", name);
        Assert.EndsWith(".jpg", name);
    }

    [Fact]
    public void BuildFileName_AppendsExtension()
    {
        var name = NamingTemplate.BuildFileName("{yyyy}-{MM}", Meta(new DateTime(2024, 1, 2)), 0, ".JPG");
        Assert.Equal("2024-01.jpg", name);
    }

    [Fact]
    public void InvalidCharacters_AreRemoved()
    {
        var sanitized = NamingTemplate.SanitizeFileName("a<b>c:d/e\\f|g?h*i\"j");
        Assert.DoesNotContain('<', sanitized);
        Assert.DoesNotContain('|', sanitized);
        Assert.DoesNotContain('"', sanitized);
    }

    [Fact]
    public void EnsureUniquePath_AppendsCounter()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var first = Path.Combine(dir, "photo.jpg");
            File.WriteAllText(first, "x");
            var unique = NamingTemplate.EnsureUniquePath(dir, "photo.jpg");
            Assert.Equal("photo-1.jpg", Path.GetFileName(unique));
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void FolderTemplate_StripsTraversal()
    {
        var folder = NamingTemplate.RenderRelativeFolder(@"..\{yyyy}\..\{MM}",
            Meta(new DateTime(2024, 6, 7)));
        Assert.DoesNotContain("..", folder);
        Assert.Contains("2024", folder);
        Assert.Contains("06", folder);
    }

    [Theory]
    [InlineData("Sony", "ILCE-7M3", "Sony ILCE-7M3")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6", "NIKON Z 6")]
    [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
    [InlineData("SONY", null, "Sony")]
    [InlineData(null, null, "未识别相机")]
    public void BuildDeviceFolder_ProducesStableCameraRoot(string? make, string? model, string expected)
    {
        var meta = Meta(DateTime.Now, model, make: make);
        Assert.Equal(expected, NamingTemplate.BuildDeviceFolder(meta));
    }
}
