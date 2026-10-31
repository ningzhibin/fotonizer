using Fotonizer.Core.Models;

namespace Fotonizer.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData(null, "zh-CN")]
    [InlineData("", "zh-CN")]
    [InlineData("en", "en-US")]
    [InlineData("en-US", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("fr-FR", "zh-CN")]
    public void Language_IsNormalized(string? input, string expected)
    {
        var settings = new AppSettings { Language = input! };
        settings.Normalize();
        Assert.Equal(expected, settings.Language);
    }
}
