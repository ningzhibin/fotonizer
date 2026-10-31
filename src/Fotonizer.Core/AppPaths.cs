namespace Fotonizer.Core;

public static class AppPaths
{
    public const string AppName = "Fotonizer";

    public static string RoamingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");

    public static string DefaultDatabasePath => Path.Combine(LocalDirectory, "library.db");

    public static string DefaultSettingsPath => Path.Combine(RoamingDirectory, "settings.json");

    public static string DefaultSecretsPath => Path.Combine(RoamingDirectory, "secrets.bin");

    public static string DefaultDestination
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                pictures = Path.Combine(profile, "Pictures");
            }
            return Path.Combine(pictures, AppName);
        }
    }

    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Environment.ExpandEnvironmentVariables(path.Trim());
    }

    public static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
