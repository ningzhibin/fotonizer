using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Fotonizer.Core.Logging;

namespace Fotonizer.App.Converters;

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is LogLevel level
            ? level switch
            {
                LogLevel.Error => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                LogLevel.Warn => new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                LogLevel.Debug => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                _ => new SolidColorBrush(Color.FromRgb(27, 36, 48))
            }
            : Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
