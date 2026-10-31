using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Fotonizer.App;

/// <summary>
/// Applies the current language to static text in a window's logical tree. Dynamic text
/// (bindings and view-model properties) is localized by the view models themselves.
/// </summary>
internal static class UiTranslator
{
    private static readonly DependencyProperty OriginalTextProperty =
        DependencyProperty.RegisterAttached("OriginalText", typeof(string), typeof(UiTranslator),
            new PropertyMetadata(null));

    public static void Apply(Window window)
    {
        ApplyElement(window);
    }

    private static void ApplyElement(DependencyObject element)
    {
        switch (element)
        {
            case Window window:
                ApplyText(window, window.Title, value => window.Title = value);
                break;

            case GridViewColumnHeader:
                // Generated from GridViewColumn.Header; translating it again would undo the header.
                return;

            case TextBlock textBlock when !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty):
                ApplyText(textBlock, textBlock.Text, value => textBlock.Text = value);
                break;

            case GridViewColumn column when column.Header is string header:
                ApplyText(column, header, value => column.Header = value);
                break;

            case ContentControl contentControl when contentControl.Content is string content:
                ApplyText(contentControl, content, value => contentControl.Content = value);
                break;
        }

        if (element is System.Windows.Controls.ListView listView && listView.View is GridView gridView)
        {
            foreach (var column in gridView.Columns)
                ApplyElement(column);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is DependencyObject dependencyObject)
                ApplyElement(dependencyObject);
        }
    }

    private static void ApplyText(DependencyObject owner, string? current, Action<string> setter)
    {
        if (string.IsNullOrEmpty(current)) return;

        var original = owner.GetValue(OriginalTextProperty) as string;
        if (original == null)
        {
            original = current;
            owner.SetValue(OriginalTextProperty, original);
        }

        var translated = Loc.Translate(original);
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            setter(translated);
    }
}
