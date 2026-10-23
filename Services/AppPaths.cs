using System.IO;

namespace TisViewer.Services;

/// <summary>
/// Central location for writable application data. Set TIS_DATA_DIR to keep
/// the cache, recent list and logs next to the application (portable mode).
/// </summary>
public static class AppPaths
{
    private const string OverrideVariable = "TIS_DATA_DIR";

    public static string DataDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return Path.GetFullPath(custom);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TisViewer");
        }
    }

    public static string CacheDirectory => Path.Combine(DataDirectory, "Cache");

    public static string RecentFile => Path.Combine(DataDirectory, "recent.json");

    public static string LogFile => Path.Combine(DataDirectory, "error.log");
}
