using System.IO;
using System.Text.Json;
using System.Windows;

namespace Fotonizer.App;

/// <summary>
/// Lightweight runtime localization. Chinese is the source language in XAML; English
/// translations live in Localization/en-US.json and are applied to the visual tree.
/// </summary>
public static class Loc
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly Dictionary<string, string> ZhToEn = LoadTranslations();

    public static string Language { get; private set; } = Chinese;

    public static bool IsEnglish => Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static event EventHandler? LanguageChanged;

    public static string Normalize(string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (string.Equals(Language, normalized, StringComparison.Ordinal))
            return;

        Language = normalized;
        LanguageChanged?.Invoke(null, EventArgs.Empty);

        if (System.Windows.Application.Current == null) return;
        foreach (Window window in System.Windows.Application.Current.Windows)
            UiTranslator.Apply(window);
    }

    public static string T(string chinese, string english) => IsEnglish ? english : chinese;

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish) return text ?? "";
        return ZhToEn.TryGetValue(text, out var english) ? english : text;
    }

    public static void Apply(Window window) => UiTranslator.Apply(window);

    private static Dictionary<string, string> LoadTranslations()
    {
        try
        {
            var assembly = typeof(Loc).Assembly;
            using var stream = assembly.GetManifestResourceStream("Fotonizer.App.Localization.en-US.json");
            if (stream == null) return new Dictionary<string, string>(StringComparer.Ordinal);

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ??
                   new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
