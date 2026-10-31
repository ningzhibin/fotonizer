using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fotonizer.Core.Models;

namespace Fotonizer.Core.Import;

/// <summary>
/// Renders file/folder name templates.
///
/// Supported tokens:
///   {yyyy} {yy} {MM} {dd} {HH} {mm} {ss} {fff}
///   {date} / {date:FORMAT}        e.g. {date:yyyy-MM-dd}
///   {time} / {time:FORMAT}
///   {datetime} / {datetime:FORMAT}
///   {camera} {make} {model} {original} {counter} / {counter:0000} {ext}
///
/// Unknown tokens are left untouched so typos are visible rather than silently dropped.
/// </summary>
public static partial class NamingTemplate
{
    [GeneratedRegex(@"\{([A-Za-z]+)(?::([^{}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public const string DefaultFileNameTemplate = "{yyyy}-{MM}-{dd}-{HH}{mm}{ss}";

    public static string Render(string template, PhotoMetadata meta, int counter = 0, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultFileNameTemplate;

        var dt = meta.CapturedAt;
        var ext = (extension ?? meta.Extension).TrimStart('.').ToLowerInvariant();

        var result = TokenRegex().Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            // Case-sensitive date/time tokens: MM = month, mm = minute.
            switch (token)
            {
                case "yyyy": return dt.ToString("yyyy", CultureInfo.InvariantCulture);
                case "yy": return dt.ToString("yy", CultureInfo.InvariantCulture);
                case "MM": return dt.ToString("MM", CultureInfo.InvariantCulture);
                case "dd": return dt.ToString("dd", CultureInfo.InvariantCulture);
                case "HH": return dt.ToString("HH", CultureInfo.InvariantCulture);
                case "mm": return dt.ToString("mm", CultureInfo.InvariantCulture);
                case "ss": return dt.ToString("ss", CultureInfo.InvariantCulture);
                case "fff": return dt.ToString("fff", CultureInfo.InvariantCulture);
            }

            switch (token.ToLowerInvariant())
            {
                case "date": return dt.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "time": return dt.ToString(format ?? "HHmmss", CultureInfo.InvariantCulture);
                case "datetime": return dt.ToString(format ?? "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
                case "camera": return meta.Camera;
                case "make": return meta.Make ?? "";
                case "model": return meta.Model ?? "";
                case "original": return Path.GetFileNameWithoutExtension(meta.SourcePath);
                case "counter": return counter.ToString(format ?? "D3", CultureInfo.InvariantCulture);
                case "ext": return ext;
                default: return match.Value;
            }
        });

        return result;
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "photo";
        var sb = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in name)
        {
            if (ch < 32 || invalid.Contains(ch)) continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.');
        while (cleaned.Contains("  ", StringComparison.Ordinal)) cleaned = cleaned.Replace("  ", " ");
        if (cleaned.Length == 0) cleaned = "photo";
        if (cleaned.Length > 150) cleaned = cleaned[..150].TrimEnd('.', ' ');
        return cleaned;
    }

    /// <summary>Renders a folder template and removes unsafe/empty segments.</summary>
    public static string RenderRelativeFolder(string template, PhotoMetadata meta, int counter = 0)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var rendered = Render(template, meta, counter);
        var segments = rendered
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeFileName)
            .Where(s => s is not "." and not "..")
            .ToArray();

        var result = Path.Combine(segments);
        if (result.Length > 240) result = result[..240];
        return result;
    }

    /// <summary>
    /// Builds the optional per-camera root folder, e.g. "Sony ILCE-7M3" or "Nikon Z 6".
    /// Falls back to "未识别相机" for files without EXIF make/model (e.g. some videos).
    /// </summary>
    public static string BuildDeviceFolder(PhotoMetadata meta, string? unknownFolder = null)
    {
        var make = NormalizeMake(meta.Make);
        var model = NormalizeModel(meta.Model);

        string name;
        if (string.IsNullOrWhiteSpace(model))
            name = string.IsNullOrWhiteSpace(make) ? unknownFolder ?? "未识别相机" : make;
        else if (string.IsNullOrWhiteSpace(make))
            name = model;
        else if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ||
                 make.StartsWith(model, StringComparison.OrdinalIgnoreCase))
            name = model.Length >= make.Length ? model : make;
        else
            name = make + " " + model;

        return SanitizeFileName(name);
    }

    private static string? NormalizeMake(string? make)
    {
        if (string.IsNullOrWhiteSpace(make)) return null;

        var value = make.Trim().Trim('\0');
        value = Regex.Replace(value,
                @"\s+(CORPORATION|CORP\.?|INC\.?|LTD\.?|CO\.?,?\s*LTD\.?)$",
                "", RegexOptions.IgnoreCase)
            .Trim();
        if (value.Length == 0) value = make.Trim();

        // "SONY" -> "Sony", "NIKON" -> "Nikon"; leave already mixed-case names alone.
        if (value.Any(char.IsLetter) && value.All(c => !char.IsLower(c)))
            value = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

        return value;
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        return Regex.Replace(model.Trim().Trim('\0'), @"\s+", " ");
    }

    /// <summary>
    /// Builds a file name (including extension). If the template contains {ext}, that token is
    /// used as the extension and no extra extension is appended.
    /// </summary>
    public static string BuildFileName(string template, PhotoMetadata meta, int counter, string outputExtension)
    {
        var ext = outputExtension.TrimStart('.').ToLowerInvariant();
        var hasExtToken = !string.IsNullOrWhiteSpace(template) &&
                          template.Contains("{ext}", StringComparison.OrdinalIgnoreCase);

        var rendered = Render(template, meta, counter, outputExtension);
        var name = SanitizeFileName(rendered);

        if (hasExtToken)
            return name;

        // Guard against a template that already ends with the extension.
        if (name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            return name;

        return name + "." + ext;
    }

    public static string EnsureUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem}-{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    public static string[] DescribeTokens() => new[]
    {
        "{yyyy} 年  {yy} 两位年  {MM} 月  {dd} 日",
        "{HH} 时  {mm} 分  {ss} 秒  {fff} 毫秒",
        "{date:yyyy-MM-dd} 自定义日期  {time:HHmmss} 自定义时间",
        "{camera} 相机型号  {make} 厂商  {original} 原文件名",
        "{counter} 序号（可写 {counter:0000}）  {ext} 扩展名"
    };
}
