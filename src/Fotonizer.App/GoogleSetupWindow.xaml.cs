using System.Diagnostics;
using System.Windows;

namespace Fotonizer.App;

public partial class GoogleSetupWindow : Window
{
    private const string ConsoleUrl = "https://console.cloud.google.com/apis/library/photoslibrary.googleapis.com";

    public GoogleSetupWindow()
    {
        InitializeComponent();
        Loc.Apply(this);
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ConsoleUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
